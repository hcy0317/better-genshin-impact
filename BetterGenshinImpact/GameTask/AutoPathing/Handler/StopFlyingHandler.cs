using System;
using Fischless.GameCapture;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.Core.Input;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using Microsoft.Extensions.Logging;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.AutoPathing.Handler;

public class StopFlyingHandler : IActionHandler
{
    public async Task RunAsync(CancellationToken ct, WaypointForTrack? waypointForTrack = null, object? config = null)
        => await RunAsync(ct, waypointForTrack, new PathMoveToIo(native: true));

    internal async Task RunAsync(CancellationToken ct, WaypointForTrack? waypointForTrack, PathMoveToIo io)
    {
        // 如果有参数，先自由落体，然后恢复飞行
        if (waypointForTrack != null
            && !string.IsNullOrEmpty(waypointForTrack.ActionParams)
            && int.TryParse(waypointForTrack.ActionParams, out var stopFlyingWaitTime))
        {
            io.Send(GIActions.Jump, KeyType.KeyPress);
            await io.Delay(stopFlyingWaitTime, ct);
            io.Send(GIActions.Jump, KeyType.KeyPress);
            await io.Delay(300, ct);
        }

        void Check()
        {
            ct.ThrowIfCancellationRequested();
            TaskExecutionScope.ThrowIfFailed();
            UiOperation.Current?.Check();
        }
        bool Fresh(CaptureFrameStamp source) => source.IsFresh(io.Clock, UiSnapshot.RecoveryMaximumAge) &&
            source.TimestampFrequency == io.Clock.TimestampFrequency;
        Check();
        using var before = io.Capture();
        var beforeSource = before.FrameStamp;
        var motion = io.Motion(before);
        var world = io.CombatHud(before);
        Check();
        if (!world || before.FrameStamp != beforeSource || !Fresh(beforeSource))
            throw new RetryException("停止飞行缺少当前普通场景新源，保留原期限");
        if (motion == MotionStatus.Normal) return;
        if (motion != MotionStatus.Fly)
            throw new RetryException("停止飞行姿态未知或未确认飞行，禁止发送下落攻击");
        DiagnosticInputReceipt? receipt = null;
        var attacked = LandingAttackGuard.TryAttack(() =>
        {
            Check();
            if (!Fresh(beforeSource)) throw new RetryException("下落攻击输入前原源过期");
            return motion;
        }, () => receipt = io.SendObserved(GIActions.NormalAttack, KeyType.KeyPress, beforeSource, ct), ct);
        if (!attacked || receipt is not { Status: DiagnosticInputStatus.Sent } sent ||
            !(sent.NativeRequested > 0 && sent.NativeRequested == sent.NativeSubmitted ||
              sent.TransportRequested > 0 && sent.TransportRequested == sent.TransportAcknowledged))
            throw new RetryException("下落攻击原生回执不完整，停止本次恢复");
        var previous = beforeSource;
        for (var i = 0; i < 50; i++)
        {
            Check();
            using var frame = io.Capture();
            var source = frame.FrameStamp;
            if (Fresh(source) && source.SessionId == beforeSource.SessionId && source.IsAfter(previous) &&
                source.TimestampFrequency == sent.Frequency && source.CapturedTimestamp - sent.CompletedAt >= sent.Frequency * .06)
            {
                previous = source;
                var observed = io.Motion(frame);
                var currentWorld = io.CombatHud(frame);
                Check();
                if (currentWorld && frame.FrameStamp == source && Fresh(source) && observed == MotionStatus.Normal)
                {
                    io.Logger.LogInformation("动作：下落攻击后取得新的普通姿态观察");
                    return; // Not a Grounded claim; subsequent MoveTo still observes its own fresh state.
                }
            }
            await io.Delay(300, ct);
        }
        throw new RetryException("停止飞行在原50次观察内未确认输入后新姿态，路线未完成");
    }
}
