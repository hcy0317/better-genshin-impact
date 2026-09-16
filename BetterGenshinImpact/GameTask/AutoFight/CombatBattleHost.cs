using System;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using Fischless.GameCapture;
using BetterGenshinImpact.GameTask.Common.BgiVision;

namespace BetterGenshinImpact.GameTask.AutoFight;

internal enum CombatObservationQuality { Available, Unavailable, Faulted, Late }
internal readonly record struct CombatBattleObservation(CaptureFrameStamp Source, Guid BattleId,
    CombatObservationQuality Quality, EnemySeekDecision? Target, int Width, int Height, ulong CueFingerprint = 0)
{
    public MotionStatus Motion { get; init; } = MotionStatus.Unknown;
    public CombatControlObservation Control { get; init; }
}
internal enum CombatBattleHostResult { Continue, Completed, Unconfirmed }
internal enum CombatBattleHostInputKind { Camera, Approach, OpenParty, CloseParty, Detach, Breakout }
internal readonly record struct CombatBattleHostInput(CombatBattleHostInputKind Kind,
    int X = 0, int Y = 0, bool PartyEvidence = false)
{
    public Guid RequestId { get; init; }
    public CaptureFrameStamp Source { get; init; }
    public long DeadlineTimestamp { get; init; }
}
internal enum CombatBattleHostInputStatus { NotSent, Sent, Unknown, Failed }
internal readonly record struct CombatBattleHostInputResult(CombatBattleHostInputStatus Status,
    long? CompletedTimestamp = null, string? Reason = null, Exception? Error = null)
{
    public int? NativeRequested { get; init; }
    public int? NativeSubmitted { get; init; }
    public long? StartedTimestamp { get; init; }
    public long? ObservableAfterTimestamp { get; init; }
}

/// <summary>游戏/时钟边界；生产与回放共同驱动宿主，不在回放重写退出循环。</summary>
internal interface ICombatBattleHostIo
{
    TimeProvider Clock { get; }
    Guid BattleId { get; }
    CombatBattleObservation ObserveTarget();
    PartySetupFinishObservation ObservePartyBar();
    ValueTask<CombatBattleHostInputResult> SendAsync(CombatBattleHostInput input, CancellationToken ct);
    ValueTask DelayAsync(int milliseconds, CancellationToken ct);
    void ReleaseInput();
}

internal sealed record CombatBattleHostOptions
{
    internal bool ControlRecoveryEnabled { get; init; } // C3b只有在完整I2/V4门槛通过后接线。
    public bool ExternalCompletionAuthority { get; init; }
    public bool FinishDetectionEnabled { get; init; } = true;
    public bool SeekEnabled { get; init; } = true;
    public double TimeoutSeconds { get; init; }
    public double FinishCheckIntervalSeconds { get; init; } = 5;
    public double InitialBlockSeconds { get; init; }
    public int FinishProbeDelayMilliseconds { get; init; } = 75;
}

/// <summary>
/// 单场共享宿主。一次Advance只处理一个观察/输入/等待阶段；策略成功调用不等于战斗进展。
/// 仅编队栏独立确认可以Completed，搜索耗尽或观测不可用只能Unconfirmed。
/// </summary>
internal sealed class CombatBattleHost(ICombatBattleHostIo io, CombatBattleHostOptions options) : IDisposable
{
    private enum Phase { Fighting, BeforeParty, OpenParty, AwaitParty, CloseParty, Searching }
    private const double ObservationDeadline = 15;
    private const double NoProgressDeadline = 45;
    private const int MaximumSearchPulses = AutoFightParam.MaxSeekRotationCount * 4;
    private const int MaximumApproachPulses = 12; // 100ms/脉冲，沿用1.2秒累计接近上界。
    private readonly long _started = io.Clock.GetTimestamp();
    private Phase _phase;
    private CaptureFrameStamp _lastSource;
    private CaptureFrameFence? _inputFence;
    private PartySetupFinishObservation _before;
    private PartySetupFinishDetector? _finish;
    private double _lastValidAt, _lastProgressAt, _nextProbe, _partyDeadline, _graceUntil;
    private double _nextFinishCheck = Math.Max(options.InitialBlockSeconds, options.FinishCheckIntervalSeconds);
    private EnemySeekVisual? _stableVisual;
    private int _minimumHealthWidth, _healthBaselineCandidate, _scanPulses, _approachPulses;
    private double _healthBaselineSince;
    private double _motionSettlesAt, _nextNonCombatCheck;
    private ulong _lastDamageFingerprint;
    private bool _hadDamage, _firstTarget, _reengaged, _finalProbe, _partyEvidence, _endConfirmed, _finishRequested, _closed;
    private bool _externalSearchExhausted;
    private int _detachPulses;
    private Guid _inputRequestId;
    private long _inputRequestDeadline;
    private bool UsesPartyFinish => options.FinishDetectionEnabled && !options.ExternalCompletionAuthority;
    private CombatBattleHostResult _result;
    public string Reason { get; private set; } = "starting";
    public string State => _phase.ToString();
    public long CameraRequests { get; private set; }
    public long ApproachRequests { get; private set; }
    public MotionStatus LastMotion { get; private set; } = MotionStatus.Unknown;
    public CombatControlObservation LastControl { get; private set; }
    private double Now => io.Clock.GetElapsedTime(_started).TotalSeconds;

