using System;
using System.Linq;
using System.Threading;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.AutoTrackPath;

/// <summary>已选精通秘境的同帧身份；不授予地图坐标点击或传送完成。</summary>
internal static class SelectedTeleportPanel
{
    internal static bool Matches(ImageRegion image, string? targetType, string? targetName, bool force,
        CancellationToken ct = default, IOcrService? ocr = null, TimeProvider? clock = null,
        CaptureFrameStamp selectedSource = default)
    {
        ct.ThrowIfCancellationRequested();
        UiOperation.Current?.Check();
        clock ??= TimeProvider.System;
        if (force || targetType != "MasteryDomain" || string.IsNullOrWhiteSpace(targetName) ||
            !image.FrameStamp.IsFresh(clock, UiSnapshot.RecoveryMaximumAge) ||
            selectedSource.IsKnown && !new CaptureFrameFence(selectedSource, selectedSource.CapturedTimestamp).Accepts(image.FrameStamp) ||
            !Bv.IsInBigMapUi(image)) return false;

        using var button = image.Find(RecognitionAssets.Get("QuickTeleport", "TeleportButton", image));
        if (!button.IsExist()) return false;
        using var title = image.DeriveCrop(new Rect(image.Width * 1490 / 1920, image.Height * 8 / 1080,
            image.Width * 340 / 1920, image.Height * 64 / 1080));
        var observed = (ocr ?? OcrFactory.Paddle).OcrWithoutDetector(title.SrcMat);
        ct.ThrowIfCancellationRequested();
        UiOperation.Current?.Check();
        return image.FrameStamp.IsFresh(clock, UiSnapshot.RecoveryMaximumAge) &&
            Normalize(observed) == Normalize(targetName);
    }

    private static string Normalize(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
}
