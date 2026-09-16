using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Model.Area;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.AutoTrackPath;

internal sealed class TeleportSelectionMismatchException() : InvalidOperationException(
    "点击落入地图标记编辑页，不是传送面板；未发送传送或保存标记的确认输入");

internal static class TeleportPanelConfirmation
{
    internal static async Task<bool> TryConfirmAsync(ImageRegion image,
        Func<CancellationToken, Task> confirmInput, CancellationToken ct, IOcrService? ocr = null)
    {
        ct.ThrowIfCancellationRequested();
        UiOperation.Current?.Check();
        using var button = image.Find(RecognitionAssets.Get("QuickTeleport", "TeleportButton", image));
        var map = Bv.IsInBigMapUi(image);
        var marker = !button.IsExist() && map && IsMarkerEditor(image, ocr);
        UiOperation.Current?.Observe("识别传送按钮后发送确认输入",
            $"phase=teleport-panel,map={map},teleportButton={button.IsExist()},page={(marker ? "marker-editor" : "unconfirmed")},sourceKnown={image.FrameStamp.IsKnown},sourceSession={image.FrameStamp.SessionId}",
            image.FrameStamp.Sequence);
        // 交给既有传送恢复/重定位预算；不能在标记页按F误保存，也不能误判未激活。
        if (marker) throw new TeleportSelectionMismatchException();
        // 未识别到地图只表示未知，不能作为已发送传送确认的证据。
        if (!button.IsExist()) return false;
        ct.ThrowIfCancellationRequested();
        UiOperation.Current?.Check();
        await confirmInput(ct);
        ct.ThrowIfCancellationRequested();
        UiOperation.Current?.Check();
        return true;
    }

    private static bool IsMarkerEditor(ImageRegion image, IOcrService? ocr) =>
        image.ReadOnce((typeof(TeleportPanelConfirmation), "marker-editor"), () =>
        {
            using var title = image.DeriveCrop(new Rect(image.Width * 1430 / 1920, image.Height * 8 / 1080,
                image.Width * 400 / 1920, image.Height * 64 / 1080));
            using var gray = new Mat();
            if (title.SrcMat.Channels() == 1) title.SrcMat.CopyTo(gray);
            else Cv2.CvtColor(title.SrcMat, gray, ColorConversionCodes.BGR2GRAY);
            using var bright = new Mat();
            Cv2.Threshold(gray, bright, 180, 255, ThresholdTypes.Binary);
            if (Cv2.CountNonZero(bright) < 30) return false;
            var text = (ocr ?? OcrFactory.Paddle).OcrWithoutDetector(title.SrcMat);
            return text.Replace(" ", "", StringComparison.Ordinal).Contains("点击更改标记名称", StringComparison.Ordinal);
        });
}