    public async ValueTask<CombatBattleHostResult> AdvanceAsync(NativeCombatFlowRunner flow, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        try
        {
            ct.ThrowIfCancellationRequested();
            TaskExecutionScope.ThrowIfFailed();
            if (flow.Context.BattleId != io.BattleId) throw new InvalidOperationException("战斗宿主不能消费另一场运行");
            if (_result != CombatBattleHostResult.Continue) return _result;
            _finishRequested |= flow.TakeFinishCheckRequest();
            var now = Now;
            if (options.TimeoutSeconds > 0 && now >= options.TimeoutSeconds)
                return Stop("configured-timeout");
            // 不在原子宏或施放确认中间抢走输入；它们仍受原策略截止时间和取消约束。
            if (flow.IsAtomic || flow.HasPendingConfirmation || flow.HasAwaitingObservation)
            {
                await flow.StepAsync(ct);
                return _result;
            }
            var observation = ReadObservation(io.ObserveTarget);
            if (observation.Quality == CombatObservationQuality.Available && observation.BattleId == io.BattleId &&
                observation.Source.IsFresh(io.Clock, TimeSpan.FromMilliseconds(150)) && observation.Control.KeyboardBreakoutRequested)
            {
                if (now - _lastProgressAt >= NoProgressDeadline) return Stop("control-interruption-without-progress");
                // 未发送的菜单探测在控制条件失效时撤回，不在恢复后复用旧探测/旧期限。
                if (_phase is Phase.BeforeParty or Phase.OpenParty)
                {
                    _phase = Phase.Fighting;
                    _partyDeadline = 0;
                    _inputRequestId = Guid.Empty;
                    _inputRequestDeadline = 0;
                }
                await flow.StepAsync(ct);
                return _result;
            }
            if (_phase is Phase.BeforeParty or Phase.OpenParty or Phase.AwaitParty or Phase.CloseParty)
            {
                if (_phase is Phase.BeforeParty or Phase.OpenParty && observation.Target != null &&
                    Accept(observation, out var newCombatEvidence))
                {
                    // 未发探测可以撤回；已发探测必须经过原回执/打开UI证据收束。
                    _phase = Phase.Fighting;
                    _partyDeadline = 0;
                    _inputRequestId = Guid.Empty;
                    _inputRequestDeadline = 0;
                    _finishRequested = false;
                    _nextFinishCheck = now + Math.Max(.1, options.FinishCheckIntervalSeconds);
                    _lastValidAt = now;
                    if (newCombatEvidence) ObserveProgress(observation, now);
                    if (_finalProbe && now - _lastProgressAt >= NoProgressDeadline)
                        return Stop("bounded-search-without-game-progress");
                    await flow.StepAsync(ct);
                    return _result;
                }
                return await AdvancePartyAsync(ct);
            }

            var fresh = Accept(observation, out var newEvidence);
            if (!fresh)
            {
                Reason = observation.Quality switch { CombatObservationQuality.Faulted => "observation-faulted",
                    CombatObservationQuality.Late => "late-observation", _ => "awaiting-source-frame" };
                if (observation.Quality == CombatObservationQuality.Unavailable && observation.BattleId == io.BattleId &&
                    observation.Source.IsKnown && now >= _nextNonCombatCheck)
                {
                    _nextNonCombatCheck = now + 1;
                    // 这里只请求重新取证；恢复仍须由既有协调器的新画面许可，不能让弹窗阻断其入口。
                    flow.InspectDefeat(ct);
                }
                if (now - _lastValidAt >= ObservationDeadline && !options.ExternalCompletionAuthority) return Stop(Reason);
                await io.DelayAsync(50, ct);
                return _result;
            }
            if (newEvidence)
            {
                _lastValidAt = now;
                LastMotion = observation.Motion;
                LastControl = observation.Control;
                ObserveProgress(observation, now);
            }
            else if (_phase == Phase.Searching)
            {
                await io.DelayAsync(50, ct);
                return _result;
            }
            if (_phase == Phase.Searching) return await AdvanceSearchAsync(observation, ct);
            if (options.ControlRecoveryEnabled && newEvidence && observation.Motion == MotionStatus.Climb && flow.IsAtRootBoundary && _detachPulses < 2)
            {
                if (await SendAsync(new(CombatBattleHostInputKind.Detach), ct)) _detachPulses++;
                Reason = "confirmed-climb-bounded-detach";
                return _result;
            }

            var noProgress = now - _lastProgressAt >= NoProgressDeadline && now >= _graceUntil;
            if (flow.IsAtRootBoundary && now >= options.InitialBlockSeconds &&
                !_externalSearchExhausted &&
                (_finishRequested && UsesPartyFinish || noProgress || observation.Target == null && now >= _nextFinishCheck))
            {
                _finishRequested = false;
                if (noProgress && observation.Target != null)
                {
                    if (!options.SeekEnabled) return Stop("visible-target-without-progress");
                    _phase = Phase.Searching;
                }
                else if (UsesPartyFinish) _phase = Phase.BeforeParty;
                else if (options.SeekEnabled) _phase = Phase.Searching;
                else if (noProgress && !options.ExternalCompletionAuthority) return Stop("no-progress-without-finish-detector");
                _nextFinishCheck = now + Math.Max(.1, options.FinishCheckIntervalSeconds);
                return _result;
            }
            await flow.StepAsync(ct);
            return _result;
        }
        catch
        {
            try { io.ReleaseInput(); } catch { /* 不遮蔽原始取消/视觉/输入异常。 */ }
            throw;
        }
    }

