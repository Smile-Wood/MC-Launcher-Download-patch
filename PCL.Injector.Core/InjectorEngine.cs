using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Injector.Core;

/// <summary>
/// 核心注入引擎 —— 复用 CLI 的注入逻辑, 供 CLI 和 GUI 共用
/// </summary>
public class InjectorEngine
{
    private const string PayloadDllName = "PCL.Injector.Payload.dll";
    private const string HmclAgentJarName = "hmcl-agent.jar";

    // 已处理进程指纹 (PID+StartTime) —— 防重复接管
    private static readonly HashSet<string> _handled = new();

    // 临时补丁文件注册表
    private static readonly HashSet<string> _ephemeralFiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>运行时资源目录 (嵌入资源解压目标)</summary>
    public static string RuntimeDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PCL-Injector", "runtime");

    private static readonly object _monitorLogLock = new();

    /// <summary>监控日志路径: %LOCALAPPDATA%\PCL-Injector\monitor.log</summary>
    public static string MonitorLogPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PCL-Injector", "monitor.log");

    /// <summary>Payload 侧日志路径: %LOCALAPPDATA%\PCL-Injector\inject.log</summary>
    public static string InjectLogPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PCL-Injector", "inject.log");

    // ==================== 日志 ====================

    public static void MonitorLog(string message)
    {
        try
        {
            lock (_monitorLogLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(MonitorLogPath)!);
                File.AppendAllText(MonitorLogPath,
                    $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
        }
        catch { }
    }

    // ==================== 资源解压 ====================

    public static string LocatePayloadDll() => ExtractResource("Payload.dll", PayloadDllName);
    public static string LocateHmclAgent() => ExtractResource("hmcl-agent.jar", HmclAgentJarName);

    /// <summary>Payload 依赖一并解压 (两个 Harmony 变体)</summary>
    public static void ExtractHarmonyVariants()
    {
        try { ExtractResource("0Harmony.dll", "0Harmony.dll"); }
        catch (Exception ex) { MonitorLog("解压 0Harmony.dll 失败: " + ex.Message); }
        try { ExtractResource("0HarmonyCE.dll", "0HarmonyCE.dll"); }
        catch (Exception ex) { MonitorLog("解压 0HarmonyCE.dll 失败: " + ex.Message); }
    }

    private static string ExtractResource(string logicalName, string fileName)
    {
        Directory.CreateDirectory(RuntimeDir);
        string target = Path.Combine(RuntimeDir, fileName);
        var asm = ResolveResourceAssembly(logicalName)
            ?? throw new FileNotFoundException($"嵌入资源缺失: {logicalName}");
        using var src = asm.GetManifestResourceStream(logicalName)!;
        using var dst = File.Create(target);
        src.CopyTo(dst);
        return target;
    }

    /// <summary>
    /// 跨程序集定位嵌入资源.
    /// 重构后本代码位于 Core.dll, 但 Payload/Harmony 资源按 LogicalName 嵌入在宿主程序集
    /// (PCL-Injector.exe / PCL-Injector-Gui.exe); 单文件发布时二者是 bundle 内不同程序集,
    /// GetExecutingAssembly() 只会返回 Core.dll → 永远找不到资源.
    /// 顺序: 入口程序集 → 本程序集 → 当前 AppDomain 全部已加载程序集.
    /// </summary>
    private static Assembly? ResolveResourceAssembly(string logicalName)
    {
        var entry = Assembly.GetEntryAssembly();
        if (entry?.GetManifestResourceInfo(logicalName) != null) return entry;

        var exec = Assembly.GetExecutingAssembly();
        if (!ReferenceEquals(entry, exec) && exec.GetManifestResourceInfo(logicalName) != null)
            return exec;

        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            try { if (a.GetManifestResourceInfo(logicalName) != null) return a; }
            catch { /* 动态程序集等忽略 */ }
        }
        return null;
    }

    // ==================== 启动注入 (预启动注入, 无需重启) ====================

