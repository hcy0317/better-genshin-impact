using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Model;
using Microsoft.Extensions.Logging;
using Fischless.GameCapture;
using BetterGenshinImpact.GameTask.Common.Ui;

namespace BetterGenshinImpact.GameTask.AutoFight;

/// <summary>宿主输入与策略复用同一Native Session；只有这个适配器连接游戏设备。</summary>
internal sealed class NativeCombatBattleHostIo : ICombatBattleHostIo
{
    private readonly NativeCombatFlowRunner _flow;
    private readonly NativeCombatIo? _vision;
    private readonly ICombatHostInputDevice _device;
    private bool _partyRequested, _partyEvidence;
    private CaptureFrameFence? _partyFence;
    private CaptureFrameStamp _partyEvidenceSource;
    public TimeProvider Clock => _device.Clock;
    public Guid BattleId => _flow.Context.BattleId;

    public NativeCombatBattleHostIo(NativeCombatFlowRunner flow, CombatScenes scenes)
        : this(flow, new NativeCombatHostInputDevice()) => _vision = new(scenes);

    internal NativeCombatBattleHostIo(NativeCombatFlowRunner flow, ICombatHostInputDevice device)
    { _flow = flow; _device = device; }

    public CombatBattleObservation ObserveTarget()
    {
        var observation = AvatarRecognition.LatestPassiveObservation;
        var current = AvatarRecognition.PassiveCaptureGate;
        var quality = observation.Quality;
        if (observation.CaptureEpoch != current.Epoch || !current.CanCapture)
            quality = CombatObservationQuality.Unavailable;
        EnemySeekDecision? target = AutoFightSeek.TryCreatePassiveDecision(observation, Clock.GetUtcNow().UtcDateTime,
            out var decision, out _, out _) ? decision : null;
        return new(observation.Source, observation.BattleId, quality, target,
            observation.ImageWidth, observation.ImageHeight, observation.CueFingerprint)
        { Motion = observation.Motion, Control = observation.Control };
    }

    public PartySetupFinishObservation ObservePartyBar()
    {
        using var capture = _vision?.Capture();
        if (capture == null) return default;
        var observed = AutoFightTask.ObservePartySetupBar(capture, capture.FrameStamp.Sequence);
        if (_partyRequested && observed.BarVisible && observed.Source.IsFresh(Clock, TimeSpan.FromMilliseconds(150)) &&
                 _partyFence?.Accepts(observed.Source) == true)
        {
            _partyEvidence = true;
            _partyEvidenceSource = observed.Source;
        }
        return observed;
    }

    public ValueTask<CombatBattleHostInputResult> SendAsync(CombatBattleHostInput input, CancellationToken ct) =>
        SendCoreAsync(input, ct, controlRecovery: false);

    internal ValueTask<CombatBattleHostInputResult> SendControlAsync(CombatBattleHostInput input, CancellationToken ct)
    {
        if (input.Kind != CombatBattleHostInputKind.Breakout) throw new InvalidOperationException("控制恢复入口只允许挣脱键");
        return SendCoreAsync(input, ct, controlRecovery: true);
    }

