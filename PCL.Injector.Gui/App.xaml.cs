using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Wpf = System.Windows;
using System.Windows.Threading;
using PCL.Injector.Core;
using WinForms = System.Windows.Forms;

namespace PCL.Injector.Gui;

/// <summary>
/// 核心应用类: 托盘图标的唯一拥有者 + 启动链路决策 + 进程生命周期.
///
/// 启动三态 (OnStartup, 全程只有唯一一个 MainWindow 实例):
///   AutoStart + SilentMode → 从不创建窗口, 直接托盘, 一次性自动注入
///   SilentMode 单独        → 显示窗口 + 自动注入, 注入成功后自动隐藏到托盘
///   其余                   → 正常显示窗口
///
/// 自退规则: UI 隐藏 (托盘态) 且 曾经接管过启动器 且 当前无活跃进程 → 清理并退出。
/// </summary>
public partial class App : Wpf.Application
{
    public static new App Current => (App)Wpf.Application.Current;

    public AppSettings Settings { get; private set; } = new();

    /// <summary>UI 线程状态文案 (主窗口订阅)</summary>
    public event Action<string>? StatusChanged;

    private WinForms.NotifyIcon? _tray;
    private MainWindow? _mainWindow;
    private readonly List<Process> _tracked = new();
    private DispatcherTimer? _scanTimer;

    private bool _uiHidden = true;    // 启动时 UI 尚未显示, 默认托盘态
    private bool _isExiting;          // 托盘菜单"退出"时放行窗口关闭
    private bool _hadLauncherOnce;    // 自退门槛: 至少接管过一个启动器
    private bool _autoLaunchDone;     // 一次性自动启动 (扫描定时器不重复拉起)
    private bool _hideAfterLaunch;    // SilentMode 模式: 首个进程接管后隐藏窗口
    private bool _autoStartOnFirstLoad; // 首次窗口 Loaded 时执行一次性自动注入

    public bool HasActiveLaunchers
    {
        get { lock (_tracked) return _tracked.Any(p => !p.HasExited); }
    }

    /// <summary>托盘菜单"退出"已触发 → 窗口 Closing 不再拦截</summary>
    public bool IsExiting => _isExiting;

    /// <summary>手动"开始注入"时勾选了静默模式 → 进程接管后隐藏窗口</summary>
    public void NotifySilentHideExpected() => _hideAfterLaunch = true;

    protected override void OnStartup(Wpf.StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = Wpf.ShutdownMode.OnExplicitShutdown; // 生命周期完全由托盘/自退控制

        Settings = AppSettings.Load();
        EnsureTrayCreated();

        _scanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _scanTimer.Tick += (_, _) => BackgroundScan();
        _scanTimer.Start();

        if (Settings.AutoStart && Settings.SilentMode)
        {
            // 托盘无窗模式: 不创建任何窗口
            _uiHidden = true;
            InjectorEngine.MonitorLog("App startup: AutoStart+SilentMode -> tray-only");
            AutoLaunchOnce();
        }
        else
        {
            if (Settings.SilentMode)
            {
                _hideAfterLaunch = true;
                _autoStartOnFirstLoad = true; // 必须在 Show 之前置位 (Loaded 在 Show() 内同步触发)
                InjectorEngine.MonitorLog("App startup: SilentMode -> window then auto-hide");
            }
            ShowMainWindow(); // 内部置 _uiHidden=false
        }
    }

    // ==================== 托盘 ====================

    private void EnsureTrayCreated()
    {
        if (_tray != null) return;

        _tray = new WinForms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true,
            Text = "PCL-Injector"
        };

        var menu = new WinForms.ContextMenuStrip();
        var versionItem = new WinForms.ToolStripMenuItem("PCL-Injector v" + GetVersion())
        {
            Enabled = false
        };
        var settingsItem = new WinForms.ToolStripMenuItem("设置");
        var exitItem = new WinForms.ToolStripMenuItem("退出");

        settingsItem.Click += (_, _) => ShowMainWindow();
        exitItem.Click += (_, _) => ShutdownApp();

        menu.Items.Add(versionItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(settingsItem);
        menu.Items.Add(exitItem);

        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ToggleMainWindow();
    }

