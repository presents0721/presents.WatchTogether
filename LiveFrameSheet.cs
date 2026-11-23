using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace presents.WatchTogether;

/// <summary>
/// 实时帧的「概览拼图」：把最近几帧排成 3×N 一张图。
///
/// 为什么值得做：模型逐张看 6 帧 = 6 次视觉调用；拼成一张 = 1 次，token 和延迟都省一半以上。
/// 这是 presents.BiliScroll 里「概览拼图」思路的在线版（那边是整片抽帧后拼，这里拼最近几帧）。
///
/// 做法：每帧缩到同一宽度，一行 3 张横拼，多行再竖拼；末行不足 3 张就用最后一帧补齐。
/// 只依赖 ffmpeg 的 scale / hstack / vstack，不装任何东西。
/// </summary>
public static class LiveFrameSheet
{
    public static async Task<string?> BuildAsync(
        string ffmpeg,
        IReadOnlyList<LiveFrame> frames,
        int cells,
        CancellationToken ct)
    {
        if (frames.Count < 3) return null;

        int want = Math.Clamp(cells, 3, 9);
        List<LiveFrame> use = frames
            .Skip(Math.Max(0, frames.Count - want))
            .Where(f => File.Exists(f.Path))
            .ToList();

        if (use.Count < 3) return null;

        while (use.Count % 3 != 0)
            use.Add(use[^1]);

        int rows = use.Count / 3;
        string dir = Path.GetDirectoryName(use[^1].Path) ?? "";
        if (dir.Length == 0) return null;

        // 以「最后一帧的位置」做缓存键：同一段不重复拼
        string output = Path.Combine(dir, $"sheet_{use[^1].Pos:0}.jpg");
        if (File.Exists(output) && new FileInfo(output).Length > 0) return output;

        var args = new List<string> { "-hide_banner", "-loglevel", "error" };
        foreach (LiveFrame frame in use)
        {
            args.Add("-i");
            args.Add(frame.Path);
        }

        var filter = new StringBuilder();
        const int cellWidth = 640;

        for (int index = 0; index < use.Count; index++)
            filter.Append($"[{index}:v]scale={cellWidth}:-2,setsar=1[v{index}];");

        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < 3; column++)
                filter.Append($"[v{row * 3 + column}]");

            filter.Append($"hstack=inputs=3[r{row}];");
        }

        if (rows > 1)
        {
            for (int row = 0; row < rows; row++)
                filter.Append($"[r{row}]");

            filter.Append($"vstack=inputs={rows}[out]");
        }
        else
        {
            filter.Append("[r0]null[out]");
        }

        args.AddRange(new[]
        {
            "-filter_complex", filter.ToString(),
            "-map", "[out]",
            "-frames:v", "1",
            "-q:v", "3",
            "-y", output
        });

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

            if (process.ExitCode == 0 && File.Exists(output) && new FileInfo(output).Length > 0)
                return output;
        }
        catch
        {
            // 拼图失败不影响逐帧结果
        }

        return null;
    }
}
