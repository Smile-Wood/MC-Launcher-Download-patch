using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCL.Injector.Core;

/// <summary>
/// 已选择过的启动器列表 —— 自动保存到 %LOCALAPPDATA%\PCL-Injector\launchers.json
/// 兼容迁移旧版 last.txt.
/// 这是 CLI / GUI 共用的唯一持久化 API (旧版简单 API):
///   Load() 缓存读取 / AddOrUpdate() / RemoveAt() / Save()
///   Enabled 字段承载 GUI 的单选状态 (同一时刻至多一个为 true).
/// </summary>
public static class SavedLaunchers
{
    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCL-Injector");

    private static string StoreFile => Path.Combine(Dir, "launchers.json");

    /// <summary>写出用整数枚举 (与旧版 CLI 完全一致); 读取同时兼容整数和 "PclCe" 字符串.</summary>
    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static List<LauncherInfo>? _cache;

    public static List<LauncherInfo> Load()
    {
        if (_cache != null) return _cache;

        var list = new List<LauncherInfo>();
        try
        {
            if (File.Exists(StoreFile))
            {
                var loaded = JsonSerializer.Deserialize<List<LauncherInfo>>(File.ReadAllText(StoreFile), ReadOpts);
                if (loaded != null) list = loaded;
            }
            else
            {
                // 迁移旧版 last.txt (仅记录最后选择的一个)
                string oldFile = Path.Combine(Dir, "last.txt");
                if (File.Exists(oldFile))
                {
                    string p = File.ReadAllText(oldFile).Trim();
                    if (!string.IsNullOrEmpty(p) && File.Exists(p))
                    {
                        list.Add(new LauncherInfo
                        {
                            Path = p,
                            Name = Path.GetFileNameWithoutExtension(p),
                            Kind = Sniffer.DetectKind(p),
                            Enabled = true
                        });
                    }
                }
            }
        }
        catch { /* 损坏则从头开始 */ }

        _cache = list;
        return list;
    }

    /// <summary>丢弃进程内缓存, 下次 Load 强制读磁盘 (外部修改文件后用)</summary>
    public static void InvalidateCache() => _cache = null;

    /// <summary>
    /// 添加或更新 (按路径去重)。已存在则刷新名称/类型并保持原位置/启用状态。
    /// 新加入的条目: 列表为空时自动选中, 否则默认不选中 (单选语义)。
    /// </summary>
    public static void AddOrUpdate(LauncherInfo info)
    {
        if (string.IsNullOrEmpty(info.Path)) return;
        var list = Load();

        var existing = list.FirstOrDefault(l =>
            string.Equals(l.Path, info.Path, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.Name = info.Name;
            existing.Kind = info.Kind;
        }
        else
        {
            // 单选: 新条目默认不启用, 除非当前一个都没有
            info.Enabled = list.Count == 0;
            list.Add(info);
        }
        Save();
    }

    public static void RemoveAt(int index)
    {
        var list = Load();
        if (index < 0 || index >= list.Count) return;
        bool removedEnabled = list[index].Enabled;
        list.RemoveAt(index);
        // 删掉的是选中项且列表非空 → 选中第一个, 保证始终有一个单选结果
        if (removedEnabled && list.Count > 0) list[0].Enabled = true;
        Save();
    }

    /// <summary>把指定路径设为唯一选中项 (单选), 其余全部取消; 保存并返回该条目</summary>
    public static LauncherInfo? SelectOnly(string path)
    {
        var list = Load();
        LauncherInfo? selected = null;
        foreach (var l in list)
        {
            l.Enabled = string.Equals(l.Path, path, StringComparison.OrdinalIgnoreCase);
            if (l.Enabled) selected = l;
        }
        Save();
        return selected;
    }

    /// <summary>当前唯一选中的启动器 (Enabled=true), 没有则 null</summary>
    public static LauncherInfo? GetSelected() => Load().FirstOrDefault(l => l.Enabled);

    /// <summary>保存当前缓存列表 (GUI 直接改完列表后调用)</summary>
    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(StoreFile, JsonSerializer.Serialize(Load(), WriteOpts));
        }
        catch { /* ignore */ }
    }
}
