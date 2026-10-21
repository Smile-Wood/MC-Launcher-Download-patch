using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using dnlib.DotNet;

namespace PCL.Injector.Core;

/// <summary>
/// 多层次启动器特征检测 —— 解决混放目录下 CE/原版 误判
/// 用 dnlib 读 Metadata, 避免手写 PE RVA→FileOffset 的坑
/// </summary>
public static class LauncherDetector
{
    public static LauncherKind Detect(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        string name = Path.GetFileName(path).ToLowerInvariant();

        // === Java ===
        if (ext == ".jar")
        {
            if (IsHmclJar(path)) return LauncherKind.Hmcl;
            return LauncherKind.Hmcl; // 默认 jar 即 HMCL
        }

        // === HMCL exe 包装器 (带尾部 JAR ZIP) ===
        if ((ext == ".exe" || string.IsNullOrEmpty(ext)) && IsHmclExeWrapper(path))
            return LauncherKind.Hmcl;

        // === .NET 程序集 (用 dnlib 尝试加载) ===
        var module = TryLoadDotNetModule(path);
        if (module != null)
        {
            return ClassifyDotNet(path, module);
        }

        // === 非 CLI 入口的 exe: .NET apphost 原生壳 (PCL-CE 就是这种) ===
        // apphost 的 CLR DataDirectory[14] == 0, dnlib 无法加载;
        // 但壳内嵌 hostfxr.dll 引导串 + 内嵌包里有 PCL.Core.dll / RestrictedFeaturePolicy.
        if (ScanAnyString(path, "RestrictedFeaturePolicy", "PCL.Core.dll", "hostfxr.dll", "PCLCE", "PCL2CE"))
            return LauncherKind.PclCe;

        // === HMCL exe 包装器兜底 (尾部 ZIP) ===
        if (IsHmclExeWrapper(path)) return LauncherKind.Hmcl;

        // === 回退: 文件名 ===
        if (name.Contains("hmcl") || name.Contains("hello minecraft")) return LauncherKind.Hmcl;

        // === 回退: runtimeconfig.json 同目录 ===
        string dir = Path.GetDirectoryName(path) ?? ".";
        if (File.Exists(Path.Combine(dir, "runtimeconfig.json")))
            return LauncherKind.PclCe;

        return LauncherKind.Unknown;
    }

    // ==================== dnlib 加载 ====================

    private static ModuleDefMD? TryLoadDotNetModule(string path)
    {
        try { return ModuleDefMD.Load(path); }
        catch { return null; }
    }

    // ==================== 特征分类 ====================

    private static LauncherKind ClassifyDotNet(string path, ModuleDefMD module)
    {
        string runtimeVersion = module.RuntimeVersion;
        string dir = Path.GetDirectoryName(path) ?? ".";
        bool hasPclCore = File.Exists(Path.Combine(dir, "PCL.Core.dll"));
        bool hasRuntimeConfig = File.Exists(Path.Combine(dir, "runtimeconfig.json"));

        // 判断目标框架
        bool isFramework = runtimeVersion.StartsWith("v4.0");
        bool isCore5Plus = !isFramework; // v5 / v6 / v8 / v10

        // === CE 特征 ===
        // .NET Core+ 或同目录有 runtimeconfig.json / PCL.Core.dll
        if (isCore5Plus || hasRuntimeConfig || hasPclCore)
        {
            // 进一步用类型/字符串特征确认
            if (HasAnyType(module, "RestrictedFeaturePolicy", "FeaturePolicy"))
                return LauncherKind.PclCe;
            if (ScanEmbeddedStrings(path, "RestrictedFeaturePolicy"))
                return LauncherKind.PclCe;
            // .NET 5+ + 有 runtimeconfig → 高概率是 CE
            if (isCore5Plus) return LauncherKind.PclCe;
        }

        // === 原版特征 ===
        if (isFramework)
        {
            if (HasAnyType(module, "ModModpack", "RestrictedFeaturePolicy"))
                return LauncherKind.PclOriginal;

            // 扫描引用 Microsoft.VisualBasic (VB.NET 原版标志)
            foreach (var tref in module.GetAssemblyRefs())
            {
                if (tref.Name == "Microsoft.VisualBasic")
                    return LauncherKind.PclOriginal;
            }

            // 字符串扫描兜底
            if (ScanEmbeddedStrings(path, "Microsoft.VisualBasic"))
                return LauncherKind.PclOriginal;

            // Framework 且无 CE 信号 → 保守判原版
            return LauncherKind.PclOriginal;
        }

        // 字符串扫描兜底
        if (ScanEmbeddedStrings(path, "RestrictedFeaturePolicy"))
            return LauncherKind.PclCe;
        if (ScanEmbeddedStrings(path, "ModModpack"))
            return LauncherKind.PclOriginal;

        return LauncherKind.Unknown;
    }

