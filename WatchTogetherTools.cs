using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Alife.Function.FunctionCaller;
using Microsoft.Extensions.Logging;
using Presents.Shared.Media;

namespace presents.WatchTogether;

/// <summary>
/// watch_together 工具：接上正在播的片子、给当前段与素材、记录台词、诊断桥状态。
/// </summary>
public partial class WatchTogetherModule
{
    // ─────────────────────────────────────────────────────────────
    // 工具
    // ─────────────────────────────────────────────────────────────

    [XmlFunction(FunctionMode.OneShot, "watch_together")]
    [Description(
        "陪主人一起看视频：不给参数就自动接上他正在播放的那一支（浏览器扩展或桌面播放器媒体会话）；返回当前位置、当前段、交付点、字幕/关键帧。"
        + "不要向主人要链接。只有到了交付点才说话；statusOnly=true 只看状态。")]
    public async Task<string> WatchTogether(
        [Description("视频 URL 或本地文件路径；留空=自动读浏览器/桌面播放器")] string? input = null,
        [Description("手动播放位置（秒）；留空=自动读")] double? cursor = null,
        [Description("稳定段长（秒），默认 300")] double? segment = null,
        [Description("起步阶梯（秒，逗号分隔），默认 60,120,180,240；传 0=全程等长")] string? ramp = null,
        [Description("段末前多少秒算交付点，默认 15")] double? lead = null,
        [Description("每段抽几帧，默认 5；0=不抽")] int? cells = null,
        [Description("预取余量（秒），默认 300")] double? lookahead = null,
        [Description("只看桥接/位置/段状态，不取素材")] bool? statusOnly = null,
        [Description("显式查看某一段的起点（秒），用于补看")] double? from = null,
        [Description("显式段长（秒）；留空=按当前位置算")] double? size = null,
        [Description("把你想说的一句记进这一场（只记录，不发送、不配音）")] string? say = null,
        [Description("true=结束这一场并清空进度")] bool? reset = null,
        [Description("true=暂停（浏览器下一次上报时执行）")] bool? pause = null,
        [Description("true=继续播放（浏览器下一次上报时执行）")] bool? play = null,
        [Description("回退多少秒（1-120），浏览器下一次上报时执行")] double? seekBack = null,
        [Description("跳到某个位置（秒），浏览器下一次上报时执行")] double? seekTo = null,
        [Description("true=等视频出现（最多 20 秒）")] bool? waitForVideo = null,
        [Description("是否抽帧；留空=按配置")] bool? frames = null,
        [Description("片名（可选，用于字幕查找与显示）")] string? title = null,
    [Description("true=把当前 B 站视频下载到本地（之后抽帧/转写/发人都用本地文件）")] bool? download = null,
    [Description("true=最近陪看事件（每段一条：字幕摘句 + 画面文字）")] bool? timeline = null,
    [Description("true=复盘最近 N 分钟（配合 minutes）")] bool? recap = null,
    [Description("复盘/时间轴覆盖分钟数，默认 30")] int? minutes = null,
    [Description("true=现在读一次画面文字（OCR），不推进段进度")] bool? ocr = null)
    {
        try
        {
            if (!Configuration.Enable)
                return "[一起看] 插件已停用（控制台「启用视频陪看」）。";

            if (reset == true)
            {
                StopSession("reset");
                return "[一起看] 这一场已结束，进度清空。下次直接再调 watch_together 就能接上。";
            }

            if (download == true)
            {
                string note = await DownloadCurrentAsync(DestroyCancellationToken).ConfigureAwait(false);
                return "[一起看·下载] " + note;
            }

            if (timeline == true)
            {
                List<WatchEvent> recent = UiRecentEvents(15);

                if (recent.Count == 0)
                    return "[一起看·时间轴] 还没有记录。走到第一个交付点后，每段会记一条（字幕摘句 + 画面文字）。";

                var timelineText = new StringBuilder("[一起看·时间轴] 最近 " + recent.Count + " 段：\n");
                foreach (WatchEvent item in recent) timelineText.AppendLine(item.Line());
                return timelineText.ToString().TrimEnd();
            }

            if (recap == true)
                return "[一起看·复盘]\n" + (UiWatchEvents?.Recap(Math.Clamp(minutes ?? 30, 1, 600))
                                            ?? "事件库没初始化。");

            if (ocr == true)
            {
                LiveFrameStore? frameStore = UiLiveFrames;
                string sessionKey = session?.Key ?? "";
                List<LiveFrame> latest = frameStore?.Recent(sessionKey, 1) ?? new List<LiveFrame>();

                if (latest.Count == 0)
                    return "[一起看·OCR] 还没有实时帧：网页视频要在扩展里点「开始抓画面」。";

                string text = await MediaOcr.ReadAsync(
                    latest[0].Path,
                    Configuration.OcrLanguage,
                    Math.Clamp(Configuration.OcrMaxChars, 80, 2000)).ConfigureAwait(false);

                return text.Length > 0
                    ? $"[一起看·OCR] {WatchFormat.Mmss(latest[0].Pos)} 这一帧的文字：\n{text}\n图片：{latest[0].Path}"
                    : $"[一起看·OCR] 这一帧没读到文字（纯画面）。\n图片：{latest[0].Path}";
            }

            DateTime now = DateTime.Now;
            if (statusOnly == true)
            {
                EnsureBridge();
                EnsureDesktopMedia();
                return BuildStatusText();
            }

            ApplyPlanArgs(segment, ramp, lead);
            EnsureBridge();
            EnsureDesktopMedia();

            bool wantsControl = pause == true || play == true || seekBack is > 0 || seekTo.HasValue;
            bool desktopSession = session?.Input.StartsWith("smtc://", StringComparison.OrdinalIgnoreCase) == true
                                  || lastSample?.Url?.StartsWith("smtc://", StringComparison.OrdinalIgnoreCase) == true;
            if (wantsControl && desktopSession)
                return "[一起看] 当前是桌面播放器，插件不能控制它的暂停/跳转；请在播放器里手动操作。";
            if (wantsControl && bridge?.Running != true)
                return "[一起看] 位置桥没在运行，播放器命令发不出去；先检查端口或启动位置桥。";

            string controlNote = ApplyPlaybackControl(pause, play, seekBack, seekTo);
            bool controlOnly = !string.IsNullOrEmpty(controlNote)
                               && input == null && cursor == null && from == null
                               && segment == null && ramp == null && lead == null
                               && cells == null && lookahead == null
                               && frames == null && title == null && waitForVideo == null
                               && say == null;
            if (controlOnly)
                return $"[一起看] 已请求{controlNote}；浏览器下一次上报时执行（最多约 1 秒）。";

            string? sayText = say?.Trim();
            bool onlySay = !string.IsNullOrEmpty(sayText)
                           && input == null && cursor == null && from == null
                           && segment == null && ramp == null && lead == null
                           && cells == null && lookahead == null
                           && frames == null && title == null && waitForVideo == null;

            // 只记一句台词不该等视频：用户很可能正在聊天窗口里，扩展这时没有上报。
            if (onlySay)
            {
                if (session == null)
                    return "[一起看] 还没有在看的片子：先让主人把视频播起来，再调一次 watch_together。";
                WatchSample? live = PickSample();
                double pos = live != null && (now - live.At).TotalSeconds <= Math.Max(10, Configuration.IdentifyStaleSeconds)
                    ? live.Pos
                    : (lastEstimate?.Pos ?? 0);
                RecordSaid(sayText!, pos, now);
                return $"[一起看] 已记下这句（位置 {WatchFormat.Mmss(pos)}，只记录、不发送、不配音）。";
            }

            WatchSample? sample = await ResolveSampleAsync(input, cursor, title, waitForVideo == true).ConfigureAwait(false);
            if (sample == null)
            {
                string prefix = string.IsNullOrEmpty(controlNote)
                    ? ""
                    : $"[控制] 已请求{controlNote}（等待浏览器下一次上报）\n";
                return prefix + BuildNoSampleText(input);
            }

            if (string.IsNullOrWhiteSpace(input) && WatchSource.InputFrom(sample) == null)
                return BuildCursorOnlyText(sample);

            string result = await BuildWatchResultAsync(sample, input, from, size, cells, frames, sayText, now).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(controlNote))
                result = $"[控制] 已请求{controlNote}（浏览器下一次上报时执行）\n" + result;
            return result;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "一起看：工具执行失败");
            return "[一起看] 执行失败：" + e.Message;
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 参数 / 读数解析
    // ─────────────────────────────────────────────────────────────

