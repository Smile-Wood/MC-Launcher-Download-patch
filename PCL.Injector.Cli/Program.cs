using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using PCL.Injector.Core;

namespace PCL.Injector.Cli;

public class Program
{
    // ==================== 控制台窗口隐藏 ====================

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    private const int SW_HIDE = 0;

    public static int Main(string[] args)
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => InjectorEngine.EphemeralCleanup();

        // --detect-only <文件>: 只输出识别结果
        if (args.Length >= 2 && args[0] == "--detect-only")
        {
            var k = LauncherDetector.Detect(args[1]);
            Console.WriteLine($"{Path.GetFileName(args[1])} => {k}");
            return 0;
        }

        // --patch-only <源exe> <输出exe>: 只打静态补丁
        if (args.Length >= 3 && args[0] == "--patch-only")
        {
            try
            {
                PclIlPatcher.Patch(args[2], args[1]);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("PATCH FAILED: " + ex.Message);
                return 1;
            }
        }

        bool isAdmin = new System.Security.Principal.WindowsPrincipal(
            System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

        Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║   PCL / PCL-CE / HMCL 统一注入器 (CLI 监控模式)               ║");
        Console.WriteLine("║   只探测你启动的进程, 不会自动启动任何启动器                    ║");
        Console.WriteLine("║   注入成功后窗口自动隐藏, 启动器关闭后本工具退出并清理临时文件   ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
        Console.WriteLine();
        Console.WriteLine(isAdmin ? "✓ 管理员权限" : "· 当前用户权限 (未提权)");
        Console.WriteLine();

        // 控制台 + monitor.log 双写: 窗口隐藏后仍可在 %LOCALAPPDATA%\PCL-Injector\monitor.log 回溯
        void LogBoth(string m)
        {
            Console.WriteLine($"  {m}");
            InjectorEngine.MonitorLog(m);
        }

        // 命令行显式传入路径 → 打补丁并启动
        if (args.Length > 0 && File.Exists(args[0]))
        {
            Console.WriteLine($"手动指定: {Path.GetFileName(args[0])}");
            InjectorEngine.MonitorLog($"CLI 手动指定: {args[0]}");
            var kind = LauncherDetector.Detect(args[0]);
            var proc = InjectorEngine.LaunchAndInject(args[0], kind, LogBoth);
            if (proc != null)
            {
                HideConsole();
                // 伴随启动器退出
                proc.WaitForExit();
                InjectorEngine.EphemeralCleanup();
                return 0;
            }
            return 1;
        }

        Console.WriteLine("监控中... 请正常双击启动你的启动器, 本工具会自动接管.");
        Console.WriteLine("  - PCL 原版  → 自动关闭 → 生成补丁版 (临时文件) → 重启");
        Console.WriteLine("  - PCL-CE    → 自动关闭 → 带注入环境变量重启");
        Console.WriteLine("  - HMCL      → jcmd 运行时 attach (无需重启)");
        Console.WriteLine("按 Ctrl+C 退出监控.");
        Console.WriteLine();

        // 监控循环
        while (true)
        {
            try
            {
                var detected = InjectorEngine.ScanRunningLaunchers();
                foreach (var (proc, path, kind) in detected)
                {
                    Console.WriteLine();
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"[探测] PID={proc.Id} {Path.GetFileName(path)} ({kind})");
                    Console.ResetColor();

                    Console.ForegroundColor = ConsoleColor.Yellow;
                    if (kind == LauncherKind.Hmcl)
                        Console.WriteLine("  → jcmd attach (无需重启)");
                    else
                        Console.WriteLine("  → kill + 带注入重启");
                    Console.ResetColor();

                    var ok = InjectorEngine.TryAttachRunningProcess(proc, kind, path, LogBoth);
                    if (ok)
                    {
                        HideConsole();
                        // 成功接管一个后, 继续监控 (用户再开别的启动器也能处理)
                    }
                }
            }
            catch { }
            Thread.Sleep(2000);
        }
    }

    private static void HideConsole()
    {
        try
        {
            Console.WriteLine();
            Console.WriteLine("  注入完成. 窗口 3 秒后隐藏到后台 (继续监控, 任务栏无图标).");
            Console.WriteLine("  如需查看/关闭: 任务管理器找 PCL-Injector.exe.");
            Thread.Sleep(3000);
            ShowWindow(GetConsoleWindow(), SW_HIDE);
        }
        catch { }
    }
}
