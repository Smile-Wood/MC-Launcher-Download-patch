#if NET48
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace PCL.Injector.Payload
{
    /// <summary>
    /// PCL 原版 (.NET Framework) 修补:
    /// RestrictMcDownload=True 是编译期常量, VB 编译器把 ModpackInstall 的限制分支
    /// 直接折叠成 "弹提示→返回", Select Case 主体被整体删除。
    /// 但所有 InstallPackXXX 私有方法仍完整存在 —— 用反射重新实现安装入口。
    /// </summary>
    internal static class PclOriginalPatch
    {
        public static int Apply(HarmonyLib.Harmony harmony)
        {
            // PCL 2.99 官方版是壳程序: 真实代码在运行时才动态加载 (Assembly.Load)
            // AppDomainManager 阶段主程序集还没加载 —— 起后台线程轮询, 找到即打补丁
            System.Threading.ThreadPool.QueueUserWorkItem(_ => WaitAndPatch(harmony));
            return 0;
        }

        private static void WaitAndPatch(HarmonyLib.Harmony harmony)
        {
            const int maxSeconds = 90;
            for (int i = 0; i < maxSeconds * 2; i++)
            {
                var modpackType = FindTypeByName("ModModpack");
                if (modpackType != null)
                {
                    PatchModpackInstall(harmony, modpackType);
                    return;
                }
                Thread.Sleep(500);
            }
            Boot.Log("  90s 内未找到 ModModpack, 放弃 (非 PCL 原版?)");
        }

        private static void PatchModpackInstall(HarmonyLib.Harmony harmony, Type modpackType)
        {
            try
            {
                var overloads = modpackType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Static | BindingFlags.Instance)
                    .Where(m => m.Name == "ModpackInstall" && !m.IsGenericMethod)
                    .ToList();
                Boot.Log($"  ModModpack.ModpackInstall overloads: {overloads.Count}");

                var full = overloads.FirstOrDefault(m => m.GetParameters().Length >= 3);
                var wrappers = overloads.Where(m => m.GetParameters().Length < 3).ToList();

                // Harmony 2.3.3 在极短方法上打 Prefix 会生成坏 IL → InvalidProgramException
                // 加空 Finalizer 让 Harmony 生成 try-finally 结构改变 IL 布局, 避开栈不平衡
                var prefixFull = new HarmonyLib.HarmonyMethod(typeof(PclOriginalPatch)
                    .GetMethod(nameof(PrefixInstallFull), BindingFlags.Static | BindingFlags.NonPublic)!);
                var finalizerEmpty = new HarmonyLib.HarmonyMethod(typeof(PclOriginalPatch)
                    .GetMethod(nameof(FinalizerEmpty), BindingFlags.Static | BindingFlags.NonPublic)!);

                if (full != null)
                {
                    try
                    {
                        harmony.Patch(full, prefix: prefixFull, finalizer: finalizerEmpty);
                        Boot.Log($"  ✓ ModpackInstall({full.GetParameters().Length} 参数) -> 重实现");
                    }
                    catch (Exception ex)
                    {
                        Boot.Log($"  ✗ Patch(full) 失败: {ex.Message} —— 跳过该方法");
                    }
                }
                foreach (var w in wrappers)
                {
                    try
                    {
                        var prefixWrap = new HarmonyLib.HarmonyMethod(typeof(PclOriginalPatch)
                            .GetMethod(nameof(PrefixInstallWrapper), BindingFlags.Static | BindingFlags.NonPublic)!);
                        harmony.Patch(w, prefix: prefixWrap, finalizer: finalizerEmpty);
                        Boot.Log($"  ✓ ModpackInstall({w.GetParameters().Length} 参数, 包装) -> 重实现");
                    }
                    catch (Exception ex)
                    {
                        Boot.Log($"  ✗ Patch(wrap) 失败: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Boot.Log($"  PclOriginalPatch failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>空 Finalizer —— 让 Harmony 生成 try-finally 改变 IL 布局, 避开栈不平衡</summary>
        private static void FinalizerEmpty() { }

        // ==================== Harmony Prefix ====================
        // 用 __args 避免对 VB 参数名的硬编码依赖

        private static bool PrefixInstallFull(object[] __args)
        {
            string file = __args.Length > 0 ? __args[0] as string ?? "" : "";
            string instanceName = __args.Length > 1 ? __args[1] as string ?? "" : "";
            string logo = __args.Length > 2 ? __args[2] as string ?? "" : "";
            RunInstall(file, instanceName, logo);
            return false; // 跳过原方法 (原方法只会弹受限提示)
        }

        private static bool PrefixInstallWrapper(object[] __args)
        {
            string instanceName = __args.Length > 0 ? __args[0] as string ?? "" : "";
            string logo = __args.Length > 1 ? __args[1] as string ?? "" : "";

            string? file = SelectFileViaReflection();
            if (file == null) return false; // 用户取消

            string capturedFile = file;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { RunInstall(capturedFile, instanceName, logo); }
                catch (Exception ex) { Boot.Log($"ModpackInstall(thread) failed: {ex.Message}"); }
            });
            return false;
        }

        // ==================== 安装主逻辑 ====================

        private static void RunInstall(string file, string instanceName, string logo)
        {
            Boot.Log($"ModpackInstall: {file} (name={instanceName}, logo={logo})");
            var modpackType = FindTypeByName("ModModpack")!;
            var archive = OpenArchive(file);
            if (archive == null)
            {
                Boot.Log("  打开压缩包失败");
                return;
            }

            try
            {
                string baseFolder = DetectBaseFolder(archive);
                string kind = DetectPackKind(archive, file);
                Boot.Log($"  整合包类型: {kind}, 基准目录: '{baseFolder}'");

                string? methodName; object[] args;
                switch (kind)
                {
                    case "curseforge":
                        methodName = "InstallPackCurseForge";
                        args = new object[] { file, archive, baseFolder, instanceName, logo };
                        break;
                    case "modrinth":
                        methodName = "InstallPackModrinth";
                        args = new object[] { file, archive, baseFolder, instanceName, logo };
                        break;
                    case "mcbbs":
                        methodName = "InstallPackMCBBS";
                        args = new object[] { file, archive, baseFolder, instanceName };
                        break;
                    case "launcherpack":
                        methodName = "InstallPackLauncherPack";
                        args = new object[] { file, archive, baseFolder };
                        break;
                    case "hmcl":
                        methodName = "InstallPackHMCL";
                        args = new object[] { file, archive, baseFolder };
                        break;
                    case "mmc":
                        methodName = "InstallPackMMC";
                        args = new object[] { file, archive, baseFolder };
                        break;
                    default:
                        methodName = "InstallPackCompress";
                        args = new object[] { file, archive };
                        break;
                }

                var method = modpackType.GetMethod(methodName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (method == null)
                {
                    Boot.Log($"  ✗ 未找到 {methodName}");
                    return;
                }
                method.Invoke(null, args);
                Boot.Log($"  ✓ {methodName} 执行完成");
            }
            finally
            {
                archive.Dispose();
            }
        }

        // ==================== 检测逻辑 ====================

        private static string DetectPackKind(ZipArchive archive, string file)
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext == ".mrpack") return "modrinth";

            bool hasCurseManifest = false, hasAddonsDir = false;

            foreach (var entry in archive.Entries)
            {
                string name = entry.FullName;
                if (string.IsNullOrEmpty(name)) continue;
                string fileName = name.Split('/').Last();

                if (fileName == "mcbbs.packmeta") return "mcbbs";
                if (fileName == "mmc-pack.json") return "mmc";
                if (fileName == "modrinth.index.json") return "modrinth";
                if (fileName == "modpack.json") return "hmcl";
                if (fileName == "manifest.json") hasCurseManifest = true;
                if (name.IndexOf("/addons/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.StartsWith("addons/", StringComparison.OrdinalIgnoreCase)) hasAddonsDir = true;
            }

            if (hasCurseManifest) return hasAddonsDir ? "launcherpack" : "curseforge";
            return "compress";
        }

        private static string DetectBaseFolder(ZipArchive archive)
        {
            // 形如 "Xxx/overrides/..." 的路径 → 基准目录为 "Xxx/"
            foreach (var entry in archive.Entries)
            {
                string name = entry.FullName;
                if (string.IsNullOrEmpty(name)) continue;
                var parts = name.Split('/');
                for (int i = 1; i < parts.Length; i++)
                {
                    if (parts[i] == "overrides")
                        return string.Join("/", parts.Take(i)) + "/";
                }
            }
            // CurseForge 根级 overrides → .minecraft/
            if (archive.Entries.Any(e => e.FullName.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase)))
                return ".minecraft/";
            return "";
        }

        // ==================== 反射工具 ====================

        private static ZipArchive? OpenArchive(string file)
        {
            try
            {
                var openArchive = FindTypeByName("FileUtils", "MeloongCore")?
                    .GetMethod("OpenArchive", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (openArchive != null)
                {
                    var cts = new CancellationTokenSource();
                    var result = openArchive.Invoke(null, new object[] { file, cts.Token });
                    if (result is ZipArchive za) return za;
                }
            }
            catch (Exception ex) { Boot.Log($"  FileUtils.OpenArchive 反射失败, 回退 ZipFile: {ex.Message}"); }
            try { return ZipFile.OpenRead(file); }
            catch (Exception ex) { Boot.Log($"  ZipFile.OpenRead 失败: {ex.Message}"); return null; }
        }

        private static string? SelectFileViaReflection()
        {
            try
            {
                var dialogs = FindTypeByName("Dialogs");
                if (dialogs == null) return null;
                var filter = new string[][] { new[] { "rar", "zip", "mrpack" }, new[] { "整合包文件" } };
                var result = dialogs.GetMethod("SelectFile", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?
                    .Invoke(null, new object[] { "选择整合包压缩文件", false, filter });
                string? ret = result?.ToString();
                if (string.IsNullOrWhiteSpace(ret)) return null;
                return ret.Contains("|") ? ret.Split('|')[0] : ret;
            }
            catch (Exception ex)
            {
                Boot.Log($"  SelectFile 反射失败: {ex.Message}");
                return null;
            }
        }

        private static Type? FindTypeByName(string simpleName, string? requiredNamespace = null)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex)
                {
                    // 混淆/依赖缺失时部分类型加载失败 —— 取可用的子集继续
                    types = ex.Types.Where(t => t != null).ToArray()!;
                }
                catch { continue; }

                foreach (var t in types)
                {
                    if (t.Name != simpleName) continue;
                    if (requiredNamespace != null && (t.Namespace == null || !t.Namespace.StartsWith(requiredNamespace)))
                        continue;
                    Boot.Log($"  找到类型 {t.FullName} @ {asm.GetName().Name}");
                    return t;
                }
            }
            return null;
        }
    }
}
#endif
