using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace presents.WatchTogether;

/// <summary>扩展实时抓来的一张画面。</summary>
public sealed class LiveFrame
{
    public double Pos { get; init; }
    public string Path { get; init; } = "";
    public DateTime At { get; init; }
    public int Bytes { get; init; }
    public bool Changed { get; init; }
}

/// <summary>
/// 网页视频的「实时画面」仓库：浏览器扩展用 tabCapture 抓当前画面 → POST /api/frame
/// → 这里落盘成 jpg（按会话分目录、按播放位置命名）→ 工具层把最近几张的路径交给模型看。
///
/// 为什么必须由扩展抓：B 站/YouTube 的视频流是跨域 CDN，页面里 canvas.drawImage
/// 会污染画布，读不出像素；tabCapture 抓的是标签页合成结果，不受 CORS 限制。
///
/// PPT / 图表讲解类视频没有语音，画面就是全部内容 —— 这条通道是它们唯一的素材来源。
/// </summary>
public sealed class LiveFrameStore
{
    readonly ILogger logger;
    readonly Func<WatchTogetherConfig> config;
    readonly string root;

    readonly object gate = new();
    readonly Dictionary<string, List<LiveFrame>> bySession = new(StringComparer.OrdinalIgnoreCase);

    public int AcceptedCount { get; private set; }
    public DateTime? LastAt { get; private set; }
    public string LastError { get; private set; } = "";

    public LiveFrameStore(ILogger logger, Func<WatchTogetherConfig> config, string dataDir)
    {
        this.logger = logger;
        this.config = config;
        root = Path.Combine(dataDir, "frames", "live");
        Directory.CreateDirectory(root);
    }

    /// <summary>收到一张帧。校验 JPEG 魔数与大小，按位置命名（同位置覆盖），超量按最旧淘汰。</summary>
    public bool Accept(string sessionKey, byte[] jpeg, double pos, bool changed)
    {
        if (string.IsNullOrWhiteSpace(sessionKey) || jpeg.Length < 2048)
        {
            LastError = "帧太小或没有会话";
            return false;
        }

        if (jpeg.Length > 4 * 1024 * 1024)
        {
            LastError = "帧太大（>4MB）";
            return false;
        }

        if (jpeg[0] != 0xFF || jpeg[1] != 0xD8)
        {
            LastError = "不是 JPEG";
            return false;
        }

        try
        {
            string dir = Path.Combine(root, WatchFormat.SafeFileName(sessionKey));
            Directory.CreateDirectory(dir);

            string path = Path.Combine(dir, ((int)Math.Round(pos)).ToString("D6") + ".jpg");
            File.WriteAllBytes(path, jpeg);

            var frame = new LiveFrame
            {
                Pos = pos,
                Path = path,
                At = DateTime.Now,
                Bytes = jpeg.Length,
                Changed = changed
            };

            lock (gate)
            {
                if (!bySession.TryGetValue(sessionKey, out List<LiveFrame>? list))
                    bySession[sessionKey] = list = new List<LiveFrame>();

                list.RemoveAll(f => Math.Abs(f.Pos - pos) < 0.5);
                list.Add(frame);
                list.Sort((a, b) => a.Pos.CompareTo(b.Pos));

                int keep = Math.Clamp(config().LiveFrameKeep, 3, 60);
                while (list.Count > keep)
                {
                    LiveFrame oldest = list[0];
                    list.RemoveAt(0);
                    try { File.Delete(oldest.Path); } catch { }
                }

                AcceptedCount++;
                LastAt = frame.At;
                LastError = "";
            }

            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            logger.LogDebug(ex, "[WatchTogether] 存实时帧失败");
            return false;
        }
    }

    /// <summary>最近 N 张（按播放位置从早到晚）。</summary>
    public List<LiveFrame> Recent(string sessionKey, int max)
    {
        if (string.IsNullOrWhiteSpace(sessionKey)) return new List<LiveFrame>();

        lock (gate)
        {
            if (!bySession.TryGetValue(sessionKey, out List<LiveFrame>? list))
                return new List<LiveFrame>();

            return list.Skip(Math.Max(0, list.Count - Math.Clamp(max, 1, 60))).ToList();
        }
    }

    public int Count(string sessionKey)
    {
        lock (gate)
        {
            return bySession.TryGetValue(sessionKey, out List<LiveFrame>? list) ? list.Count : 0;
        }
    }

    /// <summary>清掉某场会话的帧（换片 / reset 时用）。</summary>
    public void Clear(string sessionKey)
    {
        lock (gate)
        {
            if (!bySession.Remove(sessionKey, out List<LiveFrame>? list)) return;

            foreach (LiveFrame frame in list)
            {
                try { File.Delete(frame.Path); } catch { }
            }
        }
    }
}
