using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.Job;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.GameTask.Model.GameUI;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.ArtifactAnalysis;

public sealed class ArtifactLockPlanExecutor : IArtifactLockPlanExecutor
{
    public async Task ExecuteAsync(
        IReadOnlyList<ArtifactExecutionActionDto> actions,
        bool reusePreparedInventory,
        CancellationToken cancellationToken)
    {
        var task = new ArtifactLockExecutionTask(actions, reusePreparedInventory);
        await new TaskRunner().RunSoloTaskAsync(task, propagateExceptions: true);
        if (!task.Completed)
        {
            throw new InvalidOperationException("Artifact lock execution did not complete all actions.");
        }
    }
}

internal sealed class ArtifactLockExecutionTask(
    IReadOnlyList<ArtifactExecutionActionDto> actions,
    bool reusePreparedInventory) : ISoloTask
{
    private readonly ILogger _logger = App.GetLogger<ArtifactLockExecutionTask>();
    public string Name => "圣遗物锁定方案";
    public bool Completed { get; private set; }

    public async Task Start(CancellationToken ct)
    {
        Exception? executionFailure = null;
        try
        {
            if (reusePreparedInventory)
            {
                _logger.LogInformation(
                    "复用数量预检已打开并回顶的圣遗物背包，直接开始锁定执行");
            }
            else
            {
                await ArtifactInventoryNavigation.PrepareAsync(_logger, ct);
            }

            var pending = actions.OrderBy(action => action.ScanIndex)
                .ToDictionary(action => action.ScanIndex);
            if (pending.Count == 0)
            {
                _logger.LogInformation("全部目标已达锁定状态，无需点击");
                Completed = true;
                return;
            }
            using var reader = new ArtifactInventoryUi(_logger);
            var expectedCount = reader.ReadArtifactCount();
            if (pending.Keys.Any(index => index < 0 || index >= expectedCount))
            {
                throw new InvalidDataException("Artifact lock action index exceeds the current inventory.");
            }

            using var gridCapture = CaptureToRectArea();
            var gridParams = ArtifactLockExecutionPolicy.CreateGridParams(
                gridCapture.SrcMat.Size(),
                expectedCount);
            var grid = new GridScreen(gridParams, _logger, ct);
            var index = 0;
            var processed = 0;
            var updated = 0;
            var alreadyDesired = 0;
            await foreach ((ImageRegion page, Rect rect) in grid.WithCancellation(ct))
            {
                if (index >= expectedCount || processed == pending.Count) break;
                if (!pending.TryGetValue(index, out var action)) { index++; continue; }

                var currentLocked = await reader.SelectItemAsync(page, rect, index, ct);
                if (ArtifactLockExecutionPolicy.FromDetail(currentLocked, action.DesiredLocked)
                    == ArtifactLockDecision.Skip)
                {
                    _logger.LogDebug("圣遗物 {Index} 详情锁状态已达目标，跳过", index);
                    processed++;
                    alreadyDesired++;
                    index++;
                    continue;
                }

                if (!await ApplyDesiredLockStateAsync(
                        currentLocked, action.DesiredLocked, page, rect, ct))
                {
                    throw new InvalidDataException($"Artifact lock toggle failed at index {index}.");
                }
                _logger.LogInformation(
                    "圣遗物 {Index} 锁状态已更新为 {DesiredLocked}",
                    index,
                    action.DesiredLocked);
                processed++;
                updated++;
                index++;
            }
            if (processed != pending.Count)
            {
                throw new InvalidDataException(
                    $"Processed {processed} of {pending.Count} artifact lock actions.");
            }
            _logger.LogInformation(
                "圣遗物加解锁遍历完成：目标 {TargetCount}，已更新 {UpdatedCount}，已达目标免详情跳过 {AlreadyDesiredCount}",
                pending.Count,
                updated,
                alreadyDesired);
            Completed = true;
        }
        catch (Exception exception)
        {
            executionFailure = exception;
            throw;
        }
        finally
        {
            try
            {
                await new ReturnMainUiTask().Start(CancellationToken.None);
            }
            catch (Exception cleanupException) when (executionFailure is not null)
            {
                _logger.LogWarning(cleanupException, "圣遗物加解锁异常后返回主界面失败");
            }
        }
    }

    private async Task<bool> ApplyDesiredLockStateAsync(
        bool initialLocked,
        bool desiredLocked,
        ImageRegion page,
        Rect itemRect,
        CancellationToken cancellationToken)
    {
        const int maxClicks = 3;
        var cellBounds = new Rect(
            page.X + itemRect.X,
            page.Y + itemRect.Y,
            itemRect.Width,
            itemRect.Height);
        for (var clickCount = 1; clickCount <= maxClicks; clickCount++)
        {
            using var capture = CaptureToRectArea();
            var button = ArtifactDetailLockDetector.ButtonCenter(capture.SrcMat.Size());
            using var initialCell = capture.DeriveCrop(cellBounds);
            var initialVisualSignature = ArtifactGridLockDetector.VisualSignature(
                initialCell.SrcMat);
            capture.ClickTo(button.X, button.Y);
            using (var item = page.DeriveCrop(itemRect))
            {
                item.Move();
            }
            var transition = await WaitForStableLockTransitionAsync(
                initialLocked,
                desiredLocked,
                initialVisualSignature,
                cellBounds,
                cancellationToken);
            var decision = ArtifactLockExecutionPolicy.FromTransition(
                transition, clickCount);
            if (decision == ArtifactToggleDecision.Complete) return true;
            if (decision == ArtifactToggleDecision.Fail) break;
            _logger.LogWarning(
                "圣遗物锁按钮连续 5 帧稳定且状态未变化，执行第 {NextClick}/{MaxClicks} 次点击",
                clickCount + 1,
                maxClicks);
        }
        _logger.LogError(
            "圣遗物锁按钮未达到稳定目标状态，任务停止并保留续跑能力");
        return false;
    }

    private static async Task<ArtifactDetailTransitionOutcome> WaitForStableLockTransitionAsync(
        bool initialLocked,
        bool desiredLocked,
        double initialVisualSignature,
        Rect cellBounds,
        CancellationToken cancellationToken)
    {
        var detector = new ArtifactDetailLockTransitionDetector(
            initialLocked,
            desiredLocked,
            initialVisualSignature,
            tolerance: 0.5);
        for (var attempt = 0; attempt < 30; attempt++)
        {
            await Delay(16, cancellationToken);
            using var refreshed = CaptureToRectArea();
            using var cell = refreshed.DeriveCrop(cellBounds);
            var isLocked = ArtifactGridLockDetector.IsLocked(cell.SrcMat);
            var outcome = detector.Observe(
                isLocked,
                ArtifactGridLockDetector.VisualSignature(cell.SrcMat));
            if (outcome != ArtifactDetailTransitionOutcome.Unstable)
            {
                return outcome;
            }
        }
        return ArtifactDetailTransitionOutcome.Unstable;
    }
}

