using System;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.Common.Ui;

/// <summary>只缓存已分类的书页像素；每次准入仍由后继帧及原有效期决定。</summary>
internal sealed class KnownHandbookFrame : IDisposable
{
    private Mat? _pixels;
    private Rect _bounds;
    private CaptureFrameStamp _source;

    internal bool Matches(ImageRegion image)
    {
        if (_pixels == null || !image.FrameStamp.IsAfter(_source) ||
            image.SrcMat.Type() != _pixels.Type() || !Inside(image, _bounds)) return false;
        using var current = new Mat(image.SrcMat, _bounds);
        using var difference = new Mat();
        using var gray = new Mat();
        using var changed = new Mat();
        Cv2.Absdiff(_pixels, current, difference);
        Cv2.CvtColor(difference, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.Threshold(gray, changed, 12, 255, ThresholdTypes.Binary);
        // 分块检查整本书及关闭区域，避免中心弹窗被大面积相同背景稀释。
        var tileSize = Math.Max(32, image.Width / 15);
        for (var y = 0; y < changed.Height; y += tileSize)
        for (var x = 0; x < changed.Width; x += tileSize)
        {
            var tileBounds = new Rect(x, y, Math.Min(tileSize, changed.Width - x),
                Math.Min(tileSize, changed.Height - y));
            using var tile = new Mat(changed, tileBounds);
            if (Cv2.CountNonZero(tile) > tileBounds.Width * tileBounds.Height * .015) return false;
        }
        return true;
    }

    internal void Remember(ImageRegion image, UiSnapshot observed)
    {
        Dispose();
        if (!observed.Handbook || observed.MainHud || observed.Revive || observed.Prompt ||
            observed.BlackConfirm || observed.Reward.IsCandidate || observed.DomainTip.IsCandidate ||
            !image.FrameStamp.IsKnown || image.Width * 9 != image.Height * 16 ||
            image.SrcMat.Type() != MatType.CV_8UC3) return;
        var scale = image.Width / 1920d;
        var bounds = new Rect((int)(220 * scale), (int)(130 * scale),
            (int)(1500 * scale), (int)(820 * scale));
        if (!Inside(image, bounds)) return;
        using var area = new Mat(image.SrcMat, bounds);
        using var gray = new Mat();
        Cv2.CvtColor(area, gray, ColorConversionCodes.BGR2GRAY);
        // 黑色替身、未知布局和被遮罩变暗的书页均不能形成快速准入依据。
        if (Cv2.Mean(gray).Val0 < 140) return;
        _pixels = area.Clone();
        _bounds = bounds;
        _source = image.FrameStamp;
    }

    private static bool Inside(ImageRegion image, Rect bounds) => bounds.Width > 0 && bounds.Height > 0 &&
        bounds.X >= 0 && bounds.Y >= 0 && bounds.Right <= image.Width && bounds.Bottom <= image.Height;

    public void Dispose()
    {
        _pixels?.Dispose();
        _pixels = null;
        _source = default;
    }
}
