using System;

namespace PCL.Injector.Core;

public enum LauncherKind
{
    Unknown,
    PclCe,       // Plain Craft Launcher Community Edition (.NET 8/10)
    PclOriginal, // Plain Craft Launcher 原版 VB.NET (.NET Framework 4.8)
    Hmcl         // Hello Minecraft! Launcher (Java)
}

public class LauncherInfo
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public LauncherKind Kind { get; set; }
    public bool Enabled { get; set; } = true;
}

public class GuiConfig
{
    /// <summary>GUI 打开后是否自动注入已配置的启动器</summary>
    public bool AutoStart { get; set; }
    /// <summary>是否静默启动（最小化到托盘，后台监控）</summary>
    public bool SilentMode { get; set; }
    /// <summary>已配置的启动器列表</summary>
    public System.Collections.Generic.List<LauncherInfo> Launchers { get; set; } = new();
}
