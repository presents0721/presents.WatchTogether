using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;

namespace presents.WatchTogether;

// ─────────────────────────────────────────────────────────────────
// 位置读数与估计
// ─────────────────────────────────────────────────────────────────

/// <summary>扩展上报的一条播放位置。At 用**服务端收到的时刻**，不用浏览器的时钟。</summary>
public sealed class WatchSample
{
    public double Pos { get; set; }
    public double? Dur { get; set; }
    public string? Title { get; set; }
    public string? Url { get; set; }
    public bool Paused { get; set; }
    public bool Ended { get; set; }
    /// <summary>页面字幕（实验）：扩展在用户开启后读到的视频字幕元素文本，随位置一起上报。</summary>
    public string? Caption { get; set; }
    /// <summary>桌面媒体会话的 app id（SMTC 来源）；浏览器扩展上报时为空。</summary>
    public string? AppId { get; set; }
    public DateTime At { get; set; } = DateTime.Now;
}

/// <summary>
/// 现在大概是第几秒，以及这个数字能不能信。
/// fresh=刚读到；extrapolated=在播但读数旧了、按时钟外推；unknown=读不到/不是播放状态，不许当实时值用。
/// </summary>
public sealed class PositionEstimate
{
    public double Pos { get; init; }
    public string Confidence { get; init; } = "unknown";
    public double StaleMs { get; init; }
    public string Why { get; init; } = "";

    public bool Fresh => Confidence == "fresh";
    public bool Known => Confidence != "unknown";

    public string ConfidenceText => Confidence switch
    {
        "fresh" => "实时",
        "extrapolated" => "外推",
        _ => "不可信"
    };

    public string WhyText => Why switch
    {
        "paused" => "暂停（读数就是当前位置）",
        "recent-read" => "刚读到",
        "wall-clock" => "在播，按墙钟外推",
        "stale" => "太久没读到，停止外推",
        "not-playing" => "不在播放状态",
        "ended" => "已播完",
        _ => "还没有位置"
    };
}

public static class WatchEstimator
{
    /// <summary>
    /// 从一条读数估计当前位置。规则来自上游实测：
    /// 暂停时位置不会自己走，**永远可信**；在播时刚读过就信，旧了按时钟推但有上限；推太久就承认不知道。
    /// </summary>
    public static PositionEstimate Estimate(
        WatchSample? anchor,
        DateTime now,
        double freshMs = 5000,
        double maxExtrapolateMs = 60000)
    {
        if (anchor == null)
            return new PositionEstimate { Pos = 0, Confidence = "unknown", StaleMs = double.PositiveInfinity, Why = "no-anchor" };

        double dur = anchor.Dur is > 0 ? anchor.Dur.Value : 0;
        double stale = Math.Max(0, (now - anchor.At).TotalMilliseconds);

        if (anchor.Paused)
            return new PositionEstimate { Pos = Clamp(anchor.Pos, dur), Confidence = "fresh", StaleMs = stale, Why = "paused" };

        if (anchor.Ended)
            return new PositionEstimate { Pos = Clamp(anchor.Pos, dur), Confidence = "unknown", StaleMs = stale, Why = "ended" };

        if (stale <= freshMs)
            return new PositionEstimate { Pos = Clamp(anchor.Pos, dur), Confidence = "fresh", StaleMs = stale, Why = "recent-read" };

        if (stale <= maxExtrapolateMs)
        {
            double pos = Clamp(anchor.Pos + stale / 1000.0, dur);
            return new PositionEstimate { Pos = pos, Confidence = "extrapolated", StaleMs = stale, Why = "wall-clock" };
        }

        return new PositionEstimate
        {
            Pos = Clamp(anchor.Pos + maxExtrapolateMs / 1000.0, dur),
            Confidence = "unknown",
            StaleMs = stale,
            Why = "stale"
        };
    }

    public static double Clamp(double pos, double dur)
    {
        if (!double.IsFinite(pos) || pos < 0) return 0;
        return dur > 0 ? Math.Min(pos, dur) : pos;
    }
}

// ─────────────────────────────────────────────────────────────────
// 段表：开头短、逐段加长、最后稳定
// ─────────────────────────────────────────────────────────────────

public sealed class WatchSegment
{
    public int Index { get; init; }
    public int From { get; init; }
    public int Size { get; init; }
    public int To => From + Size;

    public override string ToString() => $"#{Index} {WatchFormat.Mmss(From)}-{WatchFormat.Mmss(To)}";
}

/// <summary>
/// 批次时长表。**唯一的段边界来源**：调度、工具、状态展示都必须用同一张表。
/// 默认 1m → 2m → 3m → 4m → 5m → 5m …；开头短，第一次说话不用等满 5 分钟。
/// </summary>
public sealed class WatchPlan
{
    readonly int[] steps;
    readonly int[] prefix;
    readonly int steady;
    readonly int rampEnd;

    public int Steady => steady;
    public IReadOnlyList<int> Ramp => steps;