    private T ReadObservation<T>(Func<T> read)
    {
        var started = Stopwatch.GetTimestamp();
        var value = read();
        var elapsed = Stopwatch.GetElapsedTime(started);
        CombatRuntimeMetrics.Shared.Record("host.observation", elapsed);
        // 返回后才能安全丢弃迟到证据，不让超预算观察继续产生输入。
        if (elapsed > TimeSpan.FromMilliseconds(150))
        {
            // 单次迟到只否决本次证据，不能在这里升级为全场异常。原deadline继续计时。
            if (value is CombatBattleObservation battle)
                return (T)(object)(battle with { Quality = CombatObservationQuality.Late });
            if (value is PartySetupFinishObservation party)
                return (T)(object)(party with { Source = default, BarVisible = false });
            throw new InvalidOperationException("未定义的战斗观察类型");
        }
        return value;
    }

    private bool Accept(CombatBattleObservation observation, out bool newEvidence)
    {
        newEvidence = false;
        if (observation.Quality != CombatObservationQuality.Available || observation.BattleId != io.BattleId ||
            !observation.Source.IsFresh(io.Clock, TimeSpan.FromMilliseconds(150))) return false;
        if (_lastSource.IsKnown && observation.Source != _lastSource && !observation.Source.IsAfter(_lastSource)) return false;
        if (_inputFence is { } fence && !fence.Accepts(observation.Source)) return false;
        newEvidence = !_lastSource.IsKnown || observation.Source.IsAfter(_lastSource);
        if (newEvidence) _lastSource = observation.Source;
        _inputFence = null;
        return true;
    }

