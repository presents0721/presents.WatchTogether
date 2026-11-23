using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace presents.WatchTogether;

/// <summary>
/// 网页视频的「听」通道：扩展用 tabCapture 顺便录下标签页声音，按 20 秒切片传过来，
/// 这里用本机 whisper.cpp 转成字幕（SRT），再按切片起点平移成绝对时间轴。
///
/// 为什么不整段转：陪看是实时的，等整片下完再转就没有陪看意义了；
/// 20 秒切片的延迟可以接受（陪看本来按 5 分钟一段说话）。
///
/// 依赖（都可配置）：
///   whisper-cli.exe  —— 优先用配置里的路径；否则在 PATH 或插件数据目录 tools\whisper\ 下找
///   ggml-*.bin 模型  —— 默认用同目录 models\ 下第一个
///   ffmpeg           —— 复用陪看已探测到的那个（解 webm/opus 用）
/// 任一缺失就明确返回「不可用」，不静默失败。
/// </summary>
public sealed class WhisperAsr
{
    // 不再写死本机路径：兜底引擎按 PATH 查，或在插件数据目录的 tools\whisper\ 下找。
    // 之前硬编码的开发机路径既不便携也会在分享源码时暴露本机目录。
    static string[] DefaultWhisperCandidates(string dataDir) => new[] {
        Path.Combine(dataDir, "tools", "whisper", "Release", "whisper-cli.exe"),
        Path.Combine(dataDir, "tools", "whisper", "whisper-cli.exe")
    };

    readonly ILogger logger;
    readonly Func<WatchTogetherConfig> config;
    readonly string workRoot;
    readonly string dataRoot;

    public WhisperAsr(ILogger logger, Func<WatchTogetherConfig> config, string dataDir)
    {
        this.logger = logger;
        this.config = config;
        dataRoot = dataDir;
        workRoot = Path.Combine(dataDir, "asr");
        Directory.CreateDirectory(workRoot);
    }

    /// <summary>
    /// 转写结果。NoSpeech = 这一段没有可辨识的人声（纯 BGM / 静音 / 只有音乐声）。
    /// 纯 PPT + 背景音乐的讲解视频全靠这个标记——否则 whisper 会把音乐"听"成歌词或套话，
    /// 混进字幕里比没有字幕更糟。
    /// </summary>
    public sealed record Result(List<SubtitleCue> Cues, string Error, bool NoSpeech = false,
        int Dropped = 0, string Sample = "", string Raw = "");

