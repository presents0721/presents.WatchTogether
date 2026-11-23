using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace presents.WatchTogether;

// ─────────────────────────────────────────────────────────────────
// 字幕：只读本地 SRT / VTT，不下载、不做 ASR
// ─────────────────────────────────────────────────────────────────

public sealed class SubtitleCue
{
    public double From { get; init; }
    public double To { get; init; }
    public string Text { get; init; } = "";
}

public static class WatchSubtitles
{
    static readonly Regex TimeLine = new(
        @"([0-9]{1,2}:[0-9]{2}:[0-9]{2}(?:[,.][0-9]{1,3})?|[0-9]{2}:[0-9]{2}(?:[,.][0-9]{1,3})?)\s*-->\s*([0-9]{1,2}:[0-9]{2}:[0-9]{2}(?:[,.][0-9]{1,3})?|[0-9]{2}:[0-9]{2}(?:[,.][0-9]{1,3})?)",
        RegexOptions.Compiled);

    // VTT 的 cue 设置行，不是台词正文。
    static readonly Regex VttSetting = new(
        @"^(line|position|align|size|region|vertical):",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    static readonly string[] VideoExtensions =
    {
        ".mp4", ".mkv", ".webm", ".avi", ".mov", ".flv", ".ts", ".m4v", ".wmv", ".mpg", ".mpeg"
    };

    // ASR / 字幕组常见套话。外部字幕多半是机器转写，这些句子不是片子内容。
    static readonly string[] Boilerplate =
    {
        "字幕由", "字幕提供", "字幕制作", "字幕组", "字幕志愿者", "字幕下载",
        "中文字幕", "双语字幕", "翻译：", "校对：", "时间轴：", "压制：", "出品：",
        "本节目", "本期节目", "下期再见", "下次再见", "请订阅", "欢迎订阅",
        "订阅频道", "点赞订阅", "打赏", "amara", "mbc"
    };

    /// <summary>明显不是台词：纯符号、纯音乐标记、字幕组套话。</summary>
    public static bool IsBoilerplate(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        string value = text.Trim();
        int content = value.Count(char.IsLetterOrDigit);
        if (content < 1) return true;
        foreach (string word in Boilerplate)
        {
            if (value.Contains(word, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static bool IsVideoFile(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return VideoExtensions.Contains(ext);
    }

    public static bool IsSubtitleFile(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".srt" or ".vtt";
    }

    /// <summary>
    /// 找这支片子的本地字幕：先看 input 本身、再找同目录同名/同名带语言后缀，最后找素材目录。
    /// 找不到返回 null，**不猜、不下字幕**。
    /// </summary>
    public static string? Find(string input, string title, string materialDirectory, string extensions)
    {
        string[] exts = NormalizeExtensions(extensions);
        if (string.IsNullOrWhiteSpace(input)) return null;

        if (IsSubtitleFile(input) && File.Exists(input)) return input;

        string baseName = "";
        string? dir = null;
        if (File.Exists(input))
        {
            baseName = Path.GetFileNameWithoutExtension(input);
            dir = Path.GetDirectoryName(input);
        }
        else if (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                 input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            baseName = GuessUrlBaseName(input, title);
        }
        else
        {
            baseName = Path.GetFileNameWithoutExtension(input);
        }

        if (dir != null)
        {
            string? hit = FindInDirectory(dir, baseName, exts);
            if (hit != null) return hit;
        }

        if (!string.IsNullOrWhiteSpace(materialDirectory) && Directory.Exists(materialDirectory))
        {
            string? hit = FindInDirectory(materialDirectory, baseName, exts);
            if (hit != null) return hit;
        }

        return null;
    }

    static string? FindInDirectory(string dir, string baseName, string[] exts)
    {
        if (string.IsNullOrWhiteSpace(baseName)) return null;
        foreach (string ext in exts)
        {
            string exact = Path.Combine(dir, baseName + ext);
            if (File.Exists(exact)) return exact;

            string prefix = baseName + ".";
            try
            {
                foreach (string file in Directory.GetFiles(dir))
                {
                    string name = Path.GetFileName(file);
                    if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!exts.Contains(Path.GetExtension(file).ToLowerInvariant())) continue;
                    return file;
                }
            }
            catch
            {
                // 目录读不了就算了
            }
        }
        return null;
    }

    static string[] NormalizeExtensions(string value)
    {
        var list = new List<string>();
        foreach (string part in (value ?? "").Split(new[] { ',', '，', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string ext = part.Trim().ToLowerInvariant();
            if (!ext.StartsWith('.')) ext = "." + ext;
            if (ext is ".srt" or ".vtt") list.Add(ext);
        }
        if (list.Count == 0) list.Add(".srt");
        return list.Distinct().ToArray();
    }

    static string GuessUrlBaseName(string url, string title)
    {
        try
        {
            var match = Regex.Match(url, @"/video/(bv[0-9a-z]+)", RegexOptions.IgnoreCase);
            if (match.Success) return match.Groups[1].Value;
            Uri uri = new(url);
            string name = Uri.UnescapeDataString(uri.AbsolutePath.TrimEnd('/').Split('/').LastOrDefault() ?? "");
            if (name.Length > 0) return name;
        }
        catch
        {
            // 忽略
        }
        return (title ?? "").Trim();
    }

    public static List<SubtitleCue> ParseFile(string path)
    {
        try
        {
            string text = ReadTextSmart(path);
            return Parse(text);
        }
        catch
        {
            return new List<SubtitleCue>();
        }
    }

    /// <summary>整体平移时间轴（外部 ASR 字幕从录音开始计时时需要）。</summary>
    public static List<SubtitleCue> Shift(IEnumerable<SubtitleCue> cues, double offset)
    {
        var list = new List<SubtitleCue>();
        foreach (SubtitleCue cue in cues)
        {
            double from = Math.Max(0, cue.From + offset);
            double to = Math.Max(from + 0.2, cue.To + offset);
            list.Add(new SubtitleCue { From = from, To = to, Text = cue.Text });
        }
        return list;
    }

    public static List<SubtitleCue> Parse(string text)
    {
        var cues = new List<SubtitleCue>();
        if (string.IsNullOrWhiteSpace(text)) return cues;

        string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        string[] lines = normalized.Split('\n');
        double from = 0;
        double to = 0;
        bool timing = false;
        string? lastText = null;
        var buffer = new StringBuilder();

        void Flush()
        {
            if (!timing) return;
            string content = CleanCueText(buffer.ToString());
            buffer.Clear();
            timing = false;
            if (content.Length == 0) return;
            if (IsBoilerplate(content)) return;
            // 相邻重复：音乐循环 / ASR 抖动 / 字幕组重复贴，都只留一条。
            if (lastText != null && string.Equals(lastText, content, StringComparison.Ordinal)) return;
            lastText = content;
            cues.Add(new SubtitleCue
            {
                From = Math.Max(0, from),
                To = Math.Max(from + 0.2, to),
                Text = content
            });
        }

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
            {
                Flush();
                continue;
            }

            Match match = TimeLine.Match(line);
            if (match.Success)
            {
                Flush();
                if (TryParseTime(match.Groups[1].Value, out double start) &&
                    TryParseTime(match.Groups[2].Value, out double end))
                {
                    from = start;
                    to = end;
                    timing = true;
                }
                continue;
            }

            if (line.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("NOTE", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("STYLE", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("REGION", StringComparison.OrdinalIgnoreCase))
                continue;

            if (timing)
            {
                if (VttSetting.IsMatch(line)) continue;
                if (buffer.Length > 0) buffer.Append(' ');
                buffer.Append(line);
            }
        }
        Flush();

        // 按起点排序；完全重复的文案只留一条，避免同一句被念两遍。
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<SubtitleCue>();
        foreach (SubtitleCue cue in cues.OrderBy(c => c.From))
        {
            string key = $"{Math.Round(cue.From):0}|{cue.Text}";
            if (seen.Add(key)) ordered.Add(cue);
        }
        return ordered;
    }

    static bool TryParseTime(string text, out double seconds)
    {
        seconds = 0;
        string value = text.Trim().Replace(',', '.');
        string[] parts = value.Split(':');
        if (parts.Length is < 2 or > 3) return false;

        double total = 0;
        foreach (string part in parts)
        {
            if (!double.TryParse(part, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double number))
                return false;
            total = total * 60 + number;
        }
        seconds = total;
        return true;
    }

    static string CleanCueText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        string value = Regex.Replace(text, @"<[^>]*>", " ");
        value = Regex.Replace(value, @"\{\\[^}]*\}", " ");
        value = value.Replace("&nbsp;", " ").Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">");
        value = Regex.Replace(value, @"\s+", " ").Trim();
        return value;
    }

    static string ReadTextSmart(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        string utf8 = Encoding.UTF8.GetString(bytes);
        if (!LooksBroken(utf8)) return utf8;

        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            string gb = Encoding.GetEncoding("GB18030").GetString(bytes);
            if (!LooksBroken(gb)) return gb;
        }
        catch
        {
            // 没有代码页提供程序就保持 UTF-8
        }
        return utf8;
    }

    static bool LooksBroken(string text)
    {
        if (text.Contains('\uFFFD')) return true;
        int suspicious = 0;
        foreach (char c in text)
        {
            if (c is >= '\u0080' and <= '\u00FF') suspicious++;
        }
        return suspicious > Math.Max(8, text.Length / 20);
    }

    /// <summary>按段取字幕原文：只取与 [from,to] 有重叠的句子，带时间戳，限字数。</summary>
    public static string BuildSegmentText(IReadOnlyList<SubtitleCue> cues, double from, double to, int maxChars)
    {
        if (cues == null || cues.Count == 0) return "";
        int limit = Math.Clamp(maxChars, 200, 8000);
        var sb = new StringBuilder();
        foreach (SubtitleCue cue in cues)
        {
            if (cue.To < from) continue;
            if (cue.From > to) break;
            string line = $"[{WatchFormat.Mmss(Math.Max(from, cue.From))}] {cue.Text}";
            if (sb.Length + line.Length + 1 > limit)
            {
                sb.Append("…（本段字幕后略）");
                break;
            }
            sb.AppendLine(line);
        }
        return sb.ToString().TrimEnd();
    }

    public static int CountInRange(IReadOnlyList<SubtitleCue> cues, double from, double to)
    {
        if (cues == null) return 0;
        return cues.Count(c => c.To >= from && c.From <= to);
    }

    /// <summary>
    /// 游标式字幕窗口：只给「当前位置往前 back 秒、且索引在 cursor 之后」的句子。
    /// 纯函数，方便测试；游标推进由调用方决定（位置不可信时不要推进）。
    /// </summary>
    public static SubtitleWindowResult BuildWindow(
        IReadOnlyList<SubtitleCue> cues,
        double pos,
        double backSec,
        double aheadSec,
        int maxLines,
        int maxChars,
        int cursor,
        string source)
    {
        if (cues == null || cues.Count == 0)
            return new SubtitleWindowResult { Text = "", Cursor = cursor };
        if (cursor >= cues.Count || cursor < 0) cursor = 0;

        double back = Math.Max(10, backSec);
        double ahead = Math.Max(0, aheadSec);
        double end = pos + (ahead > 0 ? ahead : 2);
        int limitLines = Math.Clamp(maxLines, 3, 120);
        int limitChars = Math.Clamp(maxChars, 200, 8000);

        var visible = new List<(int Index, SubtitleCue Cue)>();
        for (int i = 0; i < cues.Count; i++)
        {
            SubtitleCue cue = cues[i];
            if (cue.To < pos - back) continue;
            if (cue.From > end) break;
            visible.Add((i, cue));
        }

        if (visible.Count == 0)
            return new SubtitleWindowResult
            {
                Text = $"[字幕] {source} · 当前位置之前 {back:0} 秒内没有台词。",
                Cursor = cursor
            };

        var fresh = visible.Where(x => x.Index > cursor).ToList();
        int skipped = 0;
        if (fresh.Count > limitLines)
        {
            skipped = fresh.Count - limitLines;
            fresh = fresh.Skip(skipped).ToList();
        }

        if (fresh.Count == 0)
        {
            SubtitleCue last = visible[^1].Cue;
            return new SubtitleWindowResult
            {
                Text = $"[字幕] {source} · 本窗口没有新台词（最近一句在 {WatchFormat.Ago(pos, last.From)}）。",
                Cursor = cursor
            };
        }

        int newCursor = Math.Max(cursor, fresh[^1].Index);
        var sb = new StringBuilder();
        sb.Append($"[字幕] {source} · 新增 {fresh.Count} 句");
        if (skipped > 0) sb.Append($"（前面还有 {skipped} 句已省略）");
        if (ahead > 0) sb.Append("（往后预告已开，注意别剧透）");
        sb.AppendLine();
        foreach (var item in fresh)
        {
            string line = $"- {WatchFormat.Ago(pos, item.Cue.From)}｜{item.Cue.Text}";
            if (sb.Length + line.Length + 1 > limitChars)
            {
                sb.AppendLine("…（后略）");
                break;
            }
            sb.AppendLine(line);
        }

        return new SubtitleWindowResult
        {
            Text = sb.ToString().TrimEnd(),
            Cursor = newCursor,
            NewCount = fresh.Count,
            Skipped = skipped
        };
    }

    /// <summary>最近几句的短摘要，不推进游标；给到点提醒/随机搭话用。</summary>
    public static string BuildDigest(
        IReadOnlyList<SubtitleCue> cues,
        double pos,
        double backSec,
        int lines,
        int maxChars,
        string source)
    {
        if (cues == null || cues.Count == 0 || lines <= 0) return "";
        double back = Math.Max(30, backSec);
        var recent = new List<SubtitleCue>();
        for (int i = cues.Count - 1; i >= 0 && recent.Count < lines; i--)
        {
            SubtitleCue cue = cues[i];
            if (cue.From > pos + 2) continue;
            if (cue.To < pos - back) break;
            recent.Add(cue);
        }
        if (recent.Count == 0) return "";
        recent.Reverse();

        var sb = new StringBuilder();
        sb.AppendLine($"[字幕] {source} · 最近 {recent.Count} 句");
        int limit = Math.Clamp(maxChars, 100, 2000);
        foreach (SubtitleCue cue in recent)
        {
            string line = $"- {WatchFormat.Ago(pos, cue.From)}｜{cue.Text}";
            if (sb.Length + line.Length > limit) break;
            sb.AppendLine(line);
        }
        return sb.ToString().TrimEnd();
    }
}

public sealed class SubtitleWindowResult
{
    public string Text { get; init; } = "";
    public int Cursor { get; init; }
    public int NewCount { get; init; }
    public int Skipped { get; init; }
}

// ─────────────────────────────────────────────────────────────────
// 抽帧：可选能力。找不到 ffmpeg 就明确降级，不假装有画面
// ─────────────────────────────────────────────────────────────────

public sealed class FrameShot
{
    public string Path { get; init; } = "";
    public double At { get; init; }
}

public sealed class FrameResult
{
    public bool Ok { get; init; }
    public string Ffmpeg { get; init; } = "";
    public List<string> Frames { get; init; } = new();
    public List<FrameShot> Shots { get; init; } = new();
    public string ContactSheet { get; init; } = "";
    public string Error { get; init; } = "";
}

public static class WatchFrames
{
    static readonly object Gate = new();
    static string? cachedFfmpeg;
    static DateTime cachedAt = DateTime.MinValue;
    static string cachedConfigured = "";

    /// <summary>定位 ffmpeg：配置路径 → PATH → python imageio-ffmpeg → 常见安装目录。</summary>
    public static string? FindFfmpeg(string configured)
    {
        lock (Gate)
        {
            if (cachedFfmpeg != null && cachedAt > DateTime.Now.AddMinutes(-1)
                && string.Equals(cachedConfigured, configured ?? "", StringComparison.OrdinalIgnoreCase))
                return cachedFfmpeg;
        }

        string? found = FindFfmpegCore(configured ?? "");
        lock (Gate)
        {
            cachedFfmpeg = found;
            cachedAt = DateTime.Now;
            cachedConfigured = configured ?? "";
        }
        return found;
    }

    static string? FindFfmpegCore(string? configured)
    {
        string cfg = (configured ?? "").Trim().Trim('"');
        if (cfg.Length > 0)
        {
            if (File.Exists(cfg)) return cfg;
            if (Directory.Exists(cfg))
            {
                foreach (string candidate in new[]
                {
                    Path.Combine(cfg, "ffmpeg.exe"),
                    Path.Combine(cfg, "bin", "ffmpeg.exe"),
                    Path.Combine(cfg, "ffmpeg", "bin", "ffmpeg.exe")
                })
                {
                    if (File.Exists(candidate)) return candidate;
                }
            }
        }

        string? onPath = Where("ffmpeg.exe") ?? Where("ffmpeg");
        if (onPath != null) return onPath;

        string? viaPython = FfmpegFromPython();
        if (viaPython != null) return viaPython;

        var candidates = new List<string>
        {
            @"C:\ffmpeg\bin\ffmpeg.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin", "ffmpeg.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "shims", "ffmpeg.exe")
        };
        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    static string? Where(string name)
    {
        try
        {
            ProcessStartInfo psi = new("where.exe", name)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using Process p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            string first = output.Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0) ?? "";
            return File.Exists(first) ? first : null;
        }
        catch
        {
            return null;
        }
    }

    static string? FfmpegFromPython()
    {
        var pythons = new List<string>();
        string? env = Environment.GetEnvironmentVariable("ALIFE_PYTHON");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) pythons.Add(env);

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string p311 = Path.Combine(local, "Programs", "Python", "Python311", "python.exe");
        if (File.Exists(p311)) pythons.Add(p311);

        string? onPath = Where("python.exe");
        if (onPath != null) pythons.Add(onPath);

        foreach (string python in pythons.Distinct())
        {
            try
            {
                ProcessStartInfo psi = new(python)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add("import imageio_ffmpeg; print(imageio_ffmpeg.get_ffmpeg_exe())");
                using Process p = Process.Start(psi)!;
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                string path = output.Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0) ?? "";
                if (File.Exists(path)) return path;
            }
            catch
            {
                // 这个解释器没有 imageio-ffmpeg，试下一个
            }
        }
        return null;
    }

    public static async Task<FrameResult> ExtractAsync(
        string? ffmpeg,
        string video,
        string cacheDir,
        int from,
        int to,
        int cells,
        int maxWidth,
        IReadOnlyList<double>? times = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(video))
            return new FrameResult { Ok = false, Error = "不是本地视频文件，网页视频不抽帧" };
        if (!WatchSubtitles.IsVideoFile(video))
            return new FrameResult { Ok = false, Error = "这个文件不是支持的视频格式" };
        if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg))
            return new FrameResult { Ok = false, Error = "没有找到 ffmpeg（可在控制台填路径，或把 ffmpeg 加进 PATH）" };

