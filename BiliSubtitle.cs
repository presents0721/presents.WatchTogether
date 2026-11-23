using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace presents.WatchTogether;

/// <summary>一条平台字幕（B 站 CC / AI 字幕）解析结果。</summary>
public sealed class PlatformSubtitle
{
    public string Label { get; init; } = "";
    public string Bvid { get; init; } = "";
    public long Cid { get; init; }
    public List<SubtitleCue> Cues { get; init; } = new();
}

/// <summary>
/// B 站字幕直取：view 拿 cid → nav 拿 wbi key → x/player/wbi/v2 拿字幕轨道 →
/// 下载 subtitle_url 的 JSON（body: from/to/content）→ 转成本地 SRT 同款的 SubtitleCue。
///
/// 为什么值得做：陪看原先只能读页面 DOM 里的字幕元素，而 B 站的 CC / AI 字幕走的是
/// 播放器内部的 JSON，DOM 里根本抓不到 —— 这就是「AI 只听到调子」的根因之一。
/// 讲解类视频大多有 AI 字幕，拿到它就等于拿到了台词。
///
/// 不需要登录也能拿「未登录可见」的字幕轨道；AI 字幕通常要带 SESSDATA。
/// </summary>
public sealed class BiliSubtitleClient : IDisposable
{
    const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    static readonly Regex BvPattern = new(@"(BV[0-9A-Za-z]{10})", RegexOptions.Compiled);

    static readonly object KeyGate = new();
    static (string Img, string Sub, DateTime At)? keyCache;

    readonly HttpClient http;
    readonly ILogger logger;
    readonly Func<string> getCookie;
    readonly Action<string> log;

    public BiliSubtitleClient(ILogger logger, Func<string> getCookie, Action<string> log)
    {
        this.logger = logger;
        this.getCookie = getCookie;
        this.log = log;

        http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
    }

