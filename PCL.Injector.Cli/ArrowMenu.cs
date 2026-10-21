using System;
using System.Collections.Generic;

namespace PCL.Injector.Cli;

public sealed class ArrowMenuResult
{
    public int Index { get; init; } = -1;
    public bool Delete { get; init; }
}

/// <summary>
/// 控制台上下键选择菜单: ↑/↓ 移动, Enter 确认, Esc 取消, Del 删除(可选)
/// </summary>
public static class ArrowMenu
{
    public static ArrowMenuResult? Show(
        string title,
        IReadOnlyList<string> items,
        IReadOnlyList<string>? subtitles = null,
        int initial = 0,
        bool allowDelete = false)
    {
        if (items.Count == 0) return null;
        int sel = Math.Clamp(initial, 0, items.Count - 1);

        // 非交互环境 (输出/输入被重定向) 退化为数字选择，避免句柄异常
        if (Console.IsOutputRedirected || Console.IsInputRedirected)
            return ShowNumbered(items, initial);

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(title);
        Console.WriteLine(allowDelete
            ? "  ↑/↓ 选择   Enter 启动   Del 删除   Esc 取消"
            : "  ↑/↓ 选择   Enter 确认   Esc 取消");
        Console.ResetColor();

        int top = Console.CursorTop;
        int width = Math.Max(20, Console.WindowWidth - 1);

        while (true)
        {
            Console.SetCursorPosition(0, top);
            for (int i = 0; i < items.Count; i++)
            {
                string sub = subtitles != null && i < subtitles.Count && !string.IsNullOrEmpty(subtitles[i])
                    ? "   " + subtitles[i] : "";
                string line = Trunc((i == sel ? " ▶ " : "   ") + items[i] + sub, width);

                if (i == sel)
                {
                    Console.BackgroundColor = ConsoleColor.DarkCyan;
                    Console.ForegroundColor = ConsoleColor.White;
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Gray;
                }
                Console.WriteLine(line.PadRight(width));
                Console.ResetColor();
            }
            // 清除菜单块下方可能残留的行 (列表缩短时)
            for (int i = items.Count; i < items.Count + 2; i++)
                Console.WriteLine(new string(' ', width));

            var key = Console.ReadKey(true).Key;
            switch (key)
            {
                case ConsoleKey.UpArrow:
                    sel = (sel - 1 + items.Count) % items.Count;
                    break;
                case ConsoleKey.DownArrow:
                    sel = (sel + 1) % items.Count;
                    break;
                case ConsoleKey.Home:
                    sel = 0;
                    break;
                case ConsoleKey.End:
                    sel = items.Count - 1;
                    break;
                case ConsoleKey.Enter:
                    ClearBlock(top, items.Count + 2, width);
                    return new ArrowMenuResult { Index = sel };
                case ConsoleKey.Delete when allowDelete:
                    ClearBlock(top, items.Count + 2, width);
                    return new ArrowMenuResult { Index = sel, Delete = true };
                case ConsoleKey.Escape:
                    ClearBlock(top, items.Count + 2, width);
                    return null;
            }
        }
    }

    private static void ClearBlock(int top, int lines, int width)
    {
        Console.SetCursorPosition(0, top);
        for (int i = 0; i < lines; i++)
            Console.WriteLine(new string(' ', width));
        Console.SetCursorPosition(0, top);
    }

    /// <summary>非交互环境回退: 数字选择</summary>
    private static ArrowMenuResult? ShowNumbered(IReadOnlyList<string> items, int initial)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  (当前环境不支持方向键，已切换为数字选择)");
        Console.ResetColor();
        for (int i = 0; i < items.Count; i++)
            Console.WriteLine($"  [{i + 1}] {items[i]}");
        Console.Write($"选择 [1-{items.Count}] (默认 {initial + 1}): ");
        string? input = Console.ReadLine()?.Trim();

        if (string.IsNullOrEmpty(input))
            return new ArrowMenuResult { Index = initial };
        if (int.TryParse(input, out int n) && n >= 1 && n <= items.Count)
            return new ArrowMenuResult { Index = n - 1 };
        return null;
    }

    private static string Trunc(string s, int max)
        => s.Length <= max ? s : s[..(max - 1)] + "…";
}