    /// <summary>直接启动带注入的启动器 (预启动注入, 最优路径)</summary>
    public static Process? LaunchAndInject(string targetPath, LauncherKind kind, Action<string>? onLog = null)
    {
        onLog ??= MonitorLog;
        onLog($"LaunchAndInject \"{targetPath}\" => {kind}");

        try
        {
            // 先结束同路径的已运行实例: 单实例启动器发现旧实例时新进程会立刻退出
            // (表现为 "进程启动后立即退出"), 补丁临时 exe 也会被旧实例占用无法覆盖
            EnsureNotAlreadyRunning(targetPath, kind, onLog);

            Process? proc;
            switch (kind)
            {
                case LauncherKind.PclCe:
                    proc = LaunchPclCe(targetPath, onLog);
                    break;
                case LauncherKind.PclOriginal:
                    proc = LaunchPclOriginal(targetPath, out var ephemeralPath, onLog);
                    break;
                case LauncherKind.Hmcl:
                    proc = LaunchHmcl(targetPath, onLog);
                    break;
                default:
                    proc = null;
                    break;
            }

            if (proc != null)
            {
                Thread.Sleep(800);
                if (proc.HasExited)
                {
                    onLog($"✗ 进程启动后立即退出 (exit={proc.ExitCode})");
                    return null;
                }
                _handled.Add($"{proc.Id}:{proc.StartTime:o}");
                onLog($"✓ 已启动 PID={proc.Id} (注入已生效)");
                return proc;
            }
            onLog("✗ 启动失败");
            return null;
        }
        catch (Exception ex)
        {
            onLog($"✗ 启动异常: {ex.Message}");
            return null;
        }
    }

    // ==================== 启动前: 结束已运行的旧实例 ====================

