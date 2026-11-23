using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace presents.WatchTogether;

/// <summary>
/// B 站视频下载：view 拿 cid → playurl（DASH）拿音视频流 → 分别下载 → ffmpeg -c copy 合流。
///
/// 为什么要下载：
///   · 转写（ASR）要整段音频，在线抓流只能拿到「现在」；
///   · 抽帧/回看/发给别人都需要本地文件；
///   · 一次下载，后面所有功能（字幕对齐、封面、切片）都省事。
///
/// 只下「能直接播的」清晰度（1080p 以内，qn=80）；大会员 4K/HDR 需要额外鉴权，拿不到就明确说。
/// </summary>
public sealed class BiliMedia : IDisposable
{
    const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    readonly HttpClient http;
    readonly ILogger logger;
    readonly Func<string> getCookie;

    public BiliMedia(ILogger logger, Func<string> getCookie)
    {
        this.logger = logger;
        this.getCookie = getCookie;

        http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
    }

    public sealed record Streams(string VideoUrl, string AudioUrl, string Title, int Quality, string Note);

    /// <summary>拿一支视频的最佳可用音视频流地址。</summary>
    public async Task<(Streams? Result, string Error)> GetStreamsAsync(string bvid, CancellationToken ct)
    {
        long aid = 0, cid = 0;
        string title = "";

        try
        {
            using JsonDocument view = await GetJsonAsync(
                $"https://api.bilibili.com/x/web-interface/view?bvid={bvid}", ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("view 接口无响应");

            if (!view.RootElement.TryGetProperty("data", out JsonElement data))
                return (null, "取视频信息失败（可能需要登录）");

            aid = data.TryGetProperty("aid", out JsonElement a) ? a.GetInt64() : 0;
            cid = data.TryGetProperty("cid", out JsonElement c) ? c.GetInt64() : 0;
            title = data.TryGetProperty("title", out JsonElement t) ? t.GetString() ?? "" : "";
        }
        catch (Exception ex)
        {
            return (null, "取视频信息失败：" + ex.Message);
        }

        if (cid <= 0) return (null, "拿不到 cid（不支持的分P或番剧）");

        var pars = new Dictionary<string, string>
        {
            ["bvid"] = bvid,
            ["cid"] = cid.ToString(),
            ["fnval"] = "16",
            ["qn"] = "80",
            ["fourk"] = "1"
        };

        JsonDocument? play = null;

        // 先试 wbi 签名版，失败退回普通版（部分视频只有其中一种能用）
        (string Img, string Sub)? keys = await TryGetWbiKeysAsync(ct).ConfigureAwait(false);

        if (keys != null)
        {
            string query = WbiSigner.Sign(pars, keys.Value.Img, keys.Value.Sub);
            play = await GetJsonAsync($"https://api.bilibili.com/x/player/wbi/playurl?{query}", ct).ConfigureAwait(false);
        }

        if (play == null)
        {
            string query = string.Join("&", pars.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
            play = await GetJsonAsync($"https://api.bilibili.com/x/player/playurl?{query}", ct).ConfigureAwait(false);
        }

        if (play == null) return (null, "playurl 没有响应");

        using (play)
        {
            if (!play.RootElement.TryGetProperty("data", out JsonElement data))
                return (null, "playurl 返回异常（可能需要登录或该视频受限）");

            if (!data.TryGetProperty("dash", out JsonElement dash))
            {
                // 老接口只给 durl 单一文件：直接下它
                if (data.TryGetProperty("durl", out JsonElement durl)
                    && durl.ValueKind == JsonValueKind.Array && durl.GetArrayLength() > 0)
                {
                    string? single = durl[0].TryGetProperty("url", out JsonElement u) ? u.GetString() : null;

                    if (!string.IsNullOrWhiteSpace(single))
                        return (new Streams(single!, "", title, 0, "durl 单文件"), "");
                }

                return (null, "没有 DASH 流（可能需要大会员或该视频不支持下载）");
            }

            JsonElement video = dash.TryGetProperty("video", out JsonElement v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray()
                   .OrderByDescending(item => Bandwidth(item))
                   .FirstOrDefault()
                : default;

            JsonElement audio = dash.TryGetProperty("audio", out JsonElement au) && au.ValueKind == JsonValueKind.Array
                ? au.EnumerateArray()
                   .OrderByDescending(item => Bandwidth(item))
                   .FirstOrDefault()
                : default;

            string? videoUrl = video.ValueKind == JsonValueKind.Object && video.TryGetProperty("baseUrl", out JsonElement vu)
                ? vu.GetString()
                : null;

            string? audioUrl = audio.ValueKind == JsonValueKind.Object && audio.TryGetProperty("baseUrl", out JsonElement au2)
                ? au2.GetString()
                : null;

            int quality = video.ValueKind == JsonValueKind.Object && video.TryGetProperty("id", out JsonElement qi)
                ? qi.GetInt32()
                : 0;

            if (string.IsNullOrWhiteSpace(videoUrl))
                return (null, "没有可用的视频流");

            return (new Streams(
                videoUrl!,
                audioUrl ?? "",
                title,
                quality,
                audioUrl == null ? "仅视频流（无音轨）" : "DASH 音视频"), "");
        }
    }

    /// <summary>
    /// 下载并合流。返回最终文件路径；ffmpeg 找不到时自动降级为「音视频分开存」。
    /// </summary>
    public async Task<(string? Path, string Error)> DownloadAsync(
        Streams streams,
        string folder,
        string ffmpeg,
        CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(folder);

            string safe = Sanitize(streams.Title);
            string stamp = DateTime.Now.ToString("MMdd-HHmmss");
            string videoTmp = Path.Combine(folder, $".{safe}-{stamp}.m4v");
            string audioTmp = Path.Combine(folder, $".{safe}-{stamp}.m4a");
            string output = Path.Combine(folder, $"{safe}-{stamp}.mp4");

            if (!await DownloadToFileAsync(streams.VideoUrl, videoTmp, ct).ConfigureAwait(false))
                return (null, "视频流下载失败（链接可能过期，重试一次）");

            bool hasAudio = !string.IsNullOrWhiteSpace(streams.AudioUrl)
                            && await DownloadToFileAsync(streams.AudioUrl, audioTmp, ct).ConfigureAwait(false);

            if (!hasAudio)
            {
                string fallback = Path.ChangeExtension(videoTmp, ".mp4");
                File.Move(videoTmp, fallback, true);
                return (fallback, "");
            }

            if (string.IsNullOrWhiteSpace(ffmpeg))
            {
                // 没有 ffmpeg：保留两段，交给调用方/后续处理
                return (videoTmp, "没有 ffmpeg，音视频分开存（" + Path.GetFileName(audioTmp) + "）");
            }

            var args = new List<string>
            {
                "-hide_banner", "-loglevel", "error",
                "-i", videoTmp, "-i", audioTmp,
                "-c", "copy", "-map", "0:v:0", "-map", "1:a:0",
                "-movflags", "+faststart", "-y", output
            };

            try
            {
                ProcessStartInfo info = new(ffmpeg)
                {
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                foreach (string arg in args)
                    info.ArgumentList.Add(arg);

                using Process process = Process.Start(info)!;
                await process.WaitForExitAsync(ct).ConfigureAwait(false);

                if (process.ExitCode != 0 || !File.Exists(output))
                    return (videoTmp, "合流失败，已保留视频流文件");
            }
            catch (Exception ex)
            {
                return (videoTmp, "ffmpeg 调用失败：" + ex.Message);
            }
            finally
            {
                try { File.Delete(videoTmp); } catch { }
                try { File.Delete(audioTmp); } catch { }
            }

            return (output, "");
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[WatchTogether] 下载失败");
            return (null, ex.Message);
        }
    }

    static int Bandwidth(JsonElement item)
        => item.TryGetProperty("bandwidth", out JsonElement b) && b.TryGetInt32(out int value) ? value : 0;

    async Task<bool> DownloadToFileAsync(string url, string path, CancellationToken ct)
    {
        try
        {
            using HttpResponseMessage response = await http.GetAsync(
                url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode) return false;

            await using Stream input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using FileStream output = File.Create(path);
            await input.CopyToAsync(output, ct).ConfigureAwait(false);

            return new FileInfo(path).Length > 1024;
        }
        catch (Exception ex)
        {
            logger.LogDebug("[WatchTogether] 下载流失败：{Message}", ex.Message);
            return false;
        }
    }

    async Task<(string Img, string Sub)?> TryGetWbiKeysAsync(CancellationToken ct)
    {
        using JsonDocument? nav = await GetJsonAsync(
            "https://api.bilibili.com/x/web-interface/nav", ct).ConfigureAwait(false);

        if (nav == null || !nav.RootElement.TryGetProperty("data", out JsonElement data)
            || !data.TryGetProperty("wbi_img", out JsonElement wbi))
            return null;

        string img = wbi.TryGetProperty("img_url", out JsonElement i) ? i.GetString() ?? "" : "";
        string sub = wbi.TryGetProperty("sub_url", out JsonElement s) ? s.GetString() ?? "" : "";

        string imgKey = WbiSigner.ExtractKey(img);
        string subKey = WbiSigner.ExtractKey(sub);

        return imgKey.Length > 0 && subKey.Length > 0 ? (imgKey, subKey) : null;
    }

    async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            string cookie = (getCookie() ?? "").Trim();
            if (cookie.Length > 0)
                request.Headers.TryAddWithoutValidation("Cookie", cookie.Contains('=') ? cookie : "SESSDATA=" + cookie);

            using HttpResponseMessage response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug("[WatchTogether] 请求失败 {Url}：{Message}", url, ex.Message);
            return null;
        }
    }

    static string Sanitize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return DateTime.Now.ToString("yyyyMMdd-HHmmss");

        var sb = new System.Text.StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
                continue;
            sb.Append(c);
        }

        string value = sb.ToString().Trim().Trim('.');
        if (value.Length == 0) value = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        return value.Length <= 80 ? value : value[..80];
    }

    public void Dispose() => http.Dispose();
}