    /// <summary>把播放器命令排进桥里；返回人话描述，空串表示没请求。</summary>
    string ApplyPlaybackControl(bool? pause, bool? play, double? seekBack, double? seekTo)
    {
        if (bridge == null) return "";

        if (pause == true)
        {
            bridge.SetCommand("pause", 0);
            return "暂停";
        }
        if (play == true)
        {
            bridge.SetCommand("play", 0);
            return "继续播放";
        }
        if (seekBack is > 0)
        {
            double seconds = Math.Clamp(seekBack.Value, 1, 120);
            bridge.SetCommand("seekBack", seconds);
            return $"回退 {seconds:0} 秒";
        }
        if (seekTo.HasValue)
        {
            double pos = Math.Max(0, seekTo.Value);
            bridge.SetCommand("seekTo", pos);
            return $"跳到 {WatchFormat.Mmss(pos)}";
        }
        return "";
    }

    void ApplyPlanArgs(double? segment, string? ramp, double? lead)
    {
        int seg = (int)(segment ?? (session?.PlanSegment > 0 ? session.PlanSegment : Configuration.SegmentSeconds));
        seg = Math.Clamp(seg, 15, 3600);

        string rampText = ramp ?? (session?.PlanRamp ?? Configuration.RampSeconds);
        int leadSec = (int)(lead ?? (session?.PlanLead > 0 ? session.PlanLead : Configuration.LeadSeconds));
        leadSec = Math.Clamp(leadSec, 0, seg / 2);

        int[] steps = WatchFormat.ParseRamp(rampText)
            .Select(x => Math.Clamp(x, 5, 3600))
            .ToArray();

        planRampText = rampText;
        planLead = leadSec;
        plan = WatchPlan.Create(seg, steps);

        if (session != null)
        {
            session.PlanSegment = seg;
            session.PlanRamp = rampText;
            session.PlanLead = leadSec;
        }
    }

