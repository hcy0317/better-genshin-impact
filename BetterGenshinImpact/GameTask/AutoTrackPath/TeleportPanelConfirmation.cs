using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Model.Area;

namespace BetterGenshinImpact.GameTask.AutoTrackPath;

internal static class TeleportPanelConfirmation
{
    private static long _frameSequence;

    internal static async Task<bool> TryConfirmAsync(ImageRegion image,
        Func<CancellationToken, Task> confirmInput, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        UiOperation.Current?.Check();
        using var button = image.Find(RecognitionAssets.Get("QuickTeleport", "TeleportButton", image));
        UiOperation.Current?.Observe("识别传送按钮后发送确认输入",
            $"phase=teleport-panel,map={Bv.IsInBigMapUi(image)},teleportButton={button.IsExist()}",
            Interlocked.Increment(ref _frameSequence));
        // 未识别到地图只表示未知，不能作为已发送传送确认的证据。
        if (!button.IsExist()) return false;
        ct.ThrowIfCancellationRequested();
        UiOperation.Current?.Check();
        await confirmInput(ct);
        ct.ThrowIfCancellationRequested();
        UiOperation.Current?.Check();
        return true;
    }
}
