#if NET48
using System;

namespace PCL.Injector.Payload
{
    /// <summary>
    /// .NET Framework AppDomainManager 注入入口:
    /// 启动器进程设置 APPDOMAIN_MANAGER_TYPE / APPDOMAIN_MANAGER_ASM 环境变量后,
    /// CLR 在创建默认 AppDomain 时(Main 之前)自动实例化本类。
    /// </summary>
    public class BootstrapManager : AppDomainManager
    {
        public BootstrapManager()
        {
            try
            {
                // 必须先在本方法内加载 0Harmony, 否则 JIT BootstrapManager/Initialize 时
                // 解析 HarmonyLib 引用会因 fusion 找不到程序集而失败
                PreloadHarmony();
                Boot.Initialize();
            }
            catch (Exception ex)
            {
                Boot.Log($"BootstrapManager ctor failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void PreloadHarmony()
        {
            var asm = typeof(BootstrapManager).Assembly;
            string? dir = null;
            try { dir = System.IO.Path.GetDirectoryName(asm.Location); } catch { }
            if (string.IsNullOrEmpty(dir)) return;

            string harmonyPath = System.IO.Path.Combine(dir, "0Harmony.dll");
            if (System.IO.File.Exists(harmonyPath))
                System.Reflection.Assembly.LoadFrom(harmonyPath);
        }
    }
}
#endif