    WatchPlan(int steady, int[] steps, int[] prefix, int rampEnd)
    {
        this.steady = steady;
        this.steps = steps;
        this.prefix = prefix;
        this.rampEnd = rampEnd;
    }

    public static WatchPlan Create(int segment, IReadOnlyList<int>? ramp)
    {
        int steady = segment > 0 ? segment : 300;
        int[] steps = ramp == null
            ? Array.Empty<int>()
            : ramp.Where(x => x > 0).ToArray();

        int[] prefix = new int[steps.Length + 1];
        prefix[0] = 0;
        for (int i = 0; i < steps.Length; i++)
            prefix[i + 1] = prefix[i] + steps[i];

        return new WatchPlan(steady, steps, prefix, prefix[steps.Length]);
    }

    public int IndexAt(double pos)
    {
        double p = Math.Max(0, pos);
        if (steps.Length > 0 && p < rampEnd)
        {
            // 找最后一个 from <= p 的阶梯段。
            int lo = 0;
            int hi = steps.Length - 1;
            while (lo < hi)
            {
                int mid = (int)Math.Ceiling((lo + hi + 1) / 2.0);
                if (prefix[mid] <= p) lo = mid;
                else hi = mid - 1;
            }
            return lo;
        }

        double index = steps.Length + Math.Floor((p - rampEnd) / steady);
        if (index <= 0) return 0;
        if (index > int.MaxValue) return int.MaxValue;
        return (int)index;
    }

    public int StartAt(int index)
    {
        if (index <= 0) return 0;
        if (index < steps.Length) return prefix[index];
        long start = rampEnd + (long)(index - steps.Length) * steady;
        return start > int.MaxValue ? int.MaxValue : (int)start;
    }

    public int SizeAt(int index)
    {
        if (index >= 0 && index < steps.Length) return steps[index];
        return steady;
    }

    public WatchSegment SegmentAt(double pos)
    {
        int index = IndexAt(pos);
        int from = StartAt(index);
        return new WatchSegment { Index = index, From = from, Size = SizeAt(index) };
    }

    public List<WatchSegment> From(int index, int count)
    {
        var list = new List<WatchSegment>();
        if (count <= 0) return list;
        int start = Math.Max(0, index);
        for (int i = 0; i < count; i++)
        {
            int idx = start + i;
            int from = StartAt(idx);
            list.Add(new WatchSegment { Index = idx, From = from, Size = SizeAt(idx) });
        }
        return list;
    }

    /// <summary>从当前位置往后，要备到哪一段为止，才能保证已就绪时长 ≥ lookahead。</summary>
    public List<WatchSegment> WindowFor(double pos, int lookahead)
    {
        var list = new List<WatchSegment>();
        int start = IndexAt(pos);
        for (int i = 0; i < 512; i++)
        {
            int idx = start + i;
            WatchSegment seg = new() { Index = idx, From = StartAt(idx), Size = SizeAt(idx) };
            list.Add(seg);
            if (lookahead <= 0) break;
            if (seg.To - pos >= lookahead) break;
        }
        return list;
    }

    /// <summary>片长已知时的总段数；取片尾前 1 毫秒所在段，避免片长正好落在边界时多报一段。</summary>
    public int TotalSegments(double duration)
    {
        if (!double.IsFinite(duration) || duration <= 0) return 0;
        return SegmentAt(Math.Max(0, duration - 0.001)).Index + 1;
    }

    public bool IsRamping(int index) => index < steps.Length;

    public string Describe()
    {
        string head = string.Join(" → ", steps.Select(x => $"{Math.Max(1, (int)Math.Round(x / 60.0))}m"));
        string tail = $"{Math.Max(1, (int)Math.Round(steady / 60.0))}m";
        return head.Length > 0 ? $"{head} → {tail}（之后固定）" : tail;
    }
}

// ─────────────────────────────────────────────────────────────────
// 视频身份
// ─────────────────────────────────────────────────────────────────

public static class WatchSource
{
    /// <summary>
    /// 从读数里认出"在放什么"：优先页面 URL；file:// 转成本地路径。
    /// 扩展只在页面里有 &lt;video&gt; 时才上报，所以 http(s) 地址可以直接当源。
    /// </summary>
    public static string? InputFrom(WatchSample? sample)
    {
        if (sample == null) return null;
        string url = (sample.Url ?? "").Trim();
        if (url.Length == 0) return null;
        if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            return FileUrlToPath(url);
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return url;
        if (url.StartsWith("smtc://", StringComparison.OrdinalIgnoreCase))
            return url;
        return null;
    }