internal enum ArtifactLockDecision
{
    Skip,
    Inspect
}

internal enum ArtifactDetailTransitionOutcome
{
    DesiredStable,
    UnchangedStable,
    Unstable
}

internal enum ArtifactToggleDecision
{
    Complete,
    Retry,
    Fail
}

internal static class ArtifactLockExecutionPolicy
{
    internal static GridParams CreateGridParams(Size captureSize, int totalItems) =>
        GridParams.ArtifactsForCapture(captureSize, totalItems);

    internal static ArtifactLockDecision FromDetail(
        bool isLocked,
        bool desiredLocked)
    {
        return isLocked == desiredLocked
            ? ArtifactLockDecision.Skip
            : ArtifactLockDecision.Inspect;
    }

    internal static bool IsStableDetailState(int consecutiveDesiredFrames) =>
        consecutiveDesiredFrames >= 3;

    internal static bool CanTreatAsStableUnchanged(int consecutiveUnchangedFrames) =>
        consecutiveUnchangedFrames >= 5;

    internal static ArtifactToggleDecision FromTransition(
        ArtifactDetailTransitionOutcome transition,
        int clickCount)
    {
        if (transition == ArtifactDetailTransitionOutcome.DesiredStable)
        {
            return ArtifactToggleDecision.Complete;
        }
        if (transition == ArtifactDetailTransitionOutcome.UnchangedStable && clickCount < 3)
        {
            return ArtifactToggleDecision.Retry;
        }
        return ArtifactToggleDecision.Fail;
    }
}
