using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace presents.WatchTogether;

/// <summary>陪看的一件事：某一段讲了什么、屏幕上有什么字。</summary>
public sealed class WatchEvent
{
    public DateTime At { get; set; }
    public string Title { get; set; } = "";
    public double Pos { get; set; }
    public int Segment { get; set; }
    public string Source { get; set; } = "";   // 字幕来源（B站字幕 / 本地转写 / 页面字幕 / 无）
    public string Gist { get; set; } = "";     // 这一段讲了什么（字幕摘句）
    public string Screen { get; set; } = "";   // 画面文字（OCR，PPT 类主要靠它）
    public string Frame { get; set; } = "";    // 概览帧路径

    [JsonIgnore] public bool HasText => Gist.Length > 0 || Screen.Length > 0;

    public string Line(bool withScreen = true)
    {
        var sb = new StringBuilder();
        sb.Append($"· {WatchFormat.Mmss(Pos)}｜{Segment} 段");

        if (Source.Length > 0) sb.Append("｜").Append(Source);
        if (Gist.Length > 0) sb.Append("｜").Append(Gist);

        if (withScreen && Screen.Length > 0)
            sb.Append("\n   屏幕：").Append(Screen);

        return sb.ToString();
    }
}

/// <summary>
/// 陪看事件时间轴：每交付一段就记一条（字幕摘句 + 画面 OCR 文字 + 帧路径），
/// 只存文本不存像素。有了它，角色才能回答「刚才这半小时讲了什么」，
/// 而不是只能说「现在这一页是什么」。
/// </summary>
public sealed class WatchEventStore
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    readonly string path;
    readonly List<WatchEvent> events = new();

    public WatchEventStore(string filePath)
    {
        path = filePath;
        Load();
    }

    public int Count => events.Count;

    public IReadOnlyList<WatchEvent> All => events;

    public WatchEvent? Last => events.Count > 0 ? events[^1] : null;

    public void Add(WatchEvent value, int keep)
    {
        events.Add(value);

        int limit = Math.Clamp(keep, 20, 2000);
        if (events.Count > limit)
            events.RemoveRange(0, events.Count - limit);

        Save();
    }

    public List<WatchEvent> Recent(int count)
    {
        int take = Math.Clamp(count, 1, 50);
        return events.Skip(Math.Max(0, events.Count - take)).ToList();
    }

    /// <summary>最近 N 分钟的复盘文本。</summary>
    public string Recap(int minutes, bool withScreen = true, int maxLines = 24)
    {
        DateTime from = DateTime.Now.AddMinutes(-Math.Clamp(minutes, 1, 600));
        List<WatchEvent> slice = events.Where(e => e.At >= from).ToList();

        if (slice.Count == 0)
            return $"最近 {minutes} 分钟没有陪看记录（可能没在放视频，或还没走到交付点）。";

        var sb = new StringBuilder();
        sb.AppendLine($"[最近 {minutes} 分钟｜{slice.Count} 段]《{slice[^1].Title}》");

        foreach (WatchEvent item in slice.Skip(Math.Max(0, slice.Count - maxLines)))
            sb.AppendLine(item.Line(withScreen));

        return sb.ToString().TrimEnd();
    }

    void Load()
    {
        try
        {
            if (!File.Exists(path)) return;

            List<WatchEvent>? data = JsonSerializer.Deserialize<List<WatchEvent>>(File.ReadAllText(path), Json);
            if (data != null) events.AddRange(data);
        }
        catch
        {
            // 坏了就当空时间轴
        }
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? "");
            File.WriteAllText(path, JsonSerializer.Serialize(events, Json));
        }
        catch
        {
            // 写不进去只丢历史，不影响陪看
        }
    }
}
