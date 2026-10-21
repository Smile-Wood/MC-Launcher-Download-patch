using System;
using System.Runtime.InteropServices;

namespace PCL.Injector.Core;

/// <summary>
/// 解析 Windows .lnk 快捷方式 —— 使用 WScript.Shell COM 对象
/// </summary>
public static class ShellLinkResolver
{
    public static string? Resolve(string lnkPath)
    {
        try
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return null;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(lnkPath);
            string target = shortcut.TargetPath;
            Marshal.ReleaseComObject(shortcut);
            Marshal.ReleaseComObject(shell);
            return target;
        }
        catch
        {
            return null;
        }
    }
}
