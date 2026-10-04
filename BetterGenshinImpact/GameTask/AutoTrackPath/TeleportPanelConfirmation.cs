using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Model.Area;
using OpenCvSharp;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Input;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.Job;
using BetterGenshinImpact.Helpers;
using Fischless.GameCapture;

namespace BetterGenshinImpact.GameTask.AutoTrackPath;

internal sealed class TeleportSelectionMismatchException() : InvalidOperationException(
    "点击落入地图标记编辑页，不是传送面板；未发送传送或保存标记的确认输入");

internal static class TeleportPanelConfirmation
{
    internal static async Task<bool> TryConfirmWithFeedbackAsync(ImageRegion image,
        Func<ImageRegion> capture, Func<CancellationToken, Task> key,
        Func<ImageRegion, Rect, CancellationToken, Task> click,
        Func<int, CancellationToken, Task> delay, CancellationToken ct,
        IOcrService? ocr = null, TimeProvider? clock = null, Action<ImageRegion>? observe = null,
        Action<CaptureFrameStamp>? mapClosed = null)
    {
        clock ??= TimeProvider.System;
        if (!image.FrameStamp.IsFresh(clock, UiSnapshot.RecoveryMaximumAge) || !Bv.IsInBigMapUi(image)) return false;
        if (!await TryConfirmAsync(image, token =>
        {
            token.ThrowIfCancellationRequested();
            if (!image.FrameStamp.IsFresh(clock, UiSnapshot.RecoveryMaximumAge))
                throw new InvalidOperationException("Teleport panel source expired before confirmation input.");
            return key(token);
        }, ct, ocr)) return false;
        var fence = new CaptureFrameFence(image.FrameStamp, clock.GetTimestamp());
        var previous = image.FrameStamp;
        async Task<bool> WaitForMapClosed(int checks)
        {
            var progress = new SereniteaPotTeleportProgress();
            for (var index = 0; index < checks; index++)
            {
                ct.ThrowIfCancellationRequested();
                UiOperation.Current?.Check();
                await delay(150, ct);
                using var frame = capture();
                if (!fence.Accepts(frame.FrameStamp) || !frame.FrameStamp.IsAfter(previous) ||
                    !frame.FrameStamp.IsFresh(clock, UiSnapshot.RecoveryMaximumAge)) continue;
                previous = frame.FrameStamp;
                observe?.Invoke(frame);
                if (progress.Observe(Bv.IsInBigMapUi(frame)))
                {
                    mapClosed?.Invoke(frame.FrameStamp);
                    return true;
                }
            }
            return false;
        }
        if (await WaitForMapClosed(6)) return true;
        using var current = capture();
        if (!current.FrameStamp.IsAfter(previous) || !current.FrameStamp.IsFresh(clock, UiSnapshot.RecoveryMaximumAge) ||
            !Bv.IsInBigMapUi(current)) return false;
        using var button = current.Find(RecognitionAssets.Get("QuickTeleport", "TeleportButton", current));
        if (IsMarkerEditor(current, ocr) || !button.IsExist()) return false;
        var bounds = ReadButtonBody(current, ocr ?? OcrFactory.Paddle);
        ct.ThrowIfCancellationRequested();
        UiOperation.Current?.Check();
        if (bounds == default || !current.FrameStamp.IsFresh(clock, UiSnapshot.RecoveryMaximumAge)) return false;
        await click(current, bounds, ct);
        fence = new(current.FrameStamp, clock.GetTimestamp());
        previous = current.FrameStamp;
        return await WaitForMapClosed(10);
    }

    internal static async Task<bool> TryConfirmNativeAsync(ImageRegion previous, CancellationToken ct,
        TeleportArrivalProgress? arrival = null)
    {
        ApplicationHostBootstrapGuard.EnsureAllowed();
        TaskControl.CheckAndSleep(0);
        ct.ThrowIfCancellationRequested();
        UiOperation.Current?.Check();
        using var current = TaskControl.CaptureToRectArea();
        if (!current.FrameStamp.IsAfter(previous.FrameStamp)) return false;
        return await TryConfirmWithFeedbackAsync(current, () => TaskControl.CaptureToRectArea(),
            token => { InputHub.Foreground.SimulateKeyPulse(KeyId.F, token); return Task.CompletedTask; },
            (frame, bounds, token) =>
            {
                using var body = frame.DeriveCrop(bounds);
                void Admit()
                {
                    token.ThrowIfCancellationRequested();
                    UiOperation.Current?.Check();
                    if (!frame.FrameStamp.IsFresh(TimeProvider.System, UiSnapshot.RecoveryMaximumAge))
                        throw new InvalidOperationException("Teleport button source expired before native input.");
                }
                DomainTipClick.Run(Admit, body.Move, () => InputHub.Foreground.Mouse.LeftButtonDown(),
                    () => InputHub.Foreground.Mouse.LeftButtonUp(), Thread.Sleep);
                return Task.CompletedTask;
            }, TaskControl.Delay, ct, observe: arrival == null ? null : frame =>
                arrival.Observe(frame.FrameStamp, WorldFrameAvailability.ReadNative(frame)),
            mapClosed: arrival == null ? null : arrival.ConfirmMapClosure);
    }

    internal static async Task WaitForArrivalAsync(TeleportArrivalProgress arrival,
        Func<ImageRegion> capture, Func<ImageRegion, WorldFrameKind> inspect,
        Func<int, CancellationToken, Task> delay, CancellationToken ct, TimeSpan timeout,
        TimeProvider? clock = null, Func<TimeSpan, Task>? whileWaiting = null)
    {
        clock ??= TimeProvider.System;
        var started = clock.GetTimestamp();
        while (clock.GetElapsedTime(started) < timeout)
        {
            ct.ThrowIfCancellationRequested();
            UiOperation.Current?.Check();
            using var frame = capture();
            var kind = inspect(frame);
            ct.ThrowIfCancellationRequested();
            UiOperation.Current?.Check();
            var arrived = arrival.Observe(frame.FrameStamp, kind);
            UiOperation.Current?.Observe("等待传送后的可操作大世界", arrival.Describe(kind), frame.FrameStamp.Sequence);
            if (arrived) return;
            await delay(150, ct);
            if (arrival.MapClosureConfirmed && kind != WorldFrameKind.Playable && whileWaiting != null)
                await whileWaiting(clock.GetElapsedTime(started));
        }
        throw new TimeoutException("传送等待超时：未确认传送输入后地图关闭并稳定返回可操作大世界");
    }

    private static Rect ReadButtonBody(ImageRegion image, IOcrService ocr)
    {
        var roi = new Rect(image.Width * 80 / 100, image.Height * 88 / 100,
            image.Width * 18 / 100, image.Height * 8 / 100);
        var labels = image.FindMulti(RecognitionObject.Ocr(roi.X, roi.Y, roi.Width, roi.Height), ocrService: ocr);
        try
        {
            Region? candidate = null;
            foreach (var label in labels)
            {
                var text = label.Text.Replace(" ", "", StringComparison.Ordinal);
                if (text is not ("传送" or "傳送" or "Teleport")) continue;
                if (candidate != null) return default;
                candidate = label;
            }
            return candidate == null ? default : new(candidate.X, candidate.Y, candidate.Width, candidate.Height);
        }
        finally { foreach (var label in labels) label.Dispose(); }
    }

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
