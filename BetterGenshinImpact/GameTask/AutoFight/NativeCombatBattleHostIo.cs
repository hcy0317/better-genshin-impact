using System;
using System.Collections.Generic;
using System.Linq;
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
using Fischless.WindowsInput;
using BetterGenshinImpact.Core.Input;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Model.Area;

namespace BetterGenshinImpact.GameTask.AutoFight;

/// <summary>宿主输入与策略复用同一Native Session；只有这个适配器连接游戏设备。</summary>
internal sealed class NativeCombatBattleHostIo : ICombatBattleHostIo
{
    private readonly NativeCombatFlowRunner _flow;
    private readonly NativeCombatIo? _vision;
    private readonly ICombatHostInputDevice _device;
    private readonly Func<PartySetupFinishObservation>? _partyObservation;
    private readonly Func<CombatBattleObservation>? _targetObservation;
    private bool _partyRequested, _partyEvidence;
    private CaptureFrameFence? _partyFence;
    private CaptureFrameStamp _partyEvidenceSource;
    private Guid _partyInputRequest;
    private CaptureFrameStamp _lastPartyProbeSource;
    private CaptureFrameStamp _lastAdmittedPartySource;
    private CombatDecisionEvidence? _decisionEvidence;
    private Guid _evidenceRequest;
    private long? _evidenceInputCompleted;
    private bool _evidenceInvalidated;
    private CancellationTokenSource? _reservationCancellation;
    private sealed record PendingInputResult(CombatBattleHostInput Input, CombatBattleHostInputResult Result,
        double ElapsedMs, long Deadline, long? CompletedAt);
    private PendingInputResult? _pendingInputResult;
    private Guid? _lastLoggedInputRequest;
    public bool InputEvidenceInvalidated => _evidenceInvalidated;
    public TimeProvider Clock => _device.Clock;
    public Guid BattleId => _flow.Context.BattleId;

    public NativeCombatBattleHostIo(NativeCombatFlowRunner flow, CombatScenes scenes, InputSessionOwnership.Owner? inputOwner = null)
        : this(flow, new NativeCombatHostInputDevice(inputOwner)) => _vision = new(scenes, inputOwner);

    internal NativeCombatBattleHostIo(NativeCombatFlowRunner flow, ICombatHostInputDevice device,
        Func<PartySetupFinishObservation>? partyObservation = null, Func<CombatBattleObservation>? targetObservation = null)
    { _flow = flow; _device = device; _partyObservation = partyObservation; _targetObservation = targetObservation; }

    public CombatBattleObservation ObserveTarget()
    {
        // Business perception always reads the latest publication, never a diagnostic reservation.
        if (_targetObservation != null)
        {
            var injected = _targetObservation();
            return injected;
        }
        var observation = AvatarRecognition.LatestPassiveObservation;
        var current = AvatarRecognition.PassiveCaptureGate;
        var projected = ProjectObservation(observation, current.Epoch, current.CanCapture, Clock.GetUtcNow().UtcDateTime);
        return projected;
    }

    public void ConfirmInputFeedback(CombatBattleObservation observation)
    {
        if (_evidenceInputCompleted is { } completed && _decisionEvidence?.TryRead(out var decision) == true &&
            observation.Source.IsAfter(decision.Source) && observation.Source.TimestampFrequency == Clock.TimestampFrequency &&
            observation.Source.CapturedTimestamp >= completed + Clock.TimestampFrequency * 60 / 1000 &&
            observation.Source.IsFresh(Clock, TimeSpan.FromMilliseconds(150)) && observation.BattleId == BattleId &&
            observation.Quality == CombatObservationQuality.Available)
        {
            try { SaveDecisionEvidence("host-input-feedback-accepted"); }
            finally { ClearDecisionEvidence(); }
        }
    }

