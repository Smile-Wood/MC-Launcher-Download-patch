using System;
using System.Collections.Generic;
using System.IO;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace PCL.Injector.Core;

/// <summary>
/// PCL2 2.99 静态 IL 补丁 —— 移植自 github.com/Ryorin1337/pcl-patcher (MIT License).
/// VB 编译器把 RestrictMcDownload=True 折叠后, 受限功能的 IL 在官方 exe 里已被删除,
/// 运行时注入 (Harmony) 无法复活不存在的代码; 本类直接在 exe 文件层面重建:
///   [1] PCL.ModSecret.RestrictMcDownload 常量 True → False (元数据 Constant blob)
///   [2] PageDownloadInstall.LoaderInit: 折叠出来的假 Loader → 真 DlClientListLoader (恢复版本列表下载)
///   [3] PageDownloadModify.LoadInstanceFinished: CardMinecraft.Visibility Collapsed → Visible
///   [4] ModpackInstall(3参): 重建被删的整合包类型分发 (新增 ModpackInstallDispatch 私有静态方法)
///   [5] ModpackInstall(): 重建文件选择对话框入口
/// </summary>
public static class PclIlPatcher
{
    private static ModuleDefMD _mod = null!;

    /// <summary>把 src exe 打补丁后的字节写入流. 抛异常表示补丁不适用 (版本不匹配).</summary>
    public static void Patch(Stream dst, string srcPath)
    {
        _mod = ModuleDefMD.Load(srcPath);

        // [1] RestrictMcDownload = False
        TypeDef modSecret = _mod.Find("PCL.ModSecret", false)
            ?? throw new Exception("PCL.ModSecret 不存在 (不是 PCL2 2.99.x?)");
        FieldDef restrict = modSecret.FindField("RestrictMcDownload")
            ?? throw new Exception("RestrictMcDownload 字段不存在 (该版本可能没有限制)");
        restrict.Constant.Value = false;
        Console.WriteLine("  [1/5] RestrictMcDownload -> False");

        PatchLoaderInit();
        PatchVisibility();
        PatchModpackInstall();
        PatchModpackInstallNoArg();

        _mod.Write(dst);
        Console.WriteLine("  ✓ 补丁字节已生成");
    }

    /// <summary>兼容 --patch-only 模式: 直接写文件.</summary>
    public static void Patch(string src, string dst)
    {
        using var fs = File.Create(dst);
        Patch(fs, src);
    }

    // [2] PageDownloadInstall.LoaderInit: 把"关闭下载"假 Loader 换回真 DlClientListLoader
    private static void PatchLoaderInit()
    {
        TypeDef t = _mod.Find("PCL.PageDownloadInstall", false)
            ?? throw new Exception("PCL.PageDownloadInstall 不存在");
        MethodDef m = t.FindMethod("LoaderInit") ?? throw new Exception("LoaderInit 不存在");
        IList<Instruction> ins = m.Body.Instructions;

        int k = -1;
        for (int i = 0; i + 12 < ins.Count; i++)
        {
            if (ins[i].OpCode.Code != Code.Ldstr) continue;
            if (ins[i + 12].OpCode.Code != Code.Newobj) continue;
            IMethod ctor = ins[i + 12].Operand as IMethod;
            if (ctor == null || ctor.DeclaringType == null) continue;
            if (!ctor.DeclaringType.Name.String.Contains("LoaderTask")) continue;
            k = i;
            break;
        }
        if (k < 0) throw new Exception("LoaderInit 模式未匹配");

        FieldDef dl = _mod.Find("PCL.ModDownload", false).FindField("DlClientListLoader")
            ?? throw new Exception("DlClientListLoader 字段不存在");
        ins[k].OpCode = OpCodes.Ldsfld;
        ins[k].Operand = dl;
        for (int j = 1; j <= 12; j++)
        {
            ins[k + j].OpCode = OpCodes.Nop;
            ins[k + j].Operand = null;
        }
        Console.WriteLine("  [2/5] LoaderInit -> DlClientListLoader (版本列表下载已恢复)");
    }

