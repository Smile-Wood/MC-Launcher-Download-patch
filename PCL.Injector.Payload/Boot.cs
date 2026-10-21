using System;
using System.IO;
using System.Reflection;

namespace PCL.Injector.Payload;

/// <summary>
/// Harmony Payload — 在目标进程内运行时修补地区检测/下载限制。
/// 加载途径:
///   .NET 5+  : DOTNET_STARTUP_HOOKS (StartupHook)
///   .NET FW  : AppDomainManager (BootstrapManager)
/// 注意: Initialize 本体不能直接引用 HarmonyLib 类型 (JIT 安全 —— 必须等 0Harmony 加载完)
/// </summary>
public static class Boot
{
    private static bool _initialized;

    private static readonly object LogLock = new();
    public static string LogFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PCL-Injector", "inject.log");

    public static void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        try
        {
            lock (LogLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
                File.AppendAllText(LogFile, line + Environment.NewLine);
            }
        }
        catch { }
        Console.WriteLine("[PCL-Injector] " + message);
    }

    /// <summary>.NET 启动钩子入口 (DOTNET_STARTUP_HOOKS)</summary>
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        try
        {
            // 预加载同目录的 0Harmony.dll (子进程不会在启动钩子目录搜索依赖)
            PreloadHarmony();
            ApplyPatches();
        }
        catch (Exception ex)
        {
            Log($"Patch error: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private static void PreloadHarmony()
    {
        string? dir = null;
        try { dir = Path.GetDirectoryName(typeof(Boot).Assembly.Location); } catch { }
        if (string.IsNullOrEmpty(dir))
        {
            Log("WARN: assembly location unavailable (single-file host?)");
            return;
        }
#if NET48
        string harmonyName = "0Harmony.dll";
#else
        // .NET 5+ 用 net8.0 变体 —— net472 变体在 .NET 10 上 HarmonySharedState 初始化崩溃
        string harmonyName = "0HarmonyCE.dll";
#endif
        string harmonyPath = Path.Combine(dir, harmonyName);
        if (File.Exists(harmonyPath))
        {
            Assembly.LoadFrom(harmonyPath);
            Log($"{harmonyName} preloaded: {harmonyPath}");
        }
        else
        {
            Log($"WARN: {harmonyName} not found at {harmonyPath}");
        }
        Log($"Payload loaded from: {typeof(Boot).Assembly.Location} ({(Environment.Version >= new Version(5, 0) ? ".NET " + Environment.Version : ".NET Framework " + Environment.Version)})");
    }

    /// <summary>HarmonyLib 引用全部隔离在本方法之后的调用链中</summary>
    private static void ApplyPatches()
    {
        var harmony = new HarmonyLib.Harmony("PCL.Injector.Payload");

        // ===== PCL 原版 (.NET Framework 4.8) =====
#if NET48
        int patched = PclOriginalPatch.Apply(harmony);
        Log($"Done. {patched} patch(es) queued (原版异步等待主程序集).");
#else
        // ===== PCL-CE (.NET 5+/10, DOTNET_STARTUP_HOOKS) =====
        // startup hook 阶段 PCL 主程序集还没加载 —— 后台线程轮询, 类型出现即打补丁
        var t = new System.Threading.Thread(() => PatchCeWhenReady(harmony)) { IsBackground = true };
        t.Start();
#endif
    }

    // ==================== PCL-CE ====================

    /// <summary>
    /// 等待 PCL 主程序集加载后 patch RestrictedFeaturePolicy (dl 限制总开关).
    /// 已验证 (dnlib 反编译 CE 主程序集, 逐调用点核对分支方向):
    ///   PCL.RestrictedFeaturePolicy.Check(string,bool)->bool   ★ true=放行, false=拒绝
    ///     - BtnStart_Click / Install_Click / McDownloadClient(Core) / McDownloadMenuSave / McInstall
    ///       全部是 brtrue -> 主逻辑, fall-through -> ret (点击无反应)
    ///     - McLaunchStart: Check 返回 false -> throw CancelledException
    ///   PCL.RestrictedFeaturePolicy.get_IsRestricted()->bool   true=受限
    ///   PCL.RestrictedFeaturePolicy.ThrowIfRestricted(string)->void  内部 Check 后 throw
    ///   PCL.Core.App.Region.RegionAssessment.get_IsRestricted()->bool  (PCL.Core.dll)
    /// </summary>
    private static void PatchCeWhenReady(HarmonyLib.Harmony harmony)
    {
        Type? rfp = null;
        for (int i = 0; i < 180; i++) // 90s × 500ms
        {
            rfp = FindType("PCL.RestrictedFeaturePolicy");
            if (rfp != null) break;
            System.Threading.Thread.Sleep(500);
        }
        if (rfp == null)
        {
            Log("  90s 内未找到 PCL.RestrictedFeaturePolicy, 放弃 (非 PCL-CE?)");
            return;
        }

        int count = 0;
        try
        {
            var check = rfp.GetMethod("Check",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                null, new[] { typeof(string), typeof(bool) }, null);
            if (check != null)
            {
                // CE 语义: Check 返回 true = 放行 (所有调用点 brtrue -> 主逻辑)
                harmony.Patch(check, prefix: Prefix(typeof(PrefixReturnTrue)));
                Log("  ✓ PCL.RestrictedFeaturePolicy.Check -> true (放行, CE 语义)");
                count++;
            }

            var isRestricted = rfp.GetProperty("IsRestricted",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetMethod;
            if (isRestricted != null)
            {
                harmony.Patch(isRestricted, prefix: Prefix(typeof(PrefixReturnFalse)));
                Log("  ✓ PCL.RestrictedFeaturePolicy.IsRestricted -> false");
                count++;
            }

            var throwIf = rfp.GetMethod("ThrowIfRestricted",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                null, new[] { typeof(string) }, null);
            if (throwIf != null)
            {
                harmony.Patch(throwIf, prefix: Prefix(typeof(PrefixSkipVoid)));
                Log("  ✓ PCL.RestrictedFeaturePolicy.ThrowIfRestricted -> 跳过 (不再 throw)");
                count++;
            }
        }
        catch (Exception ex) { Log($"  CE patch error: {ex.GetType().Name}: {ex.Message}"); }

        // 兜底: PCL.Core 的 RegionAssessment (即使 RFP patch 失败也能拦住底层判定)
        try
        {
            var assessment = FindType("PCL.Core.App.Region.RegionAssessment");
            var getter = assessment?.GetProperty("IsRestricted",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetMethod;
            if (getter != null)
            {
                harmony.Patch(getter, prefix: Prefix(typeof(PrefixReturnFalse)));
                Log("  ✓ RegionAssessment.IsRestricted -> false (兜底)");
                count++;
            }
        }
        catch (Exception ex) { Log($"  RegionAssessment patch error: {ex.Message}"); }

        Log($"CE patches done. {count} applied.");
    }

    private static HarmonyLib.HarmonyMethod Prefix(Type holder) =>
        new(holder.GetMethod("Prefix", BindingFlags.Static | BindingFlags.Public)!);

    private static Type? FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = asm.GetType(fullName, false);
                if (t != null) return t;
            }
            catch { }
        }
        return null;
    }
}

// ==================== Prefix 补丁实现 ====================

/// <summary>让方法/属性 getter 始终返回 true</summary>
public static class PrefixReturnTrue
{
    public static bool Prefix(ref bool __result)
    {
        __result = true;
        return false;
    }
}

/// <summary>让方法/属性 getter 始终返回 false</summary>
public static class PrefixReturnFalse
{
    public static bool Prefix(ref bool __result)
    {
        __result = false;
        return false;
    }
}

/// <summary>跳过 void 方法原逻辑 (不执行, 不抛异常)</summary>
public static class PrefixSkipVoid
{
    public static bool Prefix() => false;
}