    private void ObserveProgress(CombatBattleObservation observation, double now)
    {
        var target = observation.Target;
        var damage = target?.Cue == SeekCueKind.DamageNumber;
        if (now < _motionSettlesAt)
        {
            _hadDamage = damage;
            _lastDamageFingerprint = observation.CueFingerprint;
            return;
        }
        var progressed = damage && (!_hadDamage || observation.CueFingerprint != 0 &&
            observation.CueFingerprint != _lastDamageFingerprint);
        _hadDamage = damage;
        _lastDamageFingerprint = observation.CueFingerprint;
        if ((target?.Cue is SeekCueKind.HealthBar or SeekCueKind.FixedTopHealth) && target.Value.Visual is { } visual)
        {
            if (_stableVisual == null) _minimumHealthWidth = visual.Width;
            if (!_firstTarget)
            {
                _firstTarget = true;
                progressed = true;
                _minimumHealthWidth = visual.Width;
            }
            var stable = _stableVisual is { } previous &&
                Math.Abs(previous.X - visual.X) <= Math.Max(3, observation.Width / 100) &&
                Math.Abs(previous.Y - visual.Y) <= Math.Max(3, observation.Height / 100) &&
                Math.Abs(previous.Height - visual.Height) <= 1;
            if (stable && visual.Width > _minimumHealthWidth * 1.3)
            {
                // 新目标/回血只建立新基线，不直接算进展；必须等后续真实缩短。
                if (_healthBaselineCandidate == 0 || Math.Abs(visual.Width - _healthBaselineCandidate) > Math.Max(3, visual.Width / 20))
                {
                    _healthBaselineCandidate = visual.Width;
                    _healthBaselineSince = now;
                }
                else if (now - _healthBaselineSince >= .15)
                {
                    _minimumHealthWidth = visual.Width;
                    _healthBaselineCandidate = 0;
                }
            }
            else _healthBaselineCandidate = 0;
            if (stable && visual.Width < _minimumHealthWidth - Math.Max(2, _minimumHealthWidth / 50))
            {
                progressed = true;
                _minimumHealthWidth = visual.Width;
            }
            _stableVisual = visual;
        }
        if (!progressed) return;
        _lastProgressAt = now;
        _reengaged = false;
        _scanPulses = _approachPulses = 0;
        _detachPulses = 0;
        _externalSearchExhausted = false;
        Reason = damage ? "new-damage-cue" : "health-progress";
    }

    private async ValueTask<CombatBattleHostResult> AdvanceSearchAsync(CombatBattleObservation observation, CancellationToken ct)
    {
        if (!options.SeekEnabled) return Stop("finish-not-confirmed");
        if (observation.Target is { } target && target.Visual is { } visual)
        {
            if (target.Cue is SeekCueKind.FixedTopHealth or SeekCueKind.DamageNumber)
            {
                if (TryResumeStrategy()) return _result;
                _scanPulses = MaximumSearchPulses;
            }
            else
            {
            var offset = target.Cue == SeekCueKind.DirectionIndicator
                ? AutoFightSeek.GetIndicatorCameraOffset(target.Direction, visual, observation.Width, observation.Height)
                : AutoFightSeek.GetVisibleEnemyCameraOffset(visual, observation.Width);
            if (Math.Abs(offset) > 20 && _scanPulses < MaximumSearchPulses)
            {
                if (!await SendAsync(new(CombatBattleHostInputKind.Camera, Math.Clamp(offset, -120, 120)), ct))
                    return _result;
            }
            else if (observation.Motion == MotionStatus.Normal && target.Cue != SeekCueKind.DamageNumber && _approachPulses < MaximumApproachPulses)
            {
                if (await SendAsync(new(CombatBattleHostInputKind.Approach), ct)) _approachPulses++;
                return _result;
            }
            else if (TryResumeStrategy())
            {
                return _result;
            }
            else _scanPulses = MaximumSearchPulses;
            }
        }
        else if (_scanPulses < MaximumSearchPulses)
        {
            var offset = AutoFightSeek.GetSeekCameraOffset(observation.Width, observation.Height,
                _scanPulses / 4, _scanPulses % 4);
            if (!await SendAsync(new(CombatBattleHostInputKind.Camera, offset.x, offset.y), ct)) return _result;
        }
        _scanPulses++;
        if (_scanPulses >= MaximumSearchPulses)
        {
            _finalProbe = true;
            if (options.ExternalCompletionAuthority)
            {
                // 秘境/幽境终态归各自检测器。本次运动预算耗尽后继续策略，但不因
                // 经过时间或输入次数重开运动预算；只由新的实际目标进展重新准入。
                _externalSearchExhausted = true;
                _phase = Phase.Fighting;
                Reason = "external-scene-awaiting-authority-after-bounded-search";
            }
            else if (UsesPartyFinish) _phase = Phase.BeforeParty;
            else return Stop("bounded-search-exhausted");
        }
        return _result;
    }

