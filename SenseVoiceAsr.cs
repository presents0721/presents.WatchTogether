using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Alife.Foundation;
using Alife.Function.Auditory;
using Microsoft.Extensions.Logging;

namespace presents.WatchTogether;

/// <summary>
/// SenseVoice（Alife 框架本地识别器）转写：ffmpeg 解 webm → 16kHz 单声道 float PCM
/// → 分块喂给 IAudioRecognizer（内置 Silero VAD，纯 BGM 不出词）。
///
/// 相对 whisper.cpp：中文 CER 约为其 1/3（AISHELL-1 ~3% vs ~10%），CPU 快一个量级，
/// 原生输出简体 + 标点 + ITN。代价：无句级时间戳，按片内时长均分近似。
/// </summary>
public sealed class SenseVoiceAsr(
    IAudioRecognizerProvider provider,
    Func<WatchTogetherConfig> config,
    ILogger logger)
{
    /// <summary>SenseVoice 的经典幻觉模板（训练语料里大量视频结尾）。整句命中才丢。</summary>
    static readonly string[] Boilerplate =
    [
        "请不吝点赞", "点赞订阅", "订阅转发", "点亮小铃铛", "打赏", "字幕由", "字幕组",
        "字幕志愿者", "感谢观看", "谢谢观看", "谢谢收看", "明镜", "点点栏目",
        "thanks for watching", "subscribe", "amara.org",
    ];

    public bool Ready => provider != null;

    /// <summary>最近一次：VAD 空输出但音频样本有能量（歌曲/强 BGM 场景）。供模块决定回退 whisper。</summary>
    public bool LastHadEnergy { get; private set; }

    /// <summary>调试钩子（模块注入到桥 trace）。</summary>
    public static Action<string>? DebugHook;

    static void Trace(string msg)
    {
        try { DebugHook?.Invoke("sv: " + msg); } catch { }
    }

    public async Task<WhisperAsr.Result> TranscribeAsync(byte[] audio, double pos, string key, CancellationToken ct)
    {
        Trace("enter");
        try
        {
        string ffmpeg = WatchFrames.FindFfmpeg(config().FfmpegPath ?? "") ?? "";
        Trace("ffmpeg=" + (ffmpeg.Length > 0 ? "ok" : "empty"));
        if (ffmpeg.Length == 0)
            return new WhisperAsr.Result(new List<SubtitleCue>(), "找不到 ffmpeg，无法解码音频");

        // 会话键形如 bili:bv16zZPBsEka，冒号在 Windows 路径里非法 → 统一换成下划线
        string safeKey = new string(key.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        string dir = Path.Combine(AlifePath.StorageFolderPath, "PluginData", "presents.WatchTogether", "asr", safeKey);
        Directory.CreateDirectory(dir);
        string raw = Path.Combine(dir, $"sense-{DateTime.Now:HHmmss}-{Environment.CurrentManagedThreadId}.webm");

        try
        {
            await File.WriteAllBytesAsync(raw, audio, ct).ConfigureAwait(false);

            // ① ffmpeg → float32 PCM（16k 单声道）到 stdout
            byte[] pcm = await DecodeAsync(ffmpeg, raw, ct).ConfigureAwait(false);
            Trace("pcm=" + pcm.Length);
            if (pcm.Length < 1600)   // < 50ms：空片
                return new WhisperAsr.Result(new List<SubtitleCue>(), "", true, 0, "", "(空)");

            float[] samples = new float[pcm.Length / 4];
            Buffer.BlockCopy(pcm, 0, samples, 0, samples.Length * 4);
            double dur = samples.Length / 16000.0;

            // ② 流式喂识别器（VAD 在内部切句）
            var texts = new List<string>();
            using (IAudioRecognizer rec = provider.CreateAudioRecognizer())
            {
                rec.Recognized += t =>
                {
                    string line = (t ?? "").Trim();
                    if (line.Length > 0) texts.Add(line);
                };

                Trace("samples=" + samples.Length + " dur=" + dur.ToString("0.0"));
                const int step = 3200;   // 200ms
                for (int i = 0; i < samples.Length; i += step)
                {
                    int n = Math.Min(step, samples.Length - i);
                    float[] slice = samples[i..(i + n)];
                    rec.AcceptWaveform(slice, n);
                    ct.ThrowIfCancellationRequested();
                }

                rec.Flush();
                Trace("texts=" + texts.Count);
            }

            // ③ 过滤幻觉 + 组 cue（片内按条数均分时间，近似）
            var kept = new List<string>();
            string rawSample = "";
            if (texts.Count > 0)
            {
                string joined = string.Join(" / ", texts);
                rawSample = joined.Length > 240 ? joined[..240] : joined;
            }
            foreach (string t in texts)
            {
                if (IsBoilerplate(t)) continue;
                kept.Add(TextZh.MaybeSimplify(t));
            }

            // VAD 没有可用输出、但音频有能量（歌曲/强 BGM）→ 标记，让模块回退 whisper
            float peak = 0f;
            for (int i = 0; i < samples.Length; i += 16)
            {
                float a = Math.Abs(samples[i]);
                if (a > peak) peak = a;
            }
            LastHadEnergy = kept.Count == 0 && peak > 0.02f;

            if (kept.Count == 0)
                return new WhisperAsr.Result(new List<SubtitleCue>(), "", true, texts.Count, "", rawSample);

            var cues = new List<SubtitleCue>(kept.Count);
            // 扩展上报的 pos 是「这一片送达时的播放位置」，也就是切片末尾；
            // 用真实音频时长往回推切片起点，否则字幕会整体晚 20 秒（AI 就跟着慢半拍）。
            double start = pos > dur ? pos - dur : 0;
            double segDur = dur / kept.Count;
            for (int i = 0; i < kept.Count; i++)
            {
                cues.Add(new SubtitleCue
                {
                    From = start + i * segDur,
                    To = start + (i + 1) * segDur,
                    Text = kept[i],
                });
            }

            return new WhisperAsr.Result(cues, "", false, texts.Count - kept.Count, "", rawSample);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            logger.LogWarning("[一起看] SenseVoice 转写失败：{0}", e.Message);
            return new WhisperAsr.Result(new List<SubtitleCue>(), "转写失败：" + e.Message);
        }
        }
        catch (Exception e2)
        {
            Trace("outer: " + e2.Message);
            logger.LogWarning("[一起看] SenseVoice 外层失败：{0}", e2.Message);
            return new WhisperAsr.Result(new List<SubtitleCue>(), "转写失败：" + e2.Message);
        }
    }

    /// <summary>ffmpeg 把音频文件解成 16kHz 单声道 f32le，返回原始字节。</summary>
    static async Task<byte[]> DecodeAsync(string ffmpeg, string file, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,     // 必须接管 stdin：否则 ffmpeg 可能等交互输入挂死
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string a in new[] { "-hide_banner", "-nostdin", "-loglevel", "error", "-i", file, "-f", "f32le", "-ar", "16000", "-ac", "1", "-" })
            psi.ArgumentList.Add(a);

        Trace("ffmpeg start");
        using Process p = Process.Start(psi)!;
        try { p.StandardInput.Close(); } catch { }
        using var ms = new MemoryStream();
        await p.StandardOutput.BaseStream.CopyToAsync(ms, ct).ConfigureAwait(false);
        Trace("ffmpeg stdout=" + ms.Length);
        string err = await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        Trace("ffmpeg exit=" + p.ExitCode);

        if (p.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg 退出 {p.ExitCode}：{err[..Math.Min(120, err.Length)]}");

        return ms.ToArray();
    }

    static bool IsBoilerplate(string text)
    {
        string t = text.Trim();
        if (t.Length <= 1) return true;

        string lower = t.ToLowerInvariant();
        foreach (string junk in Boilerplate)
        {
            if (lower.Contains(junk, StringComparison.Ordinal) && t.Length <= 30) return true;
        }

        // 全行同字（「啊啊啊啊」驻波幻觉）
        bool allSame = true;
        for (int i = 1; i < t.Length; i++)
        {
            if (t[i] != t[0]) { allSame = false; break; }
        }
        return allSame;
    }
}
