using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace presents.WatchTogether;

/// <summary>
/// 本地播放位置桥：浏览器扩展 POST /api/position，插件在 127.0.0.1 上收。
///
/// 为什么需要它：普通页面读不到别的页面的 currentTime；扩展是浏览器明确信任的通道，
/// 装一次之后用户平时怎么开浏览器都行，不需要带调试端口启动。
///
/// 为什么用 TcpListener 手写 HTTP 而不是 HttpListener：
/// Alife 插件是动态编译的，TcpListener 只依赖 System.Net.Sockets，跨平台、无 URL ACL、
/// 无前缀配置，和 presents.BiliScroll 的做法一致。请求很小，手写解析足够。
///
/// 只监听回环地址，不落盘、不上传、不读页面内容。扩展只报 pos/dur/title/url/paused/ended。
/// </summary>
public sealed class WatchBridge : IAsyncDisposable
{
    // 位置上报很小，但 /api/frame 要收 JPEG（一张 1280 宽的截图约 100–300 KB），故整体放宽到 4 MB。
    const int MaxBodyBytes = 4 * 1024 * 1024;
    const int MaxRequestBytes = MaxBodyBytes + 8192;
    const int MaxHeaderLines = 64;

    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    readonly object gate = new();
    readonly object TraceGate = new();
    readonly List<string> Trace = new();
    TcpListener? listener;
    CancellationTokenSource? cts;
    Task? loopTask;
    bool disposed;

    /// <summary>扩展面包屑（最近 60 条），用于定位扩展侧崩溃点。</summary>
    public IReadOnlyList<string> TraceSnapshot { get { lock (TraceGate) return Trace.ToList(); } }

    /// <summary>插件侧面包屑（转换验证等诊断用）。</summary>
    public void AddTrace(string step)
    {
        try
        {
            lock (TraceGate)
            {
                Trace.Add(DateTime.Now.ToString("HH:mm:ss.fff") + " " + step);
                if (Trace.Count > 60) Trace.RemoveAt(0);
            }
        }
        catch { }
    }

    public int Port { get; private set; }
    public bool Running { get; private set; }
    public string LastError { get; private set; } = "";
    public DateTime StartedAt { get; private set; }

    public WatchSample? Latest { get; private set; }
    public int PositionCount { get; private set; }
    public DateTime? LastRejectedAt { get; private set; }
    public string LastRejectedReason { get; private set; } = "";

    public DateTime? HelloAt { get; private set; }
    public string HelloVersion { get; private set; } = "";

    /// <summary>待扩展执行的播放器命令（一次一条，下一次上报时带走）。</summary>
    PlaybackCommand? pendingCommand;

    /// <summary>是否请求扩展「现在抓一张画面」（下一次上报的响应里带 capture=true）。</summary>
    bool captureRequested;

    /// <summary>收到的实时帧：url / jpeg / pos / changed → 空串=收下，否则为拒绝原因。由模块注入。</summary>
    public Func<string, byte[], double, bool, string>? FrameSink { get; set; }

    /// <summary>
    /// 收到位置时判断「现在该不该抓一帧」。由模块注入。
    /// 放在这里而不是 OnUpdate 里：陪看模块的 Tick 只在角色活跃时跑，
    /// 而用户看视频时未必有对话在走 —— 靠 Tick 会漏掉整段抓帧。
    /// </summary>
    public Func<WatchSample, bool>? CaptureDecider { get; set; }

    /// <summary>最近一次抓帧结果，用于状态展示。</summary>
    public string LastFrameNote { get; private set; } = "";

    /// <summary>是否让扩展读取页面字幕（非 B 站站点靠这条拿台词）。由模块按配置注入。</summary>
    public bool WantCaption { get; set; }

    /// <summary>自定义字幕 CSS 选择器（留空 = 用扩展内置的常见选择器）。</summary>
    public string CaptionSelector { get; set; } = "";

    /// <summary>是否让扩展录标签页声音做 ASR（切片长度见 AsrSeconds）。</summary>
    public bool WantAsr { get; set; }

    /// <summary>告诉扩展每片录多少秒（10-60，默认 10）；越短字幕越跟得上画面。</summary>
    public int AsrSeconds { get; set; } = 10;

