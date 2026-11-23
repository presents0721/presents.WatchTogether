using System;
using System.ComponentModel;

namespace presents.WatchTogether;

/// <summary>
/// 一起看：本地播放位置桥 + 分段陪看。所有默认值都取自上游 dsh-watch-together 的实测口径。
/// </summary>
public class WatchTogetherConfig
{
    // ─────────────────────────────────────────────────────────────
    // 总开关与桥接
    // ─────────────────────────────────────────────────────────────

    [DisplayName("启用视频陪看")]
    [Description("关闭后不监听本地端口，所有工具只返回「已停用」。")]
    public bool Enable { get; set; } = true;

    [DisplayName("自动启动位置桥")]
    [Description("插件唤醒时自动监听 127.0.0.1。关掉后仍可在控制台手动启动。")]
    public bool AutoStartBridge { get; set; } = true;

    [DisplayName("读取桌面播放器（SMTC）")]
    [Description("默认开：读取 Windows 系统媒体会话（Media Player、PotPlayer、VLC 等接入 SMTC 的应用）。浏览器仍由扩展上报，这里会跳过浏览器，避免重复。")]
    public bool EnableDesktopMedia { get; set; } = true;

    [DisplayName("监听端口")]
    [Description("浏览器扩展上报播放位置用的本地端口，默认 9377。改端口后扩展 popup 里的端口也要改成一样。")]
    public int Port { get; set; } = 9377;

    [DisplayName("位置多久没更新算断流（秒）")]
    [Description("扩展每秒上报一次。超过这个时间没收到新值就不再按旧位置推算，避免拿着死数据编台词，默认 20 秒。")]
    public int BridgeStaleSeconds { get; set; } = 20;

    [DisplayName("过期读数认片时长（秒）")]
    [Description("用户切到聊天窗口时扩展会暂停上报，但片子还是同一支。这段时间内仍用旧读数认片名，默认 120 秒。")]
    public int IdentifyStaleSeconds { get; set; } = 120;

    [DisplayName("等视频出现（秒）")]
    [Description("工具不带 input 调用、且当前没有可读位置时，最多等这么久让用户点开视频，默认 8 秒。0=不等。")]
    public int AutoWaitSeconds { get; set; } = 8;

    // ─────────────────────────────────────────────────────────────
    // 段表
    // ─────────────────────────────────────────────────────────────

    [DisplayName("稳定段长（秒）")]
    [Description("阶梯走完后的段长，默认 300 秒（5 分钟）。")]
    public int SegmentSeconds { get; set; } = 300;

    [DisplayName("起步阶梯（秒）")]
    [Description("逗号分隔。开头段短、逐段加长，让角色不用等满 5 分钟才第一次开口；默认 60,120,180,240。留空=全程等长。")]
    public string RampSeconds { get; set; } = "60,120,180,240";

    [DisplayName("交付提前量（秒）")]
    [Description("在段末前多少秒提醒角色说话，默认 15 秒。放在段中间插话比卡在切换点自然。")]
    public int LeadSeconds { get; set; } = 15;

    [DisplayName("预取余量（秒）")]
    [Description("从当前位置往后，至少要保证备好这么长的内容，默认 300 秒。当前版本只影响状态展示与素材预读范围。")]
    public int LookaheadSeconds { get; set; } = 300;

    [DisplayName("每段画面格数")]
    [Description("本地视频抽帧时每段取几格，默认 5。0=不抽帧。")]
    public int Cells { get; set; } = 5;

    [DisplayName("位置新鲜窗口（秒）")]
    [Description("上报在这个时间内视为「刚读到」，直接用；超过则按时钟外推。默认 5 秒。")]
    public int FreshSeconds { get; set; } = 5;

    [DisplayName("外推上限（秒）")]
    [Description("在播但读数旧了，最多按时钟往前推这么久；超过就承认不知道，默认 60 秒。")]
    public int MaxExtrapolateSeconds { get; set; } = 60;

    // ─────────────────────────────────────────────────────────────
    // 提醒策略
    // ─────────────────────────────────────────────────────────────

    [DisplayName("到点提醒（Poke）")]
    [Description("每段快到交付点时，往对话里推一条短提醒；默认开。不额外调模型，只在角色下次被叫到时省一次查进度。")]
    public bool PokeOnDue { get; set; } = true;

    [DisplayName("到点主动唤醒")]
    [Description("到交付点时主动叫醒模型说一句；默认关，避免没人看视频时白烧 token。开启后受下面的最小间隔与每日上限约束。")]
    public bool WakeOnDue { get; set; } = false;

