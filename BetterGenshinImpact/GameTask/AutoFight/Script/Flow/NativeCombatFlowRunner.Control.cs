using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using Fischless.GameCapture;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

internal sealed partial class NativeCombatFlowRunner
{
    internal sealed record CombatControlRequest(Guid Id, Guid BattleId, long DeadlineTimestamp);
    private CombatControlRequest? _activeControl;
    private NativeCombatBattleHostIo? _controlInput;
    private CombatFlowStep? _stepBeforeControl;
    private CaptureFrameStamp _lastControlSource;
    private CaptureFrameFence? _controlInputFence;
    private int _controlPulses, _clearControlFrames;
    private long _nextControlPulse;
    private Exception? _terminalControlFailure;

    internal ValueTask RunControlOperationAsync(Guid requestId, Func<CancellationToken, ValueTask> operation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        TaskExecutionScope.ThrowIfFailed();
        if (_terminalControlFailure != null) ExceptionDispatchInfo.Capture(_terminalControlFailure).Throw();
        if (_disposed || _activeControl?.Id != requestId || _activeControl.BattleId != Context.BattleId || IsAtomic ||
            _game is not NativeGame native)
            throw new InvalidOperationException("控制恢复未取得本场、已中断原子和同输入owner的许可");
        return native.RunHostOperationAsync(Context.BattleId, operation, ct);
    }

    private async ValueTask<CombatFlowStep> AdvanceControlRecoveryAsync(NativeGame native, CancellationToken ct)
    {
        var request = native.PendingControl!;
        var clock = native.ControlClock;
        ct.ThrowIfCancellationRequested();
        TaskExecutionScope.ThrowIfFailed();
        if (_terminalControlFailure != null) ExceptionDispatchInfo.Capture(_terminalControlFailure).Throw();
        if (request.BattleId != Context.BattleId || clock.GetTimestamp() >= request.DeadlineTimestamp)
            return FailControl("控制中断未在原动作期限内闭合");
        if (_activeControl?.Id != request.Id)
        {
            if (!native.ReleasePhysicalInputForControl())
            {
                await native.ControlDelayAsync(50, ct);
                return new(CombatFlowResult.AwaitingObservation, false);
            }
            _execution?.InterruptAtomicForControl();
            _jsonExecution?.InterruptAtomicForControl();
            _activeControl = request;
            _controlInput = native.ControlDevice is { } device ? new(this, device) : null;
            _controlPulses = _clearControlFrames = 0;
            _nextControlPulse = 0;
            _lastControlSource = default;
            _controlInputFence = null;
        }

        var sample = native.ObserveControlFrame();
        var fresh = sample.Source.IsFresh(clock, TimeSpan.FromMilliseconds(150)) &&
            (!_lastControlSource.IsKnown || sample.Source.IsAfter(_lastControlSource)) &&
            (_controlInputFence == null || _controlInputFence.Value.Accepts(sample.Source));
        if (!fresh || !sample.Control.IsObserved)
        {
            _clearControlFrames = 0;
            await native.ControlDelayAsync(50, ct);
            return new(CombatFlowResult.AwaitingObservation, false);
        }
        _lastControlSource = sample.Source;
        if (sample.Control.KeyboardBreakoutRequested)
        {
            _clearControlFrames = 0;
            if (_controlInput != null && _controlPulses < 8 && clock.GetTimestamp() >= _nextControlPulse)
            {
                var result = await _controlInput.SendControlAsync(new(CombatBattleHostInputKind.Breakout)
                { RequestId = request.Id, Source = sample.Source, DeadlineTimestamp = request.DeadlineTimestamp }, ct);
                ct.ThrowIfCancellationRequested();
                if (result.Status == CombatBattleHostInputStatus.Failed)
                {
                    _terminalControlFailure = result.Error ?? new InvalidOperationException(result.Reason);
                    ExceptionDispatchInfo.Capture(_terminalControlFailure).Throw();
                }
                if (result.Status == CombatBattleHostInputStatus.Unknown)
                    return FailControl("挣脱输入结果未知，不重发：" + result.Reason);
                if (result.Status == CombatBattleHostInputStatus.Sent)
                {
                    if (result.CompletedTimestamp is not { } completed || completed < sample.Source.CapturedTimestamp ||
                        completed > clock.GetTimestamp()) return FailControl("挣脱输入完成证据无效");
                    _controlPulses++;
                    _controlInputFence = new(sample.Source, completed);
                    _nextControlPulse = completed + clock.TimestampFrequency / 10;
                }
            }
        }
        else if (++_clearControlFrames >= 2)
        {
            native.CompleteControlRecovery();
            _diagnosticLogger.LogDebug("FIGHT_CONTROL_RECOVERED battle={Battle} request={Request} pulses={Pulses} sourceSequence={Source} macroSuccess=false",
                Context.BattleId, request.Id, _controlPulses, sample.Source.Sequence);
            _activeControl = null;
            _controlInput = null;
            var interrupted = _stepBeforeControl;
            _stepBeforeControl = null;
            // 不把失败宏或尚未确认技能补成成功；下一步由原调用方/原游标复核。
            return interrupted is { RoundCompleted: true } completedStep
                ? completedStep : new(CombatFlowResult.AwaitingObservation, false);
        }
        await native.ControlDelayAsync(50, ct);
        return new(CombatFlowResult.AwaitingObservation, false);
    }

    private CombatFlowStep FailControl(string reason)
    {
        try { TaskExecutionScope.StopUnconfirmedCombat(reason); }
        catch (Exception error) { _terminalControlFailure ??= error; throw; }
        return new(CombatFlowResult.Failed, true);
    }
}
