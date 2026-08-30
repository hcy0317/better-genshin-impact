using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.Model.GameUI;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.ArtifactAnalysis;

internal static class ArtifactGameIdentityVerifier
{
    internal static async Task EnsureExpectedUidAsync(
        string expectedUid,
        IOcrService ocrService,
        CancellationToken cancellationToken)
        => await EnsureExpectedUidAsync(
            expectedUid,
            uidRegion => ocrService.OcrWithoutDetector(uidRegion),
            cancellationToken);

    internal static async Task EnsureExpectedUidAsync(
        string expectedUid,
        Func<OpenCvSharp.Mat, string> recognizeWithoutDetector,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string lastText = string.Empty;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            using var capture = CaptureToRectArea();
            using var uidRegion = ArtifactUiCoordinateMapper.CropNormalized(
                capture.SrcMat,
                1395, 868, 205, 32);
            lastText = recognizeWithoutDetector(uidRegion);
            if (TryParseUid(lastText, out var liveUid)
                && string.Equals(liveUid, expectedUid, StringComparison.Ordinal))
            {
                return;
            }
            if (attempt < 2) await Delay(160, cancellationToken);
        }

        throw new InvalidOperationException(
            $"当前游戏 UID 与网页任务不一致或无法识别：期望 {expectedUid}，OCR '{lastText}'");
    }

    internal static bool TryParseUid(string? rawText, out string uid)
    {
        uid = string.Concat((rawText ?? string.Empty).Where(char.IsDigit));
        return uid.Length is >= 6 and <= 12;
    }
}