    async Task<WatchSample?> ResolveSampleAsync(string? input, double? cursor, string? title, bool wait)
    {
        DateTime now = DateTime.Now;
        WatchSample? latest = PickSample();
        double latestAge = latest == null ? double.MaxValue : (now - latest.At).TotalSeconds;
        bool latestFresh = latestAge <= Math.Max(5, Configuration.BridgeStaleSeconds);

        if (cursor.HasValue)
        {
            string url = input ?? latest?.Url ?? session?.Url ?? "";
            WatchSample? known = latest;
            bool sameSource = false;
            if (known != null && !string.IsNullOrWhiteSpace(url))
            {
                string? knownInput = WatchSource.InputFrom(known);
                sameSource = knownInput != null &&
                             WatchSource.SessionKey(knownInput) == WatchSource.SessionKey(url);
            }
            return new WatchSample
            {
                Pos = Math.Max(0, cursor.Value),
                Dur = sameSource ? known?.Dur : (session?.Duration > 0 ? session.Duration : null),
                Title = title ?? (sameSource ? known?.Title : null) ?? session?.Title,
                Url = url,
                Paused = false,
                At = now
            };
        }

        if (!string.IsNullOrWhiteSpace(input))
        {
            string source = input.Trim();
            if (source.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                source = WatchSource.FileUrlToPath(source) ?? source;

            if (!source.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !source.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                && !File.Exists(source))
                return null;

            string key = WatchSource.SessionKey(source);
            bool same = false;
            if (latest != null)
            {
                string? latestInput = WatchSource.InputFrom(latest);
                same = latestInput != null && WatchSource.SessionKey(latestInput) == key;
            }

            string url = source.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? source
                : new Uri(Path.GetFullPath(source)).AbsoluteUri;

            return new WatchSample
            {
                Pos = same && latestAge <= Math.Max(10, Configuration.IdentifyStaleSeconds)
                    ? Math.Max(0, latest!.Pos)
                    : 0,
                Dur = same ? latest?.Dur : (session?.Duration > 0 ? session.Duration : null),
                Title = title ?? (same ? latest?.Title : null) ?? session?.Title ?? Path.GetFileName(source),
                Url = url,
                Paused = same && latest != null ? latest.Paused : false,
                Ended = same && latest != null && latest.Ended,
                At = same && latest != null && latestAge <= Math.Max(10, Configuration.IdentifyStaleSeconds)
                    ? latest.At
                    : now
            };
        }

        if (latest != null && latestAge <= Math.Max(10, Configuration.IdentifyStaleSeconds))
            return latest;

        if (wait || Configuration.AutoWaitSeconds > 0)
        {
            int cap = wait ? 20 : Math.Clamp(Configuration.AutoWaitSeconds, 1, 20);
            DateTime deadline = now.AddSeconds(cap);
            while (DateTime.Now < deadline)
            {
                WatchSample? current = PickSample();
                if (current != null
                    && (DateTime.Now - current.At).TotalSeconds <= Math.Max(3, Configuration.FreshSeconds)
                    && WatchSource.InputFrom(current) != null)
                    return current;
                try { await Task.Delay(300, DestroyCancellationToken).ConfigureAwait(false); } catch { break; }
            }
        }

        return latest;
    }

    // ─────────────────────────────────────────────────────────────
    // 结果
    // ─────────────────────────────────────────────────────────────

    async Task<string> BuildWatchResultAsync(
        WatchSample sample,
        string? givenInput,
        double? from,
        double? size,
        int? cells,
        bool? frames,
        string? say,
        DateTime now)
    {
        string? input = givenInput ?? WatchSource.InputFrom(sample);
        if (string.IsNullOrWhiteSpace(input))
            return BuildCursorOnlyText(sample);

        string key = WatchSource.SessionKey(input);
        bool newSession = session == null || !string.Equals(session.Key, key, StringComparison.Ordinal);
        if (newSession)
        {
            session = new WatchSession
            {
                Key = key,
                Input = input,
                Title = sample.Title ?? "",
                Url = sample.Url ?? "",
                Duration = sample.Dur ?? 0,
                StartedAt = now,
                UpdatedAt = now,
                PlanSegment = plan.Steady,
                PlanRamp = planRampText,
                PlanLead = planLead
            };
            lastTickPos = null;
            lastTickAt = default;
        }
        else
        {
            UpdateSessionFields(sample, now);
        }

        WatchSession current = session!;
        PositionEstimate estimate = WatchEstimator.Estimate(
            sample, now,
            Math.Max(1, Configuration.FreshSeconds) * 1000.0,
            Math.Max(5, Configuration.MaxExtrapolateSeconds) * 1000.0);

        bool explicitSegment = from.HasValue;
        WatchSegment seg;
        if (explicitSegment)
        {
            int start = Math.Max(0, (int)from!.Value);
            int length = size is > 0 ? (int)size.Value : plan.SizeAt(plan.IndexAt(start));
            if (length < 1) length = Math.Max(1, plan.SizeAt(plan.IndexAt(start)));
            seg = new WatchSegment { Index = plan.IndexAt(start), From = start, Size = length };
        }
        else
        {
            seg = plan.SegmentAt(estimate.Pos);
        }

        int lead = current.PlanLead > 0 ? current.PlanLead : Math.Max(0, planLead);
        double sayAt = Math.Max(seg.From, seg.To - lead);
        bool due = estimate.Pos >= sayAt && estimate.Pos <= seg.To + 60;
        bool delivered = current.IsDelivered(seg.From);
        if (due && !delivered && !explicitSegment && estimate.Known && !InQuietHours(now))
        {
            current.MarkDelivered(seg.From);
            current.LastDueAt = now;
            current.UpdatedAt = now;
            SaveStateNow();
        }

        string title = current.DisplayTitle;
        string stateText = sample.Ended ? "已播完" : sample.Paused ? "暂停" : "播放中";
        double age = Math.Max(0, (now - sample.At).TotalSeconds);
        string sourceLabel = givenInput == null
            ? "浏览器扩展上报"
            : WatchSource.IsLocalFile(givenInput) ? "手动指定的本地文件" : "手动指定的链接";

        var sb = new StringBuilder();
        sb.AppendLine($"[一起看] 《{title}》");
        sb.AppendLine($"[来源] {sourceLabel} · {stateText}");
        sb.AppendLine($"[位置] {WatchFormat.Mmss(estimate.Pos)} / {WatchFormat.Duration(sample.Dur ?? 0)}"
                      + $" · {estimate.ConfidenceText}（{estimate.WhyText}；{age:0.0} 秒前上报）");
        sb.AppendLine($"[当前段] {seg.Index + 1} 段 {WatchFormat.Mmss(seg.From)}-{WatchFormat.Mmss(seg.To)}"
                      + $"（剩 {Math.Max(0, seg.To - estimate.Pos):0} 秒，交付点 {WatchFormat.Mmss(sayAt)}）"
                      + (plan.TotalSegments(sample.Dur ?? 0) > 0
                          ? $" · 全片约 {plan.TotalSegments(sample.Dur ?? 0)} 段"
                          : ""));

        if (explicitSegment)
        {
            sb.AppendLine("[节奏] 补看模式：这一段不计入交付，不用按这个时间点说。");
        }
        else if (!estimate.Known)
        {
            sb.AppendLine($"[节奏] 位置不可信（{estimate.WhyText}），先不要按这个时间点说话；等新的上报。");
        }
        else if (due && !delivered)
        {
            sb.AppendLine("[节奏] 到交付点了：现在说一两句。短一点，不要复述剧情。");
        }
        else if (delivered)
        {
            sb.AppendLine("[节奏] 本段已经提醒过；不用重复，想补一句也可以。");
        }
        else
        {
            sb.AppendLine($"[节奏] 还没到交付点（{WatchFormat.Mmss(sayAt)} 再开口）；现在沉默就好。");
        }

        // 字幕用最近一次真实读数，不用外推位置：SRT 是完整时间轴，按外推会提前看到未来台词。
        string subtitle = explicitSegment
            ? TryBuildSubtitleSegment(current, seg)
            : TryBuildSubtitleWindow(current, sample.Pos, advanceCursor: estimate.Known);
        if (subtitle.Length > 0) sb.AppendLine(subtitle);

        // 字幕源与句数：决定要不要做画面 OCR（有字幕就别重复花钱）
        List<SubtitleCue>? cueList = TryGetCues(out string cueSource);
        int cueCount = cueList?.Count ?? 0;

        // 什么时候一定要 OCR：
        //   · 字幕少于 3 句（PPT 讲解常见）
        //   · 转写自己承认是纯 BGM（asrMuted）——这类片子字幕根本不可靠
        //   · 字幕来自 whisper（可能把音乐听成歌词，不能当作「有字幕了」）
        bool wantOcr = Configuration.UseOcr
                       && (!Configuration.OcrOnlyWhenNoSubtitle
                           || cueCount < 3
                           || asrMuted
                           || (cueSource ?? "").Contains("本地转写", StringComparison.Ordinal));

        // 纯 BGM 的片子：明确告诉模型别念"字幕"，以画面为准
        if (asrMuted && subtitle.Length > 0)
        {
            sb.AppendLine("[语音] 本片只有背景音乐（转写已停用），上面的字幕不可靠；以画面文字为准。");
        }

        int frameCount = cells ?? Configuration.Cells;
        bool wantFrames = frames ?? Configuration.AutoFrames;
        if (wantFrames && frameCount > 0)
        {
            string frameBlock = await TryBuildFrameBlockAsync(current, seg, frameCount, wantOcr).ConfigureAwait(false);
            if (frameBlock.Length > 0) sb.AppendLine(frameBlock);
        }

        // ── 事件时间轴：每段记一条（字幕摘句 + 画面文字），供「刚才讲了什么」复盘 ──
        try
        {
            string gist = "";

            if (cueList is { Count: > 0 })
            {
                List<string> near = cueList
                    .Where(c => c.To >= seg.From - 2 && c.From <= sample.Pos + 1)
                    .TakeLast(3)
                    .Select(c => (c.Text ?? "").Trim())
                    .Where(t => t.Length > 0)
                    .ToList();

                gist = string.Join("；", near);
            }

            string screen = "";
            string framePath = "";

            if (wantOcr)
            {
                LiveFrameStore? frameStore = UiLiveFrames;
                List<LiveFrame> recentFrames = frameStore?.Recent(current.Key, 1) ?? new List<LiveFrame>();

                if (recentFrames.Count > 0)
                {
                    framePath = recentFrames[0].Path;
                    screen = await MediaOcr.ReadAsync(
                        framePath,
                        Configuration.OcrLanguage,
                        Math.Clamp(Configuration.OcrMaxChars, 80, 2000)).ConfigureAwait(false);
                }
            }

            if (gist.Length > 0 || screen.Length > 0)
            {
                RecordWatchEvent(new WatchEvent
                {
                    At = now,
                    Title = title,
                    Pos = seg.From,
                    Segment = seg.Index + 1,
                    Source = cueSource ?? "",
                    Gist = Shorten(gist, 300),
                    Screen = screen,
                    Frame = framePath
                });
            }
        }
        catch
        {
            // 事件记录失败不影响工具返回
        }

        if (!string.IsNullOrWhiteSpace(say))
        {
            RecordSaid(say, estimate.Pos, now);
            sb.AppendLine($"[已记录] \"{say.Trim()}\" @ {WatchFormat.Mmss(estimate.Pos)}（只记录，不发送、不配音）");
        }

        if ((cueSource ?? "").Contains("本地转写", StringComparison.Ordinal))
            sb.AppendLine("[提示] 转写字幕可能混有 OP/ED 歌词与哼唱误转：唱歌段落的文字可信度低，别把它当剧情内容。");

        sb.AppendLine("[提示] 只说素材里有的：有字幕按字幕说，有画面按画面说（PPT/图表讲解类以画面文字为准，"
                      + "字幕为空不代表没内容）；两样都没有就按进度陪着聊，别编剧情。"
                      + "想结束就说一声，或调 watch_together(reset=true)。");
        // 字幕游标是这一场的指针，工具返回前存一次，重启后不会把同一段字幕再贴一遍。
        SaveStateNow();
        return sb.ToString().TrimEnd();
    }

    /// <summary>网页视频的画面块：用扩展 tabCapture 抓的实时帧（PPT/图表讲解类的唯一素材来源）。</summary>
    async Task<string> BuildLiveFrameBlockAsync(WatchSession value, int cells, bool includeOcr)
    {
        LiveFrameStore? store = UiLiveFrames;

        if (store == null || !Configuration.AutoLiveFrames)
            return "[画面] 网页视频实时抓帧已关闭（控制台「网页视频实时抓帧」）。";

        List<LiveFrame> frames = store.Recent(value.Key, Math.Clamp(cells, 1, 9));

        if (frames.Count == 0)
            return "[画面] 还没抓到画面。网页视频的帧只能由浏览器扩展截取：点扩展图标 →「开始抓画面」，"
                   + "之后每 " + Math.Max(5, Configuration.LiveFrameIntervalSeconds)
                   + " 秒或画面一变就自动拍一张；PPT 讲解类没有字幕时，等一两张再开口。";

        var sb = new StringBuilder();
        sb.AppendLine($"[画面·实时] 最近 {frames.Count} 张（翻页/切镜时抓的，可直接用视觉工具看）：");

        foreach (LiveFrame frame in frames)
            sb.AppendLine($"- {WatchFormat.Mmss(frame.Pos)}｜{Path.GetFileName(frame.Path)}｜"
                          + $"{(frame.Changed ? "画面刚变化" : "画面保持")}｜{frame.Bytes / 1024} KB");

        // 概览拼图：一张顶多张，省视觉调用（BiliScroll 那套「概览八格」的在线版）
        if (Configuration.LiveFrameSheet && frames.Count >= 3)
        {
            string? ffmpeg = WatchFrames.FindFfmpeg(Configuration.FfmpegPath);

            if (ffmpeg != null)
            {
                string? sheet = await LiveFrameSheet
                    .BuildAsync(ffmpeg, frames, Configuration.LiveFrameSheetCells, DestroyCancellationToken)
                    .ConfigureAwait(false);

                if (sheet != null)
                {
                    sb.AppendLine($"[画面·概览] {sheet}（3 列，从左上到右下依次："
                                  + string.Join("、", frames.Select(f => WatchFormat.Mmss(f.Pos)))
                                  + "）—— 先用这张扫一遍，只在需要读细节时再看单帧。");
                }
            }
        }

        // 画面上的文字：PPT 讲解类没有语音台词，这一行往往就是全部内容
        if (includeOcr)
        {
            string ocr = await MediaOcr.ReadAsync(
                frames[^1].Path,
                Configuration.OcrLanguage,
                Math.Clamp(Configuration.OcrMaxChars, 80, 2000)).ConfigureAwait(false);

            if (ocr.Length > 0)
                sb.AppendLine($"[画面·文字] {ocr}");
        }

        sb.AppendLine($"[画面·路径] 最近一张：{frames[^1].Path}");
        return sb.ToString().TrimEnd();
    }

    async Task<string> TryBuildFrameBlockAsync(WatchSession value, WatchSegment seg, int cells, bool includeOcr)
    {
        // 网页视频：走扩展实时抓帧（B 站/讲解类视频没有语音字幕，画面就是内容本体）
        if (!WatchSource.IsLocalFile(value.Input) || !WatchSubtitles.IsVideoFile(value.Input))
            return await BuildLiveFrameBlockAsync(value, cells, includeOcr).ConfigureAwait(false);

        string? ffmpeg = WatchFrames.FindFfmpeg(Configuration.FfmpegPath);
        if (ffmpeg == null)
            return "[画面] 没找到 ffmpeg，本次不抽帧；在控制台填 ffmpeg 路径，或把它加进 PATH 后就能抽。";

        int count = Math.Clamp(cells, 1, 9);
        List<SubtitleCue>? cues = TryGetCues(out _);
        var times = new List<double>();
        if (Configuration.FrameTimesFromSubtitle && cues != null && cues.Count > 0)
        {
            var inSeg = cues.Where(c => c.From >= seg.From && c.From < seg.To).ToList();
            if (inSeg.Count > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    int index = count == 1 ? 0 : (int)Math.Round((inSeg.Count - 1) * (double)i / (count - 1));
                    times.Add(inSeg[index].From);
                }
            }
        }

        string cacheDir = Path.Combine(dataDir, "frames", WatchFormat.SafeFileName(value.Key + "_" + seg.From));
        FrameResult result = await WatchFrames.ExtractAsync(
            ffmpeg, value.Input, cacheDir, seg.From, seg.To,
            count, Configuration.FrameMaxWidth, times, DestroyCancellationToken).ConfigureAwait(false);

        if (!result.Ok)
            return "[画面] 抽帧失败：" + result.Error;

        var sb = new StringBuilder();
        if (Configuration.ContactSheet && !string.IsNullOrWhiteSpace(result.ContactSheet))
        {
            sb.AppendLine($"[画面·概览] {result.ContactSheet}"
                          + $"（2×3 格，从上到下、从左到右依次是：{SheetTimesText(seg)}）");
        }
        sb.AppendLine($"[画面] 本段 {result.Shots.Count} 格（可用视觉工具看）：");
        foreach (FrameShot shot in result.Shots)
        {
            string line = $"- {WatchFormat.Mmss(shot.At)}｜{Path.GetFileName(shot.Path)}";
            if (cues != null && cues.Count > 0)
            {
                SubtitleCue? near = cues
                    .Where(c => c.To <= shot.At + 1.5 && c.From >= shot.At - 45)
                    .LastOrDefault();
                if (near != null)
                    line += $"｜字幕：{Shorten(near.Text, 60)}";
            }
            sb.AppendLine(line);
        }
        return sb.ToString().TrimEnd();
    }