        int count = Math.Clamp(cells, 1, 9);
        int width = Math.Clamp(maxWidth, 160, 1920);
        int span = Math.Max(1, to - from);
        try
        {
            Directory.CreateDirectory(cacheDir);
        }
        catch (Exception e)
        {
            return new FrameResult { Ok = false, Error = "建缓存目录失败：" + e.Message };
        }

        // 时间点：优先用调用方给的字幕句时间，不足再补均匀点；每帧的时间进文件名，避免缓存串帧。
        var plans = new List<double>();
        if (times != null)
        {
            foreach (double wanted in times)
            {
                double at = Math.Clamp(wanted, from, Math.Max(from, to - 0.5));
                if (plans.Any(x => Math.Abs(x - at) < 0.5)) continue;
                plans.Add(at);
                if (plans.Count >= count) break;
            }
        }
        if (plans.Count >= count)
        {
            plans = plans.OrderBy(x => x).Take(count).ToList();
        }
        else
        {
            // 字幕点不够就整段均匀，别把帧全挤在开头。
            plans.Clear();
            for (int i = 0; i < count; i++)
            {
                double at = from + span * (i + 0.5) / count;
                at = Math.Min(Math.Max(from, at), Math.Max(from, to - 0.5));
                plans.Add(at);
            }
        }