    private static bool HasAnyType(ModuleDefMD module, params string[] simpleNames)
    {
        try
        {
            foreach (var type in module.GetTypes())
            {
                foreach (var name in simpleNames)
                    if (type.Name == name) return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>扫描 PE 文件字节中是否包含指定 ASCII/UTF-16LE 字符串</summary>
    private static bool ScanEmbeddedStrings(string path, string needle)
    {
        try
        {
            using var fs = File.OpenRead(path);
            int cap = (int)Math.Min(fs.Length, 4 * 1024 * 1024);
            var buf = new byte[cap];
            int read = fs.Read(buf, 0, buf.Length);

            byte[] ascBytes = System.Text.Encoding.ASCII.GetBytes(needle);
            byte[] utf16Bytes = System.Text.Encoding.Unicode.GetBytes(needle);

            for (int i = 0; i < read - needle.Length; i++)
            {
                bool ascOk = true, u16Ok = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (ascOk && buf[i + j] != ascBytes[j]) ascOk = false;
                    if (u16Ok && buf[i + j * 2] != utf16Bytes[j * 2]) u16Ok = false;
                }
                if (ascOk || u16Ok) return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>扫描 PE 文件字节中是否包含任一 ASCII/UTF-16LE 字符串 (apphost 壳识别用)</summary>
    private static bool ScanAnyString(string path, params string[] needles)
    {
        try
        {
            using var fs = File.OpenRead(path);
            int cap = (int)Math.Min(fs.Length, 32L * 1024 * 1024);
            var buf = new byte[cap];
            int read = fs.Read(buf, 0, buf.Length);

            foreach (string needle in needles)
            {
                byte[] asc = System.Text.Encoding.ASCII.GetBytes(needle);
                byte[] u16 = System.Text.Encoding.Unicode.GetBytes(needle);
                int max = read - needle.Length;
                for (int i = 0; i < max; i++)
                {
                    if (buf[i] != asc[0] && buf[i] != u16[0]) continue;
                    bool ascOk = true, u16Ok = true;
                    for (int j = 1; j < needle.Length; j++)
                    {
                        if (ascOk && buf[i + j] != asc[j]) ascOk = false;
                        if (u16Ok && buf[i + j * 2] != u16[j * 2]) u16Ok = false;
                        if (!ascOk && !u16Ok) break;
                    }
                    if (ascOk || u16Ok) return true;
                }
            }
        }
        catch { }
        return false;
    }

    private static bool IsHmclExeWrapper(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            fs.Seek(Math.Max(0, fs.Length - 8192), SeekOrigin.Begin);
            var buf = new byte[8192];
            int read = fs.Read(buf, 0, buf.Length);
            for (int i = 0; i < read - 4; i++)
            {
                if (buf[i] == 0x50 && buf[i + 1] == 0x4B && buf[i + 2] == 0x03 && buf[i + 3] == 0x04)
                    return true;
            }
        }
        catch { }
        return false;
    }

    private static bool IsHmclJar(string path)
    {
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(path);
            return zip.Entries.Any(e => e.FullName == "org/jackhuang/hmcl/util/RegionUtils.class");
        }
        catch { return false; }
    }
}