    static string SheetTimesText(WatchSegment seg)
    {
        int span = Math.Max(1, seg.To - seg.From);
        var list = new List<string>();
        for (int i = 0; i < 6; i++)
            list.Add(WatchFormat.Mmss(seg.From + span * (i + 0.5) / 6.0));
        return string.Join("、", list);
    }

    static string Shorten(string? text, int max)
    {
        string value = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return value.Length <= max ? value : value[..max] + "…";
    }

    void RecordSaid(string text, double at, DateTime now)
    {
        if (session == null) return;
        session.Said.Add(new WatchSaidLine { At = Math.Max(0, at), Text = text.Trim(), SpokenAt = now });
        if (session.Said.Count > 60)
            session.Said = session.Said.Skip(session.Said.Count - 60).ToList();
        session.UpdatedAt = now;
        SaveStateNow();
    }

    // ─────────────────────────────────────────────────────────────
    // 状态与失败说明
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 片尾补交付：一场看完却一次都没到交付点（短视频 / 从中间开始看 / 素材通道没起来）时，
    /// 把最后的字幕尾巴与画面推给角色，让它能收个尾——而不是整场静默。
    /// </summary>
    internal async Task SendFinaleAsync(WatchSession value)
    {
        try
        {
            string note = await BuildFinaleBlockAsync(value).ConfigureAwait(false);
            await WakeAsync(note).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "一起看：片尾补交付失败");
        }
    }

    async Task<string> BuildFinaleBlockAsync(WatchSession value)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[一起看·片尾] 《{value.DisplayTitle}》播完了（{WatchFormat.Mmss(value.Duration)}），"
                      + "这一场还没到段交付点，我把最后的素材补给你，跟主人收个尾。");

        try
        {
            List<SubtitleCue>? cues = TryGetCues(out string source);

            if (cues is { Count: > 0 })
            {
                List<string> tail = cues
                    .Where(c => c.From <= value.MaxPos + 5)
                    .TakeLast(12)
                    .Select(c => (c.Text ?? "").Trim())
                    .Where(t => t.Length > 0)
                    .ToList();

                if (tail.Count > 0)
                    sb.AppendLine($"[字幕·尾声]（{source}）{Shorten(string.Join("；", tail), 600)}");
            }
        }
        catch
        {
            // 字幕拿不到就算了
        }

        LiveFrameStore? store = UiLiveFrames;
        List<LiveFrame> frames = store?.Recent(value.Key, 3) ?? new List<LiveFrame>();

        if (frames.Count > 0)
        {
            string ocr = "";

            if (Configuration.UseOcr)
            {
                try
                {
                    ocr = await MediaOcr.ReadAsync(
                        frames[^1].Path,
                        Configuration.OcrLanguage,
                        Math.Clamp(Configuration.OcrMaxChars, 80, 2000)).ConfigureAwait(false);
                }
                catch
                {
                    // OCR 失败不影响收尾
                }
            }

            sb.AppendLine("[画面·最后几张] " + string.Join("、",
                frames.Select(f => $"{WatchFormat.Mmss(f.Pos)} {Path.GetFileName(f.Path)}")));

            if (ocr.Length > 0) sb.AppendLine("[画面·文字] " + ocr);
            sb.AppendLine("[画面·路径] " + frames[^1].Path);
        }

        sb.AppendLine("跟主人说两句收尾：看过什么、哪段最有意思；没看到的部分别编。");
        return sb.ToString().TrimEnd();
    }

    string BuildStatusText()
    {
        BridgeStatus? st = bridge?.Snapshot();
        var sb = new StringBuilder();
        sb.AppendLine("[陪看状态]");
        sb.AppendLine($"桥：{(st?.Running == true ? "运行中" : "未运行")} · 127.0.0.1:{st?.Port ?? Configuration.Port}"
                      + $" · 累计 {(st?.PositionCount ?? 0)} 条上报");
        WatchSample? desktop = desktopMedia?.Latest;
        if (!Configuration.EnableDesktopMedia)
            sb.AppendLine("桌面媒体：已关闭");
        else if (desktop != null)
            sb.AppendLine($"桌面媒体：{desktop.Title} · {WatchFormat.Mmss(desktop.Pos)}"
                          + $"{(desktop.Paused ? " · 暂停" : "")}"
                          + (string.IsNullOrWhiteSpace(desktop.AppId) ? "" : $"（{desktop.AppId}）"));
        else
            sb.AppendLine("桌面媒体：等待播放器接入 Windows 媒体控制（SMTC）"
                          + (string.IsNullOrWhiteSpace(desktopMedia?.LastError) ? "" : $"；{desktopMedia!.LastError}"));
        if (!string.IsNullOrWhiteSpace(st?.Error))
            sb.AppendLine($"桥错误：{st.Error}");
        sb.AppendLine(HelloLine(st));

        if (st?.LastAt != null)
        {
            string state = st.Ended ? "已播完" : st.Paused ? "暂停" : "播放中";
            sb.AppendLine($"最近上报：{st.AgeSeconds:0.0} 秒前 · 《{st.Title}》 · "
                          + $"{WatchFormat.Mmss(st.Pos ?? 0)} / {WatchFormat.Duration(st.Dur ?? 0)} · {state}");
            if (!string.IsNullOrWhiteSpace(st.Caption))
                sb.AppendLine($"页面字幕：{Shorten(st.Caption, 80)}");
        }
        else
        {
            sb.AppendLine("最近上报：从没收到过");
        }

        if (!string.IsNullOrWhiteSpace(st?.PendingCommand))
            sb.AppendLine($"待执行命令：{st.PendingCommand}（下一次上报时执行）");

        // 素材通道状态（字幕 / 画面）
        PlatformSubtitle? platform = UiPlatformSubtitle;
        string sessionUrl = session?.Url ?? "";

        if (platform != null)
        {
            sb.AppendLine($"平台字幕：{platform.Label} · {platform.Cues.Count} 句（{platform.Bvid}）");
        }
        else if (!Configuration.EnableBiliSubtitle)
        {
            sb.AppendLine("平台字幕：已关闭");
        }
        else if (sessionUrl.Contains("bilibili.com", StringComparison.OrdinalIgnoreCase)
                 && BiliSubtitleClient.ExtractBvid(sessionUrl) == null)
        {
            // B 站地址但拿不到 BV 号：多半是扩展把首页/预览视频当成了当前会话
            sb.AppendLine("平台字幕：⚠ 上报地址不是视频页（无 BV 号）——扩展抓到的可能是首页/预览 video；"
                          + "在你看视频的标签页里播放一次，或确认扩展装在当前浏览器");
        }
        else if (platformMissKey.Length > 0 && platformMissKey == (BiliSubtitleClient.ExtractBvid(sessionUrl) ?? "@"))
        {
            sb.AppendLine($"平台字幕：⚠ B 站接口查无此视频（{platformMissAt:HH:mm:ss} 查过，-404：下架/私密/课程·番剧页）——"
                          + "改用画面与语音转写；页面字幕仍可用");
        }
        else
        {
            sb.AppendLine("平台字幕：等待拉取（B 站视频自动拉）");
        }

        sb.AppendLine($"抓帧预设：{CurrentFramePreset}（每 {Math.Max(5, Configuration.LiveFrameIntervalSeconds)} 秒 / 阈值 {Configuration.LiveFrameChangeThreshold:0.#} / 宽 {Configuration.LiveFrameMaxWidth}）");
        sb.AppendLine($"页面字幕：{(Configuration.AutoPageCaption ? "自动读（任意站点）" : "关")}"
                      + (string.IsNullOrWhiteSpace(Configuration.CaptionSelector) ? "" : $" · 选择器 {Configuration.CaptionSelector}"));
        sb.AppendLine($"语音转写：{(Configuration.EnableAsr ? "开" : "关")} · {UiAsrSummary}");
        sb.AppendLine($"视频下载：{(Configuration.EnableDownload ? "允许" : "关闭")} · {(string.IsNullOrWhiteSpace(Configuration.DownloadFolder) ? "默认目录" : Configuration.DownloadFolder)}");

        sb.AppendLine($"画面文字 OCR：{(Configuration.UseOcr ? "开" : "关")}"
                      + (Configuration.UseOcr ? $"（{Configuration.OcrLanguage}，{(Configuration.OcrOnlyWhenNoSubtitle ? "无字幕时才读" : "每次都读")}）" : ""));
        sb.AppendLine($"陪看事件：{UiEventCount} 条（保留 {Math.Max(20, Configuration.KeepEvents)}）");

        LiveFrameStore? store = UiLiveFrames;
        if (store == null || !Configuration.AutoLiveFrames)
            sb.AppendLine("实时画面：已关闭");
        else
            sb.AppendLine($"实时画面：已收 {store.AcceptedCount} 帧"
                          + (store.LastAt is DateTime at ? $" · 最近 {at:HH:mm:ss}" : "")
                          + (string.IsNullOrWhiteSpace(st?.LastFrameNote) ? "" : $" · {st.LastFrameNote}")
                          + (string.IsNullOrWhiteSpace(store.LastError) ? "" : $" · 错误：{store.LastError}"));

        if (lastEstimate != null)
            sb.AppendLine($"位置可信度：{lastEstimate.ConfidenceText}（{lastEstimate.WhyText}）");

        sb.AppendLine($"段表：{plan.Describe()}；预取余量 {Math.Max(0, Configuration.LookaheadSeconds)} 秒");

        if (session != null)
        {
            sb.AppendLine($"会话：{session.Key} · 《{session.DisplayTitle}》 · 已交付 {session.Delivered.Count} 段"
                          + $" · 已记台词 {session.Said.Count} 条");
            if (lastEstimate?.Known == true)
            {
                WatchSegment seg = plan.SegmentAt(lastEstimate.Pos);
                int lead = session.PlanLead > 0 ? session.PlanLead : planLead;
                double sayAt = Math.Max(seg.From, seg.To - lead);
                string timing = lastEstimate.Pos >= sayAt ? "已到交付点" : $"还差 {Math.Max(0, sayAt - lastEstimate.Pos):0} 秒";
                sb.AppendLine($"当前段：{seg.Index + 1} 段 {WatchFormat.Mmss(seg.From)}-{WatchFormat.Mmss(seg.To)}"
                              + $"（{timing}；交付点 {WatchFormat.Mmss(sayAt)}）");
                var window = plan.WindowFor(lastEstimate.Pos, Math.Max(0, Configuration.LookaheadSeconds));
                if (window.Count > 0)
                    sb.AppendLine($"预取窗口：{string.Join(" ", window.Select(x => $"{WatchFormat.Mmss(x.From)}+{x.Size}s"))}");
            }
        }
        else
        {
            sb.AppendLine("会话：还没建立（调用 watch_together 或等扩展上报）");
        }

        string subtitleSource = "";
        if (session != null) _ = TryGetCues(out subtitleSource);
        sb.AppendLine($"字幕：{(string.IsNullOrWhiteSpace(subtitleSource) ? "无（网页视频只陪聊进度）" : subtitleSource)}"
                      + $" · 随机搭话：{(Configuration.EnableRandomChat ? $"开（今日 {runtime.RandomCount}/{Math.Max(1, Configuration.RandomChatDailyLimit)}）" : "关")}");
        sb.AppendLine($"静默时段：{(InQuietHours(DateTime.Now) ? "现在处于静默" : Configuration.QuietHours?.Length > 0 ? "未到静默" : "未设置")}"
                      + $" · 主动唤醒：{(Configuration.WakeOnDue ? $"开（今日 {runtime.WakeCount}/{Math.Max(1, Configuration.WakeDailyLimit)}）" : "关")}");
        sb.AppendLine("诊断：" + BuildDiagnosis(st));
        return sb.ToString().TrimEnd();
    }

    static string HelloLine(BridgeStatus? st)
    {
        if (st?.HelloAt == null)
            return "扩展心跳：从没收到（扩展可能没装、没启用，或装到了别的浏览器）";
        string age = st.HelloAgeSeconds is double value
            ? value < 60 ? $"{value:0} 秒前" : value < 3600 ? $"{value / 60:0} 分钟前" : $"{value / 3600:0.0} 小时前"
            : "未知";
        string version = string.IsNullOrWhiteSpace(st.HelloVersion) ? "" : $"（v{st.HelloVersion}）";
        return $"扩展心跳：{age}{version}";
    }

    string BuildDiagnosis(BridgeStatus? st)
    {
        if (st == null || !st.Running)
            return "位置桥没在运行：检查端口是否被占用，或到控制台手动启动。";
        if (st.PositionCount == 0)
            return "桥在跑，但没收到任何位置：扩展没连上，或当前页面没有正在播放的视频。";
        if (st.LastAt == null)
            return "还没有可用的位置读数。";
        if (st.AgeSeconds is double age && age > Math.Max(5, Configuration.BridgeStaleSeconds))
            return $"上报断流 {age:0} 秒：主人可能切走了标签页/浏览器；位置不可信，不要按旧时间说话。"
                   + "（扩展只在可见标签页上报，这是正常行为）";
        if (st.Ended) return "视频已播完。";
        if (st.Paused) return "视频暂停中；恢复播放后按新位置继续。";
        if (session == null) return "有位置但还没建立会话：调用 watch_together 即可。";
        return "正常：位置、会话、段表都在。";
    }

    string BuildNoSampleText(string? input)
    {
        BridgeStatus? st = bridge?.Snapshot();
        var sb = new StringBuilder();
        sb.AppendLine("[一起看] 现在读不到播放位置。");
        sb.AppendLine($"桥：{(st?.Running == true ? "运行中" : "未运行")} · 127.0.0.1:{st?.Port ?? Configuration.Port}"
                      + (string.IsNullOrWhiteSpace(st?.Error) ? "" : $"（{st.Error}）"));
        sb.AppendLine(HelloLine(st));
        WatchSample? desktop = desktopMedia?.Latest;
        if (!Configuration.EnableDesktopMedia)
            sb.AppendLine("桌面媒体：已关闭");
        else if (desktop != null)
            sb.AppendLine($"桌面媒体：{desktop.Title} · {WatchFormat.Mmss(desktop.Pos)}");
        else
            sb.AppendLine("桌面媒体：等待播放器接入 Windows 媒体控制（SMTC）"
                          + (string.IsNullOrWhiteSpace(desktopMedia?.LastError) ? "" : $"；{desktopMedia!.LastError}"));
        if (st?.LastAt != null)
            sb.AppendLine($"上次读数：{st.AgeSeconds:0.0} 秒前 · 《{st.Title}》 · {WatchFormat.Mmss(st.Pos ?? 0)}");
        if (!string.IsNullOrWhiteSpace(input))
            sb.AppendLine($"指定的源现在用不了：{input}");
        sb.AppendLine("常见原因：1) 扩展没装/没启用；2) 播放视频的标签页不是当前可见的那一个；3) 视频还没开始播放；4) 桌面播放器没接入 SMTC（可在播放器设置里打开「Windows 媒体控制」）。");
        sb.AppendLine("**不要向主人要链接** —— 让他把视频点开、播起来，然后直接再调一次 watch_together（参数不用改）。");
        sb.AppendLine("也可以带 waitForVideo=true，我会等 20 秒。");
        return sb.ToString().TrimEnd();
    }

    string BuildCursorOnlyText(WatchSample sample)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[一起看] 手动位置 {WatchFormat.Mmss(sample.Pos)}"
                      + (sample.Dur is > 0 ? $" / {WatchFormat.Duration(sample.Dur.Value)}" : ""));
        sb.AppendLine("没有片源信息，不能建立会话：让主人先把视频播起来（桥会自动认片），"
                      + "或用 input 给本地路径/链接。");
        return sb.ToString().TrimEnd();
    }
}
