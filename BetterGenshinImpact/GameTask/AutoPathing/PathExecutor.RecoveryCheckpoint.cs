using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.AutoPathing.Model.Enum;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Ui;
using Fischless.GameCapture;

namespace BetterGenshinImpact.GameTask.AutoPathing;

public partial class PathExecutor
{
    internal bool ShouldExecuteWaypointAction(WaypointForTrack waypoint) =>
        (!string.IsNullOrEmpty(waypoint.Action) && !_skipOtherOperations) || waypoint.Action == ActionEnum.CombatScript.Code;

    internal static bool CanRestartAfterHealing(IReadOnlyList<WaypointForTrack>? segment, int resumeIndex) =>
        segment is { Count: > 0 } && resumeIndex >= 0 && resumeIndex < segment.Count &&
        segment[0].Type == WaypointType.Teleport.Code &&
        // 原宏同时含位移与副作用，不能为新增回血重启假定它可以安全重放。
        !segment.Take(resumeIndex).Any(point => point.Action == ActionEnum.CombatScript.Code);

    private async Task RecoverAtStatueAndRestartAsync(CaptureFrameStamp before)
    {
        var resumeIndex = RecordWaypoints == CurWaypoints && _skipOtherOperations
            ? Math.Max(CurWaypoint.Item1, RecordWaypoint.Item1) : CurWaypoint.Item1;
        // 能否重放只决定恢复后的路线处理，不能阻止当前低血角色先安全回血。
        var canRestart = CanRestartAfterHealing(CurWaypoints.Item2, resumeIndex);
        await ConfirmHealingRestartAsync(before, TpStatueOfTheSeven, () =>
        {
            using var frame = _moveIo.Capture();
            return new(frame.FrameStamp, _moveIo.CombatHud(frame) && !_moveIo.Transformed(frame),
                Bv.CurrentAvatarIsLowHp(frame));
        }, milliseconds => _moveIo.Delay(milliseconds, ct), ct, _moveIo.Clock, canRestart);
    }

    internal static async Task ConfirmHealingRestartAsync(CaptureFrameStamp before, Func<Task> recover,
        Func<HealingFrame> observe, Func<int, Task> delay, CancellationToken ct, TimeProvider clock,
        bool canRestart = true)
    {
        ct.ThrowIfCancellationRequested();
        TaskExecutionScope.ThrowIfFailed();
        UiOperation.Current?.Check();
        if (!before.IsKnown) throw new InvalidOperationException("回血恢复缺少采集来源");
        await recover();
        ct.ThrowIfCancellationRequested();
        TaskExecutionScope.ThrowIfFailed();
        UiOperation.Current?.Check();
        var fence = new CaptureFrameFence(before, clock.GetTimestamp());
        if (!await HealingObservation.WaitAsync(fence, () =>
        {
            UiOperation.Current?.Check();
            var observed = observe();
            UiOperation.Current?.Check();
            return observed;
        }, async milliseconds =>
        {
            UiOperation.Current?.Check();
            await delay(milliseconds);
            UiOperation.Current?.Check();
        }, ct, clock))
            throw new InvalidOperationException("神像恢复后未取得普通角色非低血新帧，禁止重启分段或交接");
        ct.ThrowIfCancellationRequested();
        TaskExecutionScope.ThrowIfFailed();
        UiOperation.Current?.Check();
        if (!canRestart)
            throw new InvalidOperationException("已确认神像回血，但缺少可安全回放的原传送入口或前缀；路线未完成，不重放路径宏");
        throw new HealingRecoveryCompletedException();
    }
}
