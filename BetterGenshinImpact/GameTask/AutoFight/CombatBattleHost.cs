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
    public EnemySeekVisual? FixedTopHealth { get; init; }
    public MotionStatus Motion { get; init; } = MotionStatus.Unknown;
    public CombatControlObservation Control { get; init; }
    public SeekRecognitionDiagnostics? Recognition { get; init; }
    public UnconfirmedSearchHint? SearchHint { get; init; }
    public string? PassiveGate { get; init; }
    public string? DamageFallback { get; init; }
    public long CaptureEpoch { get; init; }
    public CombatSceneCapabilities Scene { get; init; } = CombatSceneCapabilities.Unknown;
}
internal enum CombatBattleHostResult { Continue, Completed, Unconfirmed }
internal sealed record CombatProgressEvidence(CaptureFrameStamp Source, string Kind, EnemySeekDecision? Target,
    EnemySeekVisual? FixedTopHealth, long ObservedTimestamp);
internal sealed record CombatBattleHostTrace(Guid BattleId, string Episode, string State, string Reason,
    CombatBattleHostResult Result, CombatBattleObservation Observation, int ScanUsed, int ApproachUsed,
    double ProgressAge, double SettleRemaining, bool FinalProbe, string? CapturePhase = null)
{
    public Guid? InputRequest { get; init; }
    public CaptureFrameStamp InputSource { get; init; }
    public CombatBattleHostInputStatus? InputStatus { get; init; }
    public CombatBattleHostInputKind? InputKind { get; init; }
    public PartySetupFinishObservation PartySample { get; init; }
    public string? PartyReason { get; init; }
    public string? ClosedEpisode { get; init; }
    public string? ObservationGate { get; init; }
    public CombatProgressEvidence? LastProgress { get; init; }
}
internal enum CombatBattleHostInputKind { Camera, Approach, OpenParty, CloseParty, Detach, Breakout, TargetLock }
internal readonly record struct CombatBattleHostInput(CombatBattleHostInputKind Kind,
    int X = 0, int Y = 0, bool PartyEvidence = false)
{
    public Guid RequestId { get; init; }
    public CaptureFrameStamp Source { get; init; }
    public long DeadlineTimestamp { get; init; }
    public Guid? SelectionGoal { get; init; }
    public CombatSceneCapabilities? Scene { get; init; }
}
internal enum CombatBattleHostInputStatus { NotSent, Sent, Unknown, Failed }
internal readonly record struct CombatInputPerformanceRecheck(Guid RequestId, CaptureFrameStamp Source,
    long DeadlineTimestamp, Guid? SelectionGoal);