    /// <summary>从任意 URL 里抠 BV 号。</summary>
    public static string? ExtractBvid(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        Match match = BvPattern.Match(url);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>拉一支视频的字幕；没有轨道 / 拉取失败返回 null（不抛，调用方按「没字幕」处理）。</summary>
    public async Task<PlatformSubtitle?> GetAsync(string bvid, CancellationToken ct)
    {
        try
        {
            long aid = 0, cid = 0;
            using (JsonDocument? view = await GetJsonAsync(
                       $"https://api.bilibili.com/x/web-interface/view?bvid={bvid}", ct).ConfigureAwait(false))
            {
                if (view == null) return null;

                JsonElement data = view.RootElement.GetProperty("data");
                aid = data.TryGetProperty("aid", out JsonElement aidNode) ? aidNode.GetInt64() : 0;
                cid = data.TryGetProperty("cid", out JsonElement cidNode) ? cidNode.GetInt64() : 0;
            }

            if (cid <= 0) return null;

            (string Img, string Sub)? keys = await GetWbiKeysAsync(ct).ConfigureAwait(false);
            if (keys == null) return null;

            var pars = new Dictionary<string, string>
            {
                ["aid"] = aid.ToString(),
                ["cid"] = cid.ToString(),
                ["bvid"] = bvid
            };

            string query = WbiSigner.Sign(pars, keys.Value.Img, keys.Value.Sub);
            string? subtitleUrl = null;
            string label = "";

            using (JsonDocument? player = await GetJsonAsync(
                       $"https://api.bilibili.com/x/player/wbi/v2?{query}", ct).ConfigureAwait(false))
            {
                if (player == null) return null;

                if (!player.RootElement.TryGetProperty("data", out JsonElement data)) return null;
                if (!data.TryGetProperty("subtitle", out JsonElement subtitle)) return null;
                if (!subtitle.TryGetProperty("subtitles", out JsonElement tracks)) return null;
                if (tracks.ValueKind != JsonValueKind.Array || tracks.GetArrayLength() == 0) return null;

                // 选轨优先级：人工中文 > AI 中文（ai-zh）> 任意中文 > 第一条
                var list = tracks.EnumerateArray().ToList();
                JsonElement? chosen = list.FirstOrDefault(t =>
                    Lan(t).StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                    && !Lan(t).StartsWith("ai-", StringComparison.OrdinalIgnoreCase));
                if (chosen is null || chosen.Value.ValueKind == JsonValueKind.Undefined)
                {
                    chosen = list.FirstOrDefault(t =>
                        Lan(t).StartsWith("ai-zh", StringComparison.OrdinalIgnoreCase));
                }
                if (chosen is null || chosen.Value.ValueKind == JsonValueKind.Undefined)
                {
                    chosen = list.FirstOrDefault(t =>
                        Lan(t).StartsWith("zh", StringComparison.OrdinalIgnoreCase));
                }

                JsonElement track = chosen is { ValueKind: not JsonValueKind.Undefined }
                    ? chosen.Value
                    : list[0];

                subtitleUrl = track.TryGetProperty("subtitle_url", out JsonElement urlNode)
                    ? urlNode.GetString()
                    : null;

                string lanDoc = track.TryGetProperty("lan_doc", out JsonElement docNode)
                    ? docNode.GetString() ?? ""
                    : "";

                label = string.IsNullOrWhiteSpace(lanDoc)
                    ? $"B站字幕（{Lan(track)}）"
                    : $"B站字幕（{lanDoc}）";
            }

            if (string.IsNullOrWhiteSpace(subtitleUrl)) return null;
            if (subtitleUrl!.StartsWith("//", StringComparison.Ordinal))
                subtitleUrl = "https:" + subtitleUrl;

            using JsonDocument? body = await GetJsonAsync(subtitleUrl, ct).ConfigureAwait(false);
            if (body == null) return null;

            var cues = new List<SubtitleCue>();
            if (body.RootElement.TryGetProperty("body", out JsonElement lines)
                && lines.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement line in lines.EnumerateArray())
                {
                    if (!line.TryGetProperty("from", out JsonElement fromNode)) continue;
                    if (!line.TryGetProperty("content", out JsonElement textNode)) continue;

                    double from = fromNode.GetDouble();
                    double to = line.TryGetProperty("to", out JsonElement toNode) ? toNode.GetDouble() : from + 2;
                    string text = (textNode.GetString() ?? "").Trim();
                    if (text.Length == 0) continue;

                    cues.Add(new SubtitleCue { From = from, To = to, Text = text });
                }
            }

            if (cues.Count == 0) return null;

            log($"平台字幕：{label}，{cues.Count} 句（{bvid}）");

            return new PlatformSubtitle
            {
                Label = label,
                Bvid = bvid,
                Cid = cid,
                Cues = cues
            };
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[WatchTogether] 拉 B 站字幕失败：{Bvid}", bvid);
            return null;
        }
    }

    static string Lan(JsonElement track)
        => track.TryGetProperty("lan", out JsonElement lan) ? lan.GetString() ?? "" : "";

    // ---------------- HTTP ----------------

    async Task<(string Img, string Sub)?> GetWbiKeysAsync(CancellationToken ct)
    {
        lock (KeyGate)
        {
            if (keyCache is { } cached && (DateTime.UtcNow - cached.At).TotalHours < 12)
                return (cached.Img, cached.Sub);
        }

        using JsonDocument? nav = await GetJsonAsync(
            "https://api.bilibili.com/x/web-interface/nav", ct).ConfigureAwait(false);
        if (nav == null) return null;

        // 未登录时 code=-101，但 data.wbi_img 仍然给
        if (!nav.RootElement.TryGetProperty("data", out JsonElement data)) return null;
        if (!data.TryGetProperty("wbi_img", out JsonElement wbiImg)) return null;

        string img = wbiImg.TryGetProperty("img_url", out JsonElement i) ? i.GetString() ?? "" : "";
        string sub = wbiImg.TryGetProperty("sub_url", out JsonElement s) ? s.GetString() ?? "" : "";

        string imgKey = WbiSigner.ExtractKey(img);
        string subKey = WbiSigner.ExtractKey(sub);
        if (imgKey.Length == 0 || subKey.Length == 0) return null;

        lock (KeyGate)
        {
            keyCache = (imgKey, subKey, DateTime.UtcNow);
        }

        return (imgKey, subKey);
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

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
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

    public void Dispose() => http.Dispose();
}
