using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.AutoPathing.Model.Enum;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Ui;
using Fischless.GameCapture;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask.AutoPathing;

public partial class PathExecutor
{
    internal sealed class HealingReplanRequiredException() : InvalidOperationException(
        "[BGI_HEALING_REPLAN_REQUIRED] 已确认回血；当前无传送入口片段尚未开始，需要父流程从已确认的纯导航检查点返回");

    internal static bool CanRequestParentHealingReplan(IReadOnlyList<WaypointForTrack>? segment, int resumeIndex, bool started) =>
        !started && resumeIndex == 0 && HealingRestartRejection(segment, resumeIndex) == "original-entry-not-teleport";
    internal bool ShouldExecuteWaypointAction(WaypointForTrack waypoint) =>
        (!string.IsNullOrEmpty(waypoint.Action) && !_skipOtherOperations) ||
        waypoint.Action == ActionEnum.CombatScript.Code &&
        !(_skipOtherOperations && _healingNavigationReplay && CanSkipCompletedHealingMacro(waypoint));

    // 回血后仅略过已经完成的、单个当前角色普攻路径点；不重放输入，也不跳过技能/交互/移动宏。
    // target/orientation宏可能承担业务交互，不属于这个可略过的导航前缀。
    internal static bool CanSkipCompletedHealingMacro(WaypointForTrack waypoint) =>
        waypoint.Type == WaypointType.Path.Code && waypoint.CombatScript?.CombatCommands is { Count: 1 } commands &&
        commands[0].Name == CombatScriptParser.CurrentAvatarName && commands[0].Method == Method.Attack &&
        !commands[0].RequiresFlow;

    internal static bool CanRestartAfterHealing(IReadOnlyList<WaypointForTrack>? segment, int resumeIndex) =>
        HealingRestartRejection(segment, resumeIndex) == null;

    internal static string? HealingRestartRejection(IReadOnlyList<WaypointForTrack>? segment, int resumeIndex)
    {
        if (segment is not { Count: > 0 }) return "segment-unavailable";
        if (resumeIndex < 0 || resumeIndex >= segment.Count) return "checkpoint-out-of-range";
        if (segment[0].Type != WaypointType.Teleport.Code) return "original-entry-not-teleport";
        for (var index = 0; index < resumeIndex; index++)
            if (segment[index].Action == ActionEnum.CombatScript.Code && !CanSkipCompletedHealingMacro(segment[index]) &&
                !CanReplayHealingEntryMacro(segment, index))
                return "unsafe-macro-prefix:index=" + index;
        return null;
    }

