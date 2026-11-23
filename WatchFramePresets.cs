using System;
using System.Collections.Generic;
using System.Linq;

namespace presents.WatchTogether;

/// <summary>
/// 抓帧预设：把 presents.Video 里那套「内容场景 → 采样参数」的经验，搬到陪看的实时抓帧上。
///
/// 对应关系（Video 是整段离线抽帧，陪看是在线抓「现在这一帧」）：
///   Video.SampleFps / FrameCount  →  LiveFrameIntervalSeconds（多久抓一张）
///   Video.OverviewCells / 宽度     →  LiveFrameMaxWidth / LiveFrameQuality
///   Video 的画面变化去重阈值        →  LiveFrameChangeThreshold
///   概览保留张数                   →  LiveFrameKeep
///
/// 注意：这里的值是给「在线抓帧」用的，不是离线全片抽帧——所以间隔普遍更长：
/// 看视频时角色只需要知道「现在这页/这个镜头是什么」，不需要每一帧都有。
/// </summary>
public static class WatchFramePresets
{
    public sealed record Preset(
        string Name,
        int IntervalSeconds,
        double ChangeThreshold,
        int MaxWidth,
        int Quality,
        int Keep,
        string Note);

    /// <summary>预设表。顺序即面板里的显示顺序。</summary>
    public static readonly IReadOnlyList<Preset> All = new List<Preset>
    {
        new("讲解/PPT", 8, 4, 1600, 78, 24,
            "PPT 翻页多、字小：抓得密、阈值低、宽 1600 —— 讲解类首选"),

        new("论文/图表", 6, 3, 1920, 80, 30,
            "图表/公式密集：最密抓帧 + 全高清，适合论文讲解、架构图"),

        new("快切资讯", 10, 12, 1600, 75, 20,
            "快切 + 大字号标题：间隔适中、阈值调高，防止每个镜头都上传"),

        new("通用（推荐）", 20, 6, 1280, 72, 12,
            "默认档：剧情、教程、访谈都够用，流量与 token 平衡"),

        new("影视省流", 60, 14, 960, 65, 6,
            "只偶尔看一眼画面：最省带宽与 token，字幕仍是主要素材"),

        // 来自 presents.BiliScroll 的实际配置（FrameIntervalSec=15 / FrameMaxWidth=768 /
        // FrameMaxCount=20 / FrameDedup=true）：那套是给「整片扫一遍 + 视觉识图」调的，
        // 搬到在线抓帧上正好适合长时间挂着看的学习类视频。
        new("B站扫片（学习流）", 15, 6, 768, 70, 20,
            "BiliScroll 那套值：15 秒 / 768 宽 / 留 20 帧，长时间看课不心疼"),
    };

    public static Preset? Find(string? name)
        => All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    /// <summary>把预设写进配置（只改抓帧五项，不动字幕和其它设置）。</summary>
    public static void Apply(Preset preset, WatchTogetherConfig config)
    {
        config.LiveFrameIntervalSeconds = preset.IntervalSeconds;
        config.LiveFrameChangeThreshold = preset.ChangeThreshold;
        config.LiveFrameMaxWidth = preset.MaxWidth;
        config.LiveFrameQuality = preset.Quality;
        config.LiveFrameKeep = preset.Keep;
        config.FramePreset = preset.Name;
    }

    /// <summary>当前配置与哪个预设一致（界面高亮用）；都不一致返回 null。</summary>
    public static Preset? Match(WatchTogetherConfig config)
    {
        foreach (Preset preset in All)
        {
            if (preset.IntervalSeconds == config.LiveFrameIntervalSeconds
                && Math.Abs(preset.ChangeThreshold - config.LiveFrameChangeThreshold) < 0.01
                && preset.MaxWidth == config.LiveFrameMaxWidth
                && preset.Quality == config.LiveFrameQuality
                && preset.Keep == config.LiveFrameKeep)
            {
                return preset;
            }
        }

        return null;
    }
}