    // [3] PageDownloadModify: CardMinecraft.Visibility = Visible
    private static void PatchVisibility()
    {
        TypeDef t = _mod.Find("PCL.PageDownloadModify", false)
            ?? throw new Exception("PCL.PageDownloadModify 不存在");
        int n = 0;
        foreach (MethodDef m in t.Methods)
        {
            if (!m.HasBody) continue;
            IList<Instruction> ins = m.Body.Instructions;
            for (int i = 0; i + 2 < ins.Count; i++)
            {
                IMethod get = ins[i].Operand as IMethod;
                if (get == null || get.Name.String != "get_CardMinecraft") continue;
                if (ins[i + 1].OpCode.Code != Code.Ldc_I4_2) continue;
                IMethod set = ins[i + 2].Operand as IMethod;
                if (set == null || set.Name.String != "set_Visibility") continue;
                ins[i + 1].OpCode = OpCodes.Ldc_I4_0; // Visible
                ins[i + 1].Operand = null;
                n++;
            }
        }
        if (n == 0) throw new Exception("CardMinecraft 可见性模式未匹配");
        Console.WriteLine($"  [3/5] CardMinecraft 恢复可见 ({n} 处)");
    }

    // [4] ModpackInstall(File, InstanceName, Logo): 重建被 const 折叠删除的分发逻辑
    private static void PatchModpackInstall()
    {
        TypeDef t = _mod.Find("PCL.ModModpack", false)
            ?? throw new Exception("PCL.ModModpack 不存在");
        MethodDef target = null;
        foreach (MethodDef m in t.Methods)
        {
            if (m.Name.String == "ModpackInstall" && m.HasBody && m.Body.Instructions.Count > 100)
            {
                target = m;
                break;
            }
        }
        if (target == null) throw new Exception("ModpackInstall(3参) 未找到");

        CilBody body = target.Body;
        IList<Instruction> ins = body.Instructions;

        // 定位折叠出来的 "qwq..." throw (限制分支)
        int ti = -1;
        for (int i = 0; i < ins.Count; i++)
        {
            if (ins[i].OpCode.Code != Code.Throw) continue;
            if (i < 2) continue;
            if (ins[i - 1].OpCode.Code != Code.Newobj) continue;
            if (ins[i - 2].OpCode.Code != Code.Ldstr) continue;
            string s = ins[i - 2].Operand as string;
            if (s == null || !s.Contains("qwq")) continue;
            ti = i;
            break;
        }
        if (ti < 0) throw new Exception("限制 throw 未找到");

        Instruction ldstrIns = ins[ti - 2];

        Local v0 = body.Variables[0];
        Local v1 = body.Variables[1];
        Local v2 = body.Variables[2];
        Local v4 = body.Variables[4];

        MethodDef dispatch = CreateDispatch(t, target);
        Instruction lout = Instruction.Create(OpCodes.Ldloc, v0);

        ldstrIns.OpCode = OpCodes.Ldloc;
        ldstrIns.Operand = v4;

        var seq = new List<Instruction>
        {
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldloc, v1),
            Instruction.Create(OpCodes.Ldloc, v2),
            Instruction.Create(OpCodes.Ldarg_1),
            Instruction.Create(OpCodes.Ldarg_2),
            Instruction.Create(OpCodes.Call, dispatch),
            Instruction.Create(OpCodes.Stloc, v0),
            Instruction.Create(OpCodes.Leave, lout),
        };

        int start = ins.IndexOf(ldstrIns);
        ins.RemoveAt(start + 2); // newobj
        ins.RemoveAt(start + 1); // throw
        for (int j = 0; j < seq.Count; j++) ins.Insert(start + 1 + j, seq[j]);

