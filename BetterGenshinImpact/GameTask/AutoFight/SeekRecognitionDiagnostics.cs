using System.Collections.Generic;
using System.Linq;

namespace BetterGenshinImpact.GameTask.AutoFight;

/// <summary>单帧识别已有分支的计数，不含图像、不重跑识别、不参与决策。</summary>
internal sealed class SeekRecognitionDiagnostics
{
    public int RawComponents { get; internal set; }
    public int Accepted { get; internal set; }
    public int HealthWidthRejected { get; internal set; }
    public Dictionary<string, int> Rejections { get; } = new();
    public List<SeekRejectedSample> NarrowBarSamples { get; } = new();

    internal void Record(EnemySeekVisual visual, string? healthFailure, string indicatorResult,
        int minimumWidth, int imageWidth, int imageHeight, bool accepted)
    {
        if (accepted) { Accepted++; return; }
        var reason = $"health:{healthFailure ?? "accepted"}/indicator:{indicatorResult}";
        Rejections[reason] = Rejections.GetValueOrDefault(reason) + 1;
        if (healthFailure != "width") return;
        HealthWidthRejected++;
        if (NarrowBarSamples.Count < 8 && visual.Width >= 6 && visual.Height >= 4 &&
            !AutoFightSeek.IsPlayerHudHealthBar(visual, imageWidth, imageHeight))
            NarrowBarSamples.Add(new(visual.X, visual.Y, visual.Width, visual.Height, minimumWidth));
    }

    internal string ToCompactString() =>
        $"raw={RawComponents} accepted={Accepted} healthWidthRejected={HealthWidthRejected} " +
        $"reject=[{string.Join(",", Rejections.Select(pair => $"{pair.Key}:{pair.Value}"))}] " +
        $"narrow=[{string.Join(";", NarrowBarSamples.Select(sample => $"{sample.X},{sample.Y},{sample.Width}x{sample.Height},minW={sample.MinimumWidth}"))}] narrowFeature=not-evaluated";
}

internal sealed record SeekRejectedSample(int X, int Y, int Width, int Height, int MinimumWidth,
    string HealthFeature = "not-evaluated");
