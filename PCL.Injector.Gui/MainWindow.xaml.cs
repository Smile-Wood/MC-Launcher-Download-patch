using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PCL.Injector.Core;

namespace PCL.Injector.Gui;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<LauncherVM> _vms = new();
    private readonly DispatcherTimer _logRefresh;
    private bool _syncing;

    public MainWindow()
    {
        InitializeComponent();
        IcLaunchers.ItemsSource = _vms;

        _logRefresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _logRefresh.Tick += (_, _) => RefreshLogs();
        _logRefresh.Start();

        ReloadData();
        RefreshLogs();
        App.Current.StatusChanged += OnAppStatus;
    }

    /// <summary>从磁盘重新加载列表和开关 (窗口每次从托盘打开时调用)</summary>
    public void ReloadData()
    {
        SavedLaunchers.InvalidateCache(); // 托盘扫描可能写过 launchers.json
        _syncing = true;
        _vms.Clear();
        foreach (var l in SavedLaunchers.Load()) _vms.Add(new LauncherVM(l));
        _syncing = false;

        var s = App.Current.Settings;
        ChkAutoStart.IsChecked = s.AutoStart;
        ChkSilentMode.IsChecked = s.SilentMode;
        UpdateStatus();
    }

    protected override void OnClosed(EventArgs e)
    {
        App.Current.StatusChanged -= OnAppStatus;
        _logRefresh.Stop();
        base.OnClosed(e);
    }

    private void OnAppStatus(string msg) => TxtStatus.Text = msg;

    // ==================== 选项 ====================

    private void ChkOption_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        var s = App.Current.Settings;
        s.AutoStart = ChkAutoStart.IsChecked == true;
        s.SilentMode = ChkSilentMode.IsChecked == true;
        s.Save();
    }

    // ==================== 单选 ====================

    private void Radio_Selected(object sender, RoutedEventArgs e)
    {
        if (_syncing || sender is not System.Windows.Controls.RadioButton rb
            || rb.DataContext is not LauncherVM vm) return;

        // 单选持久化: 该路径唯一 Enabled=true
        SavedLaunchers.SelectOnly(vm.PathDisplay);
        _syncing = true;
        foreach (var other in _vms) other.IsSelected = ReferenceEquals(other, vm);
        _syncing = false;
    }

    // ==================== 列表操作 ====================

    private void BtnAddLauncher_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "启动器 (*.exe;*.jar)|*.exe;*.jar|所有文件|*.*",
            Title = "选择启动器"
        };
        if (dlg.ShowDialog() != true) return;

        var kind = LauncherDetector.Detect(dlg.FileName);
        SavedLaunchers.AddOrUpdate(new LauncherInfo
        {
            Path = dlg.FileName,
            Name = Path.GetFileNameWithoutExtension(dlg.FileName),
            Kind = kind
        });
        ReloadData();
    }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.DataContext is not LauncherVM vm) return;

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "启动器 (*.exe;*.jar)|*.exe;*.jar|所有文件|*.*",
            Title = "选择启动器"
        };
        try { dlg.InitialDirectory = Path.GetDirectoryName(vm.PathDisplay); } catch { }
        if (dlg.ShowDialog() != true) return;

        vm.PathDisplay = dlg.FileName;
        vm.Kind = LauncherDetector.Detect(dlg.FileName);
        SavedLaunchers.Save();
    }

    private void BtnDetect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.DataContext is not LauncherVM vm) return;
        vm.Kind = LauncherDetector.Detect(vm.PathDisplay);
        SavedLaunchers.Save();
        OnAppStatus($"探测: {vm.Name} => {vm.Kind}");
    }

    private void BtnRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.DataContext is not LauncherVM vm) return;
        int idx = _vms.IndexOf(vm);
        _vms.Remove(vm);
        SavedLaunchers.InvalidateCache();
        SavedLaunchers.RemoveAt(idx); // RemoveAt 已重建缓存; 重新读盘保持一致
        ReloadData();
    }

    private void BtnAutoDetect_Click(object sender, RoutedEventArgs e)
    {
        // sender 未使用, 无需类型判断
        int added = 0;
        foreach (var (_, path, kind) in InjectorEngine.ScanRunningLaunchers())
        {
            bool known = SavedLaunchers.Load()
                .Any(l => string.Equals(l.Path, path, StringComparison.OrdinalIgnoreCase));
            SavedLaunchers.AddOrUpdate(new LauncherInfo
            {
                Path = path,
                Name = Path.GetFileNameWithoutExtension(path),
                Kind = kind
            });
            if (!known) added++;
        }
        ReloadData();
        OnAppStatus(added == 0 ? "未探测到新的运行中启动器" : $"已保存 {added} 个运行中启动器的路径");
    }

    // ==================== 开始注入 ====================

    private void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        var vm = _vms.FirstOrDefault(v => v.IsSelected);
        if (vm == null)
        {
            System.Windows.MessageBox.Show("请先选择一个启动器 (单选)", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SavedLaunchers.SelectOnly(vm.PathDisplay);
        BtnStart.IsEnabled = false;
        App.Current.StatusChanged += ReenableButtonOnce;
        App.Current.LaunchAsync(vm.Info);

        // 勾选了静默模式: 走和自动启动一致的隐藏流程
        if (App.Current.Settings.SilentMode) App.Current.NotifySilentHideExpected();
    }

    private void ReenableButtonOnce(string msg)
    {
        if (msg.StartsWith('✓') || msg.StartsWith('✗'))
        {
            BtnStart.IsEnabled = true;
            App.Current.StatusChanged -= ReenableButtonOnce;
        }
    }

    // ==================== 关闭/最小化 → 托盘 ====================

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (App.Current.IsExiting) return; // 托盘"退出"放行
        e.Cancel = true;
        App.Current.HideMainWindow();
    }

    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized) App.Current.HideMainWindow();
    }

    // ==================== 日志 ====================

    private void RefreshLogs()
    {
        TailInto(TxtMonitorLog, InjectorEngine.MonitorLogPath);
        TailInto(TxtInjectLog, InjectorEngine.InjectLogPath);
    }

    private static void TailInto(System.Windows.Controls.TextBox tb, string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            var lines = sr.ReadToEnd().Split('\n');
            int take = Math.Min(lines.Length, 300);
            string text = string.Join('\n', lines, lines.Length - take, take);
            if (tb.Text != text)
            {
                tb.Text = text;
                tb.ScrollToEnd();
            }
        }
        catch { }
    }

    private void UpdateStatus()
    {
        int n = SavedLaunchers.Load().Count;
        TxtStatus.Text = $"已保存 {n} 个启动器 · 活跃: {(App.Current.HasActiveLaunchers ? "是" : "否")}";
    }

    private void BtnOpenLogDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCL-Injector");
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch { }
    }
}

/// <summary>单选视图模型</summary>
public class LauncherVM : System.ComponentModel.INotifyPropertyChanged
{
    public LauncherInfo Info { get; }

    public LauncherVM(LauncherInfo info) { Info = info; }

    public string Name => Info.Name;

    public string PathDisplay
    {
        get => Info.Path;
        set
        {
            Info.Path = value;
            Info.Name = Path.GetFileNameWithoutExtension(value);
            OnPropertyChanged(nameof(PathDisplay));
            OnPropertyChanged(nameof(Name));
        }
    }

    public LauncherKind Kind
    {
        get => Info.Kind;
        set
        {
            Info.Kind = value;
            OnPropertyChanged(nameof(Kind));
            OnPropertyChanged(nameof(KindString));
        }
    }

    public string KindString => Kind switch
    {
        LauncherKind.PclCe => "PCL-CE",
        LauncherKind.PclOriginal => "PCL原版",
        LauncherKind.Hmcl => "HMCL",
        _ => "未知"
    };

    public bool IsSelected
    {
        get => Info.Enabled;
        set
        {
            if (Info.Enabled == value) return;
            Info.Enabled = value;
            OnPropertyChanged(nameof(IsSelected));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}
