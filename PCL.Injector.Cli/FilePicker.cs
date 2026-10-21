using System;
using System.Runtime.InteropServices;

namespace PCL.Injector.Cli;

/// <summary>
/// 文件选择对话框 (Win32 comdlg32, 无 WinForms 依赖，减小单文件体积)
/// 结构体全部使用 blittable 字段 (IntPtr)，避免 Marshal.SizeOf 对托管引用字段报错
/// </summary>
public static class FilePicker
{
    public static string? PickFile()
    {
        const int maxFile = 32768; // Vista+ 支持长路径
        IntPtr filterPtr = IntPtr.Zero, titlePtr = IntPtr.Zero, fileBuf = IntPtr.Zero;

        try
        {
            // 双 NUL 结尾的 filter 列表
            filterPtr = Marshal.StringToHGlobalUni(
                "启动器文件 (*.exe;*.jar)\0*.exe;*.jar\0所有文件 (*.*)\0*.*\0");
            titlePtr = Marshal.StringToHGlobalUni("选择启动器文件 (PCL / PCL-CE / HMCL)");
            fileBuf = Marshal.AllocHGlobal(maxFile * 2);
            Marshal.WriteInt16(fileBuf, 0); // 空字符串开头

            var ofn = new OPENFILENAMEW
            {
                lStructSize = Marshal.SizeOf<OPENFILENAMEW>(),
                hwndOwner = GetConsoleWindow(),
                lpstrFilter = filterPtr,
                lpstrFile = fileBuf,
                nMaxFile = maxFile,
                lpstrTitle = titlePtr,
                Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_HIDEREADONLY,
            };

            if (!GetOpenFileNameW(ref ofn))
                return null;

            return Marshal.PtrToStringUni(fileBuf);
        }
        finally
        {
            if (filterPtr != IntPtr.Zero) Marshal.FreeHGlobal(filterPtr);
            if (titlePtr != IntPtr.Zero) Marshal.FreeHGlobal(titlePtr);
            if (fileBuf != IntPtr.Zero) Marshal.FreeHGlobal(fileBuf);
        }
    }

    private const int OFN_FILEMUSTEXIST = 0x00001000;
    private const int OFN_PATHMUSTEXIST = 0x00000800;
    private const int OFN_HIDEREADONLY = 0x00000004;

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetOpenFileNameW(ref OPENFILENAMEW ofn);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [StructLayout(LayoutKind.Sequential)]
    private struct OPENFILENAMEW
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public IntPtr lpstrFilter;        // LPCWSTR
        public IntPtr lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;          // LPWSTR
        public int nMaxFile;
        public IntPtr lpstrFileTitle;
        public int nMaxFileTitle;
        public IntPtr lpstrInitialDir;
        public IntPtr lpstrTitle;         // LPCWSTR
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public IntPtr lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public IntPtr lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }
}
