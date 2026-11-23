using System;
using System.Globalization;
using System.Threading.Tasks;
using Alife.Framework;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;

namespace presents.WatchTogether;

/// <summary>
/// 视频陪看控制台：状态概览、快捷操作、分组配置（标签页）、扩展安装与说明。
/// </summary>
public class WatchTogetherUI : ModuleUIBase<WatchTogetherModule, WatchTogetherConfig>
{
    string message = "";
    bool busy;
    string activeTab = "桥接";

    // 抓帧预设：下拉暂存 + 套用结果提示
    string? pendingPreset;
    string presetNote = "";

    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        base.BuildRenderTree(b);
        int seq = 0;
        b.AddMarkupContent(seq++, "<style>" + Css + "</style>");

        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "wt-root");

        bool bridgeOn = Module?.UiBridgeRunning ?? false;

        // ── 顶部 ──────────────────────────────────────────────
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "wt-hero");
        b.AddMarkupContent(seq++, "<div class=\"wt-hero-main\">"
            + "<div class=\"wt-title\">视频陪看 <span class=\"wt-subtitle\">浏览器进度桥 · 分段陪看</span></div>"
            + "<div class=\"wt-hero-hint\">接住浏览器或桌面播放器里正在播放的视频，按真实进度分段陪你聊：到点提醒、暂停/拖动/换片/播完都有提示。</div>"
            + "</div>");
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "wt-badges");
        Pill(b, ref seq, bridgeOn ? "位置桥运行中" : "位置桥未运行", bridgeOn);
        Pill(b, ref seq, $"127.0.0.1:{Module?.UiBridgePort ?? 9377}", bridgeOn);
        string desktopText = Module?.UiDesktopMediaText ?? "桌面媒体：等待播放";
        Pill(b, ref seq, desktopText, desktopText.Contains("已接入", StringComparison.Ordinal));
        b.CloseElement();
        b.CloseElement();

        if (!string.IsNullOrWhiteSpace(message))
            b.AddMarkupContent(seq++, $"<div class=\"wt-msg\">{E(message)}</div>");

        // ── 状态小块 ──────────────────────────────────────────
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "wt-tiles");
        Tile(b, ref seq, "当前视频", Module?.UiNowTitle ?? "还没接到视频", "浏览器扩展 / 桌面媒体");
        Tile(b, ref seq, "播放进度", Module?.UiNowProgress ?? "—", Module?.UiNowState ?? "—");
        Tile(b, ref seq, "当前段", Module?.UiSegmentText ?? "—", "按段表实时计算");
        Tile(b, ref seq, "媒体源",
            desktopText.Contains("已接入", StringComparison.Ordinal) ? "桌面播放器" : bridgeOn ? "浏览器扩展" : "未连接",
            desktopText.Contains("已接入", StringComparison.Ordinal) ? desktopText : $"端口 {Module?.UiBridgePort ?? 9377}");
        b.CloseElement();

        // ── 快捷操作 ──────────────────────────────────────────
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "wt-actions");
        Button(b, ref seq, bridgeOn ? "停止位置桥" : "启动位置桥", () =>
        {
            message = Module?.UiToggleBridge() ?? "模块未激活";
            StateHasChanged();
        }, busy, primary: true);
        Button(b, ref seq, "结束这一场", () =>
        {
            message = Module?.UiResetSession() ?? "模块未激活";
            StateHasChanged();
        }, busy);
        Button(b, ref seq, "刷新", () =>
        {
            message = "";
            StateHasChanged();
        }, busy);
        b.CloseElement();

        // ── 详细状态 ──────────────────────────────────────────
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "wt-card");
        b.OpenElement(seq++, "details");
        b.AddAttribute(seq++, "class", "wt-details");
        b.OpenElement(seq++, "summary");
        b.AddContent(seq++, "详细状态（位置、段表、字幕源、待执行命令）");
        b.CloseElement();
        b.OpenElement(seq++, "pre");
        b.AddAttribute(seq++, "class", "wt-pre");
        b.AddContent(seq++, Module?.UiStatusText() ?? "模块未激活");
        b.CloseElement();
        b.CloseElement();
        b.CloseElement();

        // ── 配置 ──────────────────────────────────────────────
        WatchTogetherConfig? cfg = Module?.Configuration;
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "wt-card");
        b.AddMarkupContent(seq++, "<div class=\"wt-card-head\"><div class=\"wt-card-title\">配置</div>"
            + "<div class=\"wt-card-hint\">改动即时生效；点「保存配置」写盘，端口改动需重启位置桥</div></div>");

        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "wt-tabs");
        Tab(b, ref seq, "桥接", "开关与桥接");
        Tab(b, ref seq, "节奏", "分段节奏");
        Tab(b, ref seq, "提醒", "提醒与说话");
        Tab(b, ref seq, "字幕", "字幕与素材");
        Tab(b, ref seq, "会话", "会话与回注");
        b.CloseElement();

        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "wt-tab-body");

        if (activeTab == "桥接")
        {
            b.OpenElement(seq++, "div");
            b.AddAttribute(seq++, "class", "wt-cfg-grid");
            ConfigBool(b, ref seq, "启用视频陪看", "关闭后不监听端口，所有工具返回「已停用」。",
                cfg?.Enable ?? true, v => { if (Module != null) Module.Configuration.Enable = v; });
            ConfigBool(b, ref seq, "自动启动位置桥", "插件唤醒时自动监听 127.0.0.1；关掉后可手动启动。",
                cfg?.AutoStartBridge ?? true, v => { if (Module != null) Module.Configuration.AutoStartBridge = v; });
            ConfigBool(b, ref seq, "读取桌面播放器（SMTC）", "读取 Windows 媒体会话；Media Player、PotPlayer、VLC、QQ/微信内置播放器（需应用接入 SMTC）都能试。浏览器由扩展上报，这里主动跳过。",
                cfg?.EnableDesktopMedia ?? true, v => { if (Module != null) Module.Configuration.EnableDesktopMedia = v; });
            ConfigInt(b, ref seq, "监听端口", "默认 9377；改完要重启位置桥，扩展 popup 里的端口也要改成一样。",
                cfg?.Port ?? 9377, 1, 65535, v => { if (Module != null) Module.Configuration.Port = v; });
            ConfigInt(b, ref seq, "断流判定（秒）", "超过这么久没收到新位置就不再外推，默认 20。",
                cfg?.BridgeStaleSeconds ?? 20, 5, 600, v => { if (Module != null) Module.Configuration.BridgeStaleSeconds = v; });
            ConfigInt(b, ref seq, "过期读数认片时长（秒）", "切到聊天窗口时扩展暂停上报，这段时间仍用旧读数认片名，默认 120。",
                cfg?.IdentifyStaleSeconds ?? 120, 10, 3600, v => { if (Module != null) Module.Configuration.IdentifyStaleSeconds = v; });
            ConfigInt(b, ref seq, "等视频出现（秒）", "没有可读位置时最多等这么久，默认 8；0=不等。",
                cfg?.AutoWaitSeconds ?? 8, 0, 60, v => { if (Module != null) Module.Configuration.AutoWaitSeconds = v; });
            b.CloseElement();
        }
        else if (activeTab == "节奏")
        {
            b.OpenElement(seq++, "div");
            b.AddAttribute(seq++, "class", "wt-cfg-grid");
            ConfigInt(b, ref seq, "稳定段长（秒）", "阶梯走完后的段长，默认 300（5 分钟）。",
                cfg?.SegmentSeconds ?? 300, 30, 3600, v => { if (Module != null) Module.Configuration.SegmentSeconds = v; });
            ConfigText(b, ref seq, "起步阶梯（秒）", "逗号分隔，默认 60,120,180,240；留空=全程等长。",
                cfg?.RampSeconds ?? "", false, v => { if (Module != null) Module.Configuration.RampSeconds = v; }, true);
            ConfigInt(b, ref seq, "交付提前量（秒）", "段末前多少秒提醒角色说话，默认 15。",
                cfg?.LeadSeconds ?? 15, 0, 120, v => { if (Module != null) Module.Configuration.LeadSeconds = v; });
            ConfigInt(b, ref seq, "预取余量（秒）", "默认 300；影响状态展示与素材预读范围。",
                cfg?.LookaheadSeconds ?? 300, 0, 1800, v => { if (Module != null) Module.Configuration.LookaheadSeconds = v; });
            ConfigInt(b, ref seq, "每段画面格数", "本地视频每段抽几格，默认 5；0=不抽帧。",
                cfg?.Cells ?? 5, 0, 12, v => { if (Module != null) Module.Configuration.Cells = v; });
            ConfigInt(b, ref seq, "位置新鲜窗口（秒）", "上报在这个时间内直接用；超过则按时钟外推，默认 5。",
                cfg?.FreshSeconds ?? 5, 1, 120, v => { if (Module != null) Module.Configuration.FreshSeconds = v; });
            ConfigInt(b, ref seq, "外推上限（秒）", "读数旧了最多往前推这么久，超过就承认不知道，默认 60。",
                cfg?.MaxExtrapolateSeconds ?? 60, 0, 600, v => { if (Module != null) Module.Configuration.MaxExtrapolateSeconds = v; });
            b.CloseElement();
        }
        else if (activeTab == "提醒")
        {
            b.OpenElement(seq++, "div");
            b.AddAttribute(seq++, "class", "wt-cfg-grid");
            ConfigBool(b, ref seq, "到点提醒（Poke）", "每段快到交付点时推一条短提醒，默认开；不额外调模型。",
                cfg?.PokeOnDue ?? true, v => { if (Module != null) Module.Configuration.PokeOnDue = v; });
            ConfigBool(b, ref seq, "到点主动唤醒", "到交付点主动叫醒模型说一句，默认关；受间隔与每日上限约束。",
                cfg?.WakeOnDue ?? false, v => { if (Module != null) Module.Configuration.WakeOnDue = v; });
            ConfigBool(b, ref seq, "随机搭话", "陪看时按概率自己开口，默认关；每命中一次=一轮模型请求。",
                cfg?.EnableRandomChat ?? false, v => { if (Module != null) Module.Configuration.EnableRandomChat = v; });
            ConfigInt(b, ref seq, "随机搭话间隔（分钟）", "两次至少隔多久，默认 10；到了才掷骰子。",
                cfg?.RandomChatEveryMinutes ?? 10, 1, 180, v => { if (Module != null) Module.Configuration.RandomChatEveryMinutes = v; });
            ConfigDouble(b, ref seq, "随机搭话概率", "到间隔后掷骰子的命中概率，默认 0.35。",
                cfg?.RandomChatChance ?? 0.35, 0, 1, 0.05, v => { if (Module != null) Module.Configuration.RandomChatChance = v; });
            ConfigInt(b, ref seq, "随机搭话每日上限", "默认 24；到上限只保留到点提醒。",
                cfg?.RandomChatDailyLimit ?? 24, 0, 200, v => { if (Module != null) Module.Configuration.RandomChatDailyLimit = v; });
            ConfigInt(b, ref seq, "主动唤醒最小间隔（分钟）", "默认 3。",
                cfg?.WakeMinIntervalMinutes ?? 3, 1, 60, v => { if (Module != null) Module.Configuration.WakeMinIntervalMinutes = v; });
            ConfigInt(b, ref seq, "主动唤醒每日上限", "默认 12；到上限只 Poke 不唤醒。",
                cfg?.WakeDailyLimit ?? 12, 0, 200, v => { if (Module != null) Module.Configuration.WakeDailyLimit = v; });
            ConfigText(b, ref seq, "静默时段", "如 23:00-08:00；这段时间不 Poke、不唤醒；留空=不静默。",
                cfg?.QuietHours ?? "", false, v => { if (Module != null) Module.Configuration.QuietHours = v; }, true);
            ConfigBool(b, ref seq, "换片时提醒", "检测到换了另一支视频时推一条提示，默认开。",
                cfg?.PokeOnSwitch ?? true, v => { if (Module != null) Module.Configuration.PokeOnSwitch = v; });
            ConfigBool(b, ref seq, "播完时提醒", "检测到视频播完时推一条提示，默认开。",
                cfg?.PokeOnEnded ?? true, v => { if (Module != null) Module.Configuration.PokeOnEnded = v; });
            ConfigInt(b, ref seq, "拖动判定阈值（秒）", "偏离按时钟推算的位置超过这么多算拖进度，默认 60。",
                cfg?.JumpSeconds ?? 60, 5, 600, v => { if (Module != null) Module.Configuration.JumpSeconds = v; });
            ConfigInt(b, ref seq, "拖动提醒冷却（分钟）", "两次拖动提醒至少隔这么久，默认 3。",
                cfg?.JumpCooldownMinutes ?? 3, 1, 60, v => { if (Module != null) Module.Configuration.JumpCooldownMinutes = v; });
            ConfigBool(b, ref seq, "暂停时不提醒", "默认开；暂停期间不发到点提醒，恢复后按新位置继续。",
                cfg?.SuspendWhenPaused ?? true, v => { if (Module != null) Module.Configuration.SuspendWhenPaused = v; });
            b.CloseElement();
        }
        else if (activeTab == "字幕")
        {
            b.OpenElement(seq++, "div");
            b.AddAttribute(seq++, "class", "wt-cfg-grid");
            ConfigText(b, ref seq, "字幕扩展名", "逗号分隔，默认 srt,vtt；本地视频找同名字幕。",
                cfg?.SubtitleExtensions ?? "", false, v => { if (Module != null) Module.Configuration.SubtitleExtensions = v; });
            ConfigText(b, ref seq, "外部字幕文件", "可选：按视频时间轴的 .srt/.vtt 路径；文件一变就重读。",
                cfg?.ExternalSubtitleFile ?? "", false, v => { if (Module != null) Module.Configuration.ExternalSubtitleFile = v; }, true);
            ConfigInt(b, ref seq, "外部字幕偏移（秒）", "外部字幕比视频晚/早多少秒，默认 0。",
                cfg?.ExternalSubtitleOffsetSeconds ?? 0, -120, 120, v => { if (Module != null) Module.Configuration.ExternalSubtitleOffsetSeconds = v; });
            ConfigInt(b, ref seq, "字幕窗口（秒）", "工具返回时只给当前位置往前这么多秒，默认 120。",
                cfg?.SubtitleWindowSeconds ?? 120, 10, 600, v => { if (Module != null) Module.Configuration.SubtitleWindowSeconds = v; });
            ConfigInt(b, ref seq, "字幕往后预告（秒）", "0=不剧透，只给看过的；大于 0 给一点未来字幕。",
                cfg?.SubtitleAheadSeconds ?? 0, 0, 120, v => { if (Module != null) Module.Configuration.SubtitleAheadSeconds = v; });
            ConfigInt(b, ref seq, "字幕窗口最多几句", "默认 40；超出只给最近的。",
                cfg?.SubtitleMaxLines ?? 40, 1, 200, v => { if (Module != null) Module.Configuration.SubtitleMaxLines = v; });
            ConfigInt(b, ref seq, "到点摘要句数", "提醒里附最近几句字幕，默认 3；0=不附。",
                cfg?.SubtitleDigestLines ?? 3, 0, 10, v => { if (Module != null) Module.Configuration.SubtitleDigestLines = v; });
            ConfigInt(b, ref seq, "实时上下文刷新（秒）", "播放中每隔多少秒把当前进度+最近几句字幕静默刷给 AI，让它随时开口都知道看到哪；0=关，默认 30。",
                cfg?.LiveContextSeconds ?? 30, 0, 300, v => { if (Module != null) Module.Configuration.LiveContextSeconds = v; });
            ConfigText(b, ref seq, "素材目录", "额外的字幕/视频查找目录，可留空。",
                cfg?.MaterialDirectory ?? "", false, v => { if (Module != null) Module.Configuration.MaterialDirectory = v; }, true);
            ConfigInt(b, ref seq, "每段字幕字数上限", "默认 1200；太多会占 token。",
                cfg?.MaxSubtitleChars ?? 1200, 100, 5000, v => { if (Module != null) Module.Configuration.MaxSubtitleChars = v; });
            ConfigBool(b, ref seq, "按字幕时间点抽帧", "有字幕时帧和台词更容易对上，默认开。",
                cfg?.FrameTimesFromSubtitle ?? true, v => { if (Module != null) Module.Configuration.FrameTimesFromSubtitle = v; });
            ConfigBool(b, ref seq, "自动抽帧", "本地视频自动抽当前段画面；需要 ffmpeg，默认开。",
                cfg?.AutoFrames ?? true, v => { if (Module != null) Module.Configuration.AutoFrames = v; });
            ConfigBool(b, ref seq, "生成概览拼图", "抽帧后拼一张 2×3 概览图，默认开；需要 ffmpeg tile。",
                cfg?.ContactSheet ?? true, v => { if (Module != null) Module.Configuration.ContactSheet = v; });
            ConfigText(b, ref seq, "ffmpeg 路径", "留空=自动找；可填 ffmpeg.exe 或解压目录。",
                cfg?.FfmpegPath ?? "", false, v => { if (Module != null) Module.Configuration.FfmpegPath = v; }, true);
            ConfigInt(b, ref seq, "抽帧宽度", "缩略图宽度上限，默认 640；比它窄不放大。",
                cfg?.FrameMaxWidth ?? 640, 160, 1920, v => { if (Module != null) Module.Configuration.FrameMaxWidth = v; });

            AddLabel(b, ref seq, "B 站平台字幕（讲解类视频的台词来源）");
            ConfigBool(b, ref seq, "读取 B 站字幕", "开：自动走官方接口取 CC / AI 字幕（比读页面 DOM 稳得多）；关：只认外部文件 / 页面字幕。",
                cfg?.EnableBiliSubtitle ?? true, v => { if (Module != null) Module.Configuration.EnableBiliSubtitle = v; });
            ConfigText(b, ref seq, "B 站 SESSDATA（可选）", "AI 字幕通常要登录态；留空会自动借用 presents.BiliScroll 的 credentials.json。",
                cfg?.BiliSessdata ?? "", false, v => { if (Module != null) Module.Configuration.BiliSessdata = v; }, true);
            ConfigInt(b, ref seq, "平台字幕缓存（分钟）", "同一支视频字幕拉一次就够，默认缓存 30 分钟。",
                cfg?.PlatformSubtitleCacheMinutes ?? 30, 5, 240, v => { if (Module != null) Module.Configuration.PlatformSubtitleCacheMinutes = v; });

            AddLabel(b, ref seq, "实时画面（网页视频）");

            // 抓帧预设：一键套用整套参数（对应 presents.Video 的内容场景经验）
            b.OpenElement(seq++, "div");
            b.AddAttribute(seq++, "class", "wt-field wt-wide");
            AddLabel(b, ref seq, $"抓帧预设（当前：{Module?.CurrentFramePreset ?? "自定义"}）");

            b.OpenElement(seq++, "div");
            b.AddAttribute(seq++, "style", "display:flex;gap:8px;align-items:center;");

            b.OpenElement(seq++, "select");
            b.AddAttribute(seq++, "class", "wt-input");
            b.AddAttribute(seq++, "id", "wt-frame-preset");
            b.AddAttribute(seq++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
            {
                pendingPreset = e.Value as string;
            }));

            foreach (WatchFramePresets.Preset item in WatchFramePresets.All)
            {
                b.OpenElement(seq++, "option");
                b.AddAttribute(seq++, "value", item.Name);
                if (item.Name == (Module?.CurrentFramePreset ?? ""))
                    b.AddAttribute(seq++, "selected", true);
                b.AddContent(seq++, item.Name);
                b.CloseElement();
            }

            b.CloseElement();

            Button(b, ref seq, "套用", () =>
            {
                if (Module == null) return;
                string? picked = pendingPreset ?? Module.CurrentFramePreset;
                presetNote = Module.ApplyFramePreset(picked);
                _ = InvokeAsync(StateHasChanged);
            });

            b.CloseElement();

            AddHint(b, ref seq, "讲解/PPT：密抓+阈值低；论文/图表：最清晰；快切资讯：防刷屏；通用：默认；影视省流：最省。套用后下面五项仍可单项微调。");

            if (presetNote.Length > 0)
            {
                b.OpenElement(seq++, "div");
                b.AddAttribute(seq++, "class", "wt-hint");
                b.AddAttribute(seq++, "style", "color:#5b8def;margin-top:6px;");
                b.AddContent(seq++, presetNote);
                b.CloseElement();
            }

            b.CloseElement();

            ConfigBool(b, ref seq, "网页视频实时抓帧", "开：扩展用 tabCapture 抓当前帧，画面变了才上传。PPT / 图表讲解类全靠它。",
                cfg?.AutoLiveFrames ?? true, v => { if (Module != null) Module.Configuration.AutoLiveFrames = v; });
            ConfigInt(b, ref seq, "抓帧最小间隔（秒）", "两次抓帧至少隔多久，默认 20；翻页会立刻触发不受它限制。",
                cfg?.LiveFrameIntervalSeconds ?? 20, 5, 300, v => { if (Module != null) Module.Configuration.LiveFrameIntervalSeconds = v; });
            ConfigDouble(b, ref seq, "画面变化阈值（0–100）", "缩成 96×54 灰度比对，超过才上传；默认 6，音乐可视化类可调到 15。",
                cfg?.LiveFrameChangeThreshold ?? 6, 0, 100, 1, v => { if (Module != null) Module.Configuration.LiveFrameChangeThreshold = v; });
            ConfigInt(b, ref seq, "实时帧保留张数", "每场会话留最近几张，默认 12；旧的自动删。",
                cfg?.LiveFrameKeep ?? 12, 3, 60, v => { if (Module != null) Module.Configuration.LiveFrameKeep = v; });
            ConfigInt(b, ref seq, "实时帧宽度", "上传帧最大宽度，默认 1280（PPT 文字要看清就别降）。",
                cfg?.LiveFrameMaxWidth ?? 1280, 480, 1920, v => { if (Module != null) Module.Configuration.LiveFrameMaxWidth = v; });
            ConfigInt(b, ref seq, "实时帧质量（55–90）", "JPEG 编码质量，默认 72。",
                cfg?.LiveFrameQuality ?? 72, 55, 90, v => { if (Module != null) Module.Configuration.LiveFrameQuality = v; });

            ConfigBool(b, ref seq, "画面概览拼图", "把最近几帧拼成一张（3 列），模型看一张就够，省视觉调用。少于 3 帧不拼。",
                cfg?.LiveFrameSheet ?? true, v => { if (Module != null) Module.Configuration.LiveFrameSheet = v; });
            ConfigInt(b, ref seq, "概览格数", "4 / 6 / 9；格数越多单格越小，PPT 要看清选 4。",
                cfg?.LiveFrameSheetCells ?? 6, 3, 9, v => { if (Module != null) Module.Configuration.LiveFrameSheetCells = v; });

            AddLabel(b, ref seq, "任意站点字幕（非 B 站）");
            ConfigBool(b, ref seq, "自动读取页面字幕",
                "开：让扩展自动读页面里的字幕元素（YouTube / 各类播放器 / 自制页面都认），不读正文与评论。",
                cfg?.AutoPageCaption ?? true, v => { if (Module != null) Module.Configuration.AutoPageCaption = v; });
            ConfigText(b, ref seq, "自定义字幕选择器（可选）",
                "站点类名很怪、自动认不出来时填 CSS 选择器（如 .my-sub-line），扩展会优先用它。",
                cfg?.CaptionSelector ?? "", false, v => { if (Module != null) Module.Configuration.CaptionSelector = v; }, true);

            AddLabel(b, ref seq, "画面文字与事件时间轴");
            ConfigBool(b, ref seq, "画面文字 OCR",
                "用 Windows 自带 OCR 读画面文字；PPT/图表讲解没有语音台词时，这是唯一的素材来源。零依赖、不出网。",
                cfg?.UseOcr ?? true, v => { if (Module != null) Module.Configuration.UseOcr = v; });
            ConfigText(b, ref seq, "OCR 语言", "默认 zh-Hans；英文视频填 en-US。",
                cfg?.OcrLanguage ?? "zh-Hans", false, v => { if (Module != null) Module.Configuration.OcrLanguage = v; });
            ConfigInt(b, ref seq, "OCR 字数上限", "默认 600；PPT 一页通常 100–300 字。",
                cfg?.OcrMaxChars ?? 600, 80, 2000, v => { if (Module != null) Module.Configuration.OcrMaxChars = v; });
            ConfigBool(b, ref seq, "无字幕时才 OCR",
                "开（默认）：有足够字幕时不再 OCR，省时省 token；关=每次都给画面文字。",
                cfg?.OcrOnlyWhenNoSubtitle ?? true, v => { if (Module != null) Module.Configuration.OcrOnlyWhenNoSubtitle = v; });
            ConfigInt(b, ref seq, "保留事件条数", "默认 200；只存字幕摘句与 OCR 文本，不存像素。",
                cfg?.KeepEvents ?? 200, 20, 2000, v => { if (Module != null) Module.Configuration.KeepEvents = v; });

            AddLabel(b, ref seq, "视频下载与语音转写");
            ConfigBool(b, ref seq, "允许下载视频", "看 B 站视频时可让角色把片子下到本地，之后抽帧/转写/发人都用本地文件。",
                cfg?.EnableDownload ?? true, v => { if (Module != null) Module.Configuration.EnableDownload = v; });
            ConfigText(b, ref seq, "下载目录", @"默认 D:\AI-Cover\downloads。", 
                cfg?.DownloadFolder ?? "", false, v => { if (Module != null) Module.Configuration.DownloadFolder = v; }, true);
            ConfigBool(b, ref seq, "启用语音转写", "录标签页声音，每 20 秒切片给本机转写：优先 SenseVoice（框架本地），不可用/超时才回退 whisper。有语音没字幕的视频才有用；默认关。",
                cfg?.EnableAsr ?? false, v => { if (Module != null) Module.Configuration.EnableAsr = v; });
            ConfigInt(b, ref seq, "录音切片（秒）", "10-30，默认 10。越短字幕越跟得上画面；SenseVoice 走 CPU、不占显存。",
                cfg?.AsrChunkSeconds ?? 10, 10, 60, v => { if (Module != null) Module.Configuration.AsrChunkSeconds = v; });
            ConfigBool(b, ref seq, "VAD 人声检测",
                "开（默认）：whisper 兜底时先用 Silero VAD 把纯音乐/静音切掉；SenseVoice 自带 VAD，不受影响。",
                cfg?.AsrVad ?? true, v => { if (Module != null) Module.Configuration.AsrVad = v; });
            ConfigBool(b, ref seq, "繁体转写转简体",
                "开（默认）：whisper 兜底常输出繁体，检测到繁体特征字时用 Windows 原生映射转成简体。",
                cfg?.AsrSimplified ?? true, v => { if (Module != null) Module.Configuration.AsrSimplified = v; });
            ConfigBool(b, ref seq, "歌曲/音乐兜底",
                "开：SenseVoice 滤空但音频有能量时回退 whisper（关 VAD）转写歌词——看 MV/音乐视频时开。纯 BGM 的 PPT/讲课视频请保持关，否则伴奏会被编成歌词。",
                cfg?.AsrMusicFallback ?? false, v => { if (Module != null) Module.Configuration.AsrMusicFallback = v; });
            ConfigText(b, ref seq, "VAD 模型路径", @"留空自动找（推荐）；手动填 silero-*-ggml.bin 绝对路径。",
                cfg?.AsrVadModel ?? "", false, v => { if (Module != null) Module.Configuration.AsrVadModel = v; }, true);
            ConfigText(b, ref seq, "Whisper 路径（兜底）", @"whisper-cli.exe；只有 SenseVoice 不可用/超时才用。留空自动从 PATH 或插件数据目录 tools\whisper\ 找。",
                cfg?.WhisperPath ?? "", false, v => { if (Module != null) Module.Configuration.WhisperPath = v; }, true);
            ConfigText(b, ref seq, "Whisper 模型（兜底）", @"ggml-*.bin；SenseVoice 可用时不用。留空自动用同目录 models\ 下第一个。",
                cfg?.WhisperModel ?? "", false, v => { if (Module != null) Module.Configuration.WhisperModel = v; }, true);
            ConfigText(b, ref seq, "转写语言", "auto / zh / en。",
                cfg?.AsrLanguage ?? "auto", false, v => { if (Module != null) Module.Configuration.AsrLanguage = v; });

            if (Module != null)
                AddHint(b, ref seq, "转写状态：" + Module.UiAsrSummary);

            b.CloseElement();
        }
        else
        {
            b.OpenElement(seq++, "div");
            b.AddAttribute(seq++, "class", "wt-cfg-grid");
            ConfigBool(b, ref seq, "保存陪看进度", "把当前段进度写到 state.json，重启后接着看，默认开。",
                cfg?.PersistSession ?? true, v => { if (Module != null) Module.Configuration.PersistSession = v; });
            ConfigInt(b, ref seq, "会话保留时长（小时）", "超过这么久没更新就丢掉旧会话，默认 12。",
                cfg?.SessionMaxHours ?? 12, 1, 168, v => { if (Module != null) Module.Configuration.SessionMaxHours = v; });
            ConfigBool(b, ref seq, "播完自动写观看笔记", "播完/结束/换片时写一条 [我的观看笔记]，默认开。",
                cfg?.AutoSaveWatchNote ?? true, v => { if (Module != null) Module.Configuration.AutoSaveWatchNote = v; });
            ConfigInt(b, ref seq, "工具结果回注上限（字）", "默认 3800；限制单条 Poke 的最大字数。",
                cfg?.PokeMaxChars ?? 3800, 500, 10000, v => { if (Module != null) Module.Configuration.PokeMaxChars = v; });
            b.CloseElement();
        }

        b.CloseElement();

        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "wt-config-footer");
        Button(b, ref seq, "保存配置", () =>
        {
            message = Module?.UiSaveConfig() ?? "模块未激活";
            StateHasChanged();
        }, busy, primary: true);
        Button(b, ref seq, "刷新", () =>
        {
            message = "";
            StateHasChanged();
        }, busy);
        b.CloseElement();
        b.CloseElement();

        // ── 扩展与说明 ────────────────────────────────────────
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "wt-card");
        b.OpenElement(seq++, "details");
        b.AddAttribute(seq++, "class", "wt-details");
        b.OpenElement(seq++, "summary");
        b.AddContent(seq++, "浏览器扩展安装");
        b.CloseElement();
        b.AddMarkupContent(seq++, $"<div class=\"wt-muted\">扩展目录：<code>{E(Module?.UiExtensionDir ?? "")}</code></div>");
        b.AddMarkupContent(seq++, """
            <ol class="wt-steps">
              <li>打开 Edge / Chrome 的「扩展管理」页，开启右上角「开发者模式」。</li>
              <li>点「加载解压缩的扩展」，选择上面这个 <code>extension</code> 目录。</li>
              <li>浏览器里打开视频并播放；扩展 popup 里应显示「已连上 127.0.0.1」。</li>
              <li>如果你在插件配置里改了端口，扩展 popup 里的端口也要改成一样。</li>
            </ol>
            <div class="wt-muted">扩展只上报 pos / dur / title / url / paused / ended，只在当前可见标签页上报，只发到 127.0.0.1，不上传、不读页面正文。</div>
            """);
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "wt-row");
        Button(b, ref seq, "打开扩展目录", () =>
        {
            message = Module?.UiOpenExtensionFolder() ?? "模块未激活";
            StateHasChanged();
        }, busy);
        Button(b, ref seq, "清空抽帧缓存", () =>
        {
            message = Module?.UiClearFrames() ?? "模块未激活";
            StateHasChanged();
        }, busy);
        b.CloseElement();
        b.CloseElement();
        b.CloseElement();

        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "wt-card");
        b.OpenElement(seq++, "details");
        b.AddAttribute(seq++, "class", "wt-details");
        b.OpenElement(seq++, "summary");
        b.AddContent(seq++, "怎么陪看");
        b.CloseElement();
        b.AddMarkupContent(seq++, """
            <div class="wt-line">· 网页视频用浏览器扩展上报；桌面播放器用 Windows 媒体控制（SMTC）读取，QQ/微信/播放器需要接入 SMTC 才能读到进度。</div>
            <div class="wt-line">· 主人说「一起看」时，角色直接调 <code>watch_together</code>，不要问链接。</div>
            <div class="wt-line">· 开头段 1/2/3/4 分钟，之后 5 分钟一段；段末前 15 秒是交付点。</div>
            <div class="wt-line">· 本地视频读同名 SRT/VTT、按字幕时间点抽帧并拼 2×3 概览图；网页视频按标题和进度陪聊。</div>
            <div class="wt-line">· 网页视频想有字幕：扩展 popup 开「读取视频字幕」（实验，默认关），或在配置里填「外部字幕文件」（文件一变就重读）。</div>
            <div class="wt-line">· 字幕是游标式的：只给当前位置往前 120 秒、上次没给过的句子；不重复贴整段，省 token。</div>
            <div class="wt-line">· 模型可以请求 pause/play/seekBack/seekTo；命令在浏览器下一次上报时执行（最多约 1 秒）。</div>
            <div class="wt-line">· 播完/结束自动写 [我的观看笔记]；Nexus.Memory 的外部笔记标记里加上「我的观看笔记」可收成 L3。</div>
            <div class="wt-line">· 暂停、拖动、换片、播完都会推一条 [一起看] 提示；到点提醒默认只 Poke，不主动烧 token。</div>
            <div class="wt-line">· 想让它到点主动开口，开「到点主动唤醒」；想让它偶尔随口说一句，开「随机搭话」（每命中一次=一轮模型请求）。</div>
            """);
        b.CloseElement();
        b.CloseElement();

        b.AddMarkupContent(seq++, $"<div class=\"wt-footer\">状态文件：{E(Module?.UiStatePath ?? "")}</div>");
        b.CloseElement();
    }

    // ── 组件 ─────────────────────────────────────────────────

    static void Pill(RenderTreeBuilder b, ref int seq, string text, bool on)
    {
        b.OpenElement(seq++, "span");
        b.AddAttribute(seq++, "class", on ? "wt-pill wt-pill-on" : "wt-pill");
        b.AddContent(seq++, text);
        b.CloseElement();
    }

    static void Tile(RenderTreeBuilder b, ref int seq, string label, string value, string sub)
    {
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "wt-tile");
        b.AddMarkupContent(seq++, $"<div class=\"wt-tile-label\">{E(label)}</div>");
        b.AddMarkupContent(seq++, $"<div class=\"wt-tile-value\">{E(value)}</div>");
        b.AddMarkupContent(seq++, $"<div class=\"wt-tile-sub\">{E(sub)}</div>");
        b.CloseElement();
    }

    void Tab(RenderTreeBuilder b, ref int seq, string key, string label)
    {
        b.OpenElement(seq++, "button");
        b.AddAttribute(seq++, "type", "button");
        b.AddAttribute(seq++, "class", activeTab == key ? "wt-tab wt-tab-on" : "wt-tab");
        b.AddAttribute(seq++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, () =>
        {
            activeTab = key;
            StateHasChanged();
        }));
        b.AddContent(seq++, label);
        b.CloseElement();
    }

    // ── 配置控件 ─────────────────────────────────────────────

    void ConfigBool(RenderTreeBuilder b, ref int seq, string label, string hint, bool value, Action<bool> onChange)
    {
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "wt-field");
        b.OpenElement(seq++, "label");
        b.AddAttribute(seq++, "class", "wt-check");
        b.OpenElement(seq++, "input");
        b.AddAttribute(seq++, "type", "checkbox");
        b.AddAttribute(seq++, "checked", value);
        b.AddAttribute(seq++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
        {
            onChange(e.Value is bool flag && flag);
        }));
        b.CloseElement();
        b.AddContent(seq++, " " + label);
        b.CloseElement();
        AddHint(b, ref seq, hint);
        b.CloseElement();
    }

    void ConfigInt(RenderTreeBuilder b, ref int seq, string label, string hint, int value, int min, int max, Action<int> onChange)
    {
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "wt-field");
        AddLabel(b, ref seq, label);
        b.OpenElement(seq++, "input");
        b.AddAttribute(seq++, "class", "wt-input");
        b.AddAttribute(seq++, "type", "number");
        b.AddAttribute(seq++, "min", min);
        b.AddAttribute(seq++, "max", max);
        b.AddAttribute(seq++, "value", value);
        b.AddAttribute(seq++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
        {
            if (int.TryParse(e.Value?.ToString(), out int parsed)) onChange(Math.Clamp(parsed, min, max));
        }));
        b.CloseElement();
        AddHint(b, ref seq, hint);
        b.CloseElement();
    }

    void ConfigDouble(RenderTreeBuilder b, ref int seq, string label, string hint, double value, double min, double max, double step, Action<double> onChange)
    {
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "wt-field");
        AddLabel(b, ref seq, label);
        b.OpenElement(seq++, "input");
        b.AddAttribute(seq++, "class", "wt-input");
        b.AddAttribute(seq++, "type", "number");
        b.AddAttribute(seq++, "min", min);
        b.AddAttribute(seq++, "max", max);
        b.AddAttribute(seq++, "step", step);
        b.AddAttribute(seq++, "value", value);
        b.AddAttribute(seq++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
        {
            if (double.TryParse(e.Value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                onChange(Math.Clamp(parsed, min, max));
        }));
        b.CloseElement();
        AddHint(b, ref seq, hint);
        b.CloseElement();
    }

    void ConfigText(RenderTreeBuilder b, ref int seq, string label, string hint, string value, bool multiline, Action<string> onChange, bool wide = false)
    {
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", wide ? "wt-field wt-wide" : "wt-field");
        AddLabel(b, ref seq, label);
        if (multiline)
        {
            b.OpenElement(seq++, "textarea");
            b.AddAttribute(seq++, "class", "wt-input");
            b.AddAttribute(seq++, "rows", 3);
            b.AddAttribute(seq++, "oninput", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
            {
                onChange(e.Value?.ToString() ?? "");
            }));
            b.AddContent(seq++, value);
            b.CloseElement();
        }
        else
        {
            b.OpenElement(seq++, "input");
            b.AddAttribute(seq++, "class", "wt-input");
            b.AddAttribute(seq++, "type", "text");
            b.AddAttribute(seq++, "value", value);
            b.AddAttribute(seq++, "oninput", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
            {
                onChange(e.Value?.ToString() ?? "");
            }));
            b.CloseElement();
        }
        AddHint(b, ref seq, hint);
        b.CloseElement();
    }

    static void AddLabel(RenderTreeBuilder b, ref int seq, string label)
    {
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "wt-field-label");
        b.AddContent(seq++, label);
        b.CloseElement();
    }

    static void AddHint(RenderTreeBuilder b, ref int seq, string hint)
    {
        if (string.IsNullOrWhiteSpace(hint)) return;
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "wt-field-hint");
        b.AddContent(seq++, hint);
        b.CloseElement();
    }

    void Button(RenderTreeBuilder b, ref int seq, string label, Action handler, bool disabled = false, bool primary = false)
        => Button(b, ref seq, label, () => { handler(); return Task.CompletedTask; }, disabled, primary);

    void Button(RenderTreeBuilder b, ref int seq, string label, Func<Task> handler, bool disabled = false, bool primary = false)
    {
        b.OpenElement(seq++, "button");
        b.AddAttribute(seq++, "type", "button");
        b.AddAttribute(seq++, "class", primary ? "wt-btn wt-btn-primary" : "wt-btn");
        if (disabled) b.AddAttribute(seq++, "disabled", true);
        b.AddAttribute(seq++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, handler));
        b.AddContent(seq++, label);
        b.CloseElement();
    }

    static string E(string? text) => System.Net.WebUtility.HtmlEncode(text ?? "");

    const string Css = """

        .wt-root{font-family:"Microsoft YaHei UI","Segoe UI",sans-serif;color:#1f2b3a;padding:20px;max-width:1040px;margin:0 auto;background:linear-gradient(180deg,#f7fafd 0%,#ffffff 320px)}
        .wt-hero{display:flex;align-items:flex-start;justify-content:space-between;gap:16px;padding:18px 20px;border:1px solid #dbe5f0;border-left:4px solid #2f6fed;border-radius:14px;background:linear-gradient(135deg,#ffffff 0%,#f3f8ff 100%);box-shadow:0 2px 10px rgba(47,111,237,.06)}
        .wt-title{font-size:22px;font-weight:700;letter-spacing:.2px}
        .wt-subtitle{font-size:12px;font-weight:400;color:#7b8798;margin-left:6px}
        .wt-hero-hint{font-size:12px;color:#5b6b7f;margin-top:6px;line-height:1.6}
        .wt-badges{display:flex;gap:6px;flex-wrap:wrap;padding-top:4px}
        .wt-pill{font-size:11px;padding:4px 10px;border-radius:999px;background:#eef2f7;color:#64748b;border:1px solid #e2e8f0;white-space:nowrap}
        .wt-pill-on{background:#e8f7ee;color:#17803f;border-color:#c8ecd5}
        .wt-msg{margin:12px 0 0;padding:10px 12px;border-radius:10px;background:#eef6ff;color:#1f4f8b;white-space:pre-wrap;font-size:13px}
        .wt-muted{color:#7b8798;font-size:12px;font-weight:400}
        .wt-tiles{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:10px;margin-top:14px}
        .wt-tile{padding:12px 14px;border:1px solid #e3ebf4;border-radius:12px;background:#fff}
        .wt-tile-label{font-size:11px;color:#8a97a8;font-weight:600}
        .wt-tile-value{font-size:17px;font-weight:700;margin-top:4px;color:#1e3a5f;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
        .wt-tile-sub{font-size:11px;color:#94a3b8;margin-top:3px}
        .wt-actions{display:flex;gap:8px;flex-wrap:wrap;margin-top:14px}
        .wt-btn{border:1px solid #cbd6e3;background:#fff;border-radius:9px;padding:8px 13px;cursor:pointer;font-size:12px;color:#334155;transition:background .15s,border-color .15s}
        .wt-btn:hover{background:#f3f7fd;border-color:#a9c0e4}
        .wt-btn:disabled{opacity:.55;cursor:default}
        .wt-btn-primary{background:#2f6fed;border-color:#2f6fed;color:#fff}
        .wt-btn-primary:hover{background:#2560d4;border-color:#2560d4}
        .wt-card{border:1px solid #e3ebf4;border-radius:14px;padding:16px 18px;margin-top:16px;background:#fff;box-shadow:0 1px 4px rgba(30,58,95,.04)}
        .wt-card-head{display:flex;align-items:baseline;justify-content:space-between;gap:12px;margin-bottom:10px}
        .wt-card-title{font-weight:700;font-size:14px}
        .wt-card-hint{font-size:11px;color:#94a3b8}
        .wt-tabs{display:flex;gap:4px;flex-wrap:wrap;padding:4px;background:#f3f6fa;border-radius:10px;margin-bottom:14px}
        .wt-tab{border:0;background:transparent;border-radius:7px;padding:6px 12px;font-size:12px;color:#64748b;cursor:pointer}
        .wt-tab:hover{color:#2560d4}
        .wt-tab-on{background:#fff;color:#1f4f8b;font-weight:700;box-shadow:0 1px 3px rgba(30,58,95,.12)}
        .wt-cfg-grid{display:grid;grid-template-columns:minmax(0,1fr) minmax(0,1fr);gap:14px 18px}
        .wt-field{display:flex;flex-direction:column;gap:4px;min-width:0}
        .wt-wide{grid-column:1/-1}
        .wt-field-label{font-size:12px;font-weight:600;color:#334155}
        .wt-field-hint{font-size:11px;color:#8a97a8;line-height:1.5}
        .wt-check{display:flex;align-items:center;gap:6px;font-size:12px;font-weight:600;color:#334155;cursor:pointer}
        .wt-input{width:100%;box-sizing:border-box;border:1px solid #d3deea;border-radius:8px;padding:7px 9px;font-family:inherit;font-size:12px;color:#1f2b3a;background:#fbfdff}
        .wt-input:focus{outline:2px solid #cfe0fb;border-color:#9dbcf0}
        textarea.wt-input{resize:vertical;line-height:1.55}
        .wt-config-footer{display:flex;gap:8px;flex-wrap:wrap;justify-content:flex-end;margin-top:16px;padding-top:12px;border-top:1px dashed #e6edf5}
        .wt-pre{margin:8px 0 0;padding:10px 12px;background:#f7f9fc;border-radius:10px;font-size:12px;max-height:320px;overflow:auto;white-space:pre-wrap;color:#3b4a5e}
        .wt-details summary{cursor:pointer;font-size:13px;font-weight:700;color:#334155;padding:2px 0}
        .wt-details[open] summary{margin-bottom:10px}
        .wt-row{display:flex;gap:8px;flex-wrap:wrap;margin-top:8px}
        .wt-steps{margin:8px 0;padding-left:20px;font-size:12px;line-height:1.85;color:#4b5b6f}
        .wt-line{font-size:12px;line-height:1.85;color:#4b5b6f;margin-top:6px}
        .wt-footer{margin-top:14px;font-size:11px;color:#a3aebd;word-break:break-all}
        code{background:#f1f5f9;border-radius:4px;padding:1px 5px;color:#475569}
        @media (max-width:860px){
            .wt-tiles{grid-template-columns:repeat(2,minmax(0,1fr))}
            .wt-cfg-grid{grid-template-columns:1fr}
            .wt-wide{grid-column:1}
            .wt-hero{flex-direction:column}
        }
        """;
}