    public static string? FileUrlToPath(string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return null;
            string path = uri.LocalPath;
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 同一支片子的不同 URL 形态必须落到同一个键：
    /// B 站用 BV 号；其他 URL 去掉 query/hash 与尾斜杠；本地文件只看文件名。
    /// </summary>
    public static string SessionKey(string input)
    {
        string raw = (input ?? "").Trim();
        if (raw.Length == 0) return "unknown";
        // 本地文件只看文件名：file:// 先转回路径，别让 host 为空时把整条路径当键。
        if (raw.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            string? filePath = FileUrlToPath(raw);
            if (!string.IsNullOrWhiteSpace(filePath)) raw = filePath;
        }
        if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Uri uri = new(raw);
                string path = uri.AbsolutePath ?? "";
                var match = System.Text.RegularExpressions.Regex.Match(path, @"/video/(bv[0-9a-z]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success) return "bili:" + match.Groups[1].Value.ToLowerInvariant();
                string decoded = Uri.UnescapeDataString(path).TrimEnd('/');
                string key = (uri.Host + decoded).ToLowerInvariant();
                if (key.Length > 0) return key;
            }
            catch
            {
                // 不是合法 URL，按路径处理
            }
        }

        string local = raw;
        if (local.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            local = FileUrlToPath(local) ?? local;
        string[] parts = local.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        string file = parts.Length > 0 ? parts[^1] : local;
        return file.ToLowerInvariant();
    }

    public static bool IsLocalFile(string input) => !string.IsNullOrWhiteSpace(input) && System.IO.File.Exists(input);

    public static string DisplayName(string input, string? title)
    {
        string t = (title ?? "").Trim();
        if (t.Length > 0) return t;
        if (IsLocalFile(input)) return System.IO.Path.GetFileName(input);
        return input;
    }
}

// ─────────────────────────────────────────────────────────────────
// 会话
// ─────────────────────────────────────────────────────────────────

public sealed class WatchSaidLine
{
    public double At { get; set; }
    public string Text { get; set; } = "";
    public DateTime SpokenAt { get; set; } = DateTime.Now;
}

public sealed class WatchSession
{
    public string Key { get; set; } = "";
    public string Input { get; set; } = "";
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public double Duration { get; set; }
    public double MaxPos { get; set; }
    public int PlanSegment { get; set; }
    public string PlanRamp { get; set; } = "";
    public int PlanLead { get; set; }
    /// <summary>字幕游标：上次工具返回到的字幕绝对索引。只推进「已经给过」的，不重复贴。</summary>
    public int SubtitleCursor { get; set; }
    public List<int> Delivered { get; set; } = new();
    public List<WatchSaidLine> Said { get; set; } = new();
    public DateTime StartedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public DateTime LastDueAt { get; set; }
    public DateTime LastJumpAt { get; set; }
    public DateTime LastPokeAt { get; set; }
    public bool EndedNotified { get; set; }
    /// <summary>这一场的观看笔记是否已经写过，避免播完 + 手动结束写两遍。</summary>
    public bool WatchNoteSaved { get; set; }

    [JsonIgnore]
    public string DisplayTitle => WatchSource.DisplayName(Input, Title);

    public bool IsDelivered(int from) => Delivered.Any(x => Math.Abs(x - from) < 0.5);

    public void MarkDelivered(int from)
    {
        if (!IsDelivered(from)) Delivered.Add(from);
        if (Delivered.Count > 200) Delivered = Delivered.Skip(Delivered.Count - 200).ToList();
    }
}

/// <summary>存盘用的轻量状态：只记指针，不记内容和图。</summary>
public sealed class WatchRuntimeState
{
    public WatchSession? Session { get; set; }
    public string WakeDay { get; set; } = "";
    public int WakeCount { get; set; }
    public DateTime LastWakeAt { get; set; }
    public string RandomDay { get; set; } = "";
    public int RandomCount { get; set; }
    public DateTime LastRandomAt { get; set; }
}

// ─────────────────────────────────────────────────────────────────
// 小工具
// ─────────────────────────────────────────────────────────────────

public static class WatchFormat
{
    public static string Mmss(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) seconds = 0;
        int total = (int)Math.Floor(seconds);
        int h = total / 3600;
        int m = (total % 3600) / 60;
        int s = total % 60;
        return h > 0 ? $"{h:00}:{m:00}:{s:00}" : $"{m:00}:{s:00}";
    }

    public static string Duration(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) return "未知";
        return Mmss(seconds);
    }

    /// <summary>相对播放位置的时间描述。用播放位置而不是墙钟，暂停时不会漂。</summary>
    public static string Ago(double pos, double at)
    {
        double diff = pos - at;
        if (diff < -1) return $"还有 {-diff:0} 秒";
        if (diff <= 3) return "刚刚";
        if (diff < 60) return $"{diff:0} 秒前";
        return $"{diff / 60:0.0} 分前";
    }

    public static double? ToDouble(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : null;
    }

    public static int[] ParseRamp(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<int>();
        var list = new List<int>();
        foreach (string part in text.Split(new[] { ',', '，', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value > 0)
                list.Add(value);
        }
        return list.ToArray();
    }

    public static string SafeFileName(string text)
    {
        string name = text ?? "";
        foreach (char c in System.IO.Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Replace(':', '_').Replace('/', '_').Replace('\\', '_');
        if (name.Length > 60) name = name[^60..];
        return name.Length == 0 ? "session" : name;
    }
}
