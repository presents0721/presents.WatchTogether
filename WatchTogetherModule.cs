using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Alife.Foundation;
using Alife.Function.Auditory;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace presents.WatchTogether;

/// <summary>
/// 一起看：把浏览器里正在播的视频进度接进来，按真实播放位置分段陪着看。
///
/// 和上游 dsh-watch-together 的差别：
///  - 位置桥用 C# HttpListener，扩展仍是原逻辑（只读 &lt;video&gt;.currentTime）；
///  - 不做视频下载、不内置 ASR：网页视频只在进度层面陪聊；
///  - 本地视频可读同名 SRT/VTT 字幕，装了 ffmpeg 还能抽当前段的关键帧；
///  - 到点提醒 / 主动唤醒可选，默认只 Poke 不自动烧 token。
/// </summary>
[Module("视频陪看",
    "把主人正在播放的视频接进来，按播放位置一段一段陪着看。素材三条通道：① B 站 CC / AI 字幕自动直取（讲解类视频的台词）；② 网页视频实时抓帧（扩展 tabCapture，画面一变就拍，PPT / 图表讲解类靠它）；③ 本地视频读同名字幕 + ffmpeg 按字幕时间点抽帧 + 概览拼图。段末前提醒角色开口，暂停/拖动/换片/播完都有提示；字幕按游标式窗口只给新增，可选随机搭话，播完自动写观看笔记。",
    defaultCategory: "presents/陪伴",
    editorUI: typeof(WatchTogetherUI))]