    /// <summary>
    /// 启动注入前确保目标启动器没有旧实例在运行。
    /// 单实例启动器 (PCL 系列 / HMCL) 检测到已有实例时新进程会立刻退出,
    /// 表现为 "进程启动后立即退出"; PCL 原版补丁临时 exe 还会被旧实例占用无法覆盖。
    /// 策略: 先 CloseMainWindow 优雅关闭 (3s), 失败再 Kill。
    /// 只结束启动器自身, 不杀进程树 —— Minecraft 游戏 java 是其子进程, 必须保留。
    /// </summary>
    private static void EnsureNotAlreadyRunning(string targetPath, LauncherKind kind, Action<string> onLog)
    {
        var victims = new List<Process>();
        string full;
        try { full = Path.GetFullPath(targetPath); } catch { full = targetPath; }

        void AddIfMatch(Process p, string? file)
        {
            if (string.IsNullOrEmpty(file)) return;
            try
            {
                if (string.Equals(Path.GetFullPath(file), full, StringComparison.OrdinalIgnoreCase)
                    && !victims.Any(v => v.Id == p.Id))
                    victims.Add(p);
            }
            catch { }
        }

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == Environment.ProcessId || p.HasExited) continue;
                AddIfMatch(p, p.MainModule?.FileName);
            }
            catch { }
        }

        // PCL 原版: 补丁版临时 exe "xxx (patched).exe" 也是同一实例
        if (kind == LauncherKind.PclOriginal)
        {
            string patchedName = Path.GetFileNameWithoutExtension(full) + " (patched).exe";
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id == Environment.ProcessId || p.HasExited) continue;
                    var f = p.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(f)
                        && string.Equals(Path.GetFileName(f), patchedName, StringComparison.OrdinalIgnoreCase)
                        && !victims.Any(v => v.Id == p.Id))
                        victims.Add(p);
                }
                catch { }
            }
        }

        // HMCL: 官方 exe 是 launch4j 壳, 真正的进程是 java(w).exe -jar ...HMCL..., 按命令行补匹配
        if (kind == LauncherKind.Hmcl)
        {
            string stem = Path.GetFileNameWithoutExtension(full);
            foreach (var (p, cmd) in EnumerateJavaCommandLines())
            {
                try
                {
                    if (p.HasExited) continue;
                    if (!string.IsNullOrEmpty(cmd)
                        && cmd.Contains(stem, StringComparison.OrdinalIgnoreCase)
                        && !victims.Any(v => v.Id == p.Id))
                        victims.Add(p);
                }
                catch { }
            }
        }

        foreach (var p in victims)
        {
            string name;
            try { name = p.ProcessName; } catch { name = "?"; }
            onLog($"⚠ {name} (PID={p.Id}) 已在运行, 先结束旧实例再注入");
            if (!TryGracefulOrKill(p))
                onLog($"✗ 无法结束 PID={p.Id} (可能需要管理员权限), 继续启动可能因单实例冲突而失败");
        }
        if (victims.Count > 0) Thread.Sleep(1500); // .NET 运行时释放启动钩子/加载锁较慢, 等句柄/单实例锁完全释放
    }

    /// <summary>先请求主窗口优雅关闭, 超时则强杀; 返回进程是否已退出。不杀子进程。</summary>
    private static bool TryGracefulOrKill(Process p)
    {
        try
        {
            if (p.CloseMainWindow() && p.WaitForExit(3000)) return true;
        }
        catch { /* 无窗口或无权限 → 直接强杀 */ }

        for (int attempt = 0; attempt < 2; attempt++)
        {
            try { p.Kill(); }
            catch { return false; }
            try { if (p.WaitForExit(3000)) return true; } catch { }
        }
        try { return p.HasExited; } catch { return false; }
    }

    /// <summary>枚举 java/javaw 进程及其命令行 (WMI), 供 HMCL 实例识别</summary>
    private static IEnumerable<(Process proc, string commandLine)> EnumerateJavaCommandLines()
    {
        var moList = new List<(int pid, string cmd)>();
        System.Management.ManagementObjectSearcher? searcher = null;
        try
        {
            searcher = new System.Management.ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process "
                + "WHERE Name = 'java.exe' OR Name = 'javaw.exe'");
            foreach (System.Management.ManagementObject mo in searcher.Get())
                moList.Add((Convert.ToInt32(mo["ProcessId"]), mo["CommandLine"]?.ToString() ?? ""));
        }
        catch { /* WMI 不可用 → 返回空 */ }
        finally { searcher?.Dispose(); }

        // yield return 不在含 catch 的 try 块内 (CS1626 限制)
        foreach (var (pid, cmd) in moList)
        {
            Process? p = null;
            try { p = Process.GetProcessById(pid); } catch { }
            if (p != null) yield return (p, cmd);
        }
    }

    private static Process? LaunchPclCe(string exePath, Action<string> onLog)
    {
        onLog("策略: DOTNET_STARTUP_HOOKS → Payload.Initialize() → Harmony Prefix");
        ExtractHarmonyVariants();
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? "."
        };
        psi.Environment["DOTNET_STARTUP_HOOKS"] = LocatePayloadDll();
        // CE 不能回退 ShellExecute: 那样 DOTNET_STARTUP_HOOKS 环境变量会丢失, 注入直接失效.
        // __COMPAT_LAYER=RunAsInvoker 已能覆盖 RUNASADMIN 兼容性 shim.
        return StartProcessSmart(psi, allowShellFallback: false, exePath, onLog);
    }

    private static Process? LaunchPclOriginal(string exePath, out string? ephemeralPath, Action<string> onLog)
    {
        ephemeralPath = null;
        string dir = Path.GetDirectoryName(exePath) ?? ".";
        string name = Path.GetFileNameWithoutExtension(exePath);
        string patchedPath = Path.Combine(dir, $"{name} (patched).exe");

        onLog("策略: dnlib 静态 IL 补丁 (临时文件, 关闭后自动删除)");

        bool created = false;
        try
        {
            try { if (File.Exists(patchedPath)) File.Delete(patchedPath); }
            catch (IOException) { /* 被占用 → 直接启动现有文件 */ }

            if (!File.Exists(patchedPath))
            {
                using var mem = new MemoryStream();
                PclIlPatcher.Patch(mem, exePath);
                byte[] bytes = mem.ToArray();
                using (var ws = new FileStream(patchedPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                    ws.Write(bytes, 0, bytes.Length);
                created = true;
            }
        }
        catch (Exception ex)
        {
            onLog($"✗ 静态补丁失败: {ex.Message}");
            if (!File.Exists(patchedPath))
            {
                onLog("→ 回退: 启动原版 exe (未打补丁)");
                patchedPath = exePath;
            }
            else
            {
                onLog("→ 使用现有补丁版文件继续");
            }
        }

        if (created)
        {
            try { File.SetAttributes(patchedPath, FileAttributes.Hidden); } catch { }
            lock (_ephemeralFiles) _ephemeralFiles.Add(patchedPath);
            ephemeralPath = patchedPath;
        }

        var psi = new ProcessStartInfo
        {
            FileName = patchedPath,
            UseShellExecute = false,
            WorkingDirectory = dir
        };
        // 静态补丁不依赖环境变量, 740 时允许回退 ShellExecute 弹 UAC
        var proc = StartProcessSmart(psi, allowShellFallback: true, patchedPath, onLog);
        if (proc != null && created)
        {
            TrackEphemeralForProcess(patchedPath, proc);
        }
        return proc;
    }

    private static Process? LaunchHmcl(string exePath, Action<string> onLog)
    {
        string agentJar = LocateHmclAgent();
        string? javaPath = FindJava();
        if (string.IsNullOrEmpty(javaPath)) { onLog("✗ 找不到 java.exe"); return null; }
        string? jarPath = ExtractOrLocateJar(exePath);
        if (string.IsNullOrEmpty(jarPath)) { onLog("✗ 找不到 HMCL 内嵌 JAR"); return null; }

        onLog("策略: java -javaagent + -jar");
        string jvmArgs = " --add-opens java.base/java.lang=ALL-UNNAMED --add-opens java.base/java.lang.reflect=ALL-UNNAMED --add-opens java.base/jdk.internal.loader=ALL-UNNAMED --enable-native-access=ALL-UNNAMED";
        var psi = new ProcessStartInfo
        {
            FileName = javaPath,
            Arguments = $"{jvmArgs} -javaagent:\"{agentJar}\" -jar \"{jarPath}\"",
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? "."
        };
        // -javaagent 走命令行参数, ShellExecute 同样能传递, 允许 740 回退
        return StartProcessSmart(psi, allowShellFallback: true, javaPath, onLog);
    }

    // ==================== 进程启动 (RUNASADMIN shim 处理) ====================

    /// <summary>
    /// 统一进程启动入口, 解决 "请求的操作需要提升 (Win32 740 / ERROR_ELEVATION_REQUIRED)".
    /// 根因: 用户在文件属性→兼容性里勾了 "以管理员身份运行此程序",
    /// 对应 HKCU\...\AppCompatFlags\Layers 中该*精确路径*的 RUNASADMIN 值 ——
    /// 即使 exe manifest 是 asInvoker 也会被强制提升; 资源管理器双击走 ShellExecute 会弹
    /// UAC, 而 CreateProcess (UseShellExecute=false) 直接失败 740.
    ///
    /// 策略:
    /// 1) 子进程环境注入 __COMPAT_LAYER=RunAsInvoker, 让兼容层基础设施静默忽略
    ///    RUNASADMIN shim (PCL 系列 manifest 本就是 asInvoker, 不需要管理员)
    /// 2) 仍 740 且 allowShellFallback → UseShellExecute=true 走正常 UAC 弹窗.
    ///    依赖自定义环境变量注入的场景 (PCL-CE DOTNET_STARTUP_HOOKS) 不能回退, 否则注入失效.
    /// </summary>
    private static Process? StartProcessSmart(ProcessStartInfo psi, bool allowShellFallback,
        string shimProbePath, Action<string> onLog)
    {
        bool shim = HasRunAsAdminLayer(shimProbePath);
        if (shim)
            onLog("⚠ 检测到该路径被设置了 \"以管理员身份运行\" 兼容性选项 (RUNASADMIN), "
                + "注入 __COMPAT_LAYER=RunAsInvoker 以普通身份静默启动");

        ApplyRunAsInvoker(psi);

        try
        {
            return Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 740)
        {
            if (!allowShellFallback)
            {
                onLog("✗ 目标强制要求管理员权限, 且此注入方式不能弹 UAC (环境变量注入会失效). "
                    + "请在文件属性→兼容性中取消 \"以管理员身份运行此程序\" 后重试");
                return null;
            }

            onLog("⚠ RunAsInvoker 未绕过提升要求, 改为 ShellExecute 启动 (将弹出 UAC 确认框, 请点 \"是\")");
            var shellPsi = new ProcessStartInfo
            {
                FileName = psi.FileName,
                Arguments = psi.Arguments,
                WorkingDirectory = string.IsNullOrEmpty(psi.WorkingDirectory)
                    ? Path.GetDirectoryName(psi.FileName) : psi.WorkingDirectory,
                UseShellExecute = true
            };
            return Process.Start(shellPsi);
        }
    }

    /// <summary>给子进程环境块追加 __COMPAT_LAYER=RunAsInvoker (保留已有其它 compat 标记)</summary>
    private static void ApplyRunAsInvoker(ProcessStartInfo psi)
    {
        string existing = psi.Environment.TryGetValue("__COMPAT_LAYER", out var v) ? v ?? "" : "";
        if (!existing.Contains("RunAsInvoker", StringComparison.OrdinalIgnoreCase))
            psi.Environment["__COMPAT_LAYER"] = (existing + " RunAsInvoker").Trim();
    }

    /// <summary>检查精确目标路径是否在 HKCU AppCompat Layers 中被设为 RUNASADMIN</summary>
    private static bool HasRunAsAdminLayer(string targetPath)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers");
            var val = key?.GetValue(targetPath) as string;
            return val != null
                && val.Contains("RUNASADMIN", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // ==================== 运行中进程接管 (kill+重启 或 jcmd attach) ====================

    /// <summary>扫描并接管运行中的启动器进程</summary>
    public static bool TryAttachRunningProcess(Process proc, LauncherKind kind, string path, Action<string>? onLog = null)
    {
        onLog ??= MonitorLog;
        switch (kind)
        {
            case LauncherKind.Hmcl:
                return AttachHmcl(proc, onLog);
            case LauncherKind.PclCe:
            case LauncherKind.PclOriginal:
                var (ok, _) = RelaunchWithInjection(proc, kind, path, onLog);
                return ok;
            default:
                return false;
        }
    }

    public static (bool ok, Process? proc) RelaunchWithInjection(Process proc, LauncherKind kind, string path, Action<string> onLog)
    {
        onLog($"接管 {kind} PID={proc.Id}: kill -> relaunch");
        try
        {
            proc.Kill();
            proc.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            onLog($"kill 失败: {ex.Message}");
            return (false, null);
        }
        Thread.Sleep(2000);
        var newProc = LaunchAndInject(path, kind, onLog);
        return (newProc != null, newProc);
    }

    public static bool AttachHmcl(Process proc, Action<string> onLog)
    {
        string agentJar = LocateHmclAgent();
        if (!File.Exists(agentJar)) { onLog("✗ hmcl-agent.jar 不存在"); return false; }

        onLog($"正在 jcmd attach PID={proc.Id} ...");
        string? javaPath = FindJava();
        if (string.IsNullOrEmpty(javaPath)) { onLog("✗ 找不到 java.exe"); return false; }
        string jcmdPath = Path.Combine(Path.GetDirectoryName(javaPath)!, "jcmd.exe");
        if (!File.Exists(jcmdPath))
        {
            onLog("⚠ 找不到 jcmd.exe (需要 JDK 而非 JRE) — 无法运行时 attach");
            return false;
        }

        try
        {
            using var jcmd = Process.Start(new ProcessStartInfo
            {
                FileName = jcmdPath,
                Arguments = $"{proc.Id} VM.loadAgent \"{agentJar}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetTempPath()
            });
            jcmd!.WaitForExit(15000);
            string err = jcmd.StandardError.ReadToEnd().Trim();
            string outp = jcmd.StandardOutput.ReadToEnd().Trim();
            string combined = $"{outp} {err}";

            if (jcmd.ExitCode == 0 && !outp.Contains("Error") && !combined.Contains("failed"))
            {
                onLog("✓ HMCL attach 成功 (RegionUtils 已 patch)");
                return true;
            }
            onLog($"✗ attach 失败: {combined}");
            return false;
        }
        catch (Exception ex) { onLog($"✗ jcmd 异常: {ex.Message}"); return false; }
    }

    // ==================== 进程扫描 ====================

    /// <summary>扫描系统进程, 发现启动器就返回 (供 GUI 自动探测保存用)</summary>
    public static List<(Process proc, string path, LauncherKind kind)> ScanRunningLaunchers()
    {
        var results = new List<(Process, string, LauncherKind)>();
        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                if (proc.HasExited) continue;
                if (proc.Id == Environment.ProcessId) continue;
                var mainFile = proc.MainModule?.FileName;
                if (string.IsNullOrEmpty(mainFile)) continue;
                var name = Path.GetFileNameWithoutExtension(mainFile).ToLower();
                if (!IsCandidateLauncherName(name)) continue;

                string fp = $"{proc.Id}:{proc.StartTime:o}";
                if (_handled.Contains(fp)) continue;

                if (mainFile.Contains(" (patched)")) continue;

                var kind = LauncherDetector.Detect(mainFile);
                if (kind != LauncherKind.Unknown)
                {
                    results.Add((proc, mainFile, kind));
                }
            }
            catch { }
        }
        return results;
    }

    private static bool IsCandidateLauncherName(string lowerName) =>
        lowerName.Contains("pcl") || lowerName.Contains("craft") || lowerName.Contains("hmcl");

    // ==================== 临时文件管理 ====================

    private static void TrackEphemeralForProcess(string path, Process proc)
    {
        lock (_ephemeralFiles) _ephemeralFiles.Add(path);
        _ = Task.Run(async () =>
        {
            try { await proc.WaitForExitAsync(); } catch { }
            ReleaseEphemeral(path);
        });
    }

    private static void ReleaseEphemeral(string path)
    {
        lock (_ephemeralFiles) _ephemeralFiles.Remove(path);
        for (int i = 0; i < 6; i++)
        {
            try
            {
                if (File.Exists(path))
                {
                    try { File.SetAttributes(path, FileAttributes.Normal); } catch { }
                    File.Delete(path);
                }
                return;
            }
            catch (IOException) { Thread.Sleep(300); }
            catch { return; }
        }
    }

    public static void EphemeralCleanup()
    {
        List<string> paths;
        lock (_ephemeralFiles) paths = _ephemeralFiles.ToList();
        foreach (var path in paths) ReleaseEphemeral(path);
    }

    // ==================== Java 查找 ====================

    private static string? FindJava()
    {
        string? javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrEmpty(javaHome))
        {
            string c = Path.Combine(javaHome, "bin", "java.exe");
            if (File.Exists(c)) return c;
        }
        string? pathDir = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathDir))
        {
            foreach (var dir in pathDir.Split(Path.PathSeparator))
            {
                string c = Path.Combine(dir, "java.exe");
                if (File.Exists(c)) return c;
            }
        }
        foreach (var dir in new[] { @"C:\Program Files\Java", @"C:\Program Files (x86)\Java", @"C:\Program Files\Microsoft", @"C:\Program Files (x86)\Microsoft" })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var sub in Directory.GetDirectories(dir))
            {
                string c = Path.Combine(sub, "bin", "java.exe");
                if (File.Exists(c)) return c;
            }
        }
        return null;
    }

    private static string? ExtractOrLocateJar(string exePath)
    {
        try
        {
            using var fs = File.OpenRead(exePath);
            fs.Seek(Math.Max(0, fs.Length - 8192), SeekOrigin.Begin);
            var buf = new byte[8192];
            int read = fs.Read(buf, 0, buf.Length);
            for (int i = 0; i < read - 4; i++)
                if (buf[i] == 0x50 && buf[i + 1] == 0x4B && buf[i + 2] == 0x03 && buf[i + 3] == 0x04)
                {
                    long jarStart = fs.Length - (buf.Length - i);
                    if (jarStart > 0)
                    {
                        string tempJar = Path.GetTempFileName() + ".jar";
                        using var outFs = File.Create(tempJar);
                        fs.Seek(jarStart, SeekOrigin.Begin);
                        fs.CopyTo(outFs);
                        return tempJar;
                    }
                }
        }
        catch { }
        return exePath.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ? exePath : null;
    }
}