    /// <summary>转写一段音频（webm/opus 或任意 ffmpeg 认识的格式）。</summary>
    public async Task<Result> TranscribeAsync(byte[] audio, double startPos, string sessionKey, CancellationToken ct, bool useVad = true)
    {
        string? whisper = ResolveWhisper();
        if (whisper == null)
            return new Result(new List<SubtitleCue>(), "找不到 whisper-cli.exe（在「Whisper 路径」里填）");

        string? model = ResolveModel(whisper);
        if (model == null)
            return new Result(new List<SubtitleCue>(), "找不到 ggml 模型（在「Whisper 模型」里填）");

        string? ffmpeg = WatchFrames.FindFfmpeg(config().FfmpegPath ?? "");
        if (ffmpeg == null)
            return new Result(new List<SubtitleCue>(), "找不到 ffmpeg，无法解码音频");

        string stamp = DateTime.Now.ToString("HHmmss-fff");
        string dir = Path.Combine(workRoot, WatchFormat.SafeFileName(sessionKey));
        Directory.CreateDirectory(dir);

        string raw = Path.Combine(dir, $"chunk-{stamp}.webm");
        string wav = Path.Combine(dir, $"chunk-{stamp}.wav");
        string baseName = Path.Combine(dir, $"chunk-{stamp}");
        string srt = baseName + ".srt";

        try
        {
            await File.WriteAllBytesAsync(raw, audio, ct).ConfigureAwait(false);

            // ① 解成 16k 单声道 wav（whisper.cpp 只吃 wav）
            if (!await RunAsync(ffmpeg, new[]
                {
                    "-hide_banner", "-loglevel", "error",
                    "-i", raw, "-ar", "16000", "-ac", "1", "-y", wav
                }, 60, ct).ConfigureAwait(false))
            {
                // 解码失败：保留坏切片现场（改名 .bad），供字节级排查
                try
                {
                    string bad = raw + ".bad";
                    if (File.Exists(raw)) File.Move(raw, bad, true);
                    logger.LogWarning("ASR 解码失败，坏切片已保留：{0}（{1} 字节，前 16 字节：{2}）",
                        bad, new FileInfo(bad).Length,
                        BitConverter.ToString(File.ReadAllBytes(bad), 0, Math.Min(16, (int)new FileInfo(bad).Length)));
                }
                catch { }

                return new Result(new List<SubtitleCue>(), "音频解码失败（切片可能不完整）");
            }

            // ② whisper.cpp 直接出 SRT
            string language = string.IsNullOrWhiteSpace(config().AsrLanguage) ? "zh" : config().AsrLanguage.Trim();

            // VAD（Silero 人声检测）：纯 BGM 在识别前就被切掉，whisper 没机会编歌词。
            // 模型只有 0.8 MB，找不到时自动退回「文本过滤」那条路。
            // useVad=false：歌曲/强 BGM 兜底时不能开 VAD（Silero 会把人声当非语音滤掉）
            string? vad = config().AsrVad && useVad ? ResolveVad(whisper) : null;

            var whisperArgs = new List<string>
            {
                "-m", model,
                "-f", wav,
                "-l", language,
                "-osrt",
                "-of", baseName,
                "-np"
            };

            if (vad != null)
            {
                whisperArgs.Add("--vad");
                whisperArgs.Add("-vm");
                whisperArgs.Add(vad);
            }

            bool ok = await RunAsync(whisper, whisperArgs.ToArray(), 300, ct).ConfigureAwait(false);

            if (!ok)
                return new Result(new List<SubtitleCue>(), "whisper 转写失败（看日志）");

            // VAD 把整段都判成非人声时，whisper 不会产出 SRT —— 这是「无人声」，不是失败
            if (!File.Exists(srt))
                return new Result(new List<SubtitleCue>(), "", true, 0, "",
                    Shorten("(VAD 全滤)", 24));

            // ③ 解析 + 平移
            List<SubtitleCue> cues = WatchSubtitles.ParseFile(srt);

            bool filterOn = config().AsrMusicFilter;

            var kept = new List<SubtitleCue>();
            int dropped = 0;
            string sample = "";

            // 扩展上报的 startPos 其实是「这一片送达时的播放位置」（切片末尾），
            // 用 wav 的真实时长往回推，否则字幕会整体晚一个切片（AI 跟着慢半拍）。
            double chunkSeconds = WavDurationSeconds(wav);
            double shift = Math.Max(0, startPos - chunkSeconds);

            foreach (SubtitleCue cue in cues)
            {
                if (string.IsNullOrWhiteSpace(cue.Text) || WatchSubtitles.IsBoilerplate(cue.Text))
                {
                    dropped++;
                    if (sample.Length == 0) sample = (cue.Text ?? "").Trim();
                    continue;
                }

                if (filterOn && !LooksLikeSpeech(cue.Text))
                {
                    dropped++;
                    if (sample.Length == 0) sample = (cue.Text ?? "").Trim();
                    continue;
                }

                string text = cue.Text.Trim();
                if (config().AsrSimplified) text = TextZh.MaybeSimplify(text);   // whisper 繁体输出 → 简体

                kept.Add(new SubtitleCue
                {
                    From = cue.From + shift,
                    To = cue.To + shift,
                    Text = text
                });
            }

            string rawSample = Shorten(string.Join(" | ", kept.Take(6).Select(c => (c.Text ?? "").Trim())), 240);

            // 一句都没剩下 = 这段是纯 BGM / 静音，不是「转写失败」。
            // 上层据此停用转写、改用画面，避免模型把音乐当台词。
            if (kept.Count == 0)
                return new Result(kept, "", true, dropped, Shorten(sample, 24), rawSample);

            return new Result(kept, "", false, dropped, "", rawSample);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[WatchTogether] ASR 失败");
            return new Result(new List<SubtitleCue>(), ex.Message);
        }
        finally
        {
            foreach (string temp in new[] { raw, wav, srt })
            {
                try { File.Delete(temp); } catch { }
            }
        }
    }

