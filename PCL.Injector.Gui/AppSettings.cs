using System;
using System.IO;
using System.Text.Json;

namespace PCL.Injector.Gui;

/// <summary>
/// GUI 自身的两个开关, 存 %LOCALAPPDATA%\PCL-Injector\gui-config.json.
/// 启动器列表统一走 Core 的 SavedLaunchers 旧 API, 不在这里。
/// </summary>
public sealed class AppSettings
{
    public bool AutoStart { get; set; }
    public bool SilentMode { get; set; }

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCL-Injector");

    private static string FilePath => Path.Combine(Dir, "gui-config.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                if (s != null) return s;
            }
        }
        catch { /* 损坏用默认值 */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { /* ignore */ }
    }
}
