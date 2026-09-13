using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using Fischless.GameCapture;

namespace BetterGenshinImpact.GameTask.AutoFight;

internal enum CombatObservationQuality { Available, Unavailable, Faulted }
internal readonly record struct CombatBattleObservation(CaptureFrameStamp Source, Guid BattleId,
    CombatObservationQuality Quality, EnemySeekDecision? Target, int Width, int Height, ulong CueFingerprint = 0);
internal enum CombatBattleHostResult { Continue, Completed, Unconfirmed }
internal enum CombatBattleHostInputKind { Camera, Approach, OpenParty, CloseParty }
internal readonly record struct CombatBattleHostInput(CombatBattleHostInputKind Kind,
    int X = 0, int Y = 0, bool PartyEvidence = false);

/// <summary>游戏/时钟边界；生产与回放共同驱动宿主，不在回放重写退出循环。</summary>
internal interface ICombatBattleHostIo
{
    TimeProvider Clock { get; }
    Guid BattleId { get; }
    CombatBattleObservation ObserveTarget();
    PartySetupFinishObservation ObservePartyBar();
    ValueTask SendAsync(CombatBattleHostInput input, CancellationToken ct);
    ValueTask DelayAsync(int milliseconds, CancellationToken ct);
    void ReleaseInput();
}

internal sealed record CombatBattleHostOptions
{
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
    private CombatBattleHostResult _result;
    public string Reason { get; private set; } = "starting";
    public string State => _phase.ToString();
    public long CameraRequests { get; private set; }
    public long ApproachRequests { get; private set; }
    private double Now => io.Clock.GetElapsedTime(_started).TotalSeconds;

    public async ValueTask<CombatBattleHostResult> AdvanceAsync(NativeCombatFlowRunner flow, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        try
        {
            ct.ThrowIfCancellationRequested();
            TaskExecutionScope.ThrowIfFailed();
            if (_result != CombatBattleHostResult.Continue) return _result;
            _finishRequested |= flow.TakeFinishCheckRequest();
            var now = Now;
            if (options.TimeoutSeconds > 0 && now >= options.TimeoutSeconds)
                return Stop("configured-timeout");
            // 不在原子宏或施放确认中间抢走输入；它们仍受原策略截止时间和取消约束。
            if (flow.IsAtomic || flow.HasPendingConfirmation)
            {
                await flow.StepAsync(ct);
                return _result;
            }
            if (_phase is Phase.BeforeParty or Phase.OpenParty or Phase.AwaitParty or Phase.CloseParty)
                return await AdvancePartyAsync(ct);

            var observation = ReadObservation(io.ObserveTarget);
            var fresh = Accept(observation, out var newEvidence);
            if (!fresh)
            {
                Reason = observation.Quality == CombatObservationQuality.Faulted ? "observation-faulted" : "awaiting-source-frame";
                if (observation.Quality == CombatObservationQuality.Unavailable && observation.BattleId == io.BattleId &&
                    observation.Source.IsKnown && now >= _nextNonCombatCheck)
                {
                    _nextNonCombatCheck = now + 1;
                    // 这里只请求重新取证；恢复仍须由既有协调器的新画面许可，不能让弹窗阻断其入口。
                    flow.InspectDefeat(ct);
                }
                if (now - _lastValidAt >= ObservationDeadline) return Stop(Reason);
                await io.DelayAsync(50, ct);
                return _result;
            }
            if (newEvidence)
            {
                _lastValidAt = now;
                ObserveProgress(observation, now);
            }
            else if (_phase == Phase.Searching)
            {
                await io.DelayAsync(50, ct);
                return _result;
            }
            if (_phase == Phase.Searching) return await AdvanceSearchAsync(observation, ct);

            var noProgress = now - _lastProgressAt >= NoProgressDeadline && now >= _graceUntil;
            if (flow.IsAtRootBoundary && now >= options.InitialBlockSeconds &&
                (_finishRequested && options.FinishDetectionEnabled || noProgress || observation.Target == null && now >= _nextFinishCheck))
            {
                _finishRequested = false;
                if (options.FinishDetectionEnabled) _phase = Phase.BeforeParty;
                else if (options.SeekEnabled) _phase = Phase.Searching;
                else if (noProgress) return Stop("no-progress-without-finish-detector");
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
            throw new TimeoutException("战斗宿主观察超过150ms，禁止用迟到证据准入输入");
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
                await SendAsync(new(CombatBattleHostInputKind.Camera, Math.Clamp(offset, -120, 120)), ct);
            else if (target.Cue != SeekCueKind.DamageNumber && _approachPulses < MaximumApproachPulses)
            {
                _approachPulses++;
                await SendAsync(new(CombatBattleHostInputKind.Approach), ct);
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
            await SendAsync(new(CombatBattleHostInputKind.Camera, offset.x, offset.y), ct);
        }
        _scanPulses++;
        if (_scanPulses >= MaximumSearchPulses)
        {
            _finalProbe = true;
            if (options.FinishDetectionEnabled) _phase = Phase.BeforeParty;
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
        _nextFinishCheck = _graceUntil;
        Reason = "bounded-reengagement";
        return true;
    }

    private async ValueTask<CombatBattleHostResult> AdvancePartyAsync(CancellationToken ct)
    {
        switch (_phase)
        {
            case Phase.BeforeParty:
                _before = ReadObservation(io.ObservePartyBar);
                if (!_before.Source.IsFresh(io.Clock, TimeSpan.FromMilliseconds(150))) return Stop("party-before-frame-unavailable");
                _phase = Phase.OpenParty;
                break;
            case Phase.OpenParty:
                await io.SendAsync(new(CombatBattleHostInputKind.OpenParty), ct);
                _finish = new(_before, io.Clock.GetUtcNow())
                { Fence = new(_before.Source, io.Clock.GetTimestamp()) };
                var probeDelay = Math.Clamp(options.FinishProbeDelayMilliseconds, 0, 9000) / 1000d;
                _partyDeadline = Now + Math.Min(10, Math.Max(1.2, probeDelay + .8));
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
                await io.SendAsync(new(CombatBattleHostInputKind.CloseParty, PartyEvidence: _partyEvidence), ct);
                _inputFence = new(_lastSource, io.Clock.GetTimestamp());
                if (_endConfirmed) return _result = CombatBattleHostResult.Completed;
                if (_finalProbe) return Stop("bounded-search-finish-unconfirmed");
                _phase = Phase.Searching;
                Reason = "searching-after-no-bar";
                break;
        }
        return _result;
    }

    private async ValueTask SendAsync(CombatBattleHostInput input, CancellationToken ct)
    {
        if (input.Kind == CombatBattleHostInputKind.Camera) CameraRequests++;
        if (input.Kind == CombatBattleHostInputKind.Approach) ApproachRequests++;
        await io.SendAsync(input, ct);
        _motionSettlesAt = Now + .35;
        _stableVisual = null;
        _minimumHealthWidth = _healthBaselineCandidate = 0;
        _inputFence = new(_lastSource, io.Clock.GetTimestamp());
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