    private static string GetVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        return $"{v.Major}.{v.Minor}.{v.Build}";
    }

    public void ToggleMainWindow()
    {
        if (_mainWindow is { IsVisible: true }) HideMainWindow();
        else ShowMainWindow();
    }

    public MainWindow ShowMainWindow()
    {
        if (_mainWindow == null)
        {
            _mainWindow = new MainWindow();
            Wpf.Application.Current.MainWindow = _mainWindow;
            // Loaded 在 Show() 内同步首次触发; 标志必须在 Show 前置好
            _mainWindow.Loaded += (_, _) =>
            {
                if (_autoStartOnFirstLoad)
                {
                    _autoStartOnFirstLoad = false;
                    AutoLaunchOnce();
                }
            };
        }
        _uiHidden = false;
        _mainWindow.ReloadData();          // 从磁盘刷新列表/开关 (托盘扫描可能新增过)
        _mainWindow.Show();
        _mainWindow.WindowState = Wpf.WindowState.Normal;
        _mainWindow.Activate();
        UpdateTrayTooltip();
        return _mainWindow;
    }

    public void HideMainWindow()
    {
        if (_mainWindow != null && _mainWindow.IsVisible) _mainWindow.Hide();
        _uiHidden = true;
        UpdateTrayTooltip();
        EvaluateSelfExit(); // 隐藏时若已无活跃进程 (且接管过启动器), 立即自退
    }

    private void ShutdownApp()
    {
        if (_isExiting) return;
        _isExiting = true;
        _scanTimer?.Stop();
        try { InjectorEngine.EphemeralCleanup(); } catch { }
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        Wpf.Application.Current.Shutdown();
    }

    public void UpdateTrayTooltip()
    {
        if (_tray == null) return;
        int n;
        lock (_tracked) n = _tracked.Count(p => !p.HasExited);
        _tray.Text = n > 0 ? $"PCL-Injector ({n} active)" : "PCL-Injector";
    }

    private void EmitStatus(string msg)
    {
        InjectorEngine.MonitorLog(msg);
        Dispatcher.BeginInvoke(new Action(() => StatusChanged?.Invoke(msg)));
    }

    // ==================== 自动注入 (一次性) ====================

    public void AutoLaunchOnce()
    {
        if (_autoLaunchDone) return;
        _autoLaunchDone = true;

        var sel = SavedLaunchers.GetSelected();
        if (sel == null)
        {
            InjectorEngine.MonitorLog("AutoInject: no enabled launchers");
            EmitStatusUi("自动注入: 没有已启用的启动器");
            return;
        }
        if (!File.Exists(sel.Path))
        {
            InjectorEngine.MonitorLog($"AutoInject: file missing: {sel.Path}");
            EmitStatusUi($"自动注入失败: 文件不存在 {sel.Path}");
            return;
        }

        InjectorEngine.MonitorLog($"AutoInject: auto-start {sel.Name} ({sel.Kind})");
        LaunchAsync(sel);
    }

    /// <summary>GUI "开始注入" 按钮和自动注入都走这里</summary>
    public void LaunchAsync(LauncherInfo info)
    {
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var proc = InjectorEngine.LaunchAndInject(info.Path, info.Kind,
                    m => InjectorEngine.MonitorLog(m));
                if (proc != null)
                {
                    TrackProcess(proc, info.Name);
                    EmitStatusUi($"✓ {info.Name} PID={proc.Id} 启动成功");
                }
                else
                {
                    EmitStatusUi($"✗ {info.Name} 启动失败");
                }
            }
            catch (Exception ex)
            {
                InjectorEngine.MonitorLog($"inject exception: {ex.Message}");
                EmitStatusUi($"✗ {ex.Message}");
            }
        });
    }

    public void TrackProcess(Process proc, string displayName)
    {
        proc.EnableRaisingEvents = true;
        lock (_tracked) _tracked.Add(proc);
        _hadLauncherOnce = true;
        UpdateTrayTooltip();

        proc.Exited += (_, _) =>
        {
            InjectorEngine.MonitorLog($"[{displayName}] PID={proc.Id} exited (code={proc.ExitCode})");
            try { InjectorEngine.EphemeralCleanup(); } catch { }
            lock (_tracked) _tracked.Remove(proc);

            Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdateTrayTooltip();
                StatusChanged?.Invoke($"启动器 [{displayName}] 已退出");
                EvaluateSelfExit();
            }));
        };

        // SilentMode 单独模式: 确认进程起来后, 延迟一点隐藏窗口 (类似一次性启动)
        if (_hideAfterLaunch)
        {
            _hideAfterLaunch = false;
            System.Threading.Tasks.Task.Delay(800).ContinueWith(_ =>
                Dispatcher.BeginInvoke(new Action(HideMainWindow)));
        }
    }

    // ==================== 托盘态自动识别 ====================

    private void BackgroundScan()
    {
        try
        {
            foreach (var (_, path, kind) in InjectorEngine.ScanRunningLaunchers())
            {
                // 已保存的路径 AddOrUpdate 只刷新名称/类型; 新路径自动入库
                var info = new LauncherInfo
                {
                    Path = path,
                    Name = Path.GetFileNameWithoutExtension(path),
                    Kind = kind
                };
                bool known = SavedLaunchers.Load()
                    .Any(l => string.Equals(l.Path, path, StringComparison.OrdinalIgnoreCase));
                SavedLaunchers.AddOrUpdate(info);
                if (!known)
                {
                    InjectorEngine.MonitorLog($"Auto-detect saved: {info.Name} ({kind})");
                    StatusChanged?.Invoke($"自动识别到运行中的启动器: {info.Name}");
                }
            }
        }
        catch { /* 扫描容错 */ }
    }

    private void EvaluateSelfExit()
    {
        bool active;
        lock (_tracked) active = _tracked.Any(p => !p.HasExited);
        if (_uiHidden && _hadLauncherOnce && !active)
        {
            InjectorEngine.MonitorLog("All injected launchers closed and UI hidden, App self-exits");
            ShutdownApp();
        }
    }

    private void EmitStatusUi(string msg) =>
        Dispatcher.BeginInvoke(new Action(() => StatusChanged?.Invoke(msg)));

    protected override void OnExit(Wpf.ExitEventArgs e)
    {
        try { InjectorEngine.EphemeralCleanup(); } catch { }
        try { _tray?.Dispose(); } catch { }
        base.OnExit(e);
    }
}