    [DisplayName("随机搭话")]
    [Description("陪看时按概率自己开口说一句，更像真的在旁边一起看；默认关。每命中一次 = 一轮模型请求，会花 token。")]
    public bool EnableRandomChat { get; set; } = false;

    [DisplayName("随机搭话最小间隔（分钟）")]
    [Description("两次随机搭话至少隔多久，默认 10 分钟；间隔到了才掷骰子。")]
    public int RandomChatEveryMinutes { get; set; } = 10;

    [DisplayName("随机搭话概率")]
    [Description("到间隔后掷骰子的命中概率，默认 0.35。骰子本身不花 token。")]
    public double RandomChatChance { get; set; } = 0.35;

    [DisplayName("随机搭话每日上限")]
    [Description("一天最多随机开口几次，默认 24。到上限后只保留到点提醒。")]
    public int RandomChatDailyLimit { get; set; } = 24;

    [DisplayName("主动唤醒最小间隔（分钟）")]
    [Description("两次主动唤醒至少隔这么久，默认 3 分钟。")]
    public int WakeMinIntervalMinutes { get; set; } = 3;

    [DisplayName("主动唤醒每日上限")]
    [Description("一天最多主动唤醒几次，默认 12 次。到上限后只 Poke 不唤醒。")]
    public int WakeDailyLimit { get; set; } = 12;

    [DisplayName("静默时段")]
    [Description("如 23:00-08:00。这段时间不 Poke、不唤醒；留空=不静默。跨午夜也支持。")]
    public string QuietHours { get; set; } = "";

    [DisplayName("换片时提醒")]
    [Description("检测到换了另一支视频时推一条提示，默认开。角色不会自动接着跟新片，需要重新调用工具。")]
    public bool PokeOnSwitch { get; set; } = true;

    [DisplayName("播完时提醒")]
    [Description("检测到视频播完时推一条提示，默认开。")]
    public bool PokeOnEnded { get; set; } = true;

    [DisplayName("拖动判定阈值（秒）")]
    [Description("实际位置比按时钟推算的期望位置偏离超过这么多，算用户拖了进度条，默认 60 秒。")]
    public int JumpSeconds { get; set; } = 60;

    [DisplayName("拖动提醒冷却（分钟）")]
    [Description("两次拖动提醒至少隔这么久，默认 3 分钟，避免拖一下问一句。")]
    public int JumpCooldownMinutes { get; set; } = 3;

    [DisplayName("暂停时不提醒")]
    [Description("默认开。暂停期间不发到点提醒；恢复播放后按新位置继续。")]
    public bool SuspendWhenPaused { get; set; } = true;

    // ─────────────────────────────────────────────────────────────
    // 素材
    // ─────────────────────────────────────────────────────────────

    [DisplayName("字幕扩展名")]
    [Description("逗号分隔，默认 srt,vtt。本地视频会找同名字幕；网页视频没有本地字幕。")]
    public string SubtitleExtensions { get; set; } = "srt,vtt";

    [DisplayName("外部字幕文件")]
    [Description("可选：一个按视频时间轴的 .srt/.vtt 路径（例如别的 ASR / 字幕工具生成的）。填了之后网页视频也有字幕；文件一变就重读，并自动重置字幕游标。")]
    public string ExternalSubtitleFile { get; set; } = "";

    [DisplayName("外部字幕时间偏移（秒）")]
    [Description("外部字幕比视频时间轴晚/早多少秒，默认 0。只有从录音开始生成的字幕才需要调。")]
    public int ExternalSubtitleOffsetSeconds { get; set; } = 0;

    [DisplayName("字幕窗口（秒）")]
    [Description("工具返回时只给当前位置往前这么多秒的字幕，默认 120。比整段 5 分钟省 token，也更像「刚刚看的」。")]
    public int SubtitleWindowSeconds { get; set; } = 120;

    [DisplayName("字幕往后预告（秒）")]
    [Description("0=只给已经看过的字幕，不剧透；大于 0 会给一点未来的字幕。默认 0。")]
    public int SubtitleAheadSeconds { get; set; } = 0;

    [DisplayName("字幕窗口最多几句")]
    [Description("一次最多返回几句，默认 40；超出只给最近的，并标明前面省略了几句。")]
    public int SubtitleMaxLines { get; set; } = 40;