internal readonly record struct CombatBattleHostInputResult(CombatBattleHostInputStatus Status,
    long? CompletedTimestamp = null, string? Reason = null, Exception? Error = null)
{
    public int? NativeRequested { get; init; }
    public int? NativeSubmitted { get; init; }
    public bool NativeUncertain { get; init; }
    public int TransportRequested { get; init; }
    public int TransportAcknowledged { get; init; }
    public bool HasCompleteReceipt => Status == CombatBattleHostInputStatus.Sent &&
        ((NativeRequested is > 0 && NativeRequested == NativeSubmitted) ||
         (TransportRequested > 0 && TransportRequested == TransportAcknowledged));
    public long? StartedTimestamp { get; init; }
    public long? ObservableAfterTimestamp { get; init; }
    public CombatInputPerformanceRecheck? PerformanceRecheck { get; init; }
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
    void Trace(CombatBattleHostTrace observation) { }
    // 返回值仅描述诊断素材状态；业务准入不得依赖留图是否成功。
    bool PrepareInputEvidence(CombatBattleHostInput input, CancellationToken ct) => true;
    bool InputEvidenceInvalidated => false;
    void ConfirmInputFeedback(CombatBattleObservation observation) { }
    void EndInputEvidence(string reason, Exception? error = null) { }
    void CancelInputEvidence() { }
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
    private enum Phase { Fighting, BeforeParty, OpenParty, AwaitParty, CloseParty, Searching, Reengaging }
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
    private CombatProgressEvidence? _lastProgressEvidence;
    private double _nextFinishCheck = Math.Max(options.InitialBlockSeconds, options.FinishCheckIntervalSeconds);
    private EnemySeekVisual? _stableVisual;
    private bool _progressHealthIsFixed;
    private int _minimumHealthWidth, _healthBaselineCandidate, _scanPulses, _approachPulses;
    private int _searchVerticalOffset;
    private double _healthBaselineSince;
    private double _motionSettlesAt, _nextNonCombatCheck;
    private ulong _lastDamageFingerprint;
    private bool _hadDamage, _firstTarget, _reengaged, _finalProbe, _partyEvidence, _endConfirmed, _finishRequested, _closed;
    private bool _externalSearchExhausted;
    private bool _hasGameProgress;
    private bool _reengagementDraining;
    private bool _engagementPending;
    private bool _engagementSearch;
    private int _behindTurnSide;
    private double _lastDirectionAt = double.NegativeInfinity;
    private readonly CombatTargetDistance _targetDistance = new();
    private bool _sceneLockUsed;
    private int _detachPulses;
    private Guid _inputRequestId;
    private long _inputRequestDeadline;
    private CaptureFrameStamp _inputRequestSource;
    private (Phase Phase, CombatBattleHostInputKind Kind, Guid? SelectionGoal, int X, int Y) _inputRequestIntent;
    private readonly record struct CandidateDecision(EnemySeekDecision? Target, MotionStatus Motion,
        CombatControlObservation Control, int Width, int Height, EnemySeekVisual? Hint, long Epoch, CombatSceneCapabilities Scene);
    private CandidateDecision _inputRequestDecision;
    private static CandidateDecision DecisionFor(CombatBattleObservation observation) =>
        new(observation.Target, observation.Motion, observation.Control, observation.Width, observation.Height,
            observation.SearchHint?.Visual, observation.CaptureEpoch, observation.Scene);

    private static bool SameCandidate(CandidateDecision before, CandidateDecision next)
    {
        if (before.Motion != next.Motion || before.Control != next.Control || before.Epoch != next.Epoch ||
            before.Width != next.Width || before.Height != next.Height || before.Scene != next.Scene) return false;
        var tolerance = Math.Max(2, before.Width / 960);
        static bool VisualMatches(EnemySeekVisual? left, EnemySeekVisual? right, int tolerance)
        {
            if (left == null || right == null) return left == right;
            var a = left.Value; var b = right.Value;
            return Math.Abs(a.CenterX - b.CenterX) <= tolerance && Math.Abs(a.CenterY - b.CenterY) <= tolerance &&
                Math.Abs(a.Height - b.Height) <= tolerance && Math.Abs(a.Width - b.Width) <= tolerance &&
                Math.Abs(a.HealthBarTrackWidth - b.HealthBarTrackWidth) <= tolerance;
        }
        if (!VisualMatches(before.Hint, next.Hint, tolerance)) return false;
        if (before.Target == null || next.Target == null) return before.Target == next.Target;
        var oldTarget = before.Target.Value; var target = next.Target.Value;
        // 多候选没有可证明的唯一身份；不能仅凭相似矩形把交叉目标当作原目标。
        return oldTarget.SignalCount == 1 && target.SignalCount == 1 && oldTarget.Cue == target.Cue &&
            oldTarget.Action == target.Action && oldTarget.Direction == target.Direction &&
            oldTarget.FixedTopHealth.HasValue == target.FixedTopHealth.HasValue &&
            VisualMatches(oldTarget.Visual, target.Visual, tolerance);
    }

    private bool SameInputIntent((Phase Phase, CombatBattleHostInputKind Kind, Guid? SelectionGoal, int X, int Y) next)
    {
        var old = _inputRequestIntent;
        if (old.Phase != next.Phase || old.Kind != next.Kind || old.SelectionGoal != next.SelectionGoal) return false;
        if (old.Kind is not (CombatBattleHostInputKind.Camera or CombatBattleHostInputKind.Approach)) return old == next;
        static bool SameAxis(int a, int b) => (a == 0 || b == 0 || Math.Sign(a) == Math.Sign(b)) && Math.Abs(a - b) <= 4;
        return SameAxis(old.X, next.X) && SameAxis(old.Y, next.Y);
    }
    private long? _performanceRecheckDeadline;
    private long _performanceRecheckNotBefore;
    private bool UsesPartyFinish => options.FinishDetectionEnabled && !options.ExternalCompletionAuthority;
    private CombatBattleHostResult _result;
    private CombatBattleObservation _diagnosticObservation;
    private string _evidenceEpisode = Guid.NewGuid().ToString("N");
    private string? _closedEvidenceEpisode;
    private bool _evidenceActive;
    private bool _terminalTraced;
    private double _nextTrace;
    private Guid? _traceInputRequest;
    private CaptureFrameStamp _traceInputSource;
    private CombatBattleHostInputStatus? _traceInputStatus;
    private CombatBattleHostInputKind? _traceInputKind;
    private PartySetupFinishObservation _tracePartySample;
    private string? _tracePartyReason;
    private string? _traceObservationGate;
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
        var started = Stopwatch.GetTimestamp();
        var previousPhase = _phase;
        var previousFinalProbe = _finalProbe;
        var previousCamera = CameraRequests;
        var previousApproach = ApproachRequests;
        string? exceptionReason = null;
        var feedbackAccepted = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            TaskExecutionScope.ThrowIfFailed();
            Common.Ui.UiOperation.Current?.Check();
            if (flow.Context.BattleId != io.BattleId) throw new InvalidOperationException("战斗宿主不能消费另一场运行");
            if (_result != CombatBattleHostResult.Continue) return _result;
            _finishRequested |= flow.TakeFinishCheckRequest();
            var now = Now;
            if (options.TimeoutSeconds > 0 && now >= options.TimeoutSeconds)
                return Stop("configured-timeout");
            // 外部秘境完成判定不拥有已创建输入请求的期限。失去新帧时也必须
            // 收束未发送请求，不能等画面恢复后才发现几分钟前就已到期。
            if (_inputRequestId != Guid.Empty && io.Clock.GetTimestamp() >= _inputRequestDeadline)
                return Stop("host-input-request-deadline");
            if (_performanceRecheckDeadline is { } beforeRead && io.Clock.GetTimestamp() >= beforeRead)
                return Stop("host-input-performance-recheck-deadline");
            var observation = ReadObservation(io.ObserveTarget);
            _diagnosticObservation = observation;
            var fresh = Accept(observation, out var newEvidence, out _traceObservationGate);
            var demand = flow.EngagementDemand;
            if (demand == CombatEngagementDemand.Unknown && _phase == Phase.Searching) demand = CombatEngagementDemand.Melee;
            if (fresh && newEvidence && demand != CombatEngagementDemand.Unknown &&
                _targetDistance.Observe(observation, demand, io.Clock) is { } needsApproach && observation.Target is { } distanceTarget)
            {
                observation = observation with { Target = distanceTarget with
                    { Action = needsApproach ? AutoFightSeekAction.ApproachVisibleEnemy : AutoFightSeekAction.KeepFighting } };
                _diagnosticObservation = observation;
            }
            feedbackAccepted = fresh && newEvidence &&
                (_performanceRecheckDeadline == null || observation.Source.CapturedTimestamp >= _performanceRecheckNotBefore);
            if (_inputRequestId != Guid.Empty &&
                _inputRequestIntent.Kind is CombatBattleHostInputKind.Camera or CombatBattleHostInputKind.Approach or CombatBattleHostInputKind.TargetLock &&
                fresh && newEvidence && !SameCandidate(_inputRequestDecision, DecisionFor(observation)))
            {
                CancelPendingCandidate();
                Reason = "host-input-candidate-changed";
                await io.DelayAsync(50, ct);
                return _result; // New target/control evidence may only establish a new candidate next turn.
            }
            if (_performanceRecheckDeadline is { } recheckDeadline)
            {
                // 先于控制/busy分支：已完整送达的辅助不能用旧画面继续切人或发第二次脉冲。
                Common.Ui.UiOperation.Current?.Check();
                if (io.Clock.GetTimestamp() >= recheckDeadline)
                    return Stop("host-input-performance-recheck-deadline");
                if (!fresh || !newEvidence || observation.Source.CapturedTimestamp < _performanceRecheckNotBefore)
                {
                    Reason = "host-input-performance-awaiting-fresh-evidence";
                    await io.DelayAsync(50, ct);
                    return _result;
                }
                _performanceRecheckDeadline = null;
                _lastValidAt = Now;
                LastMotion = observation.Motion;
                LastControl = observation.Control;
                Reason = "host-input-performance-rechecked";
                return _result; // 只解除屏障，不记进展、不报完成；下一Step再按原策略决策。
            }
            if (_engagementPending && flow.EngagementDemand == CombatEngagementDemand.Unknown)
                CancelPendingCandidate(); // A due maintenance/changed action cannot inherit an unsent movement request.
            if (newEvidence)
            {
                _lastValidAt = now;
                LastMotion = observation.Motion;
                LastControl = observation.Control;
                ObserveProgress(observation, now);
                if (observation.Target is { Cue: SeekCueKind.DirectionIndicator }) _lastDirectionAt = now;
            }
            if (_phase == Phase.Reengaging && !_finalProbe)
            {
                SetPhase(Phase.Fighting);
            }
            if (_phase != Phase.Reengaging) _reengagementDraining = false;
            if (_phase == Phase.Reengaging && Now >= _graceUntil)
            {
                if (Now - _lastProgressAt >= NoProgressDeadline)
                    return Stop("reengagement-without-game-progress");
                if (flow.IsAtomic || flow.HasPendingConfirmation || flow.HasAwaitingObservation)
                    _reengagementDraining = true;
                else
                {
                    if (!fresh)
                    {
                        // 只有既有动作收束后可等原观察上界，不能给空闲丢帧续一个窗口。
                        if (!_reengagementDraining || Now - _lastValidAt >= ObservationDeadline)
                            return Stop("reengagement-deadline-without-safe-finish-probe");
                        await io.DelayAsync(50, ct);
                        return _result;
                    }
                    if (observation.Control.KeyboardBreakoutRequested)
                        return Stop("reengagement-deadline-without-safe-finish-probe");
                    _finishRequested = false;
                    SetPhase(Phase.BeforeParty);
                    return _result;
                }
            }
            if (observation.Quality == CombatObservationQuality.Available && observation.BattleId == io.BattleId &&
                observation.Source.IsFresh(io.Clock, TimeSpan.FromMilliseconds(150)) && observation.Control.KeyboardBreakoutRequested)
            {
                if (now - _lastProgressAt >= NoProgressDeadline) return Stop("control-interruption-without-progress");
                // 未发送的菜单探测在控制条件失效时撤回，不在恢复后复用旧探测/旧期限。
                if (_phase is Phase.BeforeParty or Phase.OpenParty)
                {
                    SetPhase(Phase.Fighting);
                    _partyDeadline = 0;
                    _inputRequestId = Guid.Empty;
                    _inputRequestDeadline = 0;
                }
                await AdvanceFlowAsync(flow, ct);
                return _result;
            }
            // busy只禁止抢输入，不豁免只读危险/进展/原期限监督。
            if (flow.IsAtomic || flow.HasPendingConfirmation || flow.HasAwaitingObservation)
            {
                if (!fresh && now - _lastValidAt >= ObservationDeadline && !options.ExternalCompletionAuthority)
                    return Stop("busy-without-fresh-observation");
                if (now - _lastProgressAt >= NoProgressDeadline && now >= _graceUntil)
                    return Stop("busy-without-game-progress");
                if (options.SeekEnabled && _approachPulses < MaximumApproachPulses && newEvidence &&
                    flow.SelectionAssistance is { } assistance &&
                    CanApproach(observation, io.BattleId, io.Clock, selectionAssistance: true))
                {
                    if (await SendAsync(new(CombatBattleHostInputKind.Approach)
                        { SelectionGoal = assistance.Goal, DeadlineTimestamp = assistance.Deadline }, ct)) _approachPulses++;
                    return _result;
                }
                await AdvanceFlowAsync(flow, ct);
                return _result;
            }
            if (_phase is Phase.BeforeParty or Phase.OpenParty or Phase.AwaitParty or Phase.CloseParty)
            {
                if (_phase is Phase.BeforeParty or Phase.OpenParty && observation.Target != null &&
                    fresh)
                {
                    // 未发探测可以撤回；已发探测必须经过原回执/打开UI证据收束。
                    SetPhase(Phase.Fighting);
                    _partyDeadline = 0;
                    _inputRequestId = Guid.Empty;
                    _inputRequestDeadline = 0;
                    _finishRequested = false;
                    _nextFinishCheck = now + Math.Max(.1, options.FinishCheckIntervalSeconds);
                    _lastValidAt = now;
                    if (_finalProbe && now - _lastProgressAt >= NoProgressDeadline)
                        return Stop("bounded-search-without-game-progress");
                    await flow.StepAsync(ct);
                    return _result;
                }
                return await AdvancePartyAsync(observation, fresh, newEvidence, ct);
            }

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
            if (!newEvidence && _phase == Phase.Searching)
            {
                await io.DelayAsync(50, ct);
                return _result;
            }
            if (_phase == Phase.Searching)
            {
                if (_engagementSearch && flow.EngagementDemand == CombatEngagementDemand.Unknown)
                {
                    CancelPendingCandidate();
                    _engagementSearch = false;
                    SetPhase(Phase.Fighting);
                    await flow.StepAsync(ct);
                    return _result;
                }
                return await AdvanceSearchAsync(observation, ct);
            }
            if (_phase == Phase.Reengaging)
            {
                _finishRequested = false;
                // 只推进原策略；不因每轮check重开纯搜索，也不豁免Native输入/护盾门禁。
                await AdvanceFlowAsync(flow, ct);
                return _result;
            }
            if (options.ControlRecoveryEnabled && newEvidence && observation.Motion == MotionStatus.Climb && flow.IsAtRootBoundary && _detachPulses < 2)
            {
                if (await SendAsync(new(CombatBattleHostInputKind.Detach), ct)) _detachPulses++;
                Reason = "confirmed-climb-bounded-detach";
                return _result;
            }

            var noProgress = now - _lastProgressAt >= NoProgressDeadline && now >= _graceUntil;
            // A short disappearance after real progress is not a finish signal. The anchor
            // is the original progress timestamp; repeated misses never renew this window.
            if (observation.Target == null && (_hasGameProgress && now < _lastProgressAt + .8 || now < _lastDirectionAt + .8))
            {
                Reason = "recent-progress-awaiting-target";
                await io.DelayAsync(50, ct);
                return _result;
            }
            if (options.SeekEnabled && !_externalSearchExhausted && flow.EngagementDemand != CombatEngagementDemand.Unknown &&
                observation.Target is { Cue: SeekCueKind.DirectionIndicator, SignalCount: 1,
                    Direction: EnemyIndicatorDirection.Left or EnemyIndicatorDirection.Right or EnemyIndicatorDirection.Behind } && newEvidence)
            {
                _engagementSearch = true;
                SetPhase(Phase.Searching);
                return _result;
            }
            if (options.SeekEnabled && !_sceneLockUsed && flow.EngagementDemand != CombatEngagementDemand.Unknown &&
                observation.Scene is { PreferFixedTarget: true, AllowTargetLock: true, FixedHealthSlot: { } slot } &&
                observation.Target is { Cue: SeekCueKind.FixedTopHealth, Visual: { } fixedVisual } &&
                slot.Contains(fixedVisual, observation.Width, observation.Height) &&
                observation.Control.IsObserved && !observation.Control.KeyboardBreakoutRequested &&
                observation.Motion == MotionStatus.Normal && observation.Control.Motion == MotionStatus.Normal && newEvidence)
            {
                if (await SendAsync(new(CombatBattleHostInputKind.TargetLock) { Scene = observation.Scene }, ct)) _sceneLockUsed = true;
                return _result;
            }
            if (options.SeekEnabled && flow.EngagementDemand != CombatEngagementDemand.Unknown &&
                observation.Target is { Cue: SeekCueKind.HealthBar, SignalCount: 1, Visual: { } engagementVisual } &&
                observation.Control.IsObserved && !observation.Control.KeyboardBreakoutRequested &&
                observation.Motion is not (MotionStatus.Climb or MotionStatus.Fly) &&
                observation.Control.Motion is not (MotionStatus.Climb or MotionStatus.Fly))
            {
                var offset = AutoFightSeek.GetVisibleEnemyCameraOffset(engagementVisual, observation.Width);
                var approach = CanApproach(observation, io.BattleId, io.Clock);
                if (Math.Abs(offset) > 20 || approach)
                {
                    if (noProgress) return Stop("engagement-without-game-progress");
                    if (!newEvidence || !IsSettledObservation(observation, now))
                    { await io.DelayAsync(50, ct); return _result; }
                    if (Math.Abs(offset) > 20)
                    {
                        if (_scanPulses >= MaximumSearchPulses) return Stop("engagement-camera-budget-exhausted");
                        if (await SendAsync(new(CombatBattleHostInputKind.Camera, Math.Clamp(offset, -120, 120)), ct)) _scanPulses++;
                    }
                    else
                    {
                        if (_approachPulses >= MaximumApproachPulses) return Stop("engagement-approach-budget-exhausted");
                        if (await SendAsync(new(CombatBattleHostInputKind.Approach), ct)) _approachPulses++;
                    }
                    _engagementPending = _inputRequestId != Guid.Empty;
                    Reason = "safe-boundary-engagement";
                    return _result;
                }
            }
            if (flow.IsAtRootBoundary && now >= options.InitialBlockSeconds &&
                !_externalSearchExhausted &&
                (_finishRequested && UsesPartyFinish || noProgress || observation.Target == null && now >= _nextFinishCheck))
            {
                _finishRequested = false;
                if (noProgress && observation.Target != null)
                {
                    if (!options.SeekEnabled) return Stop("visible-target-without-progress");
                    SetPhase(Phase.Searching);
                }
                else if (UsesPartyFinish) SetPhase(Phase.BeforeParty);
                else if (options.SeekEnabled) SetPhase(Phase.Searching);
                else if (noProgress && !options.ExternalCompletionAuthority) return Stop("no-progress-without-finish-detector");
                _nextFinishCheck = now + Math.Max(.1, options.FinishCheckIntervalSeconds);
                return _result;
            }
            await flow.StepAsync(ct);
            return _result;
        }
        catch (ReengagementDeadlineException)
        {
            return Stop("reengagement-without-game-progress");
        }
        catch (Exception error)
        {
            exceptionReason = error is OperationCanceledException ? "cancelled" :
                "exception:" + (error.Data["CombatFailureCode"] as string ?? error.GetType().Name);
            try { io.EndInputEvidence(exceptionReason, error); } catch { }
            try { io.ReleaseInput(); } catch { /* 不遮蔽原始取消/视觉/输入异常。 */ }
            throw;
        }
        finally
        {
            try
            {
                string? capturePhase = null;
                if (!_terminalTraced && (_result != CombatBattleHostResult.Continue || exceptionReason != null))
                {
                    _terminalTraced = true;
                    capturePhase = _result == CombatBattleHostResult.Completed ? null : "terminal";
                    if (_result == CombatBattleHostResult.Completed && _evidenceActive)
                    { _closedEvidenceEpisode = _evidenceEpisode; _evidenceActive = false; }
                }
                else if (_finalProbe && !previousFinalProbe) capturePhase = "search-final";
                else if (_phase == Phase.Searching && previousPhase != Phase.Searching) capturePhase = "search-start";
                if (capturePhase is "search-start" or "search-final") _evidenceActive = true;
                if (capturePhase != null || previousPhase != _phase || previousCamera != CameraRequests ||
                    previousApproach != ApproachRequests || Now >= _nextTrace || exceptionReason != null || _closedEvidenceEpisode != null)
                {
                    _nextTrace = Now + .5;
                    io.Trace(new(io.BattleId, _evidenceEpisode, State, exceptionReason ?? Reason, _result,
                        _diagnosticObservation, _scanPulses, _approachPulses, Now - _lastProgressAt,
                        Math.Max(0, _motionSettlesAt - Now), _finalProbe, capturePhase)
                    {
                        InputRequest = _traceInputRequest, InputSource = _traceInputSource, InputStatus = _traceInputStatus,
                        InputKind = _traceInputKind, PartySample = _tracePartySample, PartyReason = _tracePartyReason,
                        ClosedEpisode = _closedEvidenceEpisode, ObservationGate = _traceObservationGate,
                        LastProgress = _lastProgressEvidence
                    });
                }
            }
            catch { /* 诊断失败不得改变动作、取消或清理结果。 */ }
            _closedEvidenceEpisode = null;
            if (_result == CombatBattleHostResult.Continue && exceptionReason == null && feedbackAccepted)
                try { io.ConfirmInputFeedback(_diagnosticObservation); } catch { }
            flow.RecordHostStep(Stopwatch.GetElapsedTime(started));
        }
    }

    private sealed class ReengagementDeadlineException : Exception;

    private async ValueTask<CombatFlowStep> AdvanceFlowAsync(NativeCombatFlowRunner flow, CancellationToken ct)
    {
        if (_phase != Phase.Reengaging) return await flow.StepAsync(ct);
        ct.ThrowIfCancellationRequested();
        var remaining = _lastProgressAt + NoProgressDeadline - Now;
        if (remaining <= 0) throw new ReengagementDeadlineException();
        // 本Step的协作取消沿原执行链传到动作检查、分片等待和输入前检查。
        // 使用原绝对截止剩余量，不改动作预算；父取消/更早动作期限始终优先。
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(remaining), io.Clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            var step = await flow.StepAsync(linked.Token, allowNewRound: !_reengagementDraining);
            linked.Token.ThrowIfCancellationRequested();
            return step;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // 已等待原调用清理完成，不遗留仍能输入的后台任务。
            throw new ReengagementDeadlineException();
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

    private bool Accept(CombatBattleObservation observation, out bool newEvidence, out string reason)
    {
        newEvidence = false;
        reason = "quality:" + observation.Quality;
        if (observation.Quality != CombatObservationQuality.Available) return false;
        reason = "battle-mismatch";
        if (observation.BattleId != io.BattleId) return false;
        reason = observation.Source.IsKnown ? "source-age-or-clock-invalid" : "source-unknown";
        if (!observation.Source.IsFresh(io.Clock, TimeSpan.FromMilliseconds(150))) return false;
        reason = "source-order-or-session-rejected";
        if (_lastSource.IsKnown && observation.Source != _lastSource && !observation.Source.IsAfter(_lastSource)) return false;
        reason = "input-fence-rejected";
        if (_inputFence is { } fence && !fence.Accepts(observation.Source)) return false;
        newEvidence = !_lastSource.IsKnown || observation.Source.IsAfter(_lastSource);
        if (newEvidence) _lastSource = observation.Source;
        _inputFence = null;
        reason = newEvidence ? "accepted-new-frame" : "accepted-reused-frame";
        return true;
    }

    private void ObserveProgress(CombatBattleObservation observation, double now)
    {
        var target = observation.Target;
        var damage = target?.Cue == SeekCueKind.DamageNumber;
        if (!IsSettledObservation(observation, now))
        {
            _hadDamage = damage;
            _lastDamageFingerprint = observation.CueFingerprint;
            return;
        }
        var progressed = damage && (!_hadDamage || observation.CueFingerprint != 0 &&
            observation.CueFingerprint != _lastDamageFingerprint);
        _hadDamage = damage;
        _lastDamageFingerprint = observation.CueFingerprint;
        var fixedHealth = observation.FixedTopHealth ?? (target?.Cue == SeekCueKind.FixedTopHealth ? target.Value.Visual : null);
        var health = fixedHealth ?? (target?.Cue == SeekCueKind.HealthBar ? target.Value.Visual : null);
        if (health is { } visual)
        {
            if (_progressHealthIsFixed != fixedHealth.HasValue)
            {
                _stableVisual = null;
                _healthBaselineCandidate = 0;
            }
            _progressHealthIsFixed = fixedHealth.HasValue;
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
        _hasGameProgress = true;
        if (_evidenceActive)
        {
            _closedEvidenceEpisode = _evidenceEpisode;
            _evidenceEpisode = Guid.NewGuid().ToString("N");
            _evidenceActive = false;
        }
        _lastProgressAt = now;
        _lastProgressEvidence = new(observation.Source, damage ? "new-damage-cue" : "health-progress",
            observation.Target, observation.FixedTopHealth, io.Clock.GetTimestamp());
        _reengaged = false;
        _scanPulses = _approachPulses = 0;
        _searchVerticalOffset = 0;
        _detachPulses = 0;
        _externalSearchExhausted = false;
        _behindTurnSide = 0;
        _lastDirectionAt = double.NegativeInfinity;
        _finalProbe = false; // 真实游戏进展关闭旧搜索episode，不污染下一次结束探测。
        Reason = damage ? "new-damage-cue" : "health-progress";
    }

    private async ValueTask<CombatBattleHostResult> AdvanceSearchAsync(CombatBattleObservation observation, CancellationToken ct)
    {
        if (!options.SeekEnabled) return Stop("finish-not-confirmed");
        if (observation.Target == null && Now < _lastDirectionAt + .8)
        {
            Reason = "direction-awaiting-screen-target";
            await io.DelayAsync(50, ct);
            return _result;
        }
        // 输入后的新帧不一定是稳定画面。先给场景稳定机会，不能让连续镜头
        // 不断延后进展观测的稳定期并提前用完原搜索预算。
        if (!IsSettledObservation(observation, Now))
        {
            await io.DelayAsync(50, ct);
            return _result;
        }
        // 固定Boss条仅是存在/进展证据；没有空间线索时走下方既有有界扫描。
        if (observation.Target is { } target && target.Cue != SeekCueKind.FixedTopHealth && target.Visual is { } visual)
        {
            if (target.Cue == SeekCueKind.DamageNumber)
            {
                if (TryResumeStrategy()) return _result;
                _scanPulses = MaximumSearchPulses;
            }
            else
            {
            var offset = target.Cue == SeekCueKind.DirectionIndicator
                ? DirectionStep(target, visual, observation)
                : AutoFightSeek.GetVisibleEnemyCameraOffset(visual, observation.Width);
            // 屏外提示不是可攻击目标。预算耗尽后不能在Searching/Fighting之间空转，
            // 也不能靠重新进入策略续借进展宽限期；最后一次输入仍先等稳定新帧。
            if (target.Cue == SeekCueKind.DirectionIndicator &&
                target.Direction is EnemyIndicatorDirection.Left or EnemyIndicatorDirection.Right or EnemyIndicatorDirection.Behind &&
                _scanPulses >= MaximumSearchPulses && !options.ExternalCompletionAuthority)
                return Stop("offscreen-target-search-exhausted");
            if (Math.Abs(offset) > 20 && _scanPulses < MaximumSearchPulses)
            {
                if (!await SendAsync(new(CombatBattleHostInputKind.Camera, Math.Clamp(offset, -120, 120)), ct))
                    return _result;
                _scanPulses++;
                return _result; // 包括最后一次镜头：下一次稳定新帧才判定是否耗尽。
            }
            else if (CanApproach(observation, io.BattleId, io.Clock) && _approachPulses < MaximumApproachPulses)
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
            if (observation.SearchHint is { } hint && hint.Source.IsKnown &&
                hint.Source == observation.Source && hint.Width == observation.Width && hint.Height == observation.Height)
            {
                // 未确认箭头朝向只能提供屏幕位置线索，不能成为目标或进展证据。
                var hintOffset = AutoFightSeek.GetIndicatorCameraOffset(EnemyIndicatorDirection.None,
                    hint.Visual with { IndicatorBearingDegrees = null }, observation.Width, observation.Height);
                // 位置线索只修正水平扫描；持续模糊箭头不能饿死原有上下轨道。
                var hintVerticalTarget = AutoFightSeek.GetSeekCameraVerticalTargetOffset(observation.Height,
                    _scanPulses / 4, _scanPulses % 4 + 1);
                if (!await SendAsync(new(CombatBattleHostInputKind.Camera, Math.Clamp(hintOffset, -120, 120),
                        hintVerticalTarget - _searchVerticalOffset), ct))
                    return _result;
                _searchVerticalOffset = hintVerticalTarget;
                _scanPulses++;
                Reason = "scan-unconfirmed-direction-hint";
                return _result;
            }
            var offset = AutoFightSeek.GetSeekCameraOffset(observation.Width, observation.Height,
                _scanPulses / 4, _scanPulses % 4);
            // 旧寻敌入口会先定位轨道中心；宿主没有该步骤，不能只累加轨道内的波浪差值。
            // 按本轮已发送的纵向位移进入下一采样高度，发送失败不推进轨迹。
            var verticalTarget = AutoFightSeek.GetSeekCameraVerticalTargetOffset(observation.Height,
                _scanPulses / 4, _scanPulses % 4 + 1);
            if (!await SendAsync(new(CombatBattleHostInputKind.Camera, offset.x, verticalTarget - _searchVerticalOffset), ct)) return _result;
            _searchVerticalOffset = verticalTarget;
            _scanPulses++;
            return _result;
        }
        if (_scanPulses >= MaximumSearchPulses)
        {
            _finalProbe = true;
            if (options.ExternalCompletionAuthority)
            {
                // 秘境/幽境终态归各自检测器。本次运动预算耗尽后继续策略，但不因
                // 经过时间或输入次数重开运动预算；只由新的实际目标进展重新准入。
                _externalSearchExhausted = true;
                SetPhase(Phase.Fighting);
                Reason = "external-scene-awaiting-authority-after-bounded-search";
            }
            else if (UsesPartyFinish) SetPhase(Phase.BeforeParty);
            else return Stop("bounded-search-exhausted");
        }
        return _result;
    }

    private int DirectionStep(EnemySeekDecision target, EnemySeekVisual visual, CombatBattleObservation observation)
    {
        var step = AutoFightSeek.GetIndicatorCameraOffset(target.Direction, visual, observation.Width, observation.Height);
        if (target.Direction is EnemyIndicatorDirection.Left or EnemyIndicatorDirection.Right)
            _behindTurnSide = Math.Sign(step); // Clear contrary lateral evidence may change the side.
        if (target.Direction != EnemyIndicatorDirection.Behind) return step;
        if (_behindTurnSide == 0) _behindTurnSide = Math.Sign(step);
        return _behindTurnSide * 120;
    }

    // 姿态Unknown保持Unknown；只有同帧控制检查和实际对准的目标共同允许有界试探。
    internal static bool CanApproach(CombatBattleObservation observation, Guid battleId, TimeProvider clock,
        bool selectionAssistance = false) =>
        observation.Quality == CombatObservationQuality.Available && observation.BattleId == battleId &&
        observation.Source.IsFresh(clock, TimeSpan.FromMilliseconds(150)) &&
        observation.Control.IsObserved && !observation.Control.KeyboardBreakoutRequested &&
        observation.Motion is not (MotionStatus.Climb or MotionStatus.Fly) &&
        observation.Control.Motion is not (MotionStatus.Climb or MotionStatus.Fly) &&
        observation.Target is { Cue: SeekCueKind.HealthBar, SignalCount: 1, Visual: { } visual } target &&
        // 显式选角恢复的小步不属于按距离接近，仍绑定原SelectionGoal和期限；普通寻敌必须尊重判近结果。
        (selectionAssistance || target.Action == AutoFightSeekAction.ApproachVisibleEnemy) &&
        observation.Width > 0 && observation.Height > 0 &&
        Math.Abs(target.Cue == SeekCueKind.DirectionIndicator
            ? AutoFightSeek.GetIndicatorCameraOffset(target.Direction, visual, observation.Width, observation.Height)
            : AutoFightSeek.GetVisibleEnemyCameraOffset(visual, observation.Width)) <= 20;

    private bool TryResumeStrategy()
    {
        if (_reengaged && Now - _lastProgressAt >= NoProgressDeadline) return false;
        _reengaged = true;
        _graceUntil = Now + CombatFlowPolicy.EpisodeTimeoutSeconds;
        SetPhase(Phase.Fighting);
        _inputRequestId = Guid.Empty;
        _inputRequestDeadline = 0;
        _nextFinishCheck = _graceUntil;
        Reason = "bounded-reengagement";
        return true;
    }

    private bool IsSettledObservation(CombatBattleObservation observation, double now) =>
        now >= _motionSettlesAt && (_motionSettlesAt == 0 ||
            io.Clock.GetElapsedTime(_started, observation.Source.CapturedTimestamp).TotalSeconds >= _motionSettlesAt);

    private async ValueTask<CombatBattleHostResult> AdvancePartyAsync(CombatBattleObservation observation, bool fresh,
        bool newEvidence, CancellationToken ct)
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
                _tracePartySample = _before;
                _tracePartyReason = "pre-input-sample";
                if (!_before.Source.IsFresh(io.Clock, TimeSpan.FromMilliseconds(150)))
                {
                    _tracePartyReason = _before.Source.IsKnown ? "pre-input-stale-source" : "pre-input-source-unknown";
                    await io.DelayAsync(50, ct);
                    return _result;
                }
                SetPhase(Phase.OpenParty);
                break;
            case Phase.OpenParty:
                var opened = await SendRequestAsync(new(CombatBattleHostInputKind.OpenParty), _before.Source, ct,
                    TimeSpan.FromSeconds(Math.Max(0, _partyDeadline - Now)));
                if (opened.Status != CombatBattleHostInputStatus.Sent || _result != CombatBattleHostResult.Continue)
                {
                    if (_result == CombatBattleHostResult.Continue) SetPhase(Phase.BeforeParty);
                    return _result;
                }
                _finish = new(_before, io.Clock.GetUtcNow())
                { Fence = new(_before.Source, opened.CompletedTimestamp!.Value) };
                _nextProbe = Now + probeDelay;
                _partyEvidence = false;
                SetPhase(Phase.AwaitParty);
                break;
            case Phase.AwaitParty:
                if (Now < _nextProbe) { await io.DelayAsync(50, ct); break; }
                var sample = ReadObservation(io.ObservePartyBar);
                _tracePartySample = sample;
                var freshPartySource = sample.Source.IsFresh(io.Clock, TimeSpan.FromMilliseconds(150)) &&
                    _finish!.Fence?.Accepts(sample.Source) == true;
                _tracePartyReason = !sample.Source.IsKnown ? "source-unknown" :
                    !sample.Source.IsFresh(io.Clock, TimeSpan.FromMilliseconds(150)) ? "stale-source" :
                    _finish!.Fence?.Accepts(sample.Source) != true ? "input-fence-rejected" :
                    !sample.BarVisible ? "bar-not-visible" : "awaiting-independent-confirmation";
                _partyEvidence |= sample.BarVisible && freshPartySource;
                if (freshPartySource && _finish!.Observe(sample))
                {
                    _endConfirmed = true;
                    _tracePartyReason = "confirmed";
                    Reason = "confirmed-post-input-party-bar";
                    SetPhase(Phase.CloseParty);
                }
                else if (Now >= _partyDeadline) SetPhase(Phase.CloseParty);
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
                // 已发探测先收束；只有新鲜的输入后目标及无编队栏样本允许恢复。
                // 不能把关闭UI之前的旧目标用于恢复，也不能重置原搜索/进展预算。
                if (!_partyEvidence && fresh && observation.Target != null &&
                    _finish?.Fence?.Accepts(observation.Source) == true &&
                    !_tracePartySample.BarVisible &&
                    _tracePartySample.Source.IsFresh(io.Clock, TimeSpan.FromMilliseconds(150)) &&
                    _finish.Fence.Value.Accepts(_tracePartySample.Source) && TryResumeStrategy())
                {
                    _finishRequested = false;
                    Reason = "target-returned-after-party-probe";
                    return _result;
                }
                if (_finalProbe && !_partyEvidence && _hasGameProgress && !_reengaged &&
                    observation.Target == null && Now - _lastProgressAt < NoProgressDeadline &&
                    _finish?.Fence is { } finishFence && !_tracePartySample.BarVisible &&
                    _tracePartySample.Source.IsFresh(io.Clock, TimeSpan.FromMilliseconds(150)) &&
                    finishFence.Accepts(_tracePartySample.Source))
                {
                    // AwaitParty转入CloseParty时常仍是同一目标帧；不借重复/失效帧准入。
                    // 仅在本次负样本尚新鲜时等待下一帧，不续期该样本或原进展预算。
                    if (!fresh || !newEvidence || !finishFence.Accepts(observation.Source))
                    {
                        await io.DelayAsync(50, ct);
                        return _result;
                    }
                    _reengaged = true;
                    _graceUntil = Math.Min(Now + CombatFlowPolicy.EpisodeTimeoutSeconds, _lastProgressAt + NoProgressDeadline);
                    SetPhase(Phase.Reengaging);
                    _finishRequested = false;
                    Reason = "bounded-strategy-after-unconfirmed-search";
                    return _result;
                }
                if (_finalProbe) return Stop("bounded-search-finish-unconfirmed");
                SetPhase(Phase.Searching);
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
        var intent = (_phase, input.Kind, input.SelectionGoal, input.X, input.Y);
        // 只有NotSent会留下可继续的请求。不同动作/阶段/选角目标不能继承它的身份和期限；
        // 同一意图等待新帧则必须保留原期限，不能靠每次重试续命。
        if (_inputRequestId != Guid.Empty && !SameInputIntent(intent))
        {
            CancelPendingCandidate();
            Reason = "host-input-candidate-changed";
            await io.DelayAsync(50, ct);
            return new(CombatBattleHostInputStatus.NotSent, Reason: Reason);
        }
        if (_inputRequestId == Guid.Empty)
        {
            _inputRequestId = Guid.NewGuid();
            _inputRequestIntent = intent;
            _inputRequestDecision = DecisionFor(_diagnosticObservation);
            _inputRequestSource = source;
            var seconds = Math.Min(ObservationDeadline, remaining?.TotalSeconds ?? ObservationDeadline);
            if (options.TimeoutSeconds > 0) seconds = Math.Min(seconds, options.TimeoutSeconds - Now);
            _inputRequestDeadline = io.Clock.GetTimestamp() + (long)(Math.Max(0, seconds) * io.Clock.TimestampFrequency);
            if (input.SelectionGoal != null) _inputRequestDeadline = Math.Min(_inputRequestDeadline, input.DeadlineTimestamp);
            if (Common.Ui.UiOperation.Current is { } parent)
                _inputRequestDeadline = Math.Min(_inputRequestDeadline, io.Clock.GetTimestamp() +
                    (long)Math.Floor(parent.Remaining.TotalSeconds * io.Clock.TimestampFrequency));
        }
        if (io.Clock.GetTimestamp() >= _inputRequestDeadline)
        {
            Stop("host-input-request-deadline");
            return new(CombatBattleHostInputStatus.NotSent, Reason: Reason);
        }
        input = input with { RequestId = _inputRequestId, Source = source, DeadlineTimestamp = _inputRequestDeadline };
        _traceInputRequest = input.RequestId;
        _traceInputSource = source;
        _traceInputKind = input.Kind;
        _traceInputStatus = null;
        var started = Stopwatch.GetTimestamp();
        CombatBattleHostInputResult result;
        try
        {
            // 留图只预约现有有界槽。新帧复核是业务自己的要求，诊断关闭/满槽/失败都走同一条路径。
            try { _ = io.PrepareInputEvidence(input, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* Native诊断端记录缺失；不能把诊断失败变成新的输入/重试预算。 */ }
            ct.ThrowIfCancellationRequested();
            TaskExecutionScope.ThrowIfFailed();
            result = input.Kind is CombatBattleHostInputKind.Camera or CombatBattleHostInputKind.Approach or CombatBattleHostInputKind.TargetLock &&
                (!source.IsAfter(_inputRequestSource) || source.CapturedTimestamp <= _inputRequestSource.CapturedTimestamp)
                ? new(CombatBattleHostInputStatus.NotSent, Reason: "awaiting-business-decision-frame")
                : await io.SendAsync(input, ct);
        }
        finally { CombatRuntimeMetrics.Shared.Record("host.input", Stopwatch.GetElapsedTime(started)); }
        _traceInputStatus = result.Status;
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
                if (result.PerformanceRecheck is { } recheck)
                {
                    if (input.Kind is not (CombatBattleHostInputKind.Approach or CombatBattleHostInputKind.Camera) ||
                        result.Error != null || result.NativeUncertain ||
                        result.NativeRequested < (input.Kind == CombatBattleHostInputKind.Approach ? 2 : 1) ||
                        result.NativeRequested == null || result.NativeSubmitted != result.NativeRequested ||
                        recheck.RequestId != input.RequestId || recheck.Source != source ||
                        recheck.SelectionGoal != input.SelectionGoal || recheck.DeadlineTimestamp > _inputRequestDeadline ||
                        recheck.DeadlineTimestamp <= io.Clock.GetTimestamp())
                    {
                        Stop("host-input-invalid-performance-recheck");
                        return result;
                    }
                    _performanceRecheckDeadline = recheck.DeadlineTimestamp;
                    _performanceRecheckNotBefore = input.Kind == CombatBattleHostInputKind.Camera
                        ? Math.Max(completed, result.ObservableAfterTimestamp ?? completed) + io.Clock.TimestampFrequency * 60 / 1000
                        : completed;
                    Reason = "host-input-performance-awaiting-fresh-evidence";
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

    private void CancelPendingCandidate()
    {
        _engagementPending = false;
        io.CancelInputEvidence();
        _inputRequestId = Guid.Empty;
        _inputRequestDeadline = 0;
    }

    private void SetPhase(Phase next)
    {
        if (_phase == Phase.Searching && next != Phase.Searching) EndSearchEpisode();
        _phase = next;
    }

    private void EndSearchEpisode()
    {
        _engagementSearch = false;
        _behindTurnSide = 0;
        if (_inputRequestId != Guid.Empty) CancelPendingCandidate();
        // Keep the global pulse/deadline budgets and the bounded last-seen handoff timestamp.
    }

    private CombatBattleHostResult Stop(string reason)
    {
        Reason = reason;
        try { io.EndInputEvidence(reason); } catch { }
        io.ReleaseInput();
        EndSearchEpisode();
        return _result = CombatBattleHostResult.Unconfirmed;
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        io.ReleaseInput();
        EndSearchEpisode();
    }
}
