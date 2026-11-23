using System;
using System.Collections.Generic;
using System.IO;

namespace presents.WatchTogether;

public static class TextZh
{
    // 繁转简映射表：从插件目录 textzh.tsv 加载（zhconv 生成 + 往返校验）。
    // whisper 繁体输出常见；Windows LCMapString 无繁体语言包时静默空转；
    // Alife 运行时编译器会损坏大段中文字符串常量 —— 所以表放文件、运行时加载。
    private static Dictionary<char, char>? map;
    private static readonly object gate = new();

    public static Action<string>? DebugHook;

    public static bool Loaded { get; private set; }

    public static void LoadFromFile(string path)
    {
        try
        {
            var d = new Dictionary<char, char>(4096);
            foreach (string line in File.ReadAllLines(path))
            {
                if (line.Length >= 3)
                {
                    if (line[1] == '\t')
                    {
                        d[line[0]] = line[2];
                    }
                }
            }

            lock (gate)
            {
                map = d;
                Loaded = d.Count > 0;
            }
        }
        catch
        {
            Loaded = false;
        }
    }

    private static Dictionary<char, char>? CurrentMap
    {
        get { lock (gate) return map; }
    }

    public static string MaybeSimplify(string text)
    {
        var d = CurrentMap;
        if (string.IsNullOrWhiteSpace(text) || d == null) return text;

        // 特征检测直接用映射表的 key（它本身就是最全的繁体字清单）
        int markers = 0;
        foreach (char c in text)
        {
            if (d.ContainsKey(c))
            {
                markers++;
                if (markers >= 2) break;
            }
        }

        if (markers < 2) return text;

        var sb = new System.Text.StringBuilder(text.Length);
        foreach (char c in text)
            sb.Append(d.TryGetValue(c, out char s) ? s : c);
        return sb.ToString();
        foreach (char c in text)
            sb.Append(d.TryGetValue(c, out char s) ? s : c);
        return sb.ToString();
    }
}