    private async ValueTask<CombatBattleHostInputResult> SendCoreAsync(CombatBattleHostInput input, CancellationToken ct, bool controlRecovery)
    {
        var requestedAt = Clock.GetTimestamp();
        var inputAttempted = false;
        long? completedAt = null;
        var result = new CombatBattleHostInputResult(CombatBattleHostInputStatus.NotSent);
        try
        {
        async ValueTask Dispatch(CancellationToken token)
        {
            var dispatch = Clock.GetElapsedTime(requestedAt);
            token.ThrowIfCancellationRequested();
            TaskExecutionScope.ThrowIfFailed();
            var remaining = (input.DeadlineTimestamp - Clock.GetTimestamp()) * 1000d / Clock.TimestampFrequency;
            if (input.RequestId == Guid.Empty || remaining <= 0 || dispatch.TotalMilliseconds > 150)
            {
                result = new(CombatBattleHostInputStatus.NotSent, Reason: "host-input-preparation-late-or-expired");
                return;
            }
            using var operation = UiOperation.Begin("combat-host-input", TimeSpan.FromMilliseconds(Math.Min(150, remaining)), token, _device.Logger, Clock);
            operation.RecordDispatch(dispatch);
            operation.Check();
            _device.PrepareInput(); // 生产仍走TaskControl的暂停/焦点入口，回放仅替换操作系统边界。
            operation.Check();
            if (input.Kind != CombatBattleHostInputKind.CloseParty)
            {
                using var admission = operation.Measure(UiOperationPhase.Admission);
                var source = input.Source;
                if (!source.IsFresh(Clock, TimeSpan.FromMilliseconds(150)))
                    throw new TimeoutException("战斗宿主输入前源帧已过期，不发送输入");
            }
            switch (input.Kind)
            {
                case CombatBattleHostInputKind.Camera:
                    inputAttempted = true;
                    using (operation.Measure(UiOperationPhase.NativeInput))
                        _device.MoveCamera(input.X, input.Y);
                    break;
                case CombatBattleHostInputKind.Approach:
                    inputAttempted = true;
                    try
                    {
                        using (operation.Measure(UiOperationPhase.NativeInput))
                            _device.MoveForward(true);
                        using (operation.Measure(UiOperationPhase.ExplicitWait))
                            await _device.DelayAsync(100, token);
                    }
                    finally
                    {
                        using (operation.Measure(UiOperationPhase.NativeInput))
                            _device.MoveForward(false);
                    }
                    break;
                case CombatBattleHostInputKind.Detach:
                    inputAttempted = true;
                    using (operation.Measure(UiOperationPhase.NativeInput))
                        _device.PressDrop();
                    break;
                case CombatBattleHostInputKind.OpenParty:
                    inputAttempted = true;
                    using (operation.Measure(UiOperationPhase.NativeInput))
                        _device.PressParty();
                    _partyRequested = true;
                    _partyEvidence = false;
                    AutoFightTask.LastFightFinishCheckTime = DateTime.Now;
                    _partyFence = new(input.Source, Clock.GetTimestamp());
                    break;
                case CombatBattleHostInputKind.Breakout:
                    if (!controlRecovery) throw new InvalidOperationException("普通宿主不能借用控制恢复输入");
                    inputAttempted = true;
                    using (operation.Measure(UiOperationPhase.NativeInput)) _device.PressBreakout();
                    break;
                case CombatBattleHostInputKind.CloseParty:
                    if (!_partyRequested || !_partyEvidence || !input.PartyEvidence ||
                        !_partyEvidenceSource.IsFresh(Clock, TimeSpan.FromMilliseconds(150)))
                    {
                        result = new(CombatBattleHostInputStatus.NotSent, Reason: "owned-party-ui-not-observed");
                        return;
                    }
                    inputAttempted = true;
                    using (operation.Measure(UiOperationPhase.NativeInput)) CloseParty(input.PartyEvidence);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(input));
            }
            completedAt = Clock.GetTimestamp();
            result = new(CombatBattleHostInputStatus.Sent, completedAt);
            operation.Check();
        }
        await (controlRecovery
            ? _flow.RunControlOperationAsync(input.RequestId, Dispatch, ct)
            : _flow.RunHostOperationAsync(Dispatch, ct));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            ct.ThrowIfCancellationRequested();
            TaskExecutionScope.ThrowIfFailed();
            if (completedAt != null && error is TimeoutException)
                result = new(CombatBattleHostInputStatus.Sent, completedAt, "input-completed-before-late-check", error);
            else if (completedAt != null)
                result = new(CombatBattleHostInputStatus.Failed, completedAt, "input-cleanup-failed", error);
            else if (inputAttempted)
                result = new(CombatBattleHostInputStatus.Unknown, Reason: "native-input-may-have-started", Error: error);
            else if (error is TimeoutException)
                result = new(CombatBattleHostInputStatus.NotSent, Reason: "input-check-late-before-send", Error: error);
            else result = new(CombatBattleHostInputStatus.Failed, Reason: "input-preparation-failed", Error: error);
        }
        try
        {
            _device.Logger.LogDebug("HOST_INPUT_RESULT request={Request} kind={Kind} status={Status} reason={Reason} sourceSequence={SourceSequence} completedAt={CompletedAt} elapsedMs={ElapsedMs:F3} errorType={ErrorType}",
                input.RequestId, input.Kind, result.Status, result.Reason, input.Source.Sequence, completedAt,
                Clock.GetElapsedTime(requestedAt).TotalMilliseconds, result.Error?.GetType().Name);
        }
        catch { /* 诊断输出不能改变已判定的输入结果。 */ }
        return result;
    }

    private void CloseParty(bool evidence)
    {
        _device.PressDrop();
        if (evidence) _device.PressParty();
        _partyRequested = false;
        _partyEvidence = false;
        _partyFence = null;
        _partyEvidenceSource = default;
    }

    public ValueTask DelayAsync(int milliseconds, CancellationToken ct) => _device.DelayAsync(milliseconds, ct);
    public void ReleaseInput()
    {
        try
        {
            if (_partyRequested && _partyEvidence && _partyEvidenceSource.IsFresh(Clock, TimeSpan.FromMilliseconds(150)))
                _flow.RunHostOperationAsync(_ =>
                {
                    CloseParty(_partyEvidence);
                    return ValueTask.CompletedTask;
                }, CancellationToken.None).GetAwaiter().GetResult();
        }
        finally
        {
            _partyRequested = _partyEvidence = false;
            _partyFence = null;
            _partyEvidenceSource = default;
            _flow.ReleaseHostInput();
        }
    }

    internal static CombatBattleHost Create(NativeCombatFlowRunner flow, CombatScenes scenes, AutoFightParam param)
    {
        var finish = new AutoFightTask.TaskFightFinishDetectConfig(param);
        return new(new NativeCombatBattleHostIo(flow, scenes), new()
        {
            TimeoutSeconds = param.Timeout,
            SeekEnabled = param.FinishDetectConfig.RotateFindEnemyEnabled || param.EnableCombatTargeting,
            FinishDetectionEnabled = param.FightFinishDetectEnabled,
            ExternalCompletionAuthority = param.ExternalCompletionAuthority,
            InitialBlockSeconds = finish.BlockCheckBeforeBattleSeconds,
            FinishCheckIntervalSeconds = Math.Max(.1, finish.CheckTime),
            FinishProbeDelayMilliseconds = finish.PaimonEndCheckEnabled ? finish.PaimonEndCheckDelayMs : finish.DetectDelayTime
        });
    }

    internal static CombatBattleHost CreateForExternalScene(NativeCombatFlowRunner flow, CombatScenes scenes)
    {
        var config = TaskContext.Instance().Config.AutoFightConfig;
        var seek = AutoDomain.AutoDomainFightSeekOptions.FromAutoFightConfig(config);
        return new(new NativeCombatBattleHostIo(flow, scenes), new()
        {
            ExternalCompletionAuthority = true,
            FinishDetectionEnabled = false,
            TimeoutSeconds = 600,
            SeekEnabled = seek.Enabled || config.EnableCombatTargeting,
            InitialBlockSeconds = seek.InitialDelay.TotalSeconds,
            FinishCheckIntervalSeconds = seek.Interval.TotalSeconds
        });
    }

    internal static bool ApplyResult(CombatBattleHost host, CombatBattleHostResult result,
        AutoFightTask.TaskFightFinishDetectConfig config)
    {
        if (result == CombatBattleHostResult.Unconfirmed)
            TaskExecutionScope.StopUnconfirmedCombat("战斗宿主未取得完成证据：" + host.Reason);
        if (result != CombatBattleHostResult.Completed) return false;
        config.EndConfirmed = true;
        TaskControl.Logger.LogInformation("战斗宿主确认结束：{Reason}", host.Reason);
        return true;
    }
}