    public bool PrepareInputEvidence(CombatBattleHostInput input, CancellationToken ct)
    {
        if (input.Kind is not (CombatBattleHostInputKind.Camera or CombatBattleHostInputKind.Approach)) return true;
        if (_evidenceRequest != input.RequestId)
        {
            if (_pendingInputResult?.Input.RequestId != input.RequestId) FlushInputResult();
            ClearDecisionEvidence();
            _evidenceRequest = input.RequestId;
            var scope = DiagnosticEvidenceScope.Current;
            if (scope == null)
            { try { _device.Logger.LogDebug("EVIDENCE_CAPTURE_MISSING request={Request} reason=no-run-scope", input.RequestId); } catch { } return true; }
            if (UiOperation.Current is { } parent)
                _reservationCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, parent.Token);
            _decisionEvidence = CombatDecisionEvidence.TryReserve(scope, BattleId,
                AvatarRecognition.PassiveCaptureGate.Epoch, input, Clock, _reservationCancellation?.Token ?? ct, out var rejectionReason);
            if (_decisionEvidence == null)
                try { _device.Logger.LogDebug("EVIDENCE_CAPTURE_MISSING request={Request} reason={Reason}", input.RequestId, rejectionReason); } catch { }
            return _decisionEvidence == null;
        }
        if (_decisionEvidence?.FailureReason is { } failure)
        {
            _evidenceInvalidated = true;
            try { _device.Logger.LogDebug("EVIDENCE_CAPTURE_MISSING request={Request} reason={Reason}", input.RequestId, failure); } catch { }
            _decisionEvidence.Dispose();
            _decisionEvidence = null;
        }
        // Readiness is diagnostic metadata only; the host independently rechecks a fresh frame.
        return !_evidenceInvalidated && (_decisionEvidence == null || _decisionEvidence.TryRead(out var observed) && observed.Source == input.Source);
    }

    private void BindDecisionEvidenceToInput(CombatBattleHostInput input)
    {
        if (input.Kind is not (CombatBattleHostInputKind.Camera or CombatBattleHostInputKind.Approach)) return;
        if (_evidenceRequest == input.RequestId && _decisionEvidence?.TryRead(out var observed) == true &&
            observed.Source == input.Source && observed.BattleId == BattleId) return;
        var reason = _decisionEvidence?.FailureReason ?? "business-source-not-reserved";
        try { _decisionEvidence?.Dispose(); }
        finally { _decisionEvidence = null; }
        try { _device.Logger.LogDebug("EVIDENCE_CAPTURE_MISSING request={Request} source={SourceSession}/{SourceSequence} reason={Reason}",
            input.RequestId, input.Source.SessionId, input.Source.Sequence, reason); } catch { }
    }

    public void EndInputEvidence(string reason, Exception? error = null)
    {
        try { FlushInputResult(); SaveDecisionEvidence(error?.ToString() ?? reason); }
        finally { ClearDecisionEvidence(); }
    }

    public void CancelInputEvidence()
    {
        FlushInputResult();
        ClearDecisionEvidence();
    }

    private void SaveDecisionEvidence(string detail)
    {
        if (_decisionEvidence is not { } evidence || evidence.Save(detail) || evidence.SaveAccepted) return;
        // Known failures are accounted and logged once by the owning scope. An unclaimed handoff
        // has no lease to save; retain an explicit caller-level diagnostic rather than dropping false.
        if (evidence.FailureReason == null)
            try { _device.Logger.LogDebug("EVIDENCE_CAPTURE_MISSING request={Request} reason=evidence-unavailable:terminal-lease-unavailable", _evidenceRequest); } catch { }
    }

    private void ClearDecisionEvidence()
    {
        _decisionEvidence?.Dispose();
        _decisionEvidence = null;
        _evidenceRequest = Guid.Empty;
        _evidenceInputCompleted = null;
        _evidenceInvalidated = false;
        _reservationCancellation?.Dispose();
        _reservationCancellation = null;
    }

    internal static CombatBattleObservation ProjectObservation(PassiveTargetObservation observation,
        long currentEpoch, bool canCapture, DateTime now)
    {
        var quality = observation.Quality;
        if (observation.CaptureEpoch != currentEpoch || !canCapture)
            quality = CombatObservationQuality.Unavailable;
        EnemySeekDecision? target = AutoFightSeek.TryCreatePassiveDecision(observation, now,
            out var decision, out _, out _, out var passiveGate) ? decision : null;
        if (observation.CaptureEpoch != currentEpoch) passiveGate = "capture-epoch-mismatch";
        else if (!canCapture) passiveGate = "capture-exclusive";
        return new(observation.Source, observation.BattleId, quality, target,
            observation.ImageWidth, observation.ImageHeight, observation.CueFingerprint)
        { Scene = observation.Scene, CaptureEpoch = observation.CaptureEpoch, Motion = observation.Motion, Control = observation.Control, Recognition = observation.Recognition,
            PassiveGate = passiveGate, DamageFallback = observation.DamageFallback,
            SearchHint = observation.SearchHint, FixedTopHealth = observation.FixedTopHealth };
    }

    public PartySetupFinishObservation ObservePartyBar()
    {
        using var capture = _partyObservation == null ? _vision?.Capture() : null;
        var observed = _partyObservation?.Invoke() ?? (capture == null ? default :
            AutoFightTask.ObservePartySetupBar(capture, capture.FrameStamp.Sequence));
        var admitted = _partyRequested && observed.Source.IsFresh(Clock, TimeSpan.FromMilliseconds(150)) &&
                 _partyFence?.Accepts(observed.Source) == true;
        if (_partyRequested) _lastPartyProbeSource = observed.Source;
        if (admitted) _lastAdmittedPartySource = observed.Source;
        if (admitted && observed.BarVisible)
        {
            _partyEvidence = true;
            _partyEvidenceSource = observed.Source;
        }
        if (capture != null)
        {
            DiagnosticEvidenceScope.Current?.CaptureRequestedFrames(BattleId.ToString("N"), capture);
            CapturePartyProbe(capture, observed, _partyRequested, _partyInputRequest, _partyFence, Clock, _device.Logger, BattleId, admitted);
        }
        return observed;
    }

    internal static void CapturePartyProbe(ImageRegion frame, PartySetupFinishObservation observed, bool requested,
        Guid request, CaptureFrameFence? fence, TimeProvider clock, ILogger logger, Guid battle, bool? admittedAtDecision = null)
    {
        try
        {
            // 输入前每场仅一帧，输入后按原请求去重；不另抓截图或改变完成规则。
            if (requested && request == Guid.Empty) return;
            var detail = $"battle={battle} inputRequest={(requested ? request.ToString() : "not-submitted")} barVisible={observed.BarVisible} " +
                $"fresh={observed.Source.IsFresh(clock, TimeSpan.FromMilliseconds(150))} " +
                $"fenceAccepted={fence?.Accepts(observed.Source) == true} inputCompleted={fence?.InputCompletedTimestamp}; party probe observation, not completion";
            var admitted = observed.Source == frame.FrameStamp && (admittedAtDecision ??
                (observed.Source.IsFresh(clock, TimeSpan.FromMilliseconds(150)) && fence?.Accepts(observed.Source) == true));
            detail += $" admittedAtDecision={admitted}";
            var evidence = DiagnosticEvidenceScope.Current;
            if (requested)
            {
                evidence?.RememberExactFrame("party-probe", request.ToString("N"), frame);
                if (admitted) evidence?.RememberExactFrame("party-probe-admitted", request.ToString("N"), frame);
            }
            evidence?.TryCapture(frame, "party-probe:" + (requested ? request : battle).ToString("N"),
                !requested ? "before-party-input" : !admitted ? "post-input-invalid-source" : observed.BarVisible ? "bar-visible" : "bar-not-visible", detail, logger);
        }
        catch { /* 精确原帧取证失败不改变结束判定。 */ }
    }

    internal static void CapturePartyTerminal(Guid request, CaptureFrameStamp finalSource,
        CaptureFrameStamp lastAdmittedSource, string detail, ILogger logger, IReadOnlyDictionary<string, string>? fields = null)
    {
        var evidence = DiagnosticEvidenceScope.Current;
        var identity = request.ToString("N");
        try
        {
            evidence?.CaptureExactWindow("party-probe", identity, finalSource, "party-terminal", detail, logger, fields);
            if (lastAdmittedSource.IsKnown && lastAdmittedSource != finalSource)
                evidence?.CaptureExactWindow("party-probe-admitted", identity, lastAdmittedSource, "party-last-admitted",
                    detail + "; previous admitted sample; final decision uses a different source", logger, fields);
        }
        catch { }
        finally
        {
            try { evidence?.ForgetExactFrame("party-probe", identity); evidence?.ForgetExactFrame("party-probe-admitted", identity); } catch { }
        }
    }

    private void RetirePartyEvidence(string detail, IReadOnlyDictionary<string, string>? fields = null)
    {
        if (_partyInputRequest == Guid.Empty) return;
        try
        {
            CapturePartyTerminal(_partyInputRequest, _lastPartyProbeSource, _lastAdmittedPartySource,
                detail, _device.Logger, fields ?? new Dictionary<string, string>
                {
                    ["battle"] = BattleId.ToString("N"),
                    ["partyInputRequest"] = _partyInputRequest.ToString("N"),
                    ["inputCompleted"] = _partyFence?.InputCompletedTimestamp.ToString() ?? "unknown",
                    ["sourceRole"] = "last-party-probe; not current target or battle-completion proof",
                    ["barObserved"] = _partyEvidence.ToString()
                });
        }
        catch { /* Evidence retirement must not prevent physical input release. */ }
        finally
        {
            _partyInputRequest = Guid.Empty;
            _lastPartyProbeSource = _lastAdmittedPartySource = default;
        }
    }

    public ValueTask<CombatBattleHostInputResult> SendAsync(CombatBattleHostInput input, CancellationToken ct) =>
        SendCoreAsync(input, ct, controlRecovery: false);

    public void Trace(CombatBattleHostTrace trace)
    {
        if (trace.CapturePhase == "terminal")
        {
            try { SaveDecisionEvidence(trace.Reason); }
            finally { ClearDecisionEvidence(); }
        }
        if (trace.ClosedEpisode is { } closed)
            DiagnosticEvidenceScope.Current?.EndFrameRequests(BattleId.ToString("N"), closed);
        var frame = trace.Observation;
        var detail = $"battle={trace.BattleId} episode={trace.Episode} state={trace.State} reason={trace.Reason} result={trace.Result} " +
            $"source={frame.Source.SessionId}/{frame.Source.Sequence} quality={frame.Quality} cue={frame.Target?.Cue} " +
            $"visual={frame.Target?.Visual} direction={frame.Target?.Direction} motion={frame.Motion} " +
            $"controlObserved={frame.Control.IsObserved} controlMotion={frame.Control.Motion} keyboardBreakout={frame.Control.KeyboardBreakoutRequested} " +
            $"scan={trace.ScanUsed}/24 approach={trace.ApproachUsed}/12 progressAge={trace.ProgressAge:F3} " +
            $"settleRemaining={trace.SettleRemaining:F3} finalProbe={trace.FinalProbe}";
        detail += $" sourceAgeMs={(frame.Source.IsKnown && frame.Source.TimestampFrequency == Clock.TimestampFrequency ? Clock.GetElapsedTime(frame.Source.CapturedTimestamp).TotalMilliseconds.ToString("F1") : "unavailable")} " +
            $"inputRequest={trace.InputRequest} inputSource={trace.InputSource.SessionId}/{trace.InputSource.Sequence} inputKind={trace.InputKind} inputStatus={trace.InputStatus} " +
            $"partySource={trace.PartySample.Source.SessionId}/{trace.PartySample.Source.Sequence} partyBar={trace.PartySample.BarVisible} partyReason={trace.PartyReason}";
        detail += $" passiveGate={frame.PassiveGate ?? "not-observed"} hostGate={trace.ObservationGate ?? "not-observed"} " +
            $"damageFallback={frame.DamageFallback ?? "not-run"} perception=[{frame.Recognition?.ToCompactString() ?? "not-observed"}]";
        _device.Logger.LogDebug("FIGHT_HOST_DECISION {Detail}", detail);
        if (trace.CapturePhase == "terminal")
            _device.Logger.LogWarning("FIGHT_FAILURE_DETAIL failureId={Battle}/{Episode} termination={Termination} hostGate={HostGate} passiveGate={PassiveGate} partyGate={PartyGate} perception={Perception}",
                trace.BattleId, trace.Episode, trace.Reason, trace.ObservationGate ?? "not-observed", frame.PassiveGate ?? "not-observed",
                trace.PartyReason ?? "not-observed", frame.Recognition?.ToCompactString() ?? "not-observed");
        if (trace.CapturePhase is not { } phase) return;
        var evidence = DiagnosticEvidenceScope.Current;
        if (phase == "terminal" && _partyInputRequest != Guid.Empty)
            RetirePartyEvidence(detail, EvidenceFields(trace));
        if (evidence == null)
            _device.Logger.LogDebug("EVIDENCE_CAPTURE_MISSING battle={Battle} phase={Phase} reason=no-run-scope", BattleId, phase);
        else
        {
            if (phase == "terminal")
                evidence.RequestExistingWindow(BattleId.ToString("N"), "battle:" + BattleId.ToString("N") + ":" + trace.Episode,
                    "combat-terminal", frame.Source, detail, _device.Logger, EvidenceFields(trace));
            evidence.RequestFrame(BattleId.ToString("N"), trace.Episode, phase, frame.Source, detail, _device.Logger, EvidenceFields(trace));
            if (phase == "terminal")
                evidence.CapturePendingTerminal(BattleId.ToString("N"), trace.Episode, () => _vision?.Capture());
        }
    }

    internal static IReadOnlyDictionary<string, string> EvidenceFields(CombatBattleHostTrace trace)
    {
        static string Limited(IEnumerable<string> values)
        {
            var samples = values.Take(7).ToArray();
            return string.Join(";", samples.Take(6).Select(value => value.Length > 64 ? value[..64] + "[truncated]" : value)) +
                (samples.Length > 6 ? ";more=omitted" : "");
        }
        var observation = trace.Observation;
        var recognition = observation.Recognition;
        return new Dictionary<string, string>
        {
            ["termination"] = $"state={trace.State} reason={trace.Reason} result={trace.Result}",
            ["currentTarget"] = $"source={observation.Source.SessionId}/{observation.Source.Sequence} cue={observation.Target?.Cue} visual={observation.Target?.Visual} direction={observation.Target?.Direction}",
            ["lastProgressSource"] = trace.LastProgress is { } progress
                ? $"source={progress.Source.SessionId}/{progress.Source.Sequence} capturedAt={progress.Source.CapturedAt:O} kind={progress.Kind} observedTimestamp={progress.ObservedTimestamp}"
                : "unknown:no-valid-progress-observed",
            ["lastProgressTarget"] = trace.LastProgress is { } last ? $"cue={last.Target?.Cue} visual={last.Target?.Visual} fixedHealth={last.FixedTopHealth}" : "unknown:no-valid-progress-observed",
            ["partyProbe"] = $"request={trace.InputRequest} kind={trace.InputKind} status={trace.InputStatus} source={trace.PartySample.Source.SessionId}/{trace.PartySample.Source.Sequence} bar={trace.PartySample.BarVisible} reason={trace.PartyReason}",
            ["recognitionCounts"] = recognition == null ? "unknown:not-observed" : $"raw={recognition.RawComponents} accepted={recognition.Accepted} widthRejected={recognition.HealthWidthRejected} darkChecked={recognition.DarkTrackChecked} darkAccepted={recognition.DarkTrackAccepted}",
            ["filterReasons"] = recognition == null ? "unknown:not-observed" : Limited(recognition.Rejections.Select(item => $"{item.Key}:{item.Value}")),
            ["narrowTargets"] = recognition == null ? "unknown:not-observed" : Limited(recognition.NarrowBarSamples.Select(item => $"{item.X},{item.Y},{item.Width}x{item.Height},minW={item.MinimumWidth}")),
            ["darkTrack"] = recognition == null ? "unknown:not-observed" : Limited(recognition.DarkTrackSamples),
            ["gates"] = $"host={trace.ObservationGate} passive={observation.PassiveGate} damage={observation.DamageFallback}"
        };
    }

    internal ValueTask<CombatBattleHostInputResult> SendControlAsync(CombatBattleHostInput input, CancellationToken ct)
    {
        if (input.Kind != CombatBattleHostInputKind.Breakout) throw new InvalidOperationException("控制恢复入口只允许挣脱键");
        return SendCoreAsync(input, ct, controlRecovery: true);
    }

    private async ValueTask<CombatBattleHostInputResult> SendCoreAsync(CombatBattleHostInput input, CancellationToken ct, bool controlRecovery)
    {
        BindDecisionEvidenceToInput(input);
        if (_pendingInputResult?.Input.RequestId != input.RequestId) FlushInputResult();
        var requestedAt = Clock.GetTimestamp();
        var inputAttempted = false;
        long? completedAt = null;
        double? downMs = null, holdMs = null, upMs = null, remainingBeforeHold = null;
        var holdMilliseconds = 0;
        InputDispatchCapture? nativeCapture = null;
        UiOperation? businessParent = null;
        var businessDeadline = input.DeadlineTimestamp;
        var releasedCompletely = false;
        var performanceOverrun = false;
        var result = new CombatBattleHostInputResult(CombatBattleHostInputStatus.NotSent);
        try
        {
        ct.ThrowIfCancellationRequested();
        // 取得输入排他权会暂停被动发布；先保留本请求的世界帧，原生输入前仍重验帧龄/控制。
        var resetObservation = input.Kind == CombatBattleHostInputKind.CameraReset ? ObserveTarget() : default;
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
            businessParent = UiOperation.Current;
            var parentObservedAt = Clock.GetTimestamp();
            if (businessParent != null)
                businessDeadline = Math.Min(businessDeadline, parentObservedAt +
                    (long)Math.Floor(businessParent.Remaining.TotalSeconds * Clock.TimestampFrequency));
            using var operation = UiOperation.Begin("combat-host-input", TimeSpan.FromMilliseconds(Math.Min(150, remaining)), token, _device.Logger, Clock);
            operation.RecordDispatch(dispatch);
            operation.Check();
            using var nativeScope = _device.BeginNativeCapture(() =>
            {
                operation.Check();
                if (input.Kind != CombatBattleHostInputKind.CloseParty &&
                    !input.Source.IsFresh(Clock, TimeSpan.FromMilliseconds(150)))
                    throw new TimeoutException("窗口准备后源帧已过期，不能进入原生输入");
            });
            nativeCapture = nativeScope;
            bool CompletePartyInput() => nativeScope is { Uncertain: false, Requested: > 0 } &&
                nativeScope.Requested == nativeScope.Submitted &&
                (input.Kind == CombatBattleHostInputKind.OpenParty && nativeScope.Requested >= 2 ||
                 input.Kind == CombatBattleHostInputKind.CloseParty && input.PartyEvidence && nativeScope.Requested >= 4);
            void PartyPulse(Action pulse)
            {
                try { pulse(); }
                catch (TimeoutException error)
                {
                    // Evaluate after stack unwinding: an exception filter runs
                    // before PulseCore finally has submitted its matching up.
                    if (!operation.IsOwnDeadlineFailure(error) || !CompletePartyInput()) throw;
                    // PulseCore has already released the key. Only this local
                    // deadline may proceed to the original post-input recheck.
                }
            }
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
                case CombatBattleHostInputKind.TargetLock:
                    if (input.Scene is not { PreferFixedTarget: true, AllowTargetLock: true, FixedHealthSlot: not null })
                        throw new InvalidOperationException("场景未明确授权固定目标定位");
                    inputAttempted = true;
                    using (operation.Measure(UiOperationPhase.NativeInput)) _device.PressTargetLock();
                    break;
                case CombatBattleHostInputKind.CameraReset:
                    if (_partyRequested || !CombatBattleHost.CanResetCamera(resetObservation, BattleId, Clock) ||
                        !(resetObservation.Source == input.Source || resetObservation.Source.IsAfter(input.Source)))
                    {
                        result = new(CombatBattleHostInputStatus.NotSent, Reason: "camera-reset-world-frame-not-admitted");
                        return;
                    }
                    // 观察与窗口准备也占原生150ms窗口，持键前留足40ms和释放余量。
                    operation.Check();
                    if (operation.Remaining.TotalMilliseconds < 50)
                    {
                        result = new(CombatBattleHostInputStatus.NotSent, Reason: "camera-reset-hold-budget-insufficient");
                        return;
                    }
                    inputAttempted = true;
                    try
                    {
                        using (operation.Measure(UiOperationPhase.NativeInput)) _device.SetCameraReset(true);
                        using (operation.Measure(UiOperationPhase.ExplicitWait)) await _device.DelayAsync(40, token);
                    }
                    finally
                    {
                        var beforeUpRequested = nativeScope?.Requested ?? 0;
                        var beforeUpSubmitted = nativeScope?.Submitted ?? 0;
                        using (operation.Measure(UiOperationPhase.NativeInput)) _device.SetCameraReset(false);
                        releasedCompletely = nativeScope != null && !nativeScope.Uncertain &&
                            nativeScope.Requested > beforeUpRequested &&
                            nativeScope.Submitted - beforeUpSubmitted == nativeScope.Requested - beforeUpRequested;
                    }
                    break;
                case CombatBattleHostInputKind.Camera:
                    inputAttempted = true;
                    using (operation.Measure(UiOperationPhase.NativeInput))
                        _device.MoveCamera(input.X, input.Y);
                    break;
                case CombatBattleHostInputKind.Approach:
                    inputAttempted = true;
                    var downStarted = Clock.GetTimestamp();
                    try
                    {
                        using (operation.Measure(UiOperationPhase.NativeInput))
                            _device.MoveForward(true);
                        downMs = Clock.GetElapsedTime(downStarted).TotalMilliseconds;
                        remainingBeforeHold = operation.Remaining.TotalMilliseconds;
                        // 焦点/原生提交也占用同一150ms预算；为释放预留余量，不续期。
                        holdMilliseconds = Math.Min(100, Math.Max(0, (int)Math.Floor(remainingBeforeHold.Value) - 10));
                        var holdStarted = Clock.GetTimestamp();
                        using (operation.Measure(UiOperationPhase.ExplicitWait))
                            if (holdMilliseconds > 0) await _device.DelayAsync(holdMilliseconds, token);
                        holdMs = Clock.GetElapsedTime(holdStarted).TotalMilliseconds;
                    }
                    finally
                    {
                        var upStarted = Clock.GetTimestamp();
                        var beforeUpRequested = nativeScope?.Requested ?? 0;
                        var beforeUpSubmitted = nativeScope?.Submitted ?? 0;
                        try
                        {
                            using (operation.Measure(UiOperationPhase.NativeInput))
                                _device.MoveForward(false);
                            releasedCompletely = nativeScope != null && !nativeScope.Uncertain &&
                                nativeScope.Requested > beforeUpRequested &&
                                nativeScope.Submitted - beforeUpSubmitted == nativeScope.Requested - beforeUpRequested;
                        }
                        finally
                        {
                            upMs = Clock.GetElapsedTime(upStarted).TotalMilliseconds;
                        }
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
                        PartyPulse(_device.PressParty);
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
                    using (operation.Measure(UiOperationPhase.NativeInput)) PartyPulse(() => CloseParty(input.PartyEvidence, () =>
                    {
                        token.ThrowIfCancellationRequested();
                        TaskExecutionScope.ThrowIfFailed();
                        businessParent?.Check();
                        operation.Check();
                    }));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(input));
            }
            completedAt = Clock.GetTimestamp();
            result = new(CombatBattleHostInputStatus.Sent, completedAt);
            // 完整执行块后的局部性能超标不是输入未知。先重验业务/父期限，
            // 且只有真实down/up回执完整时才可转入新帧复核；不豁免任何执行异常。
            if ((input.Kind == CombatBattleHostInputKind.Camera ||
                 (input.Kind is CombatBattleHostInputKind.Approach or CombatBattleHostInputKind.CameraReset) &&
                 releasedCompletely && nativeScope is { Requested: >= 2 } ||
                 CompletePartyInput()) &&
                nativeScope is { Uncertain: false, Requested: > 0 } && nativeScope.Requested == nativeScope.Submitted &&
                operation.Remaining <= TimeSpan.Zero)
            {
                token.ThrowIfCancellationRequested();
                TaskExecutionScope.ThrowIfFailed();
                businessParent?.Check();
                if (Clock.GetTimestamp() < businessDeadline)
                {
                    performanceOverrun = true;
                    return;
                }
            }
            operation.Check();
        }
        if (input.SelectionGoal != null)
        {
            if (!await _flow.RunSelectionOperationAsync(input, Dispatch, ct))
                result = new(CombatBattleHostInputStatus.NotSent, Reason: "selection-awaiting-post-input-frame");
        }
        else await (controlRecovery ? _flow.RunControlOperationAsync(input.RequestId, Dispatch, ct)
            : _flow.RunHostOperationAsync(Dispatch, ct));
        if (performanceOverrun)
        {
            // 包含owner/观察scope释放后的边界；任何释放异常仍走原失败处理。
            ct.ThrowIfCancellationRequested();
            TaskExecutionScope.ThrowIfFailed();
            businessParent?.Check();
            if (Clock.GetTimestamp() >= businessDeadline)
                throw new TimeoutException("辅助输入完成后业务期限已到，不能继续复核");
        }
        }
        catch (OperationCanceledException error)
        {
            RecordInterruptedAttempt(error);
            throw;
        }
        catch (Exception error)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                TaskExecutionScope.ThrowIfFailed();
            }
            catch (Exception terminalError)
            {
                RecordInterruptedAttempt(terminalError);
                throw;
            }
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
        if (nativeCapture != null)
        {
            var actual = CombatNativeInput.Classify(nativeCapture, result.Error, completedAt ?? Clock.GetTimestamp());
            result = actual with
            {
                NativeRequested = nativeCapture.Requested, NativeSubmitted = nativeCapture.Submitted,
                NativeUncertain = nativeCapture.Uncertain,
                TransportRequested = nativeCapture.TransportCalls, TransportAcknowledged = nativeCapture.TransportAcknowledged,
                ObservableAfterTimestamp = Clock.GetTimestamp(),
                Reason = actual.Reason ?? result.Reason
            };
        }
        if (performanceOverrun && result.Status == CombatBattleHostInputStatus.Sent && result.Error == null)
            result = result with
            {
                Reason = "host-input-complete-after-performance-budget",
                PerformanceRecheck = new(input.RequestId, input.Source, businessDeadline, input.SelectionGoal)
            };
        if (result.Status == CombatBattleHostInputStatus.Sent)
        {
            if (input.Kind == CombatBattleHostInputKind.OpenParty)
            {
                RetirePartyEvidence("previous party request retired before opening a new probe");
                _partyRequested = true;
                _partyInputRequest = input.RequestId;
                _lastPartyProbeSource = default;
                _lastAdmittedPartySource = default;
                _partyEvidence = false;
                AutoFightTask.LastFightFinishCheckTime = DateTime.Now;
                _partyFence = new(input.Source, result.CompletedTimestamp!.Value);
            }
            else if (input.Kind == CombatBattleHostInputKind.CloseParty)
            {
                try { DiagnosticEvidenceScope.Current?.ForgetExactFrame("party-probe", _partyInputRequest.ToString("N")); } catch { }
                try { DiagnosticEvidenceScope.Current?.ForgetExactFrame("party-probe-admitted", _partyInputRequest.ToString("N")); } catch { }
                _partyInputRequest = Guid.Empty;
                _lastPartyProbeSource = _lastAdmittedPartySource = default;
                _partyRequested = _partyEvidence = false;
                _partyFence = null;
                _partyEvidenceSource = default;
            }
        }
        if (input.SelectionGoal != null) _flow.ObserveSelectionAssistance(input, result);
        try { CombatInputFeedbackEvidence.Arm(DiagnosticEvidenceScope.Current, BattleId,
            AvatarRecognition.PassiveCaptureGate.Epoch, input, result, Clock, _device.Logger); } catch { }
        if (result.Status != CombatBattleHostInputStatus.NotSent)
        {
            if (result.Error != null || result.Status != CombatBattleHostInputStatus.Sent)
            { try { SaveDecisionEvidence(result.Error?.ToString() ?? result.Reason ?? "input-failed"); } finally { ClearDecisionEvidence(); } }
            else _evidenceInputCompleted = result.CompletedTimestamp;
        }
        var elapsedMs = Clock.GetElapsedTime(requestedAt).TotalMilliseconds;
        if (input.Kind == CombatBattleHostInputKind.Approach)
        {
            // 回执及完成时刻已经固定；同步日志即使很慢也不能改变输入判定。
            try { _device.Logger.LogDebug("HOST_APPROACH_TIMING request={Request} downMs={DownMs} holdRequestedMs={HoldRequestedMs} holdMs={HoldMs} upMs={UpMs} remainingBeforeHoldMs={RemainingBeforeHoldMs}",
                input.RequestId, downMs, holdMilliseconds, holdMs, upMs, remainingBeforeHold); }
            catch { }
        }
        RecordInputResult(new(input, result, elapsedMs, businessDeadline, completedAt));
        return result;

        void RecordInterruptedAttempt(Exception error)
        {
            // This attempt never reaches the normal result path. Replace any older NotSent
            // with the current exception/receipt before preserving the original throw.
            try
            {
                _pendingInputResult = null;
                var terminal = new CombatBattleHostInputResult(
                    completedAt != null ? CombatBattleHostInputStatus.Failed :
                    inputAttempted ? CombatBattleHostInputStatus.Unknown : CombatBattleHostInputStatus.NotSent,
                    completedAt, "host-input-interrupted", error)
                {
                    NativeRequested = nativeCapture?.Requested ?? 0,
                    NativeSubmitted = nativeCapture?.Submitted ?? 0,
                    NativeUncertain = nativeCapture?.Uncertain ?? inputAttempted,
                    TransportRequested = nativeCapture?.TransportCalls ?? 0,
                    TransportAcknowledged = nativeCapture?.TransportAcknowledged ?? 0
                };
                LogInputResult(new(input, terminal, Clock.GetElapsedTime(requestedAt).TotalMilliseconds,
                    businessDeadline, completedAt));
            }
            catch { /* Diagnostics must not replace the exception being rethrown. */ }
        }
    }

    private void RecordInputResult(PendingInputResult pending)
    {
        if (pending.Result.Status == CombatBattleHostInputStatus.NotSent)
        {
            _pendingInputResult = pending; // One bounded slot, replaced by the same request's next attempt.
            try { _device.Logger.LogDebug("HOST_INPUT_ATTEMPT request={Request} status={Status} reason={Reason}",
                pending.Input.RequestId, pending.Result.Status, pending.Result.Reason); } catch { }
            return;
        }
        _pendingInputResult = null;
        LogInputResult(pending);
    }

    private void FlushInputResult()
    {
        var pending = _pendingInputResult;
        _pendingInputResult = null;
        if (pending != null) LogInputResult(pending);
    }

    private void LogInputResult(PendingInputResult pending)
    {
        var (input, result, elapsedMs, businessDeadline, completedAt) = pending;
        if (_lastLoggedInputRequest == input.RequestId) return;
        _lastLoggedInputRequest = input.RequestId;
        try
        {
            static string Limited(string? text, int max) => text == null ? "none" : text.Length <= max ? text : text[..max] + "[truncated]";
            _device.Logger.LogDebug("HOST_INPUT_RESULT request={Request} kind={Kind} status={Status} reason={Reason} source={Source} completedAt={CompletedAt} elapsedMs={ElapsedMs:F3} deadline={Deadline} requested={Requested} submitted={Submitted} uncertain={Uncertain} transportRequested={TransportRequested} transportAcknowledged={TransportAcknowledged} errorType={ErrorType} errorMessage={ErrorMessage} errorStack={ErrorStack}",
                input.RequestId, input.Kind, result.Status, result.Reason, input.Source, completedAt,
                elapsedMs, businessDeadline, result.NativeRequested, result.NativeSubmitted, result.NativeUncertain,
                result.TransportRequested, result.TransportAcknowledged, result.Error?.GetType().FullName,
                Limited(result.Error?.Message, 1024), Limited(result.Error?.StackTrace, 4096));
        }
        catch { /* 诊断输出不能改变已判定的输入结果。 */ }
    }

    private void CloseParty(bool evidence, Action admitNext)
    {
        _device.PressDrop();
        if (evidence)
        {
            admitNext();
            _device.PressParty();
        }
    }

    public ValueTask DelayAsync(int milliseconds, CancellationToken ct) => _device.DelayAsync(milliseconds, ct);
    public void ReleaseInput()
    {
        try { CombatInputFeedbackEvidence.End(DiagnosticEvidenceScope.Current, BattleId); } catch { }
        try { FlushInputResult(); SaveDecisionEvidence("host-release-before-feedback"); }
        finally { ClearDecisionEvidence(); }
        // A negative party probe may resume fighting. Persist its actual pixels now,
        // then return both exact slots to selection/input evidence for that continuation.
        RetirePartyEvidence("party probe ended at host input release; no confirmed battle completion");
        // 这里只释放本owner持有的物理键；UI转换属于业务输入，未知结果不得在cleanup重放。
        _partyRequested = _partyEvidence = false;
        _partyFence = null;
        _partyEvidenceSource = default;
        _flow.ReleaseHostInput();
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

    internal static CombatBattleHost CreateForExternalScene(NativeCombatFlowRunner flow, CombatScenes scenes,
        InputSessionOwnership.Owner? inputOwner = null)
    {
        var config = TaskContext.Instance().Config.AutoFightConfig;
        var seek = AutoDomain.AutoDomainFightSeekOptions.FromAutoFightConfig(config);
        return new(new NativeCombatBattleHostIo(flow, scenes, inputOwner), new()
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
    InputDispatchCapture? BeginNativeCapture(Action beforeFirstNative) => null;
    void MoveCamera(int x, int y);
    void SetCameraReset(bool down) => throw new NotSupportedException("此输入设备未提供寻敌镜头回正");
    void MoveForward(bool down);
    void PressDrop();
    void PressParty();
    void PressTargetLock() => throw new NotSupportedException("此场景设备未提供目标定位");
    void PressBreakout() => throw new NotSupportedException("此输入设备未实现已验证的键盘挣脱");
    ValueTask DelayAsync(int milliseconds, CancellationToken ct);
}

internal sealed class NativeCombatHostInputDevice(InputSessionOwnership.Owner? inputOwner = null) : ICombatHostInputDevice
{
    public TimeProvider Clock => TimeProvider.System;
    public ILogger Logger => TaskControl.Logger;
    public void PrepareInput() => TaskControl.CheckAndSleep(0);
    public InputDispatchCapture BeginNativeCapture(Action beforeFirstNative) => new(beforeFirstNative);
    public void MoveCamera(int x, int y) => Send(() => InputHub.Foreground.Mouse.MoveMouseBy(x, y));
    public void SetCameraReset(bool down) => Send(() =>
    {
        if (down) InputHub.Foreground.Mouse.MiddleButtonDown();
        else InputHub.Foreground.Mouse.MiddleButtonUp();
    });
    public void MoveForward(bool down) => Send(() => InputHub.Foreground.SimulateAction(GIActions.MoveForward,
        down ? KeyType.KeyDown : KeyType.KeyUp));
    public void PressDrop() => Send(() => InputHub.Foreground.SimulateAction(GIActions.Drop));
    public void PressParty() => Send(() => InputHub.Foreground.SimulateActionPulse(GIActions.OpenPartySetupScreen));
    public void PressTargetLock() => Send(() => InputHub.Foreground.Mouse.MiddleButtonClick());
    public void PressBreakout() => Send(() => InputHub.Foreground.Keyboard.KeyPress(Vanara.PInvoke.User32.VK.VK_SPACE));
    public ValueTask DelayAsync(int milliseconds, CancellationToken ct) => new(Task.Delay(milliseconds, ct));
    private void Send(Action send) { if (inputOwner == null) send(); else inputOwner.Invoke(send); }
}