    /// <summary>是否需要扩展抓画面（关闭时扩展只开录音流，不开抓帧）。</summary>
    public bool WantFrames { get; set; } = true;

    /// <summary>最近一次转写结果摘要（状态接口展示用）。</summary>
    public string AsrNote { get; set; } = "";
    public string AsrRaw { get; set; } = "";
    public bool AsrMuted { get; set; }
    public DateTime LastSyncAt { get; set; }

    /// <summary>已收到的音频切片计数与最近时间（录音链路诊断）。</summary>
    public int AudioCount { get; set; }
    public DateTime? LastAudioAt { get; set; }

    /// <summary>框架本地识别器（SenseVoice）是否可用。</summary>
    public bool FrameworkAsr { get; set; }

    /// <summary>收到的音频切片：jpeg/opus 字节 + 切片起点 + 来源 url。由模块注入（入队后异步转写）。</summary>
    public Action<byte[], double, string>? AudioSink { get; set; }

    public bool Start(int port)
    {
        Stop();
        if (port <= 0 || port > 65535)
        {
            LastError = $"端口 {port} 不合法";
            return false;
        }

        try
        {
            var l = new TcpListener(IPAddress.Loopback, port);
            l.Start();

            listener = l;
            cts = new CancellationTokenSource();
            Port = port;
            Running = true;
            LastError = "";
            StartedAt = DateTime.Now;
            loopTask = Task.Run(() => LoopAsync(l, cts.Token));
            return true;
        }
        catch (Exception e)
        {
            LastError = e.Message;
            Running = false;
            return false;
        }
    }