/// <summary>仅替换操作系统/按键/时钟；请求期限、发送判定、owner与fence仍由真实适配器执行。</summary>
internal interface ICombatHostInputDevice
{
    TimeProvider Clock { get; }
    ILogger Logger { get; }
    void PrepareInput();
    void MoveCamera(int x, int y);
    void MoveForward(bool down);
    void PressDrop();
    void PressParty();
    void PressBreakout() => throw new NotSupportedException("此输入设备未实现已验证的键盘挣脱");
    ValueTask DelayAsync(int milliseconds, CancellationToken ct);
}

internal sealed class NativeCombatHostInputDevice : ICombatHostInputDevice
{
    public TimeProvider Clock => TimeProvider.System;
    public ILogger Logger => TaskControl.Logger;
    public void PrepareInput() => TaskControl.CheckAndSleep(0);
    public void MoveCamera(int x, int y) => Simulation.SendInput.Mouse.MoveMouseBy(x, y);
    public void MoveForward(bool down) => Simulation.SendInput.SimulateAction(GIActions.MoveForward,
        down ? KeyType.KeyDown : KeyType.KeyUp);
    public void PressDrop() => Simulation.SendInput.SimulateAction(GIActions.Drop);
    public void PressParty() => Simulation.SendInput.SimulateAction(GIActions.OpenPartySetupScreen);
    public void PressBreakout() => Simulation.SendInput.Keyboard.KeyPress(Vanara.PInvoke.User32.VK.VK_SPACE);
    public ValueTask DelayAsync(int milliseconds, CancellationToken ct) => new(Task.Delay(milliseconds, ct));
}
