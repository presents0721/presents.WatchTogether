using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Media.Control;

namespace presents.WatchTogether;

/// <summary>
/// 桌面播放器位置源：读 Windows 系统媒体会话（SMTC）。
/// 支持把播放状态/进度交给 Windows 媒体控制的应用（Media Player、PotPlayer、VLC、QQ/微信内置播放器等，取决于应用是否接入 SMTC）。
/// 浏览器交给扩展，这里主动跳过，避免同一个网页视频被两个来源来回切。
/// </summary>
public sealed class DesktopMediaWatcher : IDisposable
{
    static readonly string[] BrowserApps =
        { "chrome", "msedge", "firefox", "opera", "brave", "vivaldi", "iexplore", "arc", "browser" };

    CancellationTokenSource? cts;
    Task? loop;
    volatile WatchSample? latest;
    volatile string lastError = "";
    volatile string lastSource = "";

    public WatchSample? Latest => latest;
    public string LastError => lastError;
    public string LastSource => lastSource;
    public bool Running => loop != null && !loop.IsCompleted;

    public void Start()
    {
        if (Running) return;
        cts = new CancellationTokenSource();
        loop = Task.Run(() => LoopAsync(cts.Token));
    }

    public void Stop()
    {
        try { cts?.Cancel(); } catch { }
        cts = null;
        loop = null;
        latest = null;
        lastSource = "";
    }

    public void Dispose()
    {
        Stop();
        try { cts?.Dispose(); } catch { }
    }

    async Task LoopAsync(CancellationToken ct)
    {
        GlobalSystemMediaTransportControlsSessionManager? manager = null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                GlobalSystemMediaTransportControlsSession? current = manager.GetCurrentSession();
                GlobalSystemMediaTransportControlsSession? session = PickSession(manager, current);
                if (session == null)
                {
                    latest = null;
                    lastSource = "";
                    lastError = "";
                    await Task.Delay(1200, ct).ConfigureAwait(false);
                    continue;
                }

                string appId = session.SourceAppUserModelId ?? "";
                GlobalSystemMediaTransportControlsSessionPlaybackInfo? playback = session.GetPlaybackInfo();
                GlobalSystemMediaTransportControlsSessionPlaybackStatus status =
                    playback?.PlaybackStatus ?? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
                if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed)
                {
                    latest = null;
                    lastSource = "";
                    lastError = "";
                    await Task.Delay(1200, ct).ConfigureAwait(false);
                    continue;
                }

                // 纯音乐不陪看；很多播放器会把视频报成 Video/Unknown，只有明确 Music 才跳过。
                if (playback?.PlaybackType == MediaPlaybackType.Music)
                {
                    latest = null;
                    lastSource = "";
                    lastError = "";
                    await Task.Delay(1200, ct).ConfigureAwait(false);
                    continue;
                }

                GlobalSystemMediaTransportControlsSessionMediaProperties props = await session.TryGetMediaPropertiesAsync();
                GlobalSystemMediaTransportControlsSessionTimelineProperties? timeline = session.GetTimelineProperties();

                double pos = timeline?.Position.TotalSeconds ?? 0;
                double duration = timeline == null
                    ? 0
                    : (timeline.EndTime - timeline.StartTime).TotalSeconds;
                bool playing = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

                // 有些播放器不是每秒刷新时间轴；播放中按 lastUpdatedTime 补一点，避免读数总是偏旧。
                if (playing && timeline != null)
                {
                    double since = (DateTimeOffset.Now - timeline.LastUpdatedTime).TotalSeconds;
                    if (since > 0 && since < 8) pos += since;
                }

                string title = (props?.Title ?? "").Trim();
                if (title.Length == 0) title = (props?.Artist ?? "").Trim();
                if (title.Length == 0) title = appId;

                bool ended = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped
                             || (duration > 0 && pos >= duration - 1.0 && !playing);

                latest = new WatchSample
                {
                    Pos = Math.Max(0, pos),
                    Dur = duration > 0 ? duration : null,
                    Title = title,
                    Url = "smtc://" + Uri.EscapeDataString(appId.Length > 0 ? appId : "media"),
                    AppId = appId,
                    Paused = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
                    Ended = ended,
                    At = DateTime.Now
                };
                lastSource = appId;
                lastError = "";
            }
            catch (Exception e)
            {
                latest = null;
                lastError = e.Message;
                manager = null;
                try { await Task.Delay(2000, ct).ConfigureAwait(false); } catch { break; }
                continue;
            }

            try { await Task.Delay(1000, ct).ConfigureAwait(false); } catch { break; }
        }
    }

    static GlobalSystemMediaTransportControlsSession? PickSession(
        GlobalSystemMediaTransportControlsSessionManager manager,
        GlobalSystemMediaTransportControlsSession? current)
    {
        var sessions = manager.GetSessions();
        bool Usable(GlobalSystemMediaTransportControlsSession session)
        {
            if (IsBrowserApp(session.SourceAppUserModelId)) return false;
            GlobalSystemMediaTransportControlsSessionPlaybackInfo? info = session.GetPlaybackInfo();
            if (info == null) return false;
            if (info.PlaybackType == MediaPlaybackType.Music) return false;
            return info.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
        }

        // 优先正在播放的那一个；没有就退回当前会话，再退回任意可用的（含暂停/播完）。
        GlobalSystemMediaTransportControlsSession? playing = sessions.FirstOrDefault(s =>
            Usable(s) && s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
        if (playing != null) return playing;
        if (current != null && Usable(current)) return current;
        return sessions.FirstOrDefault(Usable);
    }

    static bool IsBrowserApp(string? appId)
    {
        string id = (appId ?? "").ToLowerInvariant();
        return BrowserApps.Any(b => id.Contains(b, StringComparison.Ordinal));
    }
}