    private bool TryResumeStrategy()
    {
        if (_reengaged && Now - _lastProgressAt >= NoProgressDeadline) return false;
        _reengaged = true;
        _graceUntil = Now + CombatFlowPolicy.EpisodeTimeoutSeconds;
        _phase = Phase.Fighting;
        _inputRequestId = Guid.Empty;
        _inputRequestDeadline = 0;
        _nextFinishCheck = _graceUntil;
        Reason = "bounded-reengagement";
        return true;
    }

    private async ValueTask<CombatBattleHostResult> AdvancePartyAsync(CancellationToken ct)
    {
        var probeDelay = Math.Clamp(options.FinishProbeDelayMilliseconds, 0, 9000) / 1000d;
        // 从第一次进入BeforeParty（包含截图）起计时，未发送不能重开预算。
        if (_partyDeadline == 0) _partyDeadline = Now + Math.Min(10, Math.Max(1.2, probeDelay + .8));
        if (Now >= _partyDeadline && _phase is Phase.BeforeParty or Phase.OpenParty)
            return Stop("party-input-deadline-before-send");
        switch (_phase)
        {
            case Phase.BeforeParty:
                _before = ReadObservation(io.ObservePartyBar);
                if (!_before.Source.IsFresh(io.Clock, TimeSpan.FromMilliseconds(150)))
                {
                    await io.DelayAsync(50, ct);
                    return _result;
                }
                _phase = Phase.OpenParty;
                break;
            case Phase.OpenParty:
                var opened = await SendRequestAsync(new(CombatBattleHostInputKind.OpenParty), _before.Source, ct,
                    TimeSpan.FromSeconds(Math.Max(0, _partyDeadline - Now)));
                if (opened.Status != CombatBattleHostInputStatus.Sent || _result != CombatBattleHostResult.Continue)
                {
                    if (_result == CombatBattleHostResult.Continue) _phase = Phase.BeforeParty;
                    return _result;
                }
                _finish = new(_before, io.Clock.GetUtcNow())
                { Fence = new(_before.Source, opened.CompletedTimestamp!.Value) };
                _nextProbe = Now + probeDelay;
                _partyEvidence = false;
                _phase = Phase.AwaitParty;
                break;
            case Phase.AwaitParty:
                if (Now < _nextProbe) { await io.DelayAsync(50, ct); break; }
                var sample = ReadObservation(io.ObservePartyBar);
                var freshPartySource = sample.Source.IsFresh(io.Clock, TimeSpan.FromMilliseconds(150)) &&
                    _finish!.Fence?.Accepts(sample.Source) == true;
                _partyEvidence |= sample.BarVisible && freshPartySource;
                if (freshPartySource && _finish!.Observe(sample))
                {
                    _endConfirmed = true;
                    Reason = "confirmed-post-input-party-bar";
                    _phase = Phase.CloseParty;
                }
                else if (Now >= _partyDeadline) _phase = Phase.CloseParty;
                _nextProbe = Now + .1;
                break;
            case Phase.CloseParty:
                if (_partyEvidence)
                {
                    var closed = await SendRequestAsync(new(CombatBattleHostInputKind.CloseParty, PartyEvidence: true), _before.Source, ct,
                        TimeSpan.FromSeconds(Math.Max(0, _partyDeadline - Now)));
                    if (closed.Status != CombatBattleHostInputStatus.Sent || _result != CombatBattleHostResult.Continue) return _result;
                    _inputFence = new(_lastSource, closed.CompletedTimestamp!.Value);
                }
                else io.ReleaseInput(); // 没有本次打开UI的证据，不盲发关闭键。
                _partyDeadline = 0;
                if (_endConfirmed) return _result = CombatBattleHostResult.Completed;
                if (_finalProbe) return Stop("bounded-search-finish-unconfirmed");
                _phase = Phase.Searching;
                Reason = "searching-after-no-bar";
                break;
        }
        return _result;
    }