    [DisplayName("到点摘要句数")]
    [Description("到点提醒里附最近几句字幕，默认 3；0=不附。提醒只给摘要，完整字幕留在工具结果里。")]
    public int SubtitleDigestLines { get; set; } = 3;

    [DisplayName("实时上下文刷新(秒)")]
    [Description("播放中每隔多少秒把「当前进度 + 最近几句字幕 + 最近画面变化」作为隐藏的系统上下文刷新给 AI，让它随时开口都知道现在看到哪；只保留最新一条，不额外占上下文。0=关闭，默认 30。")]
    public int LiveContextSeconds { get; set; } = 30;

    [DisplayName("素材目录")]
    [Description("额外的字幕/视频查找目录，可留空。填了会优先在这里找同名字幕。")]
    public string MaterialDirectory { get; set; } = "";

    [DisplayName("每段字幕字数上限")]
    [Description("给模型的段内字幕原文最多多少字，默认 1200。太多会占 token。")]
    public int MaxSubtitleChars { get; set; } = 1200;

    [DisplayName("抽帧优先用字幕时间点")]
    [Description("字幕里有句子时，按句子时间点抽帧，帧和台词更容易对上；默认开。没有字幕时仍均匀抽。")]
    public bool FrameTimesFromSubtitle { get; set; } = true;

    [DisplayName("自动抽帧")]
    [Description("本地视频调用工具时自动抽当前段的画面；需要 ffmpeg。默认开，找不到 ffmpeg 就自动降级。")]
    public bool AutoFrames { get; set; } = true;

    [DisplayName("生成概览拼图")]
    [Description("抽帧后额外拼一张 2×3 的概览图，模型看一张就能扫完整段；默认开。需要 ffmpeg 的 tile 滤镜。")]
    public bool ContactSheet { get; set; } = true;

    [DisplayName("ffmpeg 路径")]
    [Description("留空=自动找：PATH、imageio-ffmpeg、常见安装目录。可以直接填 ffmpeg.exe 或解压目录。")]
    public string FfmpegPath { get; set; } = "";

    [DisplayName("抽帧宽度")]
    [Description("缩略图宽度上限，默认 640，高度按比例；原片比它窄时不放大。")]
    public int FrameMaxWidth { get; set; } = 640;

    // ─────────────────────────────────────────────────────────────
    // 会话
    // ─────────────────────────────────────────────────────────────

    [DisplayName("保存陪看进度")]
    [Description("把当前片子的段进度写到 state.json，插件重启后接着看；默认开。")]
    public bool PersistSession { get; set; } = true;

    [DisplayName("会话保留时长（小时）")]
    [Description("超过这么久没更新就丢掉旧会话，默认 12 小时。")]
    public int SessionMaxHours { get; set; } = 12;

    [DisplayName("播完自动写观看笔记")]
    [Description("播完/手动结束/换片时，往聊天历史写一条 [我的观看笔记]（片名、看到哪、段数、角色说过的话）。默认开；Nexus.Memory 的外部笔记标记里加上「我的观看笔记」就会自动收成 L3。")]
    public bool AutoSaveWatchNote { get; set; } = true;

    [DisplayName("工具结果回注上限（字）")]
    [Description("Alife 的 XML 函数会丢弃返回值，插件需要把结果 Poke 回对话。这里限制单条回注的最大字数，默认 3800。")]
    public int PokeMaxChars { get; set; } = 3800;

    // ─────────────────────────────────────────────────────────────
    // 平台字幕（B 站）
    // ─────────────────────────────────────────────────────────────

    [DisplayName("读取 B 站字幕")]
    [Description("开（默认）：B 站视频自动走官方接口取 CC / AI 字幕（view → wbi/v2 → subtitle_url），比读页面 DOM 稳得多，讲解类视频的台词就靠它。关掉只认外部字幕文件 / 页面字幕。")]
    public bool EnableBiliSubtitle { get; set; } = true;

    [DisplayName("B 站 SESSDATA（可选）")]
    [Description("B 站登录 Cookie 里的 SESSDATA。AI 字幕（ai-zh）通常要登录态才给；人工 CC 字幕不登录也能拿。留空时会自动借用 presents.BiliScroll 的 credentials.json。")]
    public string BiliSessdata { get; set; } = "";

    [DisplayName("平台字幕缓存（分钟）")]
    [Description("同一支视频的字幕拉一次就能用很久，默认缓存 30 分钟；换片会立即重拉。")]
    public int PlatformSubtitleCacheMinutes { get; set; } = 30;