        // 关键: dnlib 中 HandlerEnd == null 表示 finally 延伸到方法末尾.
        // 追加代码后 leave 目标会落进保护区域 → 非法 IL → InvalidProgramException.
        // 在 endfinally 后插入真实边界 NOP, 把 finally 的 HandlerEnd 指向它.
        Instruction endMarker = Instruction.Create(OpCodes.Nop);
        ins.Add(endMarker);
        ins.Add(lout);
        ins.Add(Instruction.Create(OpCodes.Ret));
        foreach (ExceptionHandler eh in body.ExceptionHandlers)
        {
            if (eh.HandlerType == ExceptionHandlerType.Finally && eh.HandlerEnd == null)
                eh.HandlerEnd = endMarker;
        }
        Console.WriteLine("  [4/5] ModpackInstall 整合包分发逻辑已重建");
    }

    private static MethodDef CreateDispatch(TypeDef t, MethodDef orig)
    {
        TypeSig zip = orig.Body.Variables[1].Type;
        MethodSig sig = MethodSig.CreateStatic(orig.ReturnType, new TypeSig[] {
            _mod.CorLibTypes.Int32, _mod.CorLibTypes.String, zip,
            _mod.CorLibTypes.String, _mod.CorLibTypes.String, _mod.CorLibTypes.String
        });
        MethodDef md = new MethodDefUser("ModpackInstallDispatch", sig, MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig);
        t.Methods.Add(md);
        CilBody b = new CilBody();
        b.InitLocals = false;
        md.Body = b;

        if (md.ParamDefs.Count != 6)
        {
            md.ParamDefs.Clear();
            for (int i = 0; i < 6; i++) md.ParamDefs.Add(new ParamDefUser("arg" + i, (ushort)(i + 1)));
        }

        MethodDef mCurse = FindMethod(t, "InstallPackCurseForge");
        MethodDef mModrinth = FindMethod(t, "InstallPackModrinth");
        MethodDef mHmcl = FindMethod(t, "InstallPackHMCL");
        MethodDef mMmc = FindMethod(t, "InstallPackMMC");
        MethodDef mMcbbs = FindMethod(t, "InstallPackMCBBS");
        MethodDef mLauncher = FindMethod(t, "InstallPackLauncherPack");
        MethodDef mCompress = FindMethod(t, "InstallPackCompress");

        Instruction c0 = Instruction.Create(OpCodes.Nop);
        Instruction c1 = Instruction.Create(OpCodes.Nop);
        Instruction c2 = Instruction.Create(OpCodes.Nop);
        Instruction c3 = Instruction.Create(OpCodes.Nop);
        Instruction c4 = Instruction.Create(OpCodes.Nop);
        Instruction c9 = Instruction.Create(OpCodes.Nop);
        Instruction cElse = Instruction.Create(OpCodes.Nop);

        b.Instructions.Add(Instruction.Create(OpCodes.Ldarg, md.Parameters[0]));
        b.Instructions.Add(Instruction.Create(OpCodes.Switch, new List<Instruction> { c0, c1, c2, c3, c4 }));
        b.Instructions.Add(Instruction.Create(OpCodes.Ldarg, md.Parameters[0]));
        b.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, 9));
        b.Instructions.Add(Instruction.Create(OpCodes.Beq, c9));
        b.Instructions.Add(Instruction.Create(OpCodes.Br, cElse));

        AddCase(b, md, c0, mCurse, new[] { 1, 2, 3, 4, 5 });
        AddCase(b, md, c1, mHmcl, new[] { 1, 2, 3 });
        AddCase(b, md, c2, mMmc, new[] { 1, 2, 3 });
        AddCase(b, md, c3, mMcbbs, new[] { 1, 2, 3, 4 });
        AddCase(b, md, c4, mModrinth, new[] { 1, 2, 3, 4, 5 });
        AddCase(b, md, c9, mLauncher, new[] { 1, 2, 3 });
        AddCase(b, md, cElse, mCompress, new[] { 1, 2 });
        return md;
    }

    private static void AddCase(CilBody b, MethodDef md, Instruction label, MethodDef target, int[] args)
    {
        b.Instructions.Add(label);
        for (int i = 0; i < args.Length; i++)
            b.Instructions.Add(Instruction.Create(OpCodes.Ldarg, md.Parameters[args[i]]));
        b.Instructions.Add(Instruction.Create(OpCodes.Call, target));
        b.Instructions.Add(Instruction.Create(OpCodes.Ret));
    }

    // [5] ModpackInstall(): 重建文件选择对话框入口
    private static void PatchModpackInstallNoArg()
    {
        TypeDef t = _mod.Find("PCL.ModModpack", false)!;
        MethodDef m0 = null;
        MethodDef m3 = null;
        foreach (MethodDef m in t.Methods)
        {
            if (m.Name.String != "ModpackInstall") continue;
            if (m.MethodSig.Params.Count == 0) m0 = m;
            else if (m.MethodSig.Params.Count == 3) m3 = m;
        }
        if (m0 == null || m3 == null) throw new Exception("ModpackInstall 重载未找齐");

        // 从 ModJava.ManuallyImportJava 里抠出 SelectFile 相关引用 (同款调用序列)
        MethodDef jm = _mod.Find("PCL.ModJava", false).FindMethod("ManuallyImportJava")
            ?? throw new Exception("ModJava.ManuallyImportJava 不存在");
        IList<Instruction> jins = jm.Body.Instructions;
        IMethod selectFile = null, oneOf = null, nullableCtor = null, firstOrDefault = null;
        for (int i = 3; i + 2 < jins.Count; i++)
        {
            IMethod im = jins[i].Operand as IMethod;
            if (im == null || im.Name.String != "SelectFile") continue;
            selectFile = im;
            oneOf = jins[i - 2].Operand as IMethod;
            nullableCtor = jins[i - 1].Operand as IMethod;
            firstOrDefault = jins[i + 1].Operand as IMethod;
            break;
        }
        if (selectFile == null || oneOf == null || nullableCtor == null || firstOrDefault == null)
            throw new Exception("SelectFile 引用未找到");

        IMethod isNullOrEmpty = FindCall("IsNullOrEmpty")
            ?? throw new Exception("IsNullOrEmpty 引用未找到");

        CilBody b = m0.Body;
        b.Instructions.Clear();
        b.ExceptionHandlers.Clear();
        b.Variables.Clear();
        Local vFile = new Local(_mod.CorLibTypes.String);
        b.Variables.Add(vFile);
        b.InitLocals = true;

        Instruction ret = Instruction.Create(OpCodes.Ret);
        b.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "Select modpack file"));
        b.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
        b.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
        b.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "rar;zip;mrpack|Modpack files"));
        b.Instructions.Add(Instruction.Create(OpCodes.Call, oneOf));
        b.Instructions.Add(Instruction.Create(OpCodes.Newobj, nullableCtor));
        b.Instructions.Add(Instruction.Create(OpCodes.Call, selectFile));
        b.Instructions.Add(Instruction.Create(OpCodes.Call, firstOrDefault));
        b.Instructions.Add(Instruction.Create(OpCodes.Stloc, vFile));
        b.Instructions.Add(Instruction.Create(OpCodes.Ldloc, vFile));
        b.Instructions.Add(Instruction.Create(OpCodes.Call, isNullOrEmpty));
        b.Instructions.Add(Instruction.Create(OpCodes.Brtrue, ret));
        b.Instructions.Add(Instruction.Create(OpCodes.Ldloc, vFile));
        b.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
        b.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
        b.Instructions.Add(Instruction.Create(OpCodes.Call, m3));
        b.Instructions.Add(Instruction.Create(OpCodes.Pop));
        b.Instructions.Add(ret);
        Console.WriteLine("  [5/5] ModpackInstall() 文件选择入口已重建");
    }

    private static MethodDef FindMethod(TypeDef t, string name)
    {
        foreach (MethodDef m in t.Methods)
            if (m.Name.String == name) return m;
        throw new Exception("方法不存在: " + name);
    }

    private static IMethod? FindCall(string name)
    {
        foreach (TypeDef t in _mod.GetTypes())
        {
            foreach (MethodDef m in t.Methods)
            {
                if (!m.HasBody) continue;
                foreach (Instruction ins in m.Body.Instructions)
                {
                    IMethod im = ins.Operand as IMethod;
                    if (im != null && im.Name.String == name) return im;
                }
            }
        }
        return null;
    }
}