    private async ValueTask<bool> SendAsync(CombatBattleHostInput input, CancellationToken ct)
    {
        var result = await SendRequestAsync(input, _lastSource, ct);
        if (result.Status != CombatBattleHostInputStatus.Sent || _result != CombatBattleHostResult.Continue) return false;
        if (input.Kind == CombatBattleHostInputKind.Camera) CameraRequests++;
        if (input.Kind == CombatBattleHostInputKind.Approach) ApproachRequests++;
        _motionSettlesAt = Now + .35;
        _stableVisual = null;
        _minimumHealthWidth = _healthBaselineCandidate = 0;
        _inputFence = new(_lastSource, result.CompletedTimestamp!.Value);
        return true;
    }

    private async ValueTask<CombatBattleHostInputResult> SendRequestAsync(CombatBattleHostInput input,
        CaptureFrameStamp source, CancellationToken ct, TimeSpan? remaining = null)
    {
        ct.ThrowIfCancellationRequested();
        if (_inputRequestId == Guid.Empty)
        {
            _inputRequestId = Guid.NewGuid();
            var seconds = Math.Min(ObservationDeadline, remaining?.TotalSeconds ?? ObservationDeadline);
            if (options.TimeoutSeconds > 0) seconds = Math.Min(seconds, options.TimeoutSeconds - Now);
            _inputRequestDeadline = io.Clock.GetTimestamp() + (long)(Math.Max(0, seconds) * io.Clock.TimestampFrequency);
        }
        if (io.Clock.GetTimestamp() >= _inputRequestDeadline)
        {
            Stop("host-input-request-deadline");
            return new(CombatBattleHostInputStatus.NotSent, Reason: Reason);
        }
        input = input with { RequestId = _inputRequestId, Source = source, DeadlineTimestamp = _inputRequestDeadline };
        var started = Stopwatch.GetTimestamp();
        CombatBattleHostInputResult result;
        try { result = await io.SendAsync(input, ct); }
        finally { CombatRuntimeMetrics.Shared.Record("host.input", Stopwatch.GetElapsedTime(started)); }
        ct.ThrowIfCancellationRequested();
        TaskExecutionScope.ThrowIfFailed();
        switch (result.Status)
        {
            case CombatBattleHostInputStatus.Sent:
                if (result.CompletedTimestamp is not { } completed || completed > io.Clock.GetTimestamp() ||
                    completed < source.CapturedTimestamp)
                {
                    Stop("host-input-invalid-completion-evidence");
                    return new(CombatBattleHostInputStatus.Unknown, Reason: Reason);
                }
                _inputRequestId = Guid.Empty;
                if (result.Error != null) Stop("host-input-submitted-but-operation-interrupted: " + result.Error.GetType().Name);
                if (completed > _inputRequestDeadline) Stop("host-input-deadline-after-send");
                _inputRequestDeadline = 0;
                break;
            case CombatBattleHostInputStatus.NotSent:
                Reason = result.Reason ?? "host-input-awaiting-observation";
                // 不置fence，不增加已发送计数；让原请求等下一帧而不忙轮询。
                await io.DelayAsync(50, ct);
                break;
            case CombatBattleHostInputStatus.Unknown:
                Stop("host-input-outcome-unknown: " + result.Reason);
                break;
            case CombatBattleHostInputStatus.Failed:
                ExceptionDispatchInfo.Capture(result.Error ?? new InvalidOperationException(result.Reason)).Throw();
                break;
            default:
                Stop("host-input-invalid-status");
                return new(CombatBattleHostInputStatus.Unknown, Reason: Reason);
        }
        return result;
    }

    private CombatBattleHostResult Stop(string reason)
    {
        Reason = reason;
        io.ReleaseInput();
        return _result = CombatBattleHostResult.Unconfirmed;
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        io.ReleaseInput();
    }
}