    /// <summary>读 16kHz 单声道 16bit wav 的时长：优先找 data 块大小，找不到再按文件长度估。</summary>
    static double WavDurationSeconds(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            int read = (int)Math.Min(4096, fs.Length);
            byte[] head = new byte[read];
            fs.Read(head, 0, read);
            for (int i = 0; i + 8 <= head.Length; i++)
            {
                if (head[i] == (byte)'d' && head[i + 1] == (byte)'a' && head[i + 2] == (byte)'t' && head[i + 3] == (byte)'a')
                {
                    uint size = BitConverter.ToUInt32(head, i + 4);
                    return size / 32000.0;
                }
            }
            return Math.Max(0, (fs.Length - 44) / 32000.0);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// whisper 在纯音乐上常吐的东西：歌词式重复、字幕组套话、订阅/点赞诱导。
    /// 这些一旦混进字幕，角色就会照着念——比没有字幕更糟，所以宁可丢掉。
    /// 只对短句生效（长句里出现「音乐」两个字是正常内容）。
    /// </summary>
    /// <summary>只要出现就丢的强标记（字幕组、订阅诱导、音乐占位）。</summary>
    static readonly string[] AlwaysJunk =
    {
        "字幕组", "字幕由", "字幕提供", "明镜与点点", "amara", "subscribe", "订阅", "点赞", "打赏", "关注我"
    };

    /// <summary>短句里出现就丢的弱标记（长句里出现「音乐」是正常内容）。</summary>
    static readonly string[] ShortJunk =
    {
        "字幕", "谢谢观看", "谢谢大家", "感谢观看", "感谢您的观看", "谢谢收看",
        "thanks for watching", "thank you for watching", "please subscribe",
        "music", "音乐", "掌声", "applause", "♪", "♫", "♬"
    };

    /// <summary>
    /// 判断一段字幕像不像真人说话。纯音乐上 whisper 会稳定地产出三类垃圾：
    ///   ① 字幕组署名（「( 字幕:貝爾 )」）② 订阅/点赞套话 ③ 重复拟声（「啊啊啊啊」）。
    /// 命中任一类就丢掉；丢光了就判定这一段没人声。
    /// </summary>
    static bool LooksLikeSpeech(string raw)
    {
        string text = (raw ?? "").Trim();
        if (text.Length == 0) return false;

        // 先去掉音乐符号再判断长度与占比
        string cleaned = text.Replace("♪", "").Replace("♫", "").Replace("♬", "")
            .Replace("…", "").Replace("~", "").Trim();

        if (cleaned.Length < 2) return false;

        int useful = cleaned.Count(c => char.IsLetterOrDigit(c) || IsCjk(c));
        if (useful < 2) return false;

        // 有效字符占比过低（装饰线、歌词分隔符）
        if (useful * 100 / Math.Max(1, cleaned.Length) < 55) return false;

        // 高度重复（「啊啊啊啊」「啦啦啦啦」）：音乐段更常见
        if (cleaned.Length >= 6 && cleaned.Distinct().Count() <= 3) return false;

        string lower = cleaned.ToLowerInvariant();

        foreach (string junk in AlwaysJunk)
        {
            if (lower.Contains(junk, StringComparison.Ordinal)) return false;
        }

        foreach (string junk in ShortJunk)
        {
            if (lower.Contains(junk, StringComparison.Ordinal) && cleaned.Length <= 30) return false;
        }

        return true;
    }

    static string Shorten(string value, int max)
    {
        string text = (value ?? "").Replace("\n", " ").Replace("\r", " ").Trim();
        return text.Length <= max ? text : text[..max];
    }

    static bool IsCjk(char c) => c >= 0x4E00 && c <= 0x9FFF;

    /// <summary>探测结果，控制台显示用。</summary>
    public (string Whisper, string Model, bool Ready) Probe()
    {
        string? whisper = ResolveWhisper();
        string? model = whisper == null ? null : ResolveModel(whisper);
        return (whisper ?? "", model ?? "", whisper != null && model != null);
    }

    /// <summary>VAD 模型是否可用（给控制台/状态显示）。</summary>
    public bool HasVad()
    {
        string? whisper = ResolveWhisper();
        return whisper != null && ResolveVad(whisper) != null;
    }

    /// <summary>
    /// 找 Silero VAD 模型：配置指定 → whisper 同目录 → models\ 子目录。
    /// 模型文件名带 silero / vad 的 .bin / .ggml 都认。
    /// </summary>
    string? ResolveVad(string whisper)
    {
        string configured = (config().AsrVadModel ?? "").Trim().Trim('"');
        if (configured.Length > 0)
            return File.Exists(configured) ? configured : null;

        string dir = Path.GetDirectoryName(whisper) ?? "";
        string modelsDir = Path.Combine(dir, "models");

        foreach (string base_ in new[] { modelsDir, dir })
        {
            if (!Directory.Exists(base_)) continue;

            try
            {
                string? hit = Directory.GetFiles(base_, "*.bin")
                    .Concat(Directory.GetFiles(base_, "*.ggml"))
                    .Where(f => new FileInfo(f).Length > 100 * 1024)          // 跳过下载失败的 HTML 垃圾
                    .OrderByDescending(f => Path.GetFileName(f).ToLowerInvariant().Contains("silero"))
                    .ThenBy(f => Path.GetFileName(f).ToLowerInvariant().Contains("for-tests"))  // 纯 silero* 优先
                    .FirstOrDefault(f =>
                    {
                        string name = Path.GetFileName(f).ToLowerInvariant();
                        return name.Contains("silero") || name.Contains("vad");
                    });

                if (hit != null) return hit;
            }
            catch
            {
                // 目录读不了就跳过
            }
        }

        return null;
    }

    string? ResolveWhisper()
    {
        string configured = (config().WhisperPath ?? "").Trim().Trim('"');

        if (configured.Length > 0)
            return File.Exists(configured) ? configured : null;

        foreach (string candidate in DefaultWhisperCandidates(dataRoot))
        {
            if (File.Exists(candidate)) return candidate;
        }

        // PATH 里碰一下
        foreach (string name in new[] { "whisper-cli.exe", "whisper.exe" })
        {
            string? found = WhichExe(name);
            if (found != null) return found;
        }

        return null;
    }

    string? ResolveModel(string whisper)
    {
        string configured = (config().WhisperModel ?? "").Trim().Trim('"');
        if (configured.Length > 0) return File.Exists(configured) ? configured : null;

        // 猜：exe 同目录的 models\ 下第一个 ggml-*.bin
        string dir = Path.GetDirectoryName(whisper) ?? "";

        foreach (string probe in new[]
                 {
                     Path.Combine(dir, "models"),
                     Path.Combine(dir, "..", "models"),
                     dir
                 })
        {
            if (!Directory.Exists(probe)) continue;

            string? hit = Directory.EnumerateFiles(probe, "ggml-*.bin")
                .OrderBy(p => new FileInfo(p).Length)
                .FirstOrDefault();

            if (hit != null) return hit;
        }

        return null;
    }

    static string? WhichExe(string name)
    {
        try
        {
            var info = new ProcessStartInfo("where", name)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using Process? process = Process.Start(info);
            if (process == null) return null;

            string output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(5000);

            string first = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .FirstOrDefault() ?? "";

            return File.Exists(first) ? first : null;
        }
        catch
        {
            return null;
        }
    }

    async Task<bool> RunAsync(string exe, IEnumerable<string> args, int timeoutSeconds, CancellationToken ct)
    {
        try
        {
            var info = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (string arg in args)
                info.ArgumentList.Add(arg);

            using Process process = Process.Start(info)!;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            string stderr = await process.StandardError.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            if (process.ExitCode != 0)
                logger.LogWarning("[WatchTogether] ASR 进程退出 {Code}：{Args}｜stderr 前 300 字：{Err}",
                    process.ExitCode, string.Join(" ", args), stderr.Length > 300 ? stderr[..300] : stderr);

            return process.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[WatchTogether] ASR 进程启动失败");
            return false;
        }
    }
}