        var frames = new List<string>();
        var shots = new List<FrameShot>();
        var errors = new List<string>();
        for (int i = 0; i < plans.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            double at = plans[i];
            string atText = at.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

            string output = Path.Combine(cacheDir, $"seg_{from}_{i}_{atText}_{width}.jpg");
            if (File.Exists(output) && new FileInfo(output).Length > 0)
            {
                frames.Add(output);
                shots.Add(new FrameShot { Path = output, At = at });
                continue;
            }

            int code;
            string err;
            try
            {
                (code, err) = await RunAsync(ffmpeg, new[]
                {
                    "-hide_banner", "-loglevel", "error",
                    "-ss", at.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture),
                    "-i", video,
                    "-frames:v", "1",
                    "-vf", $"scale=min({width}\\,iw):-2",
                    "-q:v", "3",
                    "-y", output
                }, 15000, ct).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                errors.Add(e.Message);
                continue;
            }

            if (code == 0 && File.Exists(output) && new FileInfo(output).Length > 0)
            {
                frames.Add(output);
                shots.Add(new FrameShot { Path = output, At = at });
            }
            else
            {
                errors.Add($"第 {i + 1} 格失败：{Short(err)}");
            }
        }

        if (frames.Count == 0)
            return new FrameResult
            {
                Ok = false,
                Ffmpeg = ffmpeg,
                Error = errors.Count > 0 ? Short(string.Join("；", errors)) : "ffmpeg 没有产出画面"
            };

        // 概览拼图：一张 2×3 图看完整段，比让模型逐张看省视觉 token（借鉴 storyboard/plus-one）。
        string? sheet = await BuildContactSheetAsync(ffmpeg, video, cacheDir, from, to, width, ct).ConfigureAwait(false);
        return new FrameResult
        {
            Ok = true,
            Ffmpeg = ffmpeg,
            Frames = frames,
            Shots = shots,
            ContactSheet = sheet ?? ""
        };
    }

    /// <summary>
    /// 用 ffmpeg 的 fps + tile 把整段均匀抽 6 帧拼成 2×3 概览图。
    /// 失败只返回 null，不影响已经抽好的单帧。
    /// </summary>
    static async Task<string?> BuildContactSheetAsync(
        string ffmpeg, string video, string cacheDir, int from, int to, int width, CancellationToken ct)
    {
        int span = Math.Max(1, to - from);
        int sheetWidth = Math.Clamp(width, 160, 960);
        string output = Path.Combine(cacheDir, $"sheet_{from}_{span}_{sheetWidth}.jpg");
        if (File.Exists(output) && new FileInfo(output).Length > 0) return output;

        // 略多抽一点，保证能凑满 6 帧给 2×3。
        double fps = Math.Max(0.01, 6.2 / span);
        string fpsText = fps.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            int code;
            string err;
            (code, err) = await RunAsync(ffmpeg, new[]
            {
                "-hide_banner", "-loglevel", "error",
                "-ss", from.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture),
                "-t", span.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture),
                "-i", video,
                "-vf", $"fps={fpsText},scale={sheetWidth}:-2,tile=2x3",
                "-frames:v", "1",
                "-q:v", "3",
                "-y", output
            }, 20000, ct).ConfigureAwait(false);

            if (code == 0 && File.Exists(output) && new FileInfo(output).Length > 0)
                return output;
        }
        catch
        {
            // 老版本 ffmpeg 没有 tile / 段太短时，退化成只有单帧
        }
        return null;
    }

    static async Task<(int Code, string Error)> RunAsync(
        string exe, IEnumerable<string> args, int timeoutMs, CancellationToken ct)
    {
        ProcessStartInfo psi = new(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string arg in args) psi.ArgumentList.Add(arg);

        using Process p = Process.Start(psi) ?? throw new Exception("无法启动 ffmpeg");
        Task<string> stdout = p.StandardOutput.ReadToEndAsync();
        Task<string> stderr = p.StandardError.ReadToEndAsync();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);
        try
        {
            await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            return (-1, "ffmpeg 超时");
        }

        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        return (p.ExitCode, stderr.Result);
    }

    static string Short(string text)
    {
        string value = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return value.Length <= 160 ? value : value[..160];
    }
}