    // ─────────────────────────────────────────────────────────────
    // 实时画面（浏览器扩展抓帧）
    // ─────────────────────────────────────────────────────────────

    [DisplayName("网页视频实时抓帧")]
    [Description("开（默认）：网页视频也能有画面。做法是扩展用 tabCapture 抓标签页当前帧，画面变了才上传（PPT / 图表讲解类内容全在画面里，这条最重要）。需在扩展 popup 里点「开始抓画面」。")]
    public bool AutoLiveFrames { get; set; } = true;

    [DisplayName("抓帧最小间隔（秒）")]
    [Description("两次抓帧至少隔多久，默认 20 秒。太密会占带宽和 token；PPT 场景 20–30 秒通常够（翻页会立刻触发）。")]
    public int LiveFrameIntervalSeconds { get; set; } = 20;

    [DisplayName("画面变化阈值（0–100）")]
    [Description("扩展把画面缩成 96×54 灰度比对，平均差异超过这个值才上传。默认 6：翻页/切镜必触发，轻微噪点不触发。音乐可视化这种一直动的画面可调到 15 减少上传。")]
    public double LiveFrameChangeThreshold { get; set; } = 6;

    [DisplayName("实时帧保留张数")]
    [Description("每场会话保留最近几张实时帧，默认 12。工具会把最近几张的路径给模型；旧的自动删。")]
    public int LiveFrameKeep { get; set; } = 12;

    [DisplayName("实时帧宽度")]
    [Description("上传帧的最大宽度，默认 1280。PPT 文字要看清就保持 1280；流量敏感可降到 960。")]
    public int LiveFrameMaxWidth { get; set; } = 1280;

    [DisplayName("实时帧质量（JPEG 55–90）")]
    [Description("扩展截图编码质量，默认 72。文字清晰度和体积的平衡点。")]
    public int LiveFrameQuality { get; set; } = 72;

    [DisplayName("画面概览拼图")]
    [Description("开（默认）：工具返回时把最近几帧拼成一张（3×N），模型看一张就能扫完这一段，省视觉调用。少于 3 帧不拼。")]
    public bool LiveFrameSheet { get; set; } = true;

    [DisplayName("概览格数")]
    [Description("拼图最多几格（4 / 6 / 9），默认 6。格数越多单格越小，PPT 文字要看清选 4。")]
    public int LiveFrameSheetCells { get; set; } = 6;

    [DisplayName("画面文字 OCR")]
    [Description("开（默认）：用 Windows 自带 OCR 读画面上的文字。PPT/图表讲解类没有语音台词，内容全在字上——没配视觉模型时这是唯一的素材来源。零依赖、不出网。")]
    public bool UseOcr { get; set; } = true;

    [DisplayName("OCR 语言")]
    [Description("默认 zh-Hans；英文视频填 en-US。系统没装对应语言包会自动退回默认语言。")]
    public string OcrLanguage { get; set; } = "zh-Hans";

    [DisplayName("OCR 字数上限")]
    [Description("一次最多给多少字，默认 600。太多会占 token；PPT 一页通常 100–300 字。")]
    public int OcrMaxChars { get; set; } = 600;

    [DisplayName("无字幕时才 OCR")]
    [Description("开（默认）：有足够的字幕句子时不再 OCR，省时间也省 token；关=每次都给画面文字（字幕和画面对不上时有用）。")]
    public bool OcrOnlyWhenNoSubtitle { get; set; } = true;

    [DisplayName("保留事件条数")]
    [Description("事件时间轴最多存多少条（默认 200）。只存字幕摘句与 OCR 文本，不存像素；可用来回答「刚才讲了什么」。")]
    public int KeepEvents { get; set; } = 200;

    [DisplayName("抓帧预设")]
    [Description("一键套用整套抓帧参数（间隔 / 变化阈值 / 宽度 / 质量 / 保留张数）。讲解PPT、论文图表、快切资讯、通用、影视省流。套用后仍可单项微调。")]
    public string FramePreset { get; set; } = "通用（推荐）";

    // ─────────────────────────────────────────────────────────────
    // 通用站点字幕（非 B 站）
    // ─────────────────────────────────────────────────────────────

    [DisplayName("自动读取页面字幕（任意站点）")]
    [Description("开（默认）：非 B 站视频让扩展自动读页面里的字幕元素（YouTube / 各类播放器 / 自制页面都认）。插件会按需提示扩展开启，不读正文/评论。")]
    public bool AutoPageCaption { get; set; } = true;