    // 实际矿路在传送入口用短位移和一次角色战技离开锚点。回血后从同一个
    // 传送入口重新建立这些状态；不把后续交互、攻击、匿名物理宏认作可重放前缀。
    private static bool CanReplayHealingEntryMacro(IReadOnlyList<WaypointForTrack> segment, int index)
    {
        if (index != 1 || segment[index].Type != WaypointType.Path.Code ||
            segment[index].X != segment[0].X || segment[index].Y != segment[0].Y ||
            !Equals(segment[index].MapLayerSelector, segment[0].MapLayerSelector) ||
            segment[index].CombatScript?.CombatCommands is not { Count: > 0 } commands) return false;
        var actor = commands[0].Name;
        if (string.IsNullOrWhiteSpace(actor) || actor == CombatScriptParser.CurrentAvatarName) return false;
        var skillCount = 0;
        var movementSeconds = 0d;
        foreach (var command in commands)
        {
            if (command.Name != actor || command.RequiresFlow) return false;
            if (command.Method == Method.Skill && command.Args is null or { Count: 0 }) skillCount++;
            else if ((command.Method == Method.W || command.Method == Method.A || command.Method == Method.S || command.Method == Method.D) &&
                     command.Args is { Count: 1 } args &&
                     double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) &&
                     double.IsFinite(seconds) && seconds is > 0 and <= 1)
                movementSeconds += seconds;
            else return false;
        }
        return skillCount == 1 && movementSeconds is > 0 and <= 2;
    }

    private async Task RecoverAtStatueAndRestartAsync(CaptureFrameStamp before)
    {
        var resumeIndex = RecordWaypoints == CurWaypoints && _skipOtherOperations
            ? Math.Max(CurWaypoint.Item1, RecordWaypoint.Item1) : CurWaypoint.Item1;
        // 能否重放只决定恢复后的路线处理，不能阻止当前低血角色先安全回血。
        var canRestart = CanRestartAfterHealing(CurWaypoints.Item2, resumeIndex);
        var allowParentReplan = CanRequestParentHealingReplan(CurWaypoints.Item2, resumeIndex, _segmentHasStartedTraversal);
        var request = "healing:" + Guid.NewGuid().ToString("N");
        var context = $"route={CurWaypoint.Item2.PathingTaskFileName} segment={CurWaypoints.Item1} node={CurWaypoint.Item1} " +
            $"checkpoint={resumeIndex} recordedCheckpoint={RecordWaypoint.Item1} skipOtherOperations={_skipOtherOperations} " +
            $"canRestart={canRestart} rejection={HealingRestartRejection(CurWaypoints.Item2, resumeIndex) ?? "none"} " +
            $"beforeSource={before.SessionId}/{before.Sequence}";
        var lastObservation = "unavailable:not-observed";
        void Trace(string state, bool fault)
        {
            try
            {
                var detail = $"request={request} {context} state={state} observation={lastObservation} cancelled={ct.IsCancellationRequested}";
                _moveIo.Logger.LogDebug("PATH_HEALING_CHECKPOINT {Detail}", detail);
                if (fault) DiagnosticEvidenceScope.Current?.RequestLatestWindow(request, "healing-resume-failed", detail, _moveIo.Logger);
            }
            catch { /* 诊断不能改变回血结果或失败传播。 */ }
        }
        Trace("restore-requested", false);
        try
        {
            var prefix = CurWaypoints.Item2.Take(resumeIndex).Select((waypoint, index) => (waypoint, index))
                .Where(item => item.waypoint.Action == ActionEnum.CombatScript.Code).Take(8);
            foreach (var (waypoint, index) in prefix)
                _moveIo.Logger.LogDebug("PATH_HEALING_PREFIX request={Request} index={Index} position=({X},{Y}) entryPosition=({EntryX},{EntryY}) type={Type} checkpoint={Checkpoint} priorToCheckpoint={Prior} traversalStarted={Started} canSkip={Skip} canReplayEntry={Replay} commands={Commands}; individual action completion not recorded",
                    request, index, waypoint.X, waypoint.Y, CurWaypoints.Item2[0].X, CurWaypoints.Item2[0].Y,
                    waypoint.Type, resumeIndex, index < resumeIndex, _segmentHasStartedTraversal,
                    CanSkipCompletedHealingMacro(waypoint), CanReplayHealingEntryMacro(CurWaypoints.Item2, index),
                    string.Join(";", waypoint.CombatScript?.CombatCommands.Take(12).Select(command =>
                        $"actor={command.Name} method={command.Method.Alias[0]} requiresFlow={command.RequiresFlow}") ?? []));
        }
        catch { }
        try
        {
            await ConfirmHealingRestartAsync(before, TpStatueOfTheSeven, () =>
            {
                using var frame = _moveIo.Capture();
                DiagnosticEvidenceScope.Current?.ObserveExistingFrame(frame);
                var result = new HealingFrame(frame.FrameStamp, _moveIo.CombatHud(frame) && !_moveIo.Transformed(frame),
                    Bv.CurrentAvatarIsLowHp(frame));
                lastObservation = result.ToString();
                return result;
            }, milliseconds => _moveIo.Delay(milliseconds, ct), ct, _moveIo.Clock, canRestart, allowParentReplan);
        }
        catch (HealingRecoveryCompletedException) { Trace("healed-restart-confirmed", false); throw; }
        catch (Exception error) { Trace("failed:" + error.GetType().Name, true); throw; }
    }

    internal static async Task ConfirmHealingRestartAsync(CaptureFrameStamp before, Func<Task> recover,
        Func<HealingFrame> observe, Func<int, Task> delay, CancellationToken ct, TimeProvider clock,
        bool canRestart = true, bool allowParentReplan = false)
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
        {
            if (allowParentReplan) throw new HealingReplanRequiredException();
            throw new InvalidOperationException("已确认神像回血，但缺少可安全回放的原传送入口或前缀；路线未完成，不重放路径宏");
        }
        throw new HealingRecoveryCompletedException();
    }
}
