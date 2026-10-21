using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace PCL.Injector.Core;

// LauncherKind 和 LauncherInfo 已统一在 Models.cs 中定义
public static class Sniffer
{
    public static List<LauncherInfo> FindAll()
    {
        var results = new Dictionary<string, LauncherInfo>();

        // 1. 注册表 Uninstall 键
        ScanRegistry(results);

        // 2. 常见安装目录
        ScanCommonDirs(results);

        // 3. 开始菜单 .lnk
        ScanShortcuts(results);

        // 4. 程序目录自身 (如果运行)
        // 留给用户手选

        return results.Values
            .OrderByDescending(i => i.Kind) // 未知的排后面
            .ToList();
    }

    public static LauncherKind DetectKind(string path) => LauncherDetector.Detect(path);

    // ==================== 嗅探实现 ====================

    private static void ScanRegistry(Dictionary<string, LauncherInfo> results)
    {
        string[] roots = {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        };

        foreach (var root in roots)
        {
            using var baseKey = Registry.LocalMachine.OpenSubKey(root);
            AddFromSubkey(baseKey, results);

            using var userBase = Registry.CurrentUser.OpenSubKey(root);
            AddFromSubkey(userBase, results);
        }
    }

    private static void AddFromSubkey(RegistryKey? baseKey, Dictionary<string, LauncherInfo> results)
    {
        if (baseKey == null) return;
        foreach (var subName in baseKey.GetSubKeyNames())
        {
            using var sub = baseKey.OpenSubKey(subName);
            if (sub == null) continue;

            string? displayName = sub.GetValue("DisplayName")?.ToString();
            string? installLocation = sub.GetValue("InstallLocation")?.ToString();
            string? displayIcon = sub.GetValue("DisplayIcon")?.ToString();

            if (displayName == null) continue;

            bool isPcl = displayName.Contains("Plain Craft Launcher", StringComparison.OrdinalIgnoreCase) ||
                         (displayName.Contains("PCL", StringComparison.OrdinalIgnoreCase) &&
                          !displayName.Contains("Injector", StringComparison.OrdinalIgnoreCase));
            bool isHmcl = displayName.Contains("Hello Minecraft", StringComparison.OrdinalIgnoreCase) ||
                          displayName.Contains("HMCL", StringComparison.OrdinalIgnoreCase);

            string? exePath = null;
            if (isPcl || isHmcl)
            {
                if (!string.IsNullOrEmpty(displayIcon) && File.Exists(displayIcon))
                    exePath = displayIcon;

                if (exePath == null && !string.IsNullOrEmpty(installLocation))
                {
                    string searchName = isHmcl ? "HMCL*.exe" : "Plain Craft Launcher 2.exe";
                    foreach (var f in Directory.GetFiles(installLocation, searchName))
                    {
                        exePath = f;
                        break;
                    }
                }
            }

            if (exePath != null && !results.ContainsKey(exePath))
            {
                results[exePath] = new LauncherInfo
                {
                    Name = displayName,
                    Path = exePath,
                    Kind = DetectKind(exePath)
                };
            }
        }
    }

    private static void ScanCommonDirs(Dictionary<string, LauncherInfo> results)
    {
        string[] dirs = {
            @"C:\Program Files",
            @"C:\Program Files (x86)",
            @"C:\ProgramData",
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        };

        string[] patterns = {
            @"**\Plain Craft Launcher 2.exe",
            @"**\HMCL*.exe",
        };

        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var pattern in patterns)
                {
                    foreach (var f in Directory.EnumerateFiles(dir, Path.GetFileName(pattern), SearchOption.AllDirectories))
                    {
                        if (f.Length > 1000 * 1000 * 500) continue; // 跳过超大文件
                        if (!results.ContainsKey(f))
                        {
                            results[f] = new LauncherInfo
                            {
                                Name = Path.GetFileNameWithoutExtension(f),
                                Path = f,
                                Kind = DetectKind(f)
                            };
                        }
                    }
                }
            }
            catch { /* 权限不足等 */ }
        }
    }

    private static void ScanShortcuts(Dictionary<string, LauncherInfo> results)
    {
        string menuDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Windows\Start Menu\Programs");

        if (!Directory.Exists(menuDir)) return;

        string[] keywords = { "PCL", "Plain Craft", "HMCL", "Hello Minecraft" };

        foreach (var lnk in Directory.EnumerateFiles(menuDir, "*.lnk", SearchOption.AllDirectories))
        {
            string name = Path.GetFileNameWithoutExtension(lnk);
            if (!keywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase))) continue;

            try
            {
                string? target = ResolveShortcut(lnk);
                if (target != null && File.Exists(target) && !results.ContainsKey(target))
                {
                    results[target] = new LauncherInfo
                    {
                        Name = name,
                        Path = target,
                        Kind = DetectKind(target)
                    };
                }
            }
            catch { /* ignore */ }
        }
    }

    private static string? ResolveShortcut(string lnkPath) => ShellLinkResolver.Resolve(lnkPath);
}