    [DisplayName("自定义字幕选择器（可选）")]
    [Description("某些站点的字幕元素类名很怪，自动认不出来时，在这里填 CSS 选择器（如 .my-subtitle-line），扩展会优先用它。留空 = 只用内置常见选择器。")]
    public string CaptionSelector { get; set; } = "";

    // ─────────────────────────────────────────────────────────────
    // 视频下载（B 站）
    // ─────────────────────────────────────────────────────────────

    [DisplayName("允许下载视频")]
    [Description("开（默认）：看 B 站视频时可以让角色「把这条视频下下来」，之后转写、抽帧、发给别人都用本地文件。下载存在「下载目录」，单集几分钟的片子约几十 MB。")]
    public bool EnableDownload { get; set; } = true;

    [DisplayName("下载目录")]
    [Description(@"下载的视频存哪，默认 D:\AI-Cover\downloads。")]
    public string DownloadFolder { get; set; } = @"D:\AI-Cover\downloads";

    // ─────────────────────────────────────────────────────────────
    // 语音转写（ASR，补「有人声没字幕」的视频）
    // ─────────────────────────────────────────────────────────────

    [DisplayName("启用语音转写")]
    [Description("开：扩展会顺便录下标签页声音，每 20 秒切片交给本机转写。优先用框架内的 SenseVoice（中文更准、速度快一个量级），识别器不可用或超时才回退 whisper.cpp。纯 PPT 讲解（无语音）没用，Vlog/口播/播客有用。默认关——开了会多占一点 CPU。")]
    public bool EnableAsr { get; set; } = false;

    [DisplayName("录音切片（秒）")]
    [Description("扩展每多少秒切一片交给本机转写，10-30，默认 10。切片越短字幕越跟得上画面；SenseVoice 走 CPU、不占显存，觉得 CPU 富余就往小调。")]
    public int AsrChunkSeconds { get; set; } = 10;

    [DisplayName("Whisper 路径（兜底）")]
    [Description(@"whisper-cli.exe 的位置。只有 SenseVoice 不可用 / 转写超时 / 音乐兜底时才用到。留空自动从 PATH 或插件数据目录 tools\whisper\ 下找。")]
    public string WhisperPath { get; set; } = "";

    [DisplayName("Whisper 模型（兜底）")]
    [Description(@"ggml-*.bin 模型。留空自动用 whisper 同目录 models\ 下第一个。SenseVoice 可用时不会用到这个模型。")]
    public string WhisperModel { get; set; } = "";

    [DisplayName("VAD 人声检测")]
    [Description("开（默认）：whisper 兜底转写时用 Silero VAD 先把纯音乐/静音切掉，免得编歌词。SenseVoice 自带 VAD，不受这里影响。模型自动找 whisper 同目录或 models\\ 下带 silero/vad 的文件（0.8 MB）。")]
    public bool AsrVad { get; set; } = true;

    [DisplayName("VAD 模型路径")]
    [Description(@"留空自动找（推荐）。手动填 silero-*-ggml.bin 的绝对路径。")]
    public string AsrVadModel { get; set; } = "";

    [DisplayName("过滤音乐幻觉")]
    [Description("开（默认）：丢掉转写在纯 BGM 上编出来的字幕组署名/订阅套话/重复拟声；连续 3 段没有真语音就自动停用转写，改用画面文字。关=原样保留（只在确认误杀时关）。")]
    public bool AsrMusicFilter { get; set; } = true;

    [DisplayName("转写语言")]
    [Description("默认 auto（自动检测，中英混合视频最稳）；纯中文视频填 zh 更准，纯英文填 en。")]
    public string AsrLanguage { get; set; } = "auto";

    [DisplayName("繁体转写转简体")]
    [Description("开（默认）：whisper 兜底输出繁体的情况较多，中文转写经常带繁体。开=检测到繁体特征字时用 Windows 原生映射（LCMapStringEx）转成简体，零依赖不出网。")]
    public bool AsrSimplified { get; set; } = true;

    [DisplayName("歌曲/音乐兜底（回退 whisper）")]
    [Description("开：SenseVoice 的 VAD 滤空但音频有能量时，回退 whisper（关 VAD）转写歌词。看 MV/音乐视频时开；纯 BGM 的 PPT/讲课视频建议关——否则 whisper 会把伴奏编成歌词。默认关。")]
    public bool AsrMusicFallback { get; set; } = false;
}