public partial class WatchTogetherModule(
    XmlFunctionCaller functionCaller,
    PluginSystem pluginSystem,
    ILogger<WatchTogetherModule> logger,
    IAudioRecognizerProvider? audioRecognizer = null,
    ILanguageModel? languageModel = null) :
    ChatBehaviour,
    IConfigurable<WatchTogetherConfig>
{
    public const string Version = "2.0.0";

    public WatchTogetherConfig Configuration { get; set; } = new();

    /// <summary>框架本地语音识别（SenseVoice 流式）。为 null 时退回 whisper.cpp。</summary>
    public bool HasRecognizer => audioRecognizer != null;

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    // ─────────────────────────────────────────────────────────────
    // 运行时状态
    // ─────────────────────────────────────────────────────────────

    WatchBridge? bridge;
    DesktopMediaWatcher? desktopMedia;
    WatchPlan plan = WatchPlan.Create(300, Array.Empty<int>());
    string planRampText = "";
    int planLead;
    WatchSession? session;
    WatchRuntimeState runtime = new();
    PositionEstimate? lastEstimate;
    WatchSample? lastSample;

    string pluginDir = "";
    string dataDir = "";
    string statePath = "";

    float tickAccum;
    DateTime lastSave = DateTime.MinValue;
    DateTime lastBridgeAttempt = DateTime.MinValue;
    DateTime lastTickAt = default;
    double? lastTickPos;
    DateTime lastAdvanceAt = default;
    bool waking;
    string lastNote = "";
    DateTime lastNoteAt = DateTime.MinValue;

    readonly Dictionary<string, (DateTime Stamp, List<SubtitleCue> Cues)> subtitleCache =
        new(StringComparer.OrdinalIgnoreCase);

    // 外部字幕文件：别的 ASR / 字幕工具正在写的那份。每 2 秒看一次修改时间，变了就重读。
    readonly object externalGate = new();
    List<SubtitleCue> externalCues = new();
    DateTime externalStamp = DateTime.MinValue;
    DateTime lastExternalCheck = DateTime.MinValue;
    string externalLabel = "";

    // 页面字幕（实验）：扩展在用户开启后读到的视频字幕元素，按播放位置累积成时间轴。
    readonly object pageGate = new();
    readonly List<SubtitleCue> pageCues = new();
    string lastPageCaption = "";

    // 事件时间轴（每段一条：字幕摘句 + 画面 OCR 文字）
    WatchEventStore? watchEvents;
    readonly HashSet<string> recordedSegments = new(StringComparer.Ordinal);

    public WatchEventStore? UiWatchEvents => watchEvents;
    public int UiEventCount => watchEvents?.Count ?? 0;

    public List<WatchEvent> UiRecentEvents(int count) => watchEvents?.Recent(count) ?? new List<WatchEvent>();

    /// <summary>记一条陪看事件（同会话同段只记一次）。</summary>
    public void RecordWatchEvent(WatchEvent value)
    {
        if (watchEvents == null) return;

        string key = $"{value.Title}@{value.Pos:0}";
        if (!recordedSegments.Add(key)) return;

        if (recordedSegments.Count > 4000) recordedSegments.Clear();

        watchEvents.Add(value, Configuration.KeepEvents);
    }

    // 平台字幕（B 站 CC / AI 字幕）：view → wbi/v2 → subtitle_url，比 DOM 抓取稳得多。
    BiliSubtitleClient? biliSubs;
    PlatformSubtitle? platformSubtitle;
    string platformKey = "";
    DateTime platformAt = DateTime.MinValue;
    DateTime platformAttemptAt = DateTime.MinValue;

    // 平台字幕探测失败记录（-404 等），供状态面板明示原因
    string platformMissKey = "";
    DateTime platformMissAt = DateTime.MinValue;

    public string UiPlatformMissBvid => platformMissKey;
    public DateTime UiPlatformMissAt => platformMissAt;

    // 实时画面（扩展 tabCapture 抓帧）
    LiveFrameStore? liveFrames;
    DateTime lastCaptureAt = DateTime.MinValue;
    DateTime lastLiveContextAt = DateTime.MinValue;

    // 视频下载（B 站）与语音转写（ASR）
    BiliMedia? biliMedia;
    WhisperAsr? whisper;
    SenseVoiceAsr? senseAsr;   // 框架 SenseVoice（优先）；为 null 时退回 whisper
    readonly object asrGate = new();
    readonly List<SubtitleCue> asrCues = new();
    string asrSessionKey = "";
    string asrEngineLabel = "SenseVoice";   // 最近一次真正产出字幕的引擎：SenseVoice / whisper
    int asrDone;
    int asrFailed;
    string asrLastError = "";

    // 纯 BGM 检测：连续几段没人声就停用转写，否则 whisper 会把音乐当台词混进字幕。
    // asrMuted 跨线程读写（转写线程写 / tick 线程读），必须 volatile 保证可见性。
    int asrEmptyStreak;
    volatile bool asrMuted;

    public bool UiAsrMuted => asrMuted;

    public string UiAsrSummary
    {
        get
        {
            WhisperAsr? engine = whisper;
            (string Whisper, string Model, bool Ready) probe = engine?.Probe() ?? ("", "", false);

            WatchBridge? current = bridge;

            string engineText;
            if (senseAsr != null)
                engineText = "可用 · SenseVoice（框架本地，int8）· 内置 VAD";
            else
                engineText = (probe.Ready ? "可用" : "不可用")
                             + (probe.Whisper.Length > 0 ? $" · {System.IO.Path.GetFileName(probe.Whisper)}" : "")
                             + (probe.Model.Length > 0 ? $" · {System.IO.Path.GetFileName(probe.Model)}" : "")
                             + (whisper != null && whisper.HasVad() ? " · VAD ✓" : "");

            return engineText
                   + $" · 已收 {(current?.AudioCount ?? 0)} 片"
                   + (current?.LastAudioAt is DateTime la ? $"（{la:HH:mm:ss}）" : "")
                   + (asrMuted ? " · 已判定纯 BGM，转写停用" : "")
                   + $" · 已转 {asrDone} 段"
                   + (asrFailed > 0 ? $"（失败 {asrFailed}）" : "")
                   + (asrLastError.Length > 0 ? $" · {asrLastError}" : "");
        }
    }

    public LiveFrameStore? UiLiveFrames => liveFrames;
    public PlatformSubtitle? UiPlatformSubtitle => platformSubtitle;

    // ── 抓帧预设（面板用） ──

    public static IReadOnlyList<WatchFramePresets.Preset> FramePresetList => WatchFramePresets.All;

    /// <summary>当前配置匹配哪个预设（都不匹配 = 自定义）。</summary>
    public string CurrentFramePreset => WatchFramePresets.Match(Configuration)?.Name ?? "自定义";

    /// <summary>套用抓帧预设并落盘（面板按钮调用）。</summary>
    public string ApplyFramePreset(string name)
    {
        WatchFramePresets.Preset? preset = WatchFramePresets.Find(name);
        if (preset == null) return $"没有这个预设：{name}";

        WatchFramePresets.Apply(preset, Configuration);

        if (this is IConfigurable<WatchTogetherConfig> configurable)
            configurable.Configuration = Configuration;

        lastNote = $"已套用抓帧预设「{preset.Name}」";
        lastNoteAt = DateTime.Now;

        return $"已套用「{preset.Name}」：每 {preset.IntervalSeconds} 秒 / 阈值 {preset.ChangeThreshold:0.#} "
               + $"/ 宽 {preset.MaxWidth} / 质量 {preset.Quality} / 保留 {preset.Keep} 张";
    }

    // ─────────────────────────────────────────────────────────────
    // 面板用属性
    // ─────────────────────────────────────────────────────────────

    // 面板只显示相对路径：截图/分享控制台时不要把 Windows 用户名和完整目录暴露出去。
    // 真实路径仍由 UiStatePathFull / UiExtensionDirFull 供按钮内部使用。
    static string DisplayPath(string full, string baseDir)
    {
        try {
            string rel = Path.GetRelativePath(baseDir, full);
            return rel.StartsWith("..", StringComparison.Ordinal) ? Path.GetFileName(full) : rel;
        } catch { return Path.GetFileName(full); }
    }

    public string UiStatePathFull => statePath;
    public string UiExtensionDirFull => Path.Combine(pluginDir, "extension");
    public string UiExtensionDir => DisplayPath(Path.Combine(pluginDir, "extension"), pluginDir);
    public string UiStatePath => DisplayPath(statePath, pluginDir);
    public string UiDataDir => dataDir;
    public string UiLastNote => lastNote;
    public DateTime UiLastNoteAt => lastNoteAt;
    public WatchSession? CurrentSession => session;
    public WatchPlan CurrentPlan => plan;
    public PositionEstimate? LastEstimate => lastEstimate;
    public WatchSample? LastSample => lastSample;
    public WatchBridge? Bridge => bridge;

    // ─────────────────────────────────────────────────────────────
    // 生命周期
    // ─────────────────────────────────────────────────────────────

    protected override Task OnAwake()
    {
        try
        {
            string pluginId = pluginSystem.PluginContext.GetTypeAttachedPlugin(typeof(WatchTogetherModule))
                              ?? "presents.WatchTogether";
            pluginDir = pluginSystem.PluginContext.GetPluginDirectoryPath(pluginId);
            dataDir = Path.Combine(AlifePath.StorageFolderPath, "PluginData", "presents.WatchTogether");
            Directory.CreateDirectory(dataDir);
            statePath = Path.Combine(pluginDir, "state.json");

            liveFrames = new LiveFrameStore(logger, () => Configuration, dataDir);
            watchEvents ??= new WatchEventStore(Path.Combine(dataDir, "events.json"));
            biliMedia = new BiliMedia(logger, ResolveBiliCookie);
            whisper = new WhisperAsr(logger, () => Configuration, dataDir);

            // 框架本地识别器（SenseVoice）可用时优先用它：中文 CER 约 whisper 的 1/3、速度快一个量级
            if (audioRecognizer != null)
                senseAsr = new SenseVoiceAsr(audioRecognizer, () => Configuration, logger);
            biliSubs = new BiliSubtitleClient(logger, ResolveBiliCookie, message =>
            {
                lastNote = message;
                lastNoteAt = DateTime.Now;
            });

            LoadState();
            planRampText = session?.PlanRamp ?? Configuration.RampSeconds;
            planLead = session?.PlanLead > 0 ? session.PlanLead : Configuration.LeadSeconds;
            plan = WatchPlan.Create(
                session?.PlanSegment > 0 ? session.PlanSegment : Configuration.SegmentSeconds,
                WatchFormat.ParseRamp(planRampText));

            if (Configuration.Enable && Configuration.AutoStartBridge)
                StartBridge();

            XmlHandler handler = new(this)
            {
                Description = "一起看：读取主人浏览器里正在播的视频进度，按段算当前区间与交付点；可给本地/外部/页面字幕与关键帧，也能请求暂停/继续/回退。",
                Explanation =
                    "使用方式：\n"
                    + "1. 主人说「一起看」/「陪我看视频」→ 直接调用 watch_together()，**不要问他要链接或文件路径**："
                    + "   input 不给时，插件自动读他浏览器里正在播放的那一支（浏览器扩展每秒上报）。\n"
                    + "2. 第一次调用会返回：片名、当前位置、当前段区间、交付点、字幕（本地/外部/页面，如果有）。\n"
                    + "3. 只有到了交付点（段末前 lead 秒）才说话；没到就沉默。每段说一两句，不复述剧情。\n"
                    + "4. 暂停/拖动/换片/播完时，插件会主动推 [一起看] 提示；看到就按提示回应。\n"
                    + "   想控制播放器：pause=true 暂停、play=true 继续、seekBack=10 回退十秒、seekTo=... 跳到；"
                    + "   命令会在浏览器下一次上报时执行（最多约 1 秒），不是立即生效。\n"
                    + "5. 网页视频默认没有本地字幕和画面素材：按标题、进度、主人说的话陪聊，**不要编造剧情**。\n"
                    + "   本地视频会读同名 SRT/VTT；网页视频可在扩展 popup 开「读取视频字幕」（实验，默认关），"
                    + "   或在控制台配置「外部字幕文件」（按视频时间轴的 SRT/VTT）。\n"
                    + "   字幕是游标式窗口：只给当前位置往前、上次没给过的句子，不是整段复述。"
                    + "   装了 ffmpeg 还会返回当前段的单帧和 2×3 概览图（可用视觉工具看）。\n"
                    + "6. 想把自己准备说的话记下来，用 say=\"...\"；它只记录，不发送、不配音。\n"
                    + "7. statusOnly=true 只看桥接/位置/段状态，不取素材；reset=true 结束这一场。\n"
                    + "8. 不要下载视频、不要抓网页正文；本插件只处理播放进度和本地素材。\n"
                    + "9. 播完/手动结束/换片会自动写一条 [我的观看笔记] 到聊天历史；"
                    + "   Nexus.Memory 的外部笔记标记里加上「我的观看笔记」就会收成 L3，你不用再调记忆工具。"
            };
            WrapHandlerResultsForPoke(handler);
            functionCaller.RegisterHandler(handler, DocumentMode.Implicit, DestroyCancellationToken);

            try { TextZh.LoadFromFile(Path.Combine(pluginDir, "textzh.tsv")); } catch { }
            TextZh.DebugHook = s => bridge?.AddTrace(s);
            SenseVoiceAsr.DebugHook = s => bridge?.AddTrace(s);
            logger.LogInformation("一起看：已唤醒，位置桥 {State}，端口 {Port}",
                bridge?.Running == true ? "运行中" : "未运行", Configuration.Port);

            StartTickTimer();   // 不依赖框架 Update：交付点/抓帧/配置同步全靠它
        }
        catch (Exception e)
        {
            logger.LogError(e, "一起看：OnAwake 失败");
        }
        return Task.CompletedTask;
    }

    protected override Task OnUpdate()
    {
        // 框架的 Update 在部分场景（角色未聚焦/后台）根本不跑，导致交付点/抓帧/配置同步全哑。
        // 改用自建定时器驱动（SafeTick 防重入）；框架若真的 tick，也只是多走一次防重入空转。
        SafeTick();
        return Task.CompletedTask;
    }

    Timer? tickTimer;
    int ticking;

    /// <summary>自建 tick：模块线程无关，1.5 秒一次，与框架 Update 共用防重入锁。</summary>
    void StartTickTimer() => tickTimer ??= new Timer(_ => SafeTick(), null, 1500, 1500);

    void SafeTick()
    {
        if (Interlocked.Exchange(ref ticking, 1) == 1) return;
        try
        {
            RunTickOnce();
        }
        finally
        {
            Interlocked.Exchange(ref ticking, 0);
        }
    }

    void RunTickOnce()
    {
        try
        {
            if (!Configuration.Enable)
            {
                if (bridge?.Running == true) bridge.Stop();
                if (desktopMedia != null) { desktopMedia.Stop(); desktopMedia = null; }
                return;
            }

            RefreshExternalSubtitles(DateTime.Now);
            EnsureBridge();
            EnsureDesktopMedia();
            Tick();
            MaybeRandomChat();
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "一起看：OnUpdate 失败");
        }
    }

    protected override async Task OnDestroy()
    {
        tickTimer?.Dispose();
        tickTimer = null;

        try { SaveStateNow(); } catch { }
        WatchBridge? current = bridge;
        bridge = null;
        if (current != null)
        {
            try { await current.DisposeAsync(); } catch { }
        }

        biliSubs?.Dispose();
        biliSubs = null;
        biliMedia?.Dispose();
        biliMedia = null;
        desktopMedia?.Dispose();
        desktopMedia = null;
    }

    // ─────────────────────────────────────────────────────────────
    // 位置桥
    // ─────────────────────────────────────────────────────────────

    void EnsureBridge()
    {
        bridge ??= new WatchBridge();
        bridge.FrameSink = AcceptLiveFrame;        // 扩展抓到的帧从这里进
        bridge.CaptureDecider = ShouldCaptureNow;  // 收到位置时决定要不要拍
        bridge.WantCaption = Configuration.AutoPageCaption;
        bridge.CaptionSelector = Configuration.CaptionSelector ?? "";
        bridge.WantAsr = Configuration.EnableAsr && !asrMuted;
        bridge.AsrSeconds = Math.Clamp(Configuration.AsrChunkSeconds, 10, 60);
        bridge.WantFrames = Configuration.AutoLiveFrames;
        bridge.FrameworkAsr = HasRecognizer;   // 纯 BGM 判定后保持停用
        bridge.AudioSink = AcceptAudioChunk;
        bridge.AsrMuted = asrMuted;
        bridge.LastSyncAt = DateTime.Now;
        if (bridge.Running && bridge.Port == Configuration.Port) return;
        if ((DateTime.Now - lastBridgeAttempt).TotalSeconds < 30) return;
        lastBridgeAttempt = DateTime.Now;
        StartBridge();
    }

    void EnsureDesktopMedia()
    {
        if (!Configuration.EnableDesktopMedia)
        {
            if (desktopMedia != null)
            {
                desktopMedia.Stop();
                desktopMedia = null;
            }
            return;
        }

        desktopMedia ??= new DesktopMediaWatcher();
        if (!desktopMedia.Running) desktopMedia.Start();
    }

    WatchSample? PickSample()
    {
        WatchSample? browser = bridge?.Latest;
        WatchSample? desktop = desktopMedia?.Latest;
        if (browser == null) return desktop;
        if (desktop == null) return browser;

        TimeSpan stale = TimeSpan.FromSeconds(Math.Max(5, Configuration.BridgeStaleSeconds));
        bool browserFresh = DateTime.Now - browser.At <= stale;
        bool desktopFresh = DateTime.Now - desktop.At <= stale;
        bool currentIsDesktop = session?.Input.StartsWith("smtc://", StringComparison.OrdinalIgnoreCase) == true;
        if (currentIsDesktop) return desktopFresh ? desktop : browser;
        if (browserFresh) return browser;
        return desktopFresh ? desktop : browser;
    }

    public bool StartBridge()
    {
        bridge ??= new WatchBridge();
        bridge.FrameSink = AcceptLiveFrame;
        bridge.CaptureDecider = ShouldCaptureNow;
        bridge.WantCaption = Configuration.AutoPageCaption;
        bridge.CaptionSelector = Configuration.CaptionSelector ?? "";
        bridge.WantAsr = Configuration.EnableAsr && !asrMuted;
        bridge.AsrSeconds = Math.Clamp(Configuration.AsrChunkSeconds, 10, 60);
        bridge.WantFrames = Configuration.AutoLiveFrames;
        bridge.FrameworkAsr = HasRecognizer;
        bridge.AudioSink = AcceptAudioChunk;
        bridge.AsrMuted = asrMuted;
        bridge.LastSyncAt = DateTime.Now;

        if (bridge.Start(Configuration.Port))
        {
            lastNote = $"位置桥已监听 127.0.0.1:{Configuration.Port}";
            lastNoteAt = DateTime.Now;
            return true;
        }

        lastNote = $"位置桥启动失败：{bridge.LastError}";
        lastNoteAt = DateTime.Now;
        return false;
    }

    public void StopBridge()
    {
        bridge?.Stop();
        lastNote = "位置桥已停止。";
        lastNoteAt = DateTime.Now;
    }

    // ─────────────────────────────────────────────────────────────
    // 调度：每一拍只看状态，不做耗时活
    // ─────────────────────────────────────────────────────────────

    void Tick()
    {
        DateTime now = DateTime.Now;
        if (Configuration.PersistSession && (now - lastSave).TotalSeconds >= 30)
            SaveState();

        WatchSample? sample = PickSample();
        if (sample == null) return;

        lastSample = sample;
        LearnPageCaption(sample);
        double age = (now - sample.At).TotalSeconds;
        if (age > Math.Max(5, Configuration.BridgeStaleSeconds)) return;

        PositionEstimate estimate = WatchEstimator.Estimate(
            sample, now,
            Math.Max(1, Configuration.FreshSeconds) * 1000.0,
            Math.Max(5, Configuration.MaxExtrapolateSeconds) * 1000.0);
        lastEstimate = estimate;

        if (sample.Ended)
        {
            HandleEnded(sample, estimate, now);
            return;
        }

        if (Configuration.SuspendWhenPaused && sample.Paused)
        {
            lastTickPos = sample.Pos;
            lastTickAt = now;
            return;
        }

        if (!estimate.Known) return;

        string? input = WatchSource.InputFrom(sample);
        if (input == null) return;

        string key = WatchSource.SessionKey(input);
        if (session == null || !string.Equals(session.Key, key, StringComparison.Ordinal))
        {
            StartSession(sample, input, key, now, notifySwitch: session != null);
            return;
        }

        // 换片：时长变了 > 1 秒。比地址更硬，也不受追踪参数影响。
        if (session.Duration > 0 && sample.Dur is > 0 && Math.Abs(session.Duration - sample.Dur.Value) > 1.0)
        {
            StartSession(sample, input, key, now, notifySwitch: true);
            return;
        }

        UpdateSessionFields(sample, now);

        // 大跳：拖动/回看。只在两次读数相邻时判，断流恢复的第一拍不误报。
        bool recent = lastTickAt != default &&
                      (now - lastTickAt).TotalSeconds <= Math.Max(5, Configuration.BridgeStaleSeconds) + 3;
        if (recent && lastTickPos.HasValue && (now - session.LastJumpAt).TotalMinutes >= Math.Max(0, Configuration.JumpCooldownMinutes))
        {
            double expected = lastTickPos.Value + (now - lastTickAt).TotalSeconds;
            double jump = sample.Pos - expected;
            if (Math.Abs(jump) > Math.Max(10, Configuration.JumpSeconds))
            {
                session.LastJumpAt = now;
                PokeNote($"[一起看] 主人拖了进度条：{(jump > 0 ? "跳到" : "回看到")} {WatchFormat.Mmss(sample.Pos)}"
                         + $"（原来约 {WatchFormat.Mmss(expected)}）。"
                         + (jump > 0
                             ? "跳过的部分不用补，从新位置接着跟。"
                             : "回看的部分不要重复念。"));
            }
        }
        if (lastTickPos.HasValue && Math.Abs(sample.Pos - lastTickPos.Value) >= 0.5)
            lastAdvanceAt = now;
        session.MaxPos = Math.Max(session.MaxPos, sample.Pos);
        lastTickPos = sample.Pos;
        lastTickAt = now;

        MaybeInjectLiveContext(sample, estimate, now);

        WatchSegment seg = plan.SegmentAt(estimate.Pos);
        if (session.IsDelivered(seg.From)) return;
        if (estimate.Pos > seg.To + 60) return;

        double sayAt = Math.Max(seg.From, seg.To - Math.Max(0, session.PlanLead > 0 ? session.PlanLead : planLead));
        if (estimate.Pos < sayAt) return;
        if (InQuietHours(now)) return;
        // 两种提醒都关掉时，不要把段标成已交付：留给工具按需查询。
        if (!Configuration.WakeOnDue && !Configuration.PokeOnDue) return;

        session.MarkDelivered(seg.From);
        session.LastDueAt = now;
        session.UpdatedAt = now;

        // 字幕用最近一次真实读数，不用外推位置：SRT 有完整时间轴，按外推会提前看到未来台词。
        string subtitle = TryBuildSubtitleDigest(session, sample.Pos,
            Math.Clamp(Configuration.SubtitleDigestLines, 0, 8));
        string note = $"[一起看·到点] 《{session.DisplayTitle}》第 {seg.Index + 1} 段 "
                      + $"{WatchFormat.Mmss(seg.From)}-{WatchFormat.Mmss(seg.To)} 快到尾巴"
                      + $"（当前 {WatchFormat.Mmss(estimate.Pos)}，还剩 {Math.Max(0, seg.To - estimate.Pos):0} 秒）。"
                      + (subtitle.Length > 0 && subtitle.StartsWith("[字幕]", StringComparison.Ordinal) ? "\n" + subtitle : "")
                      + "\n现在可以说一两句；不要复述剧情，没有素材就不要编。";

        if (Configuration.WakeOnDue && CanWake(now))
        {
            SaveStateNow();
            _ = WakeAsync(note);
        }
        else if (Configuration.PokeOnDue)
        {
            PokeNote(note);
            SaveStateNow();
        }
    }

    void HandleEnded(WatchSample sample, PositionEstimate estimate, DateTime now)
    {
        if (session == null || session.EndedNotified) return;
        session.EndedNotified = true;
        session.UpdatedAt = now;
        lastTickPos = sample.Pos;
        lastTickAt = now;
        WatchSession current = session;
        bool neverDelivered = current.Delivered.Count == 0;

        if (Configuration.PokeOnEnded && !InQuietHours(now))
        {
            if (neverDelivered)
            {
                // 片子播完了却一次都没交付过（段表还没走到，或者根本没素材）：
                // 把最后一段素材补给它，否则整场等于没陪。
                _ = SendFinaleAsync(current);
            }
            else
            {
                PokeNote($"[一起看] 视频播完了：《{current.DisplayTitle}》看到 {WatchFormat.Mmss(estimate.Pos)}。"
                         + "跟主人收个尾，或者问下一支看什么；他开了新片后重新调 watch_together 就能接着陪。");
            }
        }

        _ = SaveWatchNoteAsync(current, "播完");
        SaveStateNow();
    }

    void StartSession(WatchSample sample, string input, string key, DateTime now, bool notifySwitch)
    {
        WatchSession? old = session;
        string? oldTitle = old?.DisplayTitle;
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
        lock (pageGate)
        {
            pageCues.Clear();
            lastPageCaption = "";
        }

        if (notifySwitch && !string.IsNullOrWhiteSpace(oldTitle)
            && Configuration.PokeOnSwitch && !InQuietHours(now))
        {
            PokeNote($"[一起看] 换片了：现在是《{session.DisplayTitle}》。"
                     + "要接着陪就重新调一次 watch_together；不跟也没关系。");
        }
        if (old != null) _ = SaveWatchNoteAsync(old, "换片");
        SaveStateNow();
    }

    void UpdateSessionFields(WatchSample sample, DateTime now)
    {
        if (session == null) return;
        if (sample.Dur is > 0) session.Duration = sample.Dur.Value;
        if (!string.IsNullOrWhiteSpace(sample.Url)) session.Url = sample.Url;
        if (!string.IsNullOrWhiteSpace(sample.Title)) session.Title = sample.Title;
        session.MaxPos = Math.Max(session.MaxPos, Math.Max(0, sample.Pos));
        session.UpdatedAt = now;
    }

    // ─────────────────────────────────────────────────────────────
    // 唤醒与提醒
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 播放中的「实时上下文」：每隔 N 秒把当前进度、当前段和最近几句字幕刷进 System 消息，
    /// AI 任何时候开口都知道现在看到哪；只保留最新一条，不会把上下文越堆越长。
    /// </summary>
    void MaybeInjectLiveContext(WatchSample sample, PositionEstimate estimate, DateTime now)
    {
        int interval = Configuration.LiveContextSeconds;
        if (interval <= 0 || session == null) return;
        if ((now - lastLiveContextAt).TotalSeconds < Math.Max(5, interval)) return;
        lastLiveContextAt = now;

        WatchSegment seg = plan.SegmentAt(estimate.Pos);
        string digest = TryBuildSubtitleDigest(session, sample.Pos,
            Math.Clamp(Configuration.SubtitleDigestLines, 1, 6));
        string note = $"[一起看·实时] 《{session.DisplayTitle}》现在 {WatchFormat.Mmss(estimate.Pos)}"
                      + (session.Duration > 0 ? $" / {WatchFormat.Duration(session.Duration)}" : "")
                      + $"，第 {seg.Index + 1} 段（{WatchFormat.Mmss(seg.From)}-{WatchFormat.Mmss(seg.To)}，"
                      + $"还剩 {Math.Max(0, seg.To - estimate.Pos):0} 秒）。";
        note += digest.Length > 0
            ? "\n" + digest
            : "\n最近还没有可读台词（转写可能还在追），按画面和进度陪聊。";

        double captureAge = lastCaptureAt == default ? double.MaxValue : (now - lastCaptureAt).TotalSeconds;
        if (captureAge < 180)
            note += $"\n画面：最近一次变化/抓帧约 {(int)captureAge} 秒前。";

        try { UpsertSystemNote("[一起看·实时]", note); } catch { }
    }

    /// <summary>
    /// 随机搭话：不按段、只在「确实在看」的时候掷骰子。借鉴 fish-buddy 的口径——
    /// 有动静、距上次任何提醒 90 秒、骰子命中才开一轮；骰子本身不花 token。
    /// </summary>
    void MaybeRandomChat()
    {
        try
        {
            if (!Configuration.EnableRandomChat) return;
            if (session == null || lastSample == null || lastEstimate == null) return;

            DateTime now = DateTime.Now;
            if (InQuietHours(now)) return;
            if (waking) return;
            if ((now - lastSample.At).TotalSeconds > Math.Max(5, Configuration.BridgeStaleSeconds)) return;
            if (lastSample.Paused || lastSample.Ended) return;
            if (!lastEstimate.Known) return;

            // 刚接上先安静一分钟，别一开视频就抢在到点提醒前面说话。
            if ((now - session.StartedAt).TotalMinutes < 1) return;
            // 「有动静」：最近 30 秒位置真的在走。不对着暂停/黑屏说话。
            if (lastAdvanceAt == default || (now - lastAdvanceAt).TotalSeconds > 30) return;
            // 任何提醒/搭话之后 90 秒内不插话，避免两条挨在一起。
            if (session.LastPokeAt != default && (now - session.LastPokeAt).TotalSeconds < 90) return;
            // 间隔到了才掷骰子。
            if ((now - runtime.LastRandomAt).TotalMinutes < Math.Max(1, Configuration.RandomChatEveryMinutes)) return;

            if (!functionCaller.IsIdle) return;
            if (ChatBot.IsChatOccupied) return;

            EnsureRandomDay(now);
            if (runtime.RandomCount >= Math.Max(1, Configuration.RandomChatDailyLimit)) return;

            // 每个间隔窗口只掷一次：没中就把窗口时间推走，别每 1.5 秒重掷到必中。
            runtime.LastRandomAt = now;
            if (Random.Shared.NextDouble() >= Math.Clamp(Configuration.RandomChatChance, 0.0, 1.0))
            {
                SaveStateNow();
                return;
            }

            runtime.RandomCount++;
            SaveStateNow();

            string digest = TryBuildSubtitleDigest(session, lastSample.Pos, 3);
            string note = $"[一起看·随口] 你们正一起看着《{session.DisplayTitle}》，现在 {WatchFormat.Mmss(lastSample.Pos)}。"
                          + (digest.Length > 0 ? "\n" + digest : "")
                          + "\n想随口说一句就说，不想说就沉默；不要复述剧情，不要编。";
            _ = WakeAsync(note);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "一起看：随机搭话失败");
        }
    }

    void EnsureRandomDay(DateTime now)
    {
        string today = now.ToString("yyyy-MM-dd");
        if (!string.Equals(runtime.RandomDay, today, StringComparison.Ordinal))
        {
            runtime.RandomDay = today;
            runtime.RandomCount = 0;
        }
    }

    bool CanWake(DateTime now)
    {
        if (!Configuration.WakeOnDue) return false;
        if (InQuietHours(now)) return false;
        if (!functionCaller.IsIdle) return false;
        if (ChatBot.IsChatOccupied) return false;

        int minMinutes = Math.Max(1, Configuration.WakeMinIntervalMinutes);
        if (runtime.LastWakeAt != default && (now - runtime.LastWakeAt).TotalMinutes < minMinutes)
            return false;

        EnsureWakeDay();
        return runtime.WakeCount < Math.Max(1, Configuration.WakeDailyLimit);
    }

    async Task WakeAsync(string note)
    {
        if (waking) return;
        waking = true;
        try
        {
            EnsureWakeDay();
            runtime.WakeCount++;
            runtime.LastWakeAt = DateTime.Now;
            SaveStateNow();

            ILanguageModel? model = languageModel ?? ChatBot.LanguageModel;
            OccupationMarker? marker = null;
            if (model != null)
            {
                try { marker = model.GetThinkingRequester().Rent("陪看到点"); } catch { }
            }
            try
            {
                // 用 System 角色叫醒：聊天窗口里不会出现这段字幕/提醒，模型照常进入一轮。
                await ChatBot.ChatAsync(
                        new ChatMessageContent(AuthorRole.System, "[一起看·唤醒]\n" + note), true)
                    .ConfigureAwait(false);
            }
            finally
            {
                try { marker?.Dispose(); } catch { }
            }

            lastNote = note;
            lastNoteAt = DateTime.Now;
            if (session != null) session.LastPokeAt = lastNoteAt;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "一起看：主动唤醒失败");
        }
        finally
        {
            waking = false;
        }
    }

    /// <summary>
    /// 页面字幕（实验）：扩展开启后，每次上报带当前屏幕上的字幕文本。
    /// 按「收到时的播放位置」排成时间轴；同一句连续上报只留一条，最多 400 条。
    /// </summary>
    void LearnPageCaption(WatchSample sample)
    {
        string caption = (sample.Caption ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        if (caption.Length == 0) return;
        if (caption.Length > 200) caption = caption[..200];

        lock (pageGate)
        {
            if (caption == lastPageCaption) return;
            lastPageCaption = caption;
            double from = Math.Max(0, sample.Pos - 0.5);
            double to = from + Math.Max(2, Math.Min(10, caption.Length / 4.0));
            pageCues.Add(new SubtitleCue { From = from, To = to, Text = caption });
            if (pageCues.Count > 400)
                pageCues.RemoveRange(0, pageCues.Count - 400);
        }
    }

    /// <summary>
    /// 外部字幕文件：别的 ASR / 字幕工具正在写的那份。文件一变就重读，并把游标归零，
    /// 免得把新字幕当成「已经发过」。
    /// </summary>
    void RefreshExternalSubtitles(DateTime now)
    {
        if ((now - lastExternalCheck).TotalSeconds < 2) return;
        lastExternalCheck = now;

        string path = (Configuration.ExternalSubtitleFile ?? "").Trim().Trim('"');
        if (path.Length == 0 || !File.Exists(path))
        {
            lock (externalGate)
            {
                if (externalCues.Count > 0)
                {
                    externalCues = new List<SubtitleCue>();
                    externalLabel = "";
                    externalStamp = DateTime.MinValue;
                }
            }
            return;
        }

        try
        {
            DateTime stamp = File.GetLastWriteTimeUtc(path);
            lock (externalGate)
            {
                if (stamp == externalStamp) return;
            }

            List<SubtitleCue> cues = WatchSubtitles.ParseFile(path);
            int offset = Configuration.ExternalSubtitleOffsetSeconds;
            if (offset != 0) cues = WatchSubtitles.Shift(cues, offset);

            lock (externalGate)
            {
                externalCues = cues;
                externalStamp = stamp;
                externalLabel = Path.GetFileName(path);
            }

            if (session != null) session.SubtitleCursor = 0;
            lastNote = $"外部字幕已读入：{Path.GetFileName(path)} · {cues.Count} 句";
            lastNoteAt = now;
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "一起看：读外部字幕失败");
        }
    }

    void PokeNote(string text)
    {
        DateTime now = DateTime.Now;
        lastNote = text;
        lastNoteAt = now;
        if (session != null) session.LastPokeAt = now;
        try { UpsertSystemNote("[一起看·提醒]", text); } catch (Exception e) { logger.LogDebug(e, "一起看：提醒注入失败"); }
    }

    bool InQuietHours(DateTime now)
    {
        string value = (Configuration.QuietHours ?? "").Trim();
        if (value.Length == 0) return false;
        string[] parts = value.Split(new[] { '-', '~', '至' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return false;
        if (!TimeSpan.TryParse(parts[0].Trim(), out TimeSpan start)) return false;
        if (!TimeSpan.TryParse(parts[1].Trim(), out TimeSpan end)) return false;
        if (start == end) return false;
        TimeSpan time = now.TimeOfDay;
        return start < end ? time >= start && time < end : time >= start || time < end;
    }

    void EnsureWakeDay()
    {
        string today = DateTime.Now.ToString("yyyy-MM-dd");
        if (!string.Equals(runtime.WakeDay, today, StringComparison.Ordinal))
        {
            runtime.WakeDay = today;
            runtime.WakeCount = 0;
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 会话与状态持久化
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 观看笔记：播完/手动结束/换片时往聊天历史写一条 [我的观看笔记]。
    /// 借鉴 plus-one 的「观影卡」和 FeedScroll 的阅读笔记；Nexus.Memory 把
    /// 「我的观看笔记」加进外部笔记标记后会收成 L3（不进近忆）。
    /// </summary>
    async Task SaveWatchNoteAsync(WatchSession value, string reason)
    {
        if (!Configuration.AutoSaveWatchNote || value == null || value.WatchNoteSaved) return;
        value.WatchNoteSaved = true;
        try
        {
            double pos = value.MaxPos > 0 ? value.MaxPos : value.Duration;
            if (value.Duration > 0) pos = Math.Min(pos, value.Duration);

            var sb = new StringBuilder();
            sb.AppendLine($"[我的观看笔记] {DateTime.Now:yyyy-MM-dd HH:mm} · {reason}");
            sb.AppendLine($"片名：《{value.DisplayTitle}》");
            sb.AppendLine($"看到：{WatchFormat.Mmss(pos)} / {WatchFormat.Duration(value.Duration)}"
                          + $" · 已交付 {value.Delivered.Count} 段");
            string source = "";
            if (value == session) _ = TryGetCues(out source);
            if (!string.IsNullOrWhiteSpace(source))
                sb.AppendLine($"字幕源：{source}");
            if (value.Said.Count > 0)
            {
                sb.AppendLine("我说过的话：");
                foreach (WatchSaidLine line in value.Said.TakeLast(8))
                    sb.AppendLine($"- {WatchFormat.Mmss(line.At)}｜{line.Text}");
            }

            string body = sb.ToString().TrimEnd();
            if (body.Length > 800) body = body[..800];

            await ChatBot.EditChatHistoryAsync(thread =>
            {
                thread.ChatHistory.Add(new ChatMessageContent(AuthorRole.Assistant, body));
                return Task.CompletedTask;
            }, "一起看笔记").ConfigureAwait(false);

            SaveStateNow();
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "一起看：写观看笔记失败");
        }
    }

    public void StopSession(string reason = "手动结束")
    {
        WatchSession? old = session;
        session = null;
        lastTickPos = null;
        lastTickAt = default;
        lock (pageGate)
        {
            pageCues.Clear();
            lastPageCaption = "";
        }
        SaveStateNow();
        if (old != null) _ = SaveWatchNoteAsync(old, reason);
        lastNote = "这一场已结束：" + reason;
        lastNoteAt = DateTime.Now;
    }

    void LoadState()
    {
        try
        {
            if (!File.Exists(statePath)) return;
            string json = File.ReadAllText(statePath);
            WatchRuntimeState? loaded = JsonSerializer.Deserialize<WatchRuntimeState>(json, JsonOptions);
            if (loaded == null) return;

            runtime = loaded;
            session = loaded.Session;
            int maxHours = Math.Clamp(Configuration.SessionMaxHours, 1, 168);
            if (session != null && (DateTime.Now - session.UpdatedAt).TotalHours > maxHours)
                session = null;
            if (session != null)
            {
                session.Said ??= new List<WatchSaidLine>();
                session.Delivered ??= new List<int>();
            }
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "一起看：读状态失败");
        }
    }

    void SaveState()
    {
        if (!Configuration.PersistSession) return;
        SaveStateNow();
    }

    public void SaveStateNow()
    {
        if (!Configuration.PersistSession) return;
        try
        {
            runtime.Session = session;
            Directory.CreateDirectory(Path.GetDirectoryName(statePath) ?? pluginDir);
            string json = JsonSerializer.Serialize(runtime, JsonOptions);
            string tmp = statePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, statePath, overwrite: true);
            lastSave = DateTime.Now;
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "一起看：存状态失败");
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 工具结果回注：Alife 的 XML 函数会丢弃返回值，必须 Poke 回对话
    // ─────────────────────────────────────────────────────────────

    void WrapHandlerResultsForPoke(XmlHandler handler)
    {
        foreach (XmlFunction fn in handler.Functions)
        {
            MethodInfo? method = GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m =>
                {
                    XmlFunctionAttribute? attr = m.GetCustomAttribute<XmlFunctionAttribute>();
                    return attr != null && (attr.Name ?? m.Name).Equals(fn.Name, StringComparison.OrdinalIgnoreCase);
                });
            if (method == null) continue;

            ParameterInfo[] parameters = method.GetParameters();
            fn.Invoker = async (XmlContext ctx, CancellationToken ct) =>
            {
                if (ctx.CallMode != CallMode.OneShot)
                    throw new Exception($"调用 {fn.Name} 标签的方式错误，应该使用单个自闭合标签调用。");

                object?[] args = new object?[parameters.Length];
                for (int i = 0; i < parameters.Length; i++)
                {
                    ParameterInfo p = parameters[i];
                    if (p.ParameterType == typeof(CancellationToken)) { args[i] = ct; continue; }
                    if (typeof(XmlContext).IsAssignableFrom(p.ParameterType)) { args[i] = ctx; continue; }
                    if (p.GetCustomAttribute<XmlContentAttribute>() != null) { args[i] = ctx.Content; continue; }

                    object? value = null;
                    bool filled = false;
                    if (p.Name != null && ctx.Parameters.TryGetValue(p.Name, out string? raw))
                    {
                        TypeConverter converter = TypeDescriptor.GetConverter(p.ParameterType);
                        if (converter.CanConvertFrom(typeof(string)))
                        {
                            try
                            {
                                value = converter.ConvertFromInvariantString(raw);
                                filled = true;
                            }
                            catch
                            {
                                // 格式不对，落到默认值
                            }
                        }
                    }
                    if (!filled && p.HasDefaultValue)
                    {
                        value = p.DefaultValue;
                        filled = true;
                    }
                    if (!filled)
                        throw new Exception($"<{fn.Name}> 缺少 {p.Name} 参数，或参数格式不对！");
                    args[i] = value;
                }

                object? back = method.Invoke(this, args);
                string? text = null;
                if (back is Task<string> taskOfString) text = await taskOfString.ConfigureAwait(false);
                else if (back is Task task) await task.ConfigureAwait(false);
                else if (back is string plain) text = plain;

                if (!string.IsNullOrWhiteSpace(text))
                {
                    try { UpsertSystemNote($"[一起看·{fn.Name}]", Compact(text)); } catch { }
                }
            };
        }
    }

    string Compact(string text)
    {
        int max = Math.Clamp(Configuration.PokeMaxChars, 500, 12000);
        if (text.Length <= max) return text;
        int head = (int)(max * 0.8);
        int tail = Math.Max(40, max - head - 24);
        return text[..head] + "\n…（已截断）\n" + text[^tail..];
    }

    /// <summary>
    /// 把工具结果 / 到点提醒塞进 System 角色消息：AI 下一轮能读到，但聊天窗口不显示，
    /// 而且同 tag 只保留最新一条，不会一轮接一轮把上下文越堆越长。
    /// </summary>
    void UpsertSystemNote(string tag, string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return;
        string content = tag + "\n" + body;
        ChatBot.EditChatHistory(thread =>
        {
            var history = thread.ChatHistory;
            for (int i = history.Count - 1; i >= 0; i--)
            {
                if (history[i].Role != AuthorRole.System) continue;
                if ((history[i].Content ?? "").StartsWith(tag + "\n", StringComparison.Ordinal))
                    history.RemoveAt(i);
            }
            history.AddSystemMessage(content);
        }, "一起看·上下文注入");
    }

    // ─────────────────────────────────────────────────────────────
    // 字幕缓存
    // ─────────────────────────────────────────────────────────────

    internal string? FindSubtitle(WatchSession value)
        => WatchSubtitles.Find(value.Input, value.Title, Configuration.MaterialDirectory, Configuration.SubtitleExtensions);

    internal List<SubtitleCue> LoadCues(string path)
    {
        try
        {
            DateTime stamp = File.GetLastWriteTimeUtc(path);
            if (subtitleCache.TryGetValue(path, out var cached) && cached.Stamp == stamp)
                return cached.Cues;

            List<SubtitleCue> cues = WatchSubtitles.ParseFile(path);
            subtitleCache[path] = (stamp, cues);
            if (subtitleCache.Count > 24)
            {
                foreach (string key in subtitleCache.Keys.Take(subtitleCache.Count - 24).ToArray())
                    subtitleCache.Remove(key);
            }
            return cues;
        }
        catch
        {
            return new List<SubtitleCue>();
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 平台字幕与实时画面
    // ─────────────────────────────────────────────────────────────

    /// <summary>B 站 Cookie：优先用本插件配置的 SESSDATA，其次借用 BiliScroll 的登录态。</summary>
    string ResolveBiliCookie()
    {
        string configured = (Configuration.BiliSessdata ?? "").Trim();
        if (configured.Length > 0) return configured;

        try
        {
            string path = Path.Combine(AlifePath.StorageFolderPath, "Plugins",
                "presents.BiliScroll", "credentials.json");

            if (!File.Exists(path)) return "";

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (JsonProperty property in doc.RootElement.EnumerateObject())
            {
                if (!property.Name.Equals("SessData", StringComparison.OrdinalIgnoreCase)
                    && !property.Name.Equals("SESSDATA", StringComparison.OrdinalIgnoreCase))
                    continue;

                string value = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? ""
                    : "";

                if (value.Length > 0) return "SESSDATA=" + value;
            }
        }
        catch
        {
            // 借用失败就当没登录，人工字幕仍然能拿
        }

        return "";
    }

    /// <summary>拉平台字幕（后台，不挡工具调用）。带缓存与失败退避。</summary>
    async Task EnsurePlatformSubtitleAsync(WatchSession value)
    {
        if (!Configuration.EnableBiliSubtitle || biliSubs == null) return;

        string? bvid = BiliSubtitleClient.ExtractBvid(value.Url);
        if (bvid == null) return;

        DateTime now = DateTime.Now;
        bool sameVideo = string.Equals(platformKey, bvid, StringComparison.OrdinalIgnoreCase);
        if (sameVideo && platformSubtitle != null
            && (now - platformAt).TotalMinutes < Math.Max(5, Configuration.PlatformSubtitleCacheMinutes))
            return;
        if ((now - platformAttemptAt).TotalSeconds < 60) return;

        platformAttemptAt = now;

        PlatformSubtitle? result = await biliSubs.GetAsync(bvid, DestroyCancellationToken).ConfigureAwait(false);

        platformKey = bvid;
        platformAt = DateTime.Now;

        if (result == null)
        {
            // GetAsync 返回 null：接口查无此视频（-404，下架/私密/课程番剧页）或接口失败。
            // 记下来给状态面板看，别让角色一直以为「还没拉取」。
            if (!sameVideo)
            {
                platformSubtitle = null;
                platformMissKey = bvid;
                platformMissAt = DateTime.Now;
            }
            return;
        }

        // 拉到但零句：这支视频没有字幕轨（人工/AI 都没有）。
        // 别让角色一直以为「还没拉到」，也别让它编台词——直接说明要靠画面/语音。
        if (result.Cues.Count == 0)
        {
            if (!sameVideo)
            {
                platformSubtitle = null;
                lastNote = $"《{value.DisplayTitle}》在 B 站没有字幕轨；这类片子只能靠画面或语音转写";
                lastNoteAt = DateTime.Now;
            }
            return;
        }

        // 拉到了：清掉 404 记录
        platformMissKey = "";
        platformMissAt = DateTime.MinValue;

        bool firstTime = platformSubtitle == null || !sameVideo;
        platformSubtitle = result;

        // 新拿到的字幕从头给一次：游标归零，避免「刚拉到却漏了前面几句」
        if (firstTime && session != null
            && string.Equals(session.Key, value.Key, StringComparison.Ordinal))
            session.SubtitleCursor = 0;
    }

    /// <summary>网页视频抓帧决策：桥每收到一次位置就问一次「该拍了吗」。</summary>
    bool ShouldCaptureNow(WatchSample sample)
    {
        // 每拍顺手同步一次开关：角色级配置可能覆盖全局配置，
        // 只在启动时赋值会一直用旧值（ASR/字幕开关切了不生效）。
        // 注意 WantAsr 要尊重 asrMuted——纯 BGM 判定后不能被这里覆盖回 true。
        WatchBridge? current = bridge;
        if (current != null)
        {
            current.WantAsr = Configuration.EnableAsr && !asrMuted;
            current.AsrSeconds = Math.Clamp(Configuration.AsrChunkSeconds, 10, 60);
            current.WantFrames = Configuration.AutoLiveFrames;
            current.FrameworkAsr = HasRecognizer;
            current.WantCaption = Configuration.AutoPageCaption;
            current.CaptionSelector = Configuration.CaptionSelector ?? "";
        }

        if (!Configuration.AutoLiveFrames || liveFrames == null) return false;
        if (Configuration.SuspendWhenPaused && sample.Paused) return false;

        string input = WatchSource.InputFrom(sample) ?? "";
        if (string.IsNullOrWhiteSpace(input) || WatchSource.IsLocalFile(input)) return false;

        double gap = Math.Max(5, Configuration.LiveFrameIntervalSeconds);
        if ((DateTime.Now - lastCaptureAt).TotalSeconds < gap) return false;

        lastCaptureAt = DateTime.Now;   // 先记账：请求失败也不会每拍狂刷
        return true;
    }

    /// <summary>扩展上传的音频切片：入队后异步转写，转写结果进字幕源（只保留本场会话）。</summary>
    void AcceptAudioChunk(byte[] audio, double pos, string url)
    {
        // 录音链路诊断：哪怕被 BGM 静音，也要让状态面板知道「片是送到了的」
        WatchBridge? bridgeNow = bridge;
        if (bridgeNow != null)
        {
            bridgeNow.AudioCount++;
            bridgeNow.LastAudioAt = DateTime.Now;
        }

        if (!Configuration.EnableAsr || (whisper == null && senseAsr == null)) return;

        string key = WatchSource.SessionKey(
            string.IsNullOrWhiteSpace(url) ? (session?.Input ?? "") : url);

        // 换片先重置（包括 BGM 判定）：新的片子要重新听，不能沿用上一场的结论
        lock (asrGate)
        {
            if (!string.Equals(asrSessionKey, key, StringComparison.Ordinal))
            {
                asrCues.Clear();
                asrSessionKey = key;
                asrDone = 0;
                asrFailed = 0;
                asrLastError = "";
                asrEmptyStreak = 0;
                asrMuted = false;
            }
        }

        // 这一场已确认纯 BGM：不再收音频，也不再占 CPU（换片后会自动恢复）
        if (asrMuted) return;

        _ = RunAsrAsync(audio, pos, key);
    }

    readonly SemaphoreSlim asrSemaphore = new(1, 1);

    async Task RunAsrAsync(byte[] audio, double pos, string key)
    {
        await asrSemaphore.WaitAsync().ConfigureAwait(false);

        try
        {
            // 优先 SenseVoice（框架本地识别器）；不可用时退回 whisper.cpp
            SenseVoiceAsr? sense = senseAsr;
            WhisperAsr.Result result;
            string engine = "whisper";
            try
            {
                if (sense != null)
                {
                    engine = "SenseVoice";
                    // SenseVoice 单次转写加 20 秒上限：VAD 在"人声+伴奏"上偶发卡死/空转
                    using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(DestroyCancellationToken);
                    cts.CancelAfter(TimeSpan.FromSeconds(20));
                    bool timedOut = false;
                    try
                    {
                        result = await sense.TranscribeAsync(audio, pos, key, cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!DestroyCancellationToken.IsCancellationRequested)
                    {
                        timedOut = true;
                        logger.LogInformation("[一起看] SenseVoice 超过 20 秒未返回，回退 whisper");
                        result = new WhisperAsr.Result(new List<SubtitleCue>(), "", true, 0, "", "");
                    }

                    // 兜底：SenseVoice 的 VAD 对"人声+伴奏"可能整个滤空（日语歌实测），
                    // 此时若音频明显有能量且 whisper 可用 → 用 whisper 再转一次
                    if (!timedOut && result.NoSpeech && sense.LastHadEnergy && whisper != null && Configuration.AsrMusicFallback)
                    {
                        engine = "whisper";
                        logger.LogInformation("[一起看] SenseVoice 无输出但音频有能量，回退 whisper（关 VAD，音乐兜底）");
                        // 歌曲/强 BGM：whisper 的 Silero VAD 会把人声当非语音滤掉，必须关
                        result = await whisper.TranscribeAsync(audio, pos, key, DestroyCancellationToken, useVad: false).ConfigureAwait(false);
                    }
                    else if (timedOut && whisper != null)
                    {
                        engine = "whisper";
                        // 超时兜底：保持 VAD 开启（这条路径多为异常，不是歌曲）
                        result = await whisper.TranscribeAsync(audio, pos, key, DestroyCancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    result = await whisper!.TranscribeAsync(audio, pos, key, DestroyCancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception e3)
            {
                logger.LogWarning("[一起看] 转写引擎异常：{0}", e3.Message);
                result = new WhisperAsr.Result(new List<SubtitleCue>(), "转写引擎异常：" + e3.Message);
            }

            lock (asrGate)
            {
                if (string.Equals(asrSessionKey, key, StringComparison.Ordinal) && result.Cues.Count > 0)
                {
                    asrCues.AddRange(result.Cues);
                    asrCues.Sort((a, b) => a.From.CompareTo(b.From));
                    asrEngineLabel = engine;

                    if (asrCues.Count > 4000)
                        asrCues.RemoveRange(0, asrCues.Count - 4000);
                }

                if (result.Error.Length > 0)
                {
                    asrFailed++;
                    asrLastError = result.Error;
                }
                else if (result.NoSpeech)
                {
                    // 这一段没人声（纯 BGM / 静音）：不记失败，累计连续次数
                    asrEmptyStreak++;
                    asrLastError = "";

                    if (asrEmptyStreak >= 3 && !asrMuted)
                    {
                        asrMuted = true;
                        lastNote = "连续 3 段没有可辨识人声（纯 BGM），已停用语音转写，改用画面文字";
                        lastNoteAt = DateTime.Now;
                    }
                }
                else
                {
                    asrDone++;
                    asrLastError = "";
                    asrEmptyStreak = 0;
                }
            }

            WatchBridge? current = bridge;
            if (current != null)
            {
                // 确认是纯 BGM 后，让扩展别再录音（省 CPU 也省带宽）
                if (asrMuted) current.WantAsr = false;

                current.AsrRaw = result.Raw;
            if (result.Cues.Count > 0)
                current.AddTrace("zh-in: " + result.Cues[0].Text.Substring(0, Math.Min(24, result.Cues[0].Text.Length)));
                current.AsrNote = result.Error.Length > 0
                    ? "失败：" + result.Error
                    : result.NoSpeech
                        ? (asrMuted
                            ? "本片只有背景音乐，转写已停用"
                            : result.Dropped > 0
                                ? $"这一段没人声（丢弃 {result.Dropped} 句：{result.Sample}）"
                                : $"这一段没人声 @ {WatchFormat.Mmss(pos)}")
                        : $"转写 +{result.Cues.Count} 句 @ {WatchFormat.Mmss(pos)}";
            }
        }
        catch (OperationCanceledException)
        {
            // 模块销毁，正常退出
        }
        catch (Exception ex)
        {
            lock (asrGate)
            {
                asrFailed++;
                asrLastError = ex.Message;
            }
        }
        finally
        {
            asrSemaphore.Release();
        }
    }

    /// <summary>下载当前 B 站视频（工具 download=true 调用）。</summary>
    public async Task<string> DownloadCurrentAsync(CancellationToken ct)
    {
        if (!Configuration.EnableDownload)
            return "下载功能已关闭（控制台「允许下载视频」）。";

        if (biliMedia == null)
            return "下载器未初始化。";

        string url = session?.Url ?? lastSample?.Url ?? "";
        string? bvid = BiliSubtitleClient.ExtractBvid(url);

        if (bvid == null)
            return "这条不是 B 站链接。其它站点没有通用抽取器（装 yt-dlp 才能下），"
                   + "可以先用浏览器自带的「另存为」，再调 watch_together(input=\"本地文件\")。";

        (BiliMedia.Streams? streams, string error) = await biliMedia.GetStreamsAsync(bvid, ct).ConfigureAwait(false);

        if (streams == null)
            return "拿流失败：" + error;

        string folder = (Configuration.DownloadFolder ?? "").Trim().Trim('"');
        if (folder.Length == 0) folder = Path.Combine(dataDir, "downloads");

        string? ffmpeg = WatchFrames.FindFfmpeg(Configuration.FfmpegPath ?? "");
        (string? path, string mergeNote) = await biliMedia
            .DownloadAsync(streams, folder, ffmpeg ?? "", ct)
            .ConfigureAwait(false);

        if (path == null)
            return "下载失败：" + mergeNote;

        long sizeMb = new FileInfo(path).Length / 1048576;

        return $"已下载：{path}（{sizeMb} MB，{streams.Note}）"
               + (mergeNote.Length > 0 ? " · " + mergeNote : "")
               + "\n之后可以直接 watch_together(input=\"" + path + "\") 用本地文件抽帧/转写。";
    }

    /// <summary>扩展上传的帧：按会话归档，供工具层拼「最近几张画面」。返回空串=收下，否则为拒绝原因。</summary>
    string AcceptLiveFrame(string url, byte[] jpeg, double pos, bool changed)
    {
        if (liveFrames == null) return "实时帧仓库未初始化";
        if (!Configuration.AutoLiveFrames) return "「网页视频实时抓帧」已关闭";

        string input = string.IsNullOrWhiteSpace(url) ? (session?.Input ?? "") : url;
        if (string.IsNullOrWhiteSpace(input)) return "没有会话（上报里缺 url）";

        string key = WatchSource.SessionKey(input);
        if (string.IsNullOrWhiteSpace(key)) return "会话键为空";

        if (!liveFrames.Accept(key, jpeg, pos, changed))
            return string.IsNullOrWhiteSpace(liveFrames.LastError) ? "存帧失败" : liveFrames.LastError;

        lastCaptureAt = DateTime.Now;
        return "";
    }

    /// <summary>字幕源：外部文件 → 平台字幕（B 站）→ 本地同名 → 页面字幕。找不到线索时返回 null。</summary>
    internal List<SubtitleCue>? TryGetCues(out string source)
    {
        if (session != null) _ = EnsurePlatformSubtitleAsync(session);

        lock (externalGate)
        {
            if (externalCues.Count > 0)
            {
                source = "外部字幕 " + externalLabel;
                return externalCues;
            }
        }

        PlatformSubtitle? platform = platformSubtitle;
        if (platform != null && platform.Cues.Count > 0
            && string.Equals(platform.Bvid, BiliSubtitleClient.ExtractBvid(session?.Url), StringComparison.OrdinalIgnoreCase))
        {
            source = platform.Label;
            return platform.Cues;
        }

        lock (asrGate)
        {
            if (asrCues.Count > 0 && session != null
                && string.Equals(asrSessionKey, session.Key, StringComparison.Ordinal))
            {
                source = $"本地转写 {asrEngineLabel}（{asrCues.Count} 句）";
                return asrCues.OrderBy(c => c.From).ToList();
            }
        }

        string? path = session != null ? FindSubtitle(session) : null;
        if (path != null)
        {
            source = "本地字幕 " + Path.GetFileName(path);
            return LoadCues(path);
        }

        lock (pageGate)
        {
            if (pageCues.Count > 0)
            {
                source = $"页面字幕（{pageCues.Count} 句）";
                // 回看时新字幕可能插到更早的位置，按播放时间排序后再给窗口。
                return pageCues.OrderBy(c => c.From).ToList();
            }
        }

        source = "";
        return null;
    }

    string TryBuildNoSubtitleText(WatchSession value)
    {
        if (WatchSource.IsLocalFile(value.Input))
            return "[字幕] 没找到同名字幕；可以把 .srt/.vtt 放在视频同目录，或在控制台配置「素材目录」。";

        string external = (Configuration.ExternalSubtitleFile ?? "").Trim();
        if (external.Length > 0 && File.Exists(external))
            return $"[字幕] 外部字幕文件读不出内容：{Path.GetFileName(external)}；检查格式/编码，或先清空这一项。";

        return "[字幕] 没有拿到字幕。可能的下一步：\n"
               + "· 非 B 站站点：控制台「自动读取页面字幕」开关打开（默认开），扩展会读页面字幕元素；\n"
               + "· 站点类名很怪：在「自定义字幕选择器」填 CSS 选择器；\n"
               + "· 彻底没有字幕轨：可配「外部字幕文件」（任何 ASR/字幕工具导出的 SRT 都行）。\n"
               + "都没有时按进度和画面陪聊，不要编台词。";
    }

    /// <summary>
    /// 游标式字幕窗口（fish-buddy 的白板口径）：
    /// 只给当前位置往前 N 秒、且「上次没给过」的句子；空的时候给一行短说明，不贴整段。
    /// 剧透方向默认关：只给已经看过的，不给未来。
    /// </summary>
    internal string TryBuildSubtitleWindow(WatchSession value, double pos, bool advanceCursor)
    {
        List<SubtitleCue>? cues = TryGetCues(out string source);
        if (cues == null || cues.Count == 0)
            return TryBuildNoSubtitleText(value);

        SubtitleWindowResult window = WatchSubtitles.BuildWindow(
            cues, pos,
            Math.Max(10, Configuration.SubtitleWindowSeconds),
            Math.Max(0, Configuration.SubtitleAheadSeconds),
            Math.Clamp(Configuration.SubtitleMaxLines, 3, 120),
            Math.Clamp(Configuration.MaxSubtitleChars, 200, 8000),
            value.SubtitleCursor, source);
        if (advanceCursor) value.SubtitleCursor = window.Cursor;
        return window.Text;
    }

    /// <summary>补看模式：按整段给，不推进窗口游标。</summary>
    internal string TryBuildSubtitleSegment(WatchSession value, WatchSegment seg)
    {
        List<SubtitleCue>? cues = TryGetCues(out string source);
        if (cues == null || cues.Count == 0)
            return TryBuildNoSubtitleText(value);

        int count = WatchSubtitles.CountInRange(cues, seg.From, seg.To);
        string text = WatchSubtitles.BuildSegmentText(cues, seg.From, seg.To, Configuration.MaxSubtitleChars);
        if (text.Length == 0)
            return $"[字幕] {source} · 本段没有台词（{WatchFormat.Mmss(seg.From)}-{WatchFormat.Mmss(seg.To)}）。";
        return $"[字幕] {source} · 本段 {count} 句\n{text}";
    }

    /// <summary>到点/随机搭话的短摘要：最近几句，不推进游标，不附整段字幕。</summary>
    internal string TryBuildSubtitleDigest(WatchSession value, double pos, int lines)
    {
        if (lines <= 0) return "";
        List<SubtitleCue>? cues = TryGetCues(out string source);
        if (cues == null || cues.Count == 0) return "";
        return WatchSubtitles.BuildDigest(
            cues, pos,
            Math.Max(30, Configuration.SubtitleWindowSeconds),
            lines, 420, source);
    }

    // ─────────────────────────────────────────────────────────────
    // 面板动作
    // ─────────────────────────────────────────────────────────────

    public string UiStatusText() => BuildStatusText();

    public bool UiBridgeRunning => bridge?.Running == true;
    public int UiBridgePort => bridge?.Snapshot()?.Port ?? Configuration.Port;
    public string UiNowTitle
    {
        get
        {
            string title = (lastSample?.Title ?? "").Trim();
            if (title.Length > 0) return title;
            title = (bridge?.Snapshot()?.Title ?? "").Trim();
            return title.Length > 0 ? title : (session?.DisplayTitle ?? "还没接到视频");
        }
    }
    public string UiNowProgress
    {
        get
        {
            if (lastSample != null)
                return $"{WatchFormat.Mmss(lastSample.Pos)} / {WatchFormat.Duration(lastSample.Dur ?? 0)}";
            BridgeStatus? st = bridge?.Snapshot();
            return st?.Pos is double pos ? $"{WatchFormat.Mmss(pos)} / {WatchFormat.Duration(st.Dur ?? 0)}" : "—";
        }
    }
    public string UiNowState
    {
        get
        {
            if (lastSample == null) return "还没有上报";
            return lastSample.Ended ? "已播完" : lastSample.Paused ? "暂停" : "播放中";
        }
    }
    public string UiDesktopMediaText
    {
        get
        {
            if (!Configuration.EnableDesktopMedia) return "桌面媒体：关";
            WatchSample? sample = desktopMedia?.Latest;
            if (sample != null)
                return "桌面媒体：已接入" + (string.IsNullOrWhiteSpace(sample.AppId) ? "" : " · " + sample.AppId);
            return "桌面媒体：等待播放";
        }
    }
    public string UiSegmentText
    {
        get
        {
            if (lastEstimate?.Known != true) return "—";
            WatchSegment seg = plan.SegmentAt(lastEstimate.Pos);
            return $"第 {seg.Index + 1} 段 · {WatchFormat.Mmss(seg.From)}-{WatchFormat.Mmss(seg.To)}";
        }
    }

    public string UiSaveConfig()
    {
        try
        {
            string path = Path.Combine(AlifePath.StorageFolderPath, "Configuration", typeof(WatchTogetherModule).FullName + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(Configuration, new JsonSerializerOptions { WriteIndented = true }));
            return "配置已保存：" + path;
        }
        catch (Exception e)
        {
            return "保存配置失败：" + e.Message;
        }
    }

    public string UiResetSession()
    {
        StopSession("从控制台结束");
        return "已结束当前陪看，进度清空。";
    }

    public string UiToggleBridge()
    {
        if (bridge?.Running == true)
        {
            StopBridge();
            return "位置桥已停止。";
        }
        return StartBridge() ? "位置桥已启动。" : $"启动失败：{bridge?.LastError}";
    }

    public string UiClearFrames()
    {
        try
        {
            string frames = Path.Combine(dataDir, "frames");
            if (!Directory.Exists(frames)) return "还没有抽过帧。";
            Directory.Delete(frames, recursive: true);
            return "已清空抽帧缓存。";
        }
        catch (Exception e)
        {
            return "清空失败：" + e.Message;
        }
    }

    public string UiOpenExtensionFolder()
    {
        try
        {
            string path = UiExtensionDirFull;
            if (!Directory.Exists(path)) return "扩展目录不存在：" + UiExtensionDir;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            return "已打开扩展目录：" + UiExtensionDir;
        }
        catch (Exception e)
        {
            return "打开失败：" + e.Message;
        }
    }
}