    public void Stop()
    {
        CancellationTokenSource? source = cts;
        cts = null;
        try { source?.Cancel(); } catch { }
        try { listener?.Stop(); } catch { }
        listener = null;
        try { source?.Dispose(); } catch { }

        Task? task = loopTask;
        loopTask = null;
        try { task?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        Running = false;
    }

    async Task LoopAsync(TcpListener l, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await l.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                if (ct.IsCancellationRequested) break;
                continue;
            }
            catch (Exception e)
            {
                LastError = e.Message;
                break;
            }

            _ = Task.Run(() => HandleClientAsync(client, ct), CancellationToken.None);
        }
    }

    async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using (client)
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                client.NoDelay = true;
                using NetworkStream stream = client.GetStream();
                ParsedRequest? request = await ReadRequestAsync(stream, timeout.Token).ConfigureAwait(false);
                if (request == null) return;

                if (request.ContentLength > MaxBodyBytes)
                {
                    await WriteAsync(stream, 413, "application/json",
                        Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"body-too-large\"}")).ConfigureAwait(false);
                    return;
                }

                await DispatchAsync(stream, request).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch
        {
            // 单次请求失败不影响桥继续运行
        }
    }

    async Task DispatchAsync(NetworkStream stream, ParsedRequest request)
    {
        if (request.Method == "OPTIONS")
        {
            await WriteAsync(stream, 204, "text/plain", Array.Empty<byte>()).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && request.Path.Equals("/api/position", StringComparison.OrdinalIgnoreCase))
        {
            WatchSample? sample = Normalize(request.Body, DateTime.Now);
            if (sample == null)
            {
                Reject("位置不是有效数字");
                await WriteAsync(stream, 400, "application/json",
                    Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"invalid-position\"}")).ConfigureAwait(false);
                return;
            }

            lock (gate)
            {
                Latest = sample;
                PositionCount++;
            }

            // 抓帧调度：在收到位置的这一拍判断，不依赖模块 Tick
            Func<WatchSample, bool>? decider = CaptureDecider;
            if (decider != null && !sample.Paused && !sample.Ended)
            {
                bool want;
                try { want = decider(sample); }
                catch { want = false; }

                if (want)
                {
                    lock (gate) captureRequested = true;
                }
            }

            // 把模型请求的播放器命令 / 抓帧请求挂在下一次上报的响应里；扩展收到后执行。
            PlaybackCommand? command = TakeCommand();
            bool capture = TakeCaptureRequest();
            byte[] response = JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    ok = true,
                    command,
                    capture,
                    caption = WantCaption,
                    captionSelector = CaptionSelector ?? "",
                    asr = WantAsr,
                    asrSeconds = AsrSeconds,
                    frames = WantFrames
                }, JsonOptions);
            await WriteAsync(stream, 200, "application/json", response).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && request.Path.Equals("/api/frame", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryDecodeFrame(request.Body, out FramePayload? frame, out string reason))
            {
                Reject("帧无效：" + reason);
                await WriteAsync(stream, 400, "application/json",
                    Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"invalid-frame\"}")).ConfigureAwait(false);
                return;
            }

            Func<string, byte[], double, bool, string>? sink = FrameSink;
            string reject = sink == null ? "接收器未就绪（模块还没接入）" : "";

            if (sink != null)
            {
                try
                {
                    reject = sink(frame!.Url, frame.Jpeg, frame.Pos, frame.Changed) ?? "";
                }
                catch (Exception e)
                {
                    reject = e.Message;
                }
            }

            bool accepted = reject.Length == 0;

            lock (gate)
            {
                LastFrameNote = accepted
                    ? $"收下 {frame!.Jpeg.Length / 1024} KB @ {frame.Pos:0}s"
                    : "帧被拒：" + reject;
            }

            await WriteAsync(stream, accepted ? 200 : 422, "application/json",
                Encoding.UTF8.GetBytes(accepted ? "{\"ok\":true}" : "{\"ok\":false,\"error\":\"rejected\"}"))
                .ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && request.Path.Equals("/api/hello", StringComparison.OrdinalIgnoreCase))
        {
            string version = "";
            try
            {
                HelloPayload? hello = JsonSerializer.Deserialize<HelloPayload>(request.Body, JsonOptions);
                version = Trim(hello?.Version, 40) ?? "";
            }
            catch
            {
                // 心跳内容坏了也不影响服务存在
            }

            lock (gate)
            {
                HelloAt = DateTime.Now;
                HelloVersion = version;
            }
            await WriteAsync(stream, 200, "application/json",
                Encoding.UTF8.GetBytes("{\"ok\":true}")).ConfigureAwait(false);
            return;
        }

        // 扩展崩溃排查：offscreen/SW 在关键步进调用这里（fetch keepalive，上下文死了请求照样到），
        // 面包屑序列能精确指出崩在哪一步
        if (request.Method == "POST" && request.Path.Equals("/api/trace", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                string step = (request.Body ?? "").Trim();
                if (step.Length > 0)
                {
                    lock (TraceGate)
                    {
                        Trace.Add(DateTime.Now.ToString("HH:mm:ss.fff") + " " + step);
                        if (Trace.Count > 60) Trace.RemoveAt(0);
                    }
                }
            }
            catch { }

            await WriteAsync(stream, 200, "application/json",
                Encoding.UTF8.GetBytes("{\"ok\":true}")).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && request.Path.Equals("/api/audio", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryDecodeAudio(request.Body, out byte[]? audio, out double pos, out string url, out string why))
            {
                Reject("音频无效：" + why);
                await WriteAsync(stream, 400, "application/json",
                    Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"invalid-audio\"}")).ConfigureAwait(false);
                return;
            }

            Action<byte[], double, string>? sink = AudioSink;

            if (sink != null)
            {
                try
                {
                    sink(audio!, pos, url);
                }
                catch (Exception e)
                {
                    LastFrameNote = "音频入队失败：" + e.Message;
                }
            }

            await WriteAsync(stream, 200, "application/json",
                Encoding.UTF8.GetBytes("{\"ok\":true}")).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && request.Path.Equals("/api/state", StringComparison.OrdinalIgnoreCase))
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(Snapshot(), JsonOptions);
            await WriteAsync(stream, 200, "application/json; charset=utf-8", bytes).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && request.Path == "/")
        {
            await WriteAsync(stream, 200, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(
                "presents.WatchTogether 位置桥正在运行。\n"
                + "POST /api/position 收扩展上报；GET /api/state 看状态。\n")).ConfigureAwait(false);
            return;
        }

        await WriteAsync(stream, 404, "application/json",
            Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"not-found\"}")).ConfigureAwait(false);
    }

    // ─────────────────────────────────────────────────────────────
    // 极简 HTTP 解析（请求很小，按字节读，避免 StreamReader 缓冲吞掉 body）
    // ─────────────────────────────────────────────────────────────

    sealed class ParsedRequest
    {
        public string Method { get; init; } = "";
        public string Path { get; init; } = "/";
        public int ContentLength { get; init; }
        public string Body { get; init; } = "";
    }

    static async Task<ParsedRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        byte[] data = new byte[MaxRequestBytes];
        int total = 0;
        int headerEnd = -1;
        string method = "";
        string path = "/";
        int contentLength = -1;

        while (total < data.Length)
        {
            int read = await stream.ReadAsync(data.AsMemory(total, data.Length - total), ct).ConfigureAwait(false);
            if (read <= 0) return null;
            total += read;

            if (headerEnd < 0)
            {
                headerEnd = FindHeaderEnd(data, total);
                if (headerEnd < 0) continue;
                ParseHead(data, headerEnd, out method, out path, out contentLength);
                if (contentLength > MaxBodyBytes)
                    return new ParsedRequest { Method = method, Path = path, ContentLength = contentLength };
            }

            int bodyStart = headerEnd + 4;
            int bodyHave = total - bodyStart;
            if (contentLength >= 0 && bodyHave >= contentLength)
            {
                string body = contentLength == 0
                    ? ""
                    : Encoding.UTF8.GetString(data, bodyStart, contentLength);
                return new ParsedRequest
                {
                    Method = method,
                    Path = path,
                    ContentLength = contentLength,
                    Body = body
                };
            }
        }

        return null;
    }

    static int FindHeaderEnd(byte[] data, int total)
    {
        for (int i = 0; i + 3 < total; i++)
        {
            if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10)
                return i;
        }
        return -1;
    }

    static void ParseHead(byte[] data, int headerEnd, out string method, out string path, out int contentLength)
    {
        method = "";
        path = "/";
        contentLength = 0;

        string head = Encoding.ASCII.GetString(data, 0, headerEnd);
        string[] lines = head.Split("\r\n");
        if (lines.Length == 0) return;

        string[] parts = lines[0].Split(' ');
        if (parts.Length >= 2)
        {
            method = parts[0].ToUpperInvariant();
            path = parts[1];
        }

        int query = path.IndexOf('?');
        if (query >= 0) path = path[..query];
        path = path.TrimEnd('/');
        if (path.Length == 0) path = "/";

        for (int i = 1; i < lines.Length && i <= MaxHeaderLines; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;
            string name = lines[i][..colon].Trim();
            string value = lines[i][(colon + 1)..].Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(value, out int parsed))
                contentLength = parsed;
        }
    }

    static async Task WriteAsync(NetworkStream stream, int status, string contentType, byte[] body)
    {
        string reason = status switch
        {
            200 => "OK",
            204 => "No Content",
            400 => "Bad Request",
            404 => "Not Found",
            413 => "Payload Too Large",
            _ => "OK"
        };

        var header = new StringBuilder();
        header.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
        header.Append("Content-Type: ").Append(contentType).Append("\r\n");
        header.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        header.Append("Access-Control-Allow-Origin: *\r\n");
        header.Append("Access-Control-Allow-Headers: content-type\r\n");
        header.Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n");
        header.Append("Cache-Control: no-store\r\n");
        header.Append("Connection: close\r\n\r\n");

        byte[] head = Encoding.ASCII.GetBytes(header.ToString());
        await stream.WriteAsync(head).ConfigureAwait(false);
        if (body.Length > 0)
            await stream.WriteAsync(body).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    // ─────────────────────────────────────────────────────────────
    // 数据
    // ─────────────────────────────────────────────────────────────

    public void Reject(string reason)
    {
        lock (gate)
        {
            LastRejectedAt = DateTime.Now;
            LastRejectedReason = reason;
        }
    }

    /// <summary>排队一条播放器命令。只保留最后一条，避免模型连发时排一长串。</summary>
    public void SetCommand(string action, double value)
    {
        if (string.IsNullOrWhiteSpace(action)) return;
        lock (gate)
        {
            pendingCommand = new PlaybackCommand { Action = action, Value = value };
        }
    }

    public PlaybackCommand? TakeCommand()
    {
        lock (gate)
        {
            PlaybackCommand? command = pendingCommand;
            pendingCommand = null;
            return command;
        }
    }

    /// <summary>请扩展在下次上报时抓一张画面。</summary>
    public void RequestCapture()
    {
        lock (gate)
        {
            captureRequested = true;
        }
    }

    public bool TakeCaptureRequest()
    {
        lock (gate)
        {
            bool value = captureRequested;
            captureRequested = false;
            return value;
        }
    }

    /// <summary>解析 /api/frame 的请求体：image 支持 dataURL 或裸 base64。</summary>
    static bool TryDecodeFrame(string body, out FramePayload? frame, out string reason)
    {
        frame = null;
        reason = "";

        try
        {
            FramePayload? raw = JsonSerializer.Deserialize<FramePayload>(body, JsonOptions);
            if (raw == null || string.IsNullOrWhiteSpace(raw.Image))
            {
                reason = "缺少 image";
                return false;
            }

            string base64 = raw.Image;
            int comma = base64.IndexOf(',');
            if (base64.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0)
                base64 = base64[(comma + 1)..];

            base64 = base64.Trim().Replace("\r", "").Replace("\n", "");
            if (base64.Length < 2048)
            {
                reason = "base64 太短";
                return false;
            }

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(base64);
            }
            catch
            {
                reason = "base64 解码失败";
                return false;
            }

            if (bytes.Length < 2048 || bytes.Length > 4 * 1024 * 1024)
            {
                reason = $"体积不合规（{bytes.Length}）";
                return false;
            }

            frame = new FramePayload
            {
                Image = "",
                Jpeg = bytes,
                Pos = raw.Pos,
                Url = Trim(raw.Url, 2000) ?? "",
                Title = Trim(raw.Title, 200) ?? "",
                Changed = raw.Changed
            };

            return true;
        }
        catch (Exception e)
        {
            reason = e.Message;
            return false;
        }
    }

    /// <summary>解析 /api/audio：audio 支持 dataURL 或裸 base64。</summary>
    static bool TryDecodeAudio(string body, out byte[]? audio, out double pos, out string url, out string reason)
    {
        audio = null;
        pos = 0;
        url = "";
        reason = "";

        try
        {
            AudioPayload? raw = JsonSerializer.Deserialize<AudioPayload>(body, JsonOptions);
            if (raw == null || string.IsNullOrWhiteSpace(raw.Audio))
            {
                reason = "缺少 audio";
                return false;
            }

            string base64 = raw.Audio;
            int comma = base64.IndexOf(',');
            if (base64.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0)
                base64 = base64[(comma + 1)..];

            base64 = base64.Trim().Replace("\r", "").Replace("\n", "");

            byte[] bytes;
            try { bytes = Convert.FromBase64String(base64); }
            catch { reason = "base64 解码失败"; return false; }

            if (bytes.Length < 1024 || bytes.Length > 4 * 1024 * 1024)
            {
                reason = $"体积不合规（{bytes.Length}）";
                return false;
            }

            audio = bytes;
            pos = raw.Pos;
            url = Trim(raw.Url, 2000) ?? "";
            return true;
        }
        catch (Exception e)
        {
            reason = e.Message;
            return false;
        }
    }

    public static WatchSample? Normalize(string body, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            PositionPayload? raw = JsonSerializer.Deserialize<PositionPayload>(body, JsonOptions);
            if (raw == null) return null;
            if (raw.Pos is not double pos || !double.IsFinite(pos) || pos < 0) return null;

            double? dur = null;
            if (raw.Dur is double d && double.IsFinite(d) && d > 0) dur = d;

            return new WatchSample
            {
                Pos = pos,
                Dur = dur,
                Title = Trim(raw.Title, 200),
                Url = Trim(raw.Url, 2000),
                Paused = raw.Paused == true,
                Ended = raw.Ended == true,
                Caption = Trim(raw.Caption, 300),
                At = now
            };
        }
        catch
        {
            return null;
        }
    }

    static string? Trim(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string value = text.Trim();
        return value.Length <= max ? value : value[..max];
    }

    /// <summary>当前状态快照。读的时候拷贝一份，避免 UI 与上报线程互相看到半截对象。</summary>
    public BridgeStatus Snapshot()
    {
        DateTime now = DateTime.Now;
        lock (gate)
        {
            WatchSample? sample = Latest;
            return new BridgeStatus
            {
                Running = Running,
                Port = Port,
                Error = LastError,
                StartedAt = StartedAt == default ? null : StartedAt,
                PositionCount = PositionCount,
                LastAt = sample?.At,
                AgeSeconds = sample == null ? null : Math.Max(0, (now - sample.At).TotalSeconds),
                Pos = sample?.Pos,
                Dur = sample?.Dur,
                Title = sample?.Title ?? "",
                Url = sample?.Url ?? "",
                Paused = sample?.Paused ?? false,
                Ended = sample?.Ended ?? false,
                Caption = sample?.Caption ?? "",
                PendingCommand = pendingCommand == null
                    ? ""
                    : pendingCommand.Action + (pendingCommand.Value > 0 ? $" {pendingCommand.Value:0}" : ""),
                HelloAt = HelloAt,
                HelloAgeSeconds = HelloAt == null ? null : Math.Max(0, (now - HelloAt.Value).TotalSeconds),
                HelloVersion = HelloVersion,
                LastRejectedAt = LastRejectedAt,
                LastRejectedReason = LastRejectedReason,
                Build = BridgeStatus.BuildTag,
                CaptureRequested = captureRequested,
                LastFrameNote = LastFrameNote,
                AsrNote = AsrNote,
                AsrRaw = AsrRaw,
                AsrMuted = AsrMuted,
                LastSyncAt = LastSyncAt,
                AudioCount = AudioCount,
                LastAudioAt = LastAudioAt,
                FrameworkAsr = FrameworkAsr,
                Trace = TraceSnapshot.ToList()
            };
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        Stop();
        await Task.CompletedTask;
    }

    sealed class PositionPayload
    {
        [JsonPropertyName("pos")] public double? Pos { get; set; }
        [JsonPropertyName("dur")] public double? Dur { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("paused")] public bool? Paused { get; set; }
        [JsonPropertyName("ended")] public bool? Ended { get; set; }
        [JsonPropertyName("caption")] public string? Caption { get; set; }
    }

    sealed class HelloPayload
    {
        [JsonPropertyName("version")] public string? Version { get; set; }
    }

    sealed class AudioPayload
    {
        [JsonPropertyName("audio")] public string? Audio { get; set; }
        [JsonPropertyName("pos")] public double Pos { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
    }

    sealed class FramePayload
    {
        [JsonPropertyName("image")] public string? Image { get; set; }
        [JsonPropertyName("pos")] public double Pos { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("changed")] public bool Changed { get; set; }

        /// <summary>解码后的 JPEG 字节（不进 JSON）。</summary>
        [JsonIgnore] public byte[] Jpeg { get; set; } = Array.Empty<byte>();
    }
}

public sealed class PlaybackCommand
{
    public string Action { get; init; } = "";
    public double Value { get; init; }
}

public sealed class BridgeStatus
{
    public bool Running { get; set; }
    public int Port { get; set; }
    public string Error { get; set; } = "";
    public DateTime? StartedAt { get; set; }
    public int PositionCount { get; set; }
    public DateTime? LastAt { get; set; }
    public double? AgeSeconds { get; set; }
    public double? Pos { get; set; }
    public double? Dur { get; set; }
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public bool Paused { get; set; }
    public bool Ended { get; set; }
    public string Caption { get; set; } = "";
    public string PendingCommand { get; set; } = "";
    public DateTime? HelloAt { get; set; }
    public double? HelloAgeSeconds { get; set; }
    public string HelloVersion { get; set; } = "";
    public DateTime? LastRejectedAt { get; set; }
    public string LastRejectedReason { get; set; } = "";
    public bool CaptureRequested { get; set; }
    public string LastFrameNote { get; set; } = "";
    public string AsrNote { get; set; } = "";
    public string AsrRaw { get; set; } = "";
    public bool AsrMuted { get; set; }
    public DateTime LastSyncAt { get; set; }
    public int AudioCount { get; set; }
    public DateTime? LastAudioAt { get; set; }
    public bool FrameworkAsr { get; set; }
    public List<string> Trace { get; set; } = new();

    /// <summary>构建标记：用来确认运行时加载的确实是刚编译的这一版（排查「重载没生效」）。</summary>
    public const string BuildTag = "wt-2.4.3-sv";

    public string Build { get; set; } = BuildTag;

    public bool Fresh => AgeSeconds is >= 0 and <= 5;
    public bool HasPosition => Pos.HasValue;
}
