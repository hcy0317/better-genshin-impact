using BetterGenshinImpact.GameTask.AutoArtifactSalvage;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Common.Job;
using BetterGenshinImpact.GameTask.Model.GameUI;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.ArtifactAnalysis;

internal static class ArtifactInventoryNavigation
{
    internal static Task PrepareAsync(ILogger logger, CancellationToken cancellationToken)
    {
        return PrepareAsync(
            IsArtifactInventoryOpen,
            () => new ReturnMainUiTask().Start(cancellationToken),
            () => AutoArtifactSalvageTask.OpenInventory(
                GridScreenName.Artifacts,
                Core.Simulator.Simulation.SendInput,
                logger,
                cancellationToken,
                allowRetryOpenAction: false),
            () => ResetToTopAsync(logger, cancellationToken),
            () => EnsureObtainedOrderAsync(logger, cancellationToken));
    }

    internal static async Task PrepareAsync(
        Func<bool> isArtifactInventoryOpen,
        Func<Task> returnToMainUi,
        Func<Task> openArtifactInventory,
        Func<Task> resetToTop,
        Func<Task>? ensureObtainedOrder = null)
    {
        if (!isArtifactInventoryOpen())
        {
            await returnToMainUi();
            await openArtifactInventory();
            if (!isArtifactInventoryOpen())
            {
                throw new InvalidOperationException(
                    "未能打开圣遗物背包，停止后续滑条与锁定操作");
            }
        }

        if (ensureObtainedOrder is not null)
        {
            await ensureObtainedOrder();
        }
        await resetToTop();
    }

    private static async Task EnsureObtainedOrderAsync(
        ILogger logger,
        CancellationToken cancellationToken)
    {
        SystemControl.ActivateWindow();
        await Delay(100, cancellationToken);
        await ArtifactObtainedOrderDetector.ResetAndEnsureEnabledAsync(cancellationToken);
        logger.LogInformation("已通过切换按获得时间顺序开关重置列表并保持开启");
    }

    private static bool IsArtifactInventoryOpen()
    {
        using var capture = CaptureToRectArea();
        using var artifactTab = capture.Find(ElementRecognition.Get("BagArtifactChecked"));
        return artifactTab.IsExist();
    }

    private static async Task ResetToTopAsync(
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        for (var attempt = 1; attempt <= 12; attempt++)
        {
            using var verificationCapture = CaptureToRectArea();
            var roi = GridParams.ArtifactRoiForCapture(verificationCapture.SrcMat.Size());
            using var grid = verificationCapture.DeriveCrop(roi);
            var rects = GridScreen.GridEnumerator.GetGridItems(grid.SrcMat, 8);
            var cells = GridScreen.GridEnumerator.PostProcess(
                    grid.SrcMat,
                    rects,
                    (int)(0.025 * roi.Height),
                    validatePhantomBottomColor: false)
                .ToArray();
            if (IsTopAligned(cells.Select(cell => cell.Rect), 32, roi.Height))
            {
                logger.LogInformation(
                    "圣遗物背包切换获得时间排序后第 {Attempt} 次确认回到顶部，耗时 {ElapsedMilliseconds}ms",
                    attempt,
                    timer.ElapsedMilliseconds);
                return;
            }

            await Delay(100, cancellationToken);
        }

        throw new InvalidOperationException(
            "圣遗物背包切换获得时间排序后仍未回到顶部");
    }

    internal static bool IsTopAligned(
        IEnumerable<Rect> itemRects,
        int expectedVisibleItems,
        int gridHeight)
    {
        var items = itemRects.ToArray();
        return items.Length >= expectedVisibleItems &&
               items.Min(rect => rect.Y) <= gridHeight * 0.035;
    }

    internal static bool HasVerticalMovement(bool phaseDetected, Point2d shift)
    {
        return GridScroller.HasVerticalMovement(phaseDetected, shift);
    }
}
