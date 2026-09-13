using System;
using System.Threading;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask.AutoFight.Model;

public partial class Avatar
{
    internal sealed record AvatarRecoveryRequest(ReviveTarget Target, CombatScenes Scene,
        ImageRegion? Before, ImageRegion After, CaptureFrameFence? InputFence) : IDisposable
    {
        public void Dispose()
        {
            try { Before?.Dispose(); }
            finally { After.Dispose(); }
        }
    }

    internal sealed class AvatarSelectionResult(bool confirmed, CaptureFrameStamp source,
        AvatarRecoveryRequest? recovery, ImageRegion? frame = null) : IDisposable
    {
        private ImageRegion? _frame = frame;
        internal bool Confirmed { get; } = confirmed;
        internal CaptureFrameStamp Source { get; } = source;
        internal AvatarRecoveryRequest? Recovery { get; } = recovery;
        internal ImageRegion? TakeFrame() => Interlocked.Exchange(ref _frame, null);
        public void Dispose()
        {
            try { TakeFrame()?.Dispose(); }
            finally { Recovery?.Dispose(); }
        }
    }

    // 旧API与原生战斗/路径边界共用此控制器；选择本体不执行OCR弹窗确认或传送。
    internal static void ResolveSelectionRecovery(AvatarRecoveryRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // 先实际释放输入，再进入不受战斗150ms约束的恢复阶段。
        Simulation.ReleaseAllKey();
        using var suspension = CombatActionScope.Suspend();
        using var operation = UiOperation.Begin("selection-recovery", TimeSpan.FromSeconds(60), ct, TaskControl.Logger);
        KnownReviveTarget? target = null;
        var after = request.After;
        if (!after.FrameStamp.IsFresh(TimeProvider.System, UiSnapshot.RecoveryMaximumAge))
            throw new InvalidOperationException("选角恢复证据已经过期，不能据此传送");
        var revive = Bv.ReadReviveState(after);
        if (revive == ReviveUiState.FoodPrompt && request.Before != null &&
            request.InputFence is { } fence && fence.Accepts(after.FrameStamp))
        {
            try
            {
                var before = request.Scene.ReadRecoveryFrame(request.Before, request.Target);
                var current = request.Scene.ReadRecoveryFrame(after, request.Target);
                var identity = ReviveTarget.FromSelection(request.Target, true, before, current);
                if (identity != null) target = new(identity, request.Scene);
            }
            catch (Exception error) when (error is not OperationCanceledException and not CombatNotFinishedException)
            {
                TaskControl.Logger.LogDebug(error, "复苏选角因果证据不足，角色身份保持未知");
            }
        }
        operation.Check();
        ThrowWhenDefeated(after, operation.Token, target);
    }
}
