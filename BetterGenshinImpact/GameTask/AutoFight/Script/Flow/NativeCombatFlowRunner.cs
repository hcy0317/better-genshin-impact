using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoFight.SkillData;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Model.Area;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Fischless.GameCapture;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

/// <summary>宿主拥有结束检测；此适配器只驱动一份本场流程，不启动后台输入循环。</summary>
internal sealed partial class NativeCombatFlowRunner : IDisposable
{
    internal const int SwitchAttempts = 10;
    private readonly ICombatFlowGame _game;
    private readonly CombatFlowExecution? _execution;
    private readonly JsonCombatFlowExecution? _jsonExecution;
    private readonly ILogger _diagnosticLogger;
    private readonly CombatFlowDiagnosticWriter _diagnosticWriter;
    private readonly Func<CombatFlowStatistics> _readDiagnosticStatistics;
    private readonly CombatRuntimeMetrics _outerMetrics = new();
    private string? _pendingDiagnosticBoundary;
    private IDisposable? _exclusive;
    private bool _disposed;
    private int _advancing;
    private int _consecutiveFailedPasses;
    private long _lastFailedAttemptRevision;
    private bool _hasFailedPass;
    private double _lastNewFailureAt;
    private bool _failureFinishCheckRequested;
    private string? _lastReportedMaintenanceDecision;
    private string? MaintenanceDecision => _execution?.LastMaintenanceDecision ?? _jsonExecution?.LastMaintenanceDecision;
    public CombatFlowContext Context => _execution?.Context ?? _jsonExecution!.Context;
    public CombatFlowStatistics RuntimeStatistics
    {
        get
        {
            var statistics = _execution?.RuntimeStatistics ?? _jsonExecution!.RuntimeStatistics;
            return statistics with { Populations = statistics.Populations.Concat(_outerMetrics.SnapshotPopulation())
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) };
        }
    }
    internal void RecordHostStep(TimeSpan elapsed) => _outerMetrics.Record("host-step-total-including-wait", elapsed);
    public bool IsAtomic => _execution?.IsAtomic ?? _jsonExecution!.IsAtomic;
    public bool HasPendingConfirmation => _execution?.HasPendingConfirmation ?? _jsonExecution!.HasPendingConfirmation;
    public bool HasAwaitingObservation => (_game is NativeGame { HasDefeatProbe: true } or NativeGame { HasSelection: true } or NativeGame { PendingControl: not null } or NativeGame { IsPreparingVision: true }) ||
        (_execution?.HasAwaitingObservation ?? _jsonExecution!.HasAwaitingObservation);
    public bool IsAtRootBoundary => _execution?.IsAtRootBoundary ?? _jsonExecution!.IsAtRootBoundary;
    internal (Guid Goal, long Deadline)? SelectionAssistance => !_disposed && Volatile.Read(ref _advancing) == 0 &&
        !IsAtomic && !HasPendingConfirmation && _game is NativeGame native ? native.SelectionAssistance : null;
    internal ValueTask<bool> RunSelectionOperationAsync(CombatBattleHostInput request, Func<CancellationToken, ValueTask> operation, CancellationToken ct)
    {
        if (request.Kind != CombatBattleHostInputKind.Approach || SelectionAssistance?.Goal != request.SelectionGoal ||
            _game is not NativeGame native)
            throw new InvalidOperationException("选角辅助未取得当前目标和同一输入owner的许可");
        return native.RunSelectionOperationAsync(request, operation, ct);
    }
    internal void ObserveSelectionAssistance(CombatBattleHostInput request, CombatBattleHostInputResult receipt)
    {
        if (_game is NativeGame native) native.ObserveSelectionAssistance(request, receipt);
    }
    internal ValueTask RunHostOperationAsync(Func<CancellationToken, ValueTask> operation, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Volatile.Read(ref _advancing) != 0) throw new InvalidOperationException("普通宿主不能打断正在推进的内核");
        if (IsAtomic || HasPendingConfirmation || HasAwaitingObservation)
            throw new InvalidOperationException("宿主不能中断原子宏或待确认施放");
        return _game is NativeGame native
            ? native.RunHostOperationAsync(Context.BattleId, operation, ct)
            : operation(ct);
    }
    internal void ReleaseHostInput()
    {
        if (_game is NativeGame native) native.ReleaseHeldInput();
        else _game.ReleaseHeldInput();
    }
    internal void InspectDefeat(CancellationToken ct) => _game.CheckDefeated(ct);
    public bool TakeFinishCheckRequest()
    {
        var requested = _execution?.TakeFinishCheckRequest() ?? _jsonExecution!.TakeFinishCheckRequest();
        var failedPass = _failureFinishCheckRequested;
        _failureFinishCheckRequested = false;
        return requested || failedPass;
    }

    private NativeCombatFlowRunner(CombatFlowProgram program, CombatScenes scenes,
        CombatScriptExecutionPurpose purpose = CombatScriptExecutionPurpose.Combat)
        : this(program, new NativeGame(new NativeCombatIo(scenes)) { Purpose = purpose }, null) { }

    private NativeCombatFlowRunner(CombatFlowProgram program, ICombatFlowGame game, TimeProvider? clock, ILogger? logger = null)
    {
        _diagnosticLogger = logger ?? (game is NativeGame native ? native.DiagnosticLogger : NullLogger.Instance);
        _diagnosticWriter = new(_diagnosticLogger, clock);
        _readDiagnosticStatistics = () => RuntimeStatistics;
        _game = game;
        _execution = new(program, _game, clock);
        foreach (var source in program.Sources) TraceStrategy("TXT", source.Path, source.TextHash);
        if (game is NativeGame configured) configured.NeedsBurstVision = program.NeedsBurstVision();
        if (game is NativeGame)
            foreach (var diagnostic in program.Diagnostics) _diagnosticLogger.LogWarning("战斗策略数据：{Diagnostic}", diagnostic);
    }

    internal static NativeCombatFlowRunner Create(CombatFlowProgram program, INativeCombatIo io) =>
        new(program, new NativeGame(io), io.Clock);

    internal static NativeCombatFlowRunner Create(IReadOnlyList<CombatCommand> commands, INativeCombatIo io, bool loop,
        CombatScriptExecutionMode mode = CombatScriptExecutionMode.RequiredSequence, LegacyGuardianOptions? guardian = null,
        CombatScriptExecutionPurpose purpose = CombatScriptExecutionPurpose.Combat)
    {
        var script = LegacyCombatFlowAdapter.Prepare(commands, io.Actors.Select(actor => actor.Name), loop, mode, guardian);
        var program = CombatFlowProgram.Compile(script);
        if (program.Loop && !loop) throw new InvalidOperationException("单次路径策略没有战斗结束宿主，不能运行 loop=battle");
        program.AllowHostLoop(loop);
        if (loop && purpose == CombatScriptExecutionPurpose.Pathing) throw new InvalidOperationException("正式战斗不能使用路径交互目的");
        return new(program, new NativeGame(io) { Purpose = purpose }, io.Clock);
    }

    internal static ICombatFlowGame CreateAdapter(INativeCombatIo io) => new NativeGame(io);

    internal Task PrepareVisionBeforeEntryAsync(CancellationToken ct) => _game is NativeGame native
        ? native.PrepareVisionBeforeEntryAsync(ct) : Task.CompletedTask;

    internal static NativeCombatFlowRunner Create(CombatFlowProgram program, ICombatFlowGame game, TimeProvider? clock = null, ILogger? logger = null) =>
        new(program, game, clock, logger);

    private NativeCombatFlowRunner(JsonCombatStrategy strategy, ICombatFlowGame game,
        SkillCatalogSnapshot? database, TimeProvider? clock, ILogger? logger = null, LegacyGuardianOptions? guardian = null)
    {
        _diagnosticLogger = logger ?? (game is NativeGame native ? native.DiagnosticLogger : NullLogger.Instance);
        _diagnosticWriter = new(_diagnosticLogger, clock);
        _readDiagnosticStatistics = () => RuntimeStatistics;
        _game = game;
        _jsonExecution = new(strategy, game, database, clock, (game as NativeGame)?.ActorNames, guardian);
        TraceStrategy("JSON", strategy.SourceFile, strategy.SourceTextSha256);
        if (game is NativeGame configured) configured.NeedsBurstVision = _jsonExecution.NeedsBurstVision;
        if (game is NativeGame)
            foreach (var diagnostic in _jsonExecution.Diagnostics) _diagnosticLogger.LogWarning("战斗策略数据：{Diagnostic}", diagnostic);
    }

    internal static NativeCombatFlowRunner? Create(JsonCombatStrategy strategy, ICombatFlowGame game,
        SkillCatalogSnapshot? database = null, TimeProvider? clock = null, ILogger? logger = null, LegacyGuardianOptions? guardian = null) =>
        new(strategy, game, database, clock, logger, guardian);

    private void TraceStrategy(string format, string? path, string? textHash)
    {
        try
        {
            _diagnosticLogger.LogInformation("FIGHT_STRATEGY battle={Battle} executor=NativeCombatFlowRunner format={Format} path={Path} parsedUtf8TextSha256={TextHash} assembly={Version}",
                Context.BattleId, format, path ?? "in-memory", textHash ?? "unavailable", typeof(NativeCombatFlowRunner).Assembly.GetName().Version);
        }
        catch { /* 来源日志不能改变已编译策略。 */ }
    }

    internal static NativeCombatFlowRunner? Create(JsonCombatStrategy strategy, ICombatFlowGame game,
        Func<SkillCatalogSnapshot> readSnapshot, TimeProvider? clock = null, ILogger? logger = null) =>
        new(strategy, game, ReadSnapshot(readSnapshot, logger ?? NullLogger.Instance), clock, logger);

    public static NativeCombatFlowRunner? Create(JsonCombatStrategy strategy, CombatScenes scenes, AutoFightParam? param = null)
    {
        var io = new NativeCombatIo(scenes);
        var guardian = param == null ? null : LegacyGuardianOptions.From(param, io.Actors);
        var runner = Create(strategy, new NativeGame(io), ReadSnapshot(), logger: Logger,
            guardian: guardian == null ? null : guardian with { SkipGuardianBody = false })!;
        try
        {
            ValidateActors(runner._jsonExecution!.Actors, scenes);
            return runner;
        }
        catch { runner.Dispose(); throw; }
    }

    public static NativeCombatFlowRunner Create(IReadOnlyList<CombatCommand> commands, CombatScenes scenes, bool loop,
        CombatScriptExecutionMode mode = CombatScriptExecutionMode.RequiredSequence, AutoFightParam? param = null,
        CombatScriptExecutionPurpose purpose = CombatScriptExecutionPurpose.Combat)
    {
        var script = LegacyCombatFlowAdapter.Prepare(commands, scenes.GetAvatars().Select(avatar => avatar.Name), loop,
            loop ? CombatScriptExecutionMode.LegacyPartyTemplate : mode,
            param == null ? null : LegacyGuardianOptions.From(param, new NativeCombatIo(scenes).Actors));
        var program = CombatFlowProgram.Compile(script, ReadSnapshot());
        ValidateActors(program.Actors, scenes);
        if (program.Loop && !loop) throw new InvalidOperationException("单次路径策略没有战斗结束宿主，不能运行 loop=battle");
        program.AllowHostLoop(loop);
        if (loop && purpose == CombatScriptExecutionPurpose.Pathing) throw new InvalidOperationException("正式战斗不能使用路径交互目的");
        return new(program, scenes, purpose);
    }

    private static SkillCatalogSnapshot? ReadSnapshot() =>
        ReadSnapshot(() => CombatSkillCatalog.Default.Store.ReadSnapshot(), Logger);

    private static SkillCatalogSnapshot? ReadSnapshot(Func<SkillCatalogSnapshot> readSnapshot, ILogger logger)
    {
        SkillCatalogSnapshot? snapshot = null;
        try { snapshot = readSnapshot(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or Newtonsoft.Json.JsonException)
        {
            logger.LogWarning("技能数据库不可用，尝试策略头部时间定义：{Reason}", exception.Message);
        }
        return snapshot;
    }

    private static void ValidateActors(IEnumerable<string> actors, CombatScenes scenes)
    {
        var missing = actors.Where(name => name != CombatScriptParser.CurrentAvatarName)
            .Except(scenes.GetAvatars().Select(avatar => avatar.Name)).ToArray();
        if (missing.Length != 0) throw new InvalidOperationException("增强策略的动作/接球角色不在当前队伍：" + string.Join("、", missing));
    }

    public void Step(CancellationToken ct) => StepAsync(ct).AsTask().GetAwaiter().GetResult();

    public ValueTask<CombatFlowStep> StepAsync(CancellationToken ct) => StepAsync(ct, allowNewRound: true);

    internal async ValueTask<CombatFlowStep> StepAsync(CancellationToken ct, bool allowNewRound)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.CompareExchange(ref _advancing, 1, 0) != 0)
            throw new InvalidOperationException("同一战斗内核不能并发推进两步");
        var outerStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        var phaseStarted = outerStarted;
        var phase = "preparation";
        void ChangePhase(string next)
        {
            _outerMetrics.Record("native-" + phase + "-including-wait", System.Diagnostics.Stopwatch.GetElapsedTime(phaseStarted));
            phaseStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            phase = next;
        }
        using var realtime = new RecognitionReadinessScope();
        try
        {
            ct.ThrowIfCancellationRequested();
            if (_game is NativeGame { PendingControl: not null } controlled)
            {
                phase = "control"; // 未进入模型准备，不能记一份虚构的零耗时准备。
                return await AdvanceControlRecoveryAsync(controlled, ct);
            }
            if (_game is NativeGame nativeGame && !await nativeGame.PrepareAsync(ct,
                    (_execution?.ObservationDeadline ?? _jsonExecution!.ObservationDeadline) - Context.Now))
            {
                await nativeGame.YieldAsync(ct);
                return new(CombatFlowResult.AwaitingObservation, false);
            }
            ChangePhase("execution");
            // 整步（包含产球后的接球）独占；进入 atomic 后跨 Step 保留，退出再交还普通观察。
            if (_game is NativeGame nativeOwner) _exclusive ??= nativeOwner.BeginExclusive();
            TaskExecutionScope.ThrowIfFailed();
            if (_game is NativeGame { HasDefeatProbe: true } probing)
            {
                ChangePhase("defeat-observation");
                probing.BeginStep();
                probing.AdvanceDefeatProbe(ct);
                await probing.YieldAsync(ct);
                return new(CombatFlowResult.AwaitingObservation, false);
            }
            var stalled = _hasFailedPass &&
                Context.Now - _lastNewFailureAt >= CombatFlowPolicy.EpisodeTimeoutSeconds &&
                Context.InputAttemptRevision == _lastFailedAttemptRevision;
            if (_consecutiveFailedPasses >= 3 || stalled)
            {
                // 先核实新画面中的败北，不能把复苏弹窗当作第三轮普通流程失败。
                _game.CheckDefeated(ct);
                ct.ThrowIfCancellationRequested();
                TaskExecutionScope.StopUnconfirmedCombat(_consecutiveFailedPasses >= 3
                    ? "增强战斗连续 3 轮关键流程失败，不执行复活重试"
                    : "增强战斗失败后持续无新执行进展，恢复观察时限已到；不切队或传送。" + MaintenanceDecision);
            }
            var step = await (_execution?.StepAsync(ct, beginObservationFrame: true, allowNewRound)
                              ?? _jsonExecution!.StepAsync(ct, allowNewRound));
            if (_game is NativeGame { PendingControl: not null })
            {
                _stepBeforeControl = step;
                return new(CombatFlowResult.AwaitingObservation, false);
            }
            if (_game is NativeGame && MaintenanceDecision is { } decision && decision != _lastReportedMaintenanceDecision)
            {
                _lastReportedMaintenanceDecision = decision;
                _diagnosticLogger.LogInformation("战斗 {BattleId} 维护恢复：{Decision}", Context.BattleId, decision);
            }
            if (step.RoundCompleted && step.Result == CombatFlowResult.Failed)
            {
                _pendingDiagnosticBoundary = "failed-pass";
                _game.CheckDefeated(ct);
                ct.ThrowIfCancellationRequested();
                var attemptRevision = Context.InputAttemptRevision;
                var firstFailure = !_hasFailedPass;
                var newAttempt = attemptRevision != _lastFailedAttemptRevision;
                _hasFailedPass = true;
                if (firstFailure || newAttempt) _lastNewFailureAt = Context.Now;
                if (newAttempt)
                {
                    _consecutiveFailedPasses++;
                    _lastFailedAttemptRevision = attemptRevision;
                }
                _failureFinishCheckRequested |= firstFailure || newAttempt;
                if (_game is NativeGame && newAttempt)
                    _diagnosticLogger.LogWarning("战斗 {BattleId} 第 {Failures}/3 轮关键流程失败，未确认结束；保留本场记录并交回宿主检测，不传送",
                        Context.BattleId, _consecutiveFailedPasses);
                // 秘境/幽境的独立检测器按约一秒节拍工作；失败时让出执行线程，且保留取消。
                if (!IsAtomic) EndExclusive();
                if (_game is not NativeGame { HasDefeatProbe: true }) await _game.WaitAfterFailedPassAsync(ct);
            }
            else if (step.RoundCompleted && step.Result == CombatFlowResult.Deferred)
            {
                // 暂未确认的施放/维护让出不是失败，也不能假装恢复成功清除既有失败。
                // Pending/Deferred is ordinary waiting, not a new request to open
                // the party menu. The host retains its periodic finish detector.
                if (!IsAtomic) EndExclusive();
                await _game.YieldAsync(ct);
            }
            else if (step.RoundCompleted && step.Result == CombatFlowResult.Succeeded &&
                (_execution?.LastRoundHadAction ?? _jsonExecution!.LastRoundHadAction))
            {
                _consecutiveFailedPasses = 0;
                _hasFailedPass = false;
                _lastFailedAttemptRevision = Context.InputAttemptRevision;
            }
            return step;
        }
        catch (OperationCanceledException cancelled)
        {
            _pendingDiagnosticBoundary = "cancelled";
            try { Dispose(); }
            catch (Exception cleanup) { throw new AggregateException("战斗取消且资源排空失败", cancelled, cleanup); }
            throw;
        }
        catch { _pendingDiagnosticBoundary = "exception"; EndExclusive(); throw; }
        finally
        {
            _outerMetrics.Record("native-" + phase + "-including-wait", System.Diagnostics.Stopwatch.GetElapsedTime(phaseStarted));
            _outerMetrics.Record("native-step-total-including-wait", System.Diagnostics.Stopwatch.GetElapsedTime(outerStarted));
            try { if (!IsAtomic) EndExclusive(); }
            finally { Volatile.Write(ref _advancing, 0); }
        }
    }

    private void EndExclusive()
    {
        var exclusive = Interlocked.Exchange(ref _exclusive, null);
        if (exclusive != null)
        {
            exclusive.Dispose();
        }
        if (_pendingDiagnosticBoundary is { } boundary)
        {
            _pendingDiagnosticBoundary = null;
            WriteDiagnosticTrace(boundary);
        }
        else if (!_disposed && !IsAtomic)
        {
            _diagnosticWriter.WritePeriodic(Context.BattleId, _readDiagnosticStatistics);
        }
    }

    private void WriteDiagnosticTrace(string boundary)
    {
        if (CombatFlowDiagnosticWriter.IsEnabled(_diagnosticLogger))
            _diagnosticWriter.Write(Context.BattleId, RuntimeStatistics, boundary);
    }
    public bool HasVisibleTarget => AutoFightSeek.TryCreatePassiveDecision(AvatarRecognition.LatestPassiveObservation,
        DateTime.UtcNow, out _, out _, out _);

    internal static CombatSkillObservation GatePendingSkillObservation(CombatSkillObservation sample, bool expectedActorActive)
        => expectedActorActive ? sample : sample with { CoolingDown = null, Ready = null };

    internal static CombatFlowResult? ReconcilePendingSkill(CombatSkillAttempts attempts, CombatFlowAction action,
        string actor, Func<CombatSkillObservation> capture)
    {
        if (action.IsConfirmationOnly && attempts.GetAttempt(actor, action.Command.Method)?.AttemptId != action.PendingAttempt?.AttemptId)
        {
            action.DiagnosticReason = "原请求已释放或替换，仅确认步骤不能领取其他请求或重发输入";
            return CombatFlowResult.Skipped;
        }
        var before = attempts.GetState(actor, action.Command.Method, action.Now);
        if (before == CombatSkillAttemptState.Empty) return null;
        if (!action.CanStart) return action.RemainingBudget <= 0 ? CombatFlowResult.Failed : CombatFlowResult.Skipped;
        attempts.Observe(actor, action.Command.Method, capture());
        var after = attempts.GetState(actor, action.Command.Method, action.Now);
        if (after == CombatSkillAttemptState.Empty) return null;
        if (before == CombatSkillAttemptState.Expired || after == CombatSkillAttemptState.Expired)
        {
            action.DiagnosticReason = "原技能请求确认预算耗尽，未确认新的施放，不重发输入";
            return CombatFlowResult.Failed;
        }
        var confirmation = attempts.TakeConfirmation(actor, action.Command.Method, action.CommandId, action.Now);
        if (confirmation != null && action.AcceptConfirmation(confirmation))
        {
            action.DiagnosticReason = $"迟到冷却确认原请求，原输入时点 {confirmation.InputAt:F3}s；本步没有发送技能输入";
            return CombatFlowResult.Succeeded;
        }
        action.DiagnosticReason = after == CombatSkillAttemptState.Confirmed
            ? "原施放已确认且仍处于冷却，不重复完成或发送技能输入"
            : "已有未决施放请求，等待原请求的新证据，不重复发送技能输入";
        return after == CombatSkillAttemptState.Confirmed ? CombatFlowResult.Deferred : CombatFlowResult.Pending;
    }
    public async ValueTask<CombatFlowResult> RunRoundAsync(CancellationToken ct)
    {
        // 路线片段和正式战斗经过同一准备/受控恢复/取消入口，不能旁路PendingControl和旧选角收束。
        while (true)
        {
            var step = await StepAsync(ct);
            if (step.RoundCompleted) return step.Result;
        }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _execution?.Dispose();
            _jsonExecution?.Dispose();
        }
        finally
        {
            try { (_game as IDisposable)?.Dispose(); }
            finally
            {
                EndExclusive();
                // 接收器只在输入所有权释放后调用，不能延长 atomic/角色独占。
                WriteDiagnosticTrace("dispose");
                try
                {
                    var statistics = RuntimeStatistics;
                    _diagnosticLogger.LogInformation("战斗流程诊断：{Steps} 步、{Actions} 次游戏执行调用、{Observations} 次观察调用、{FailedPasses} 次失败遍历；核心步总耗时 {Milliseconds:F1}ms。调用计数不是命中或施放成功次数。",
                        statistics.CoreSteps, statistics.GameActionCalls, statistics.GameObservationCalls,
                        statistics.FailedPasses, statistics.CoreStepMilliseconds);
                }
                catch { /* 诊断不能覆盖原失败或取消。 */ }
            }
        }
    }

    private sealed class NativeGame(INativeCombatIo io) : ICombatFlowGame, IDisposable
    {
        private sealed record HeldEOwner(Guid Battle, Guid Span, string Actor, Vanara.PInvoke.User32.VK Key);
        private HeldEOwner? _heldE;
        private CaptureFrameStamp _heldESource;
        private void ReleaseHeldEOwner()
        {
            var held = _heldE;
            _heldE = null;
            _heldESource = default;
            if (held != null) io.ReleaseHeldE(held.Key);
        }

        private void ObserveHeldE(CombatFlowAction action, NativeCombatActor actor, bool allowSameSource = false, bool release = false)
        {
            var held = _heldE;
            var previous = _heldESource;
            CombatActionInterruptedException Rejected(string reason)
            {
                action.DiagnosticReason = $"held-e:{reason} actor={actor.Name} previous={previous.SessionId}/{previous.Sequence} source={_capture?.FrameStamp.SessionId}/{_capture?.FrameStamp.Sequence} allowSame={allowSameSource} releasing={release}";
                return new CombatActionInterruptedException();
            }
            // 松键先行，即使随后的身份/控制核验失败也不能继续持有。
            if (release) ReleaseHeldEOwner();
            if (held == null || held.Battle != action.BattleId || held.Span != action.HeldESpanId ||
                held.Actor != actor.Name) throw Rejected("owner-mismatch");
            if (io.HeldEPhysicalKey() != held.Key) throw Rejected("mapping-changed");
            ClearCapture();
            var frame = CurrentFrame();
            if (frame == null || !previous.IsKnown) throw Rejected("source-missing");
            if (!(allowSameSource && frame.FrameStamp == previous) && !frame.FrameStamp.IsAfter(previous))
                throw Rejected("source-not-advanced");
            if (frame.FrameStamp.SessionId != previous.SessionId) throw Rejected("source-session-changed");
            if (!frame.FrameStamp.IsFresh(io.Clock, UiSnapshot.CombatMaximumAge)) throw Rejected("source-stale");
            if (!io.IsCombatHud(frame)) throw Rejected("hud-unavailable");
            if (ReadActive(frame) != actor.Index) throw Rejected("actor-unconfirmed");
            var control = frame.ReadOnce((io, typeof(CombatControlObservation)), () => io.ReadControl(frame));
            if (!control.IsObserved || control.KeyboardBreakoutRequested || control.Motion is MotionStatus.Climb or MotionStatus.Fly)
                throw Rejected("control-unavailable");
            if (!frame.FrameStamp.IsFresh(io.Clock, UiSnapshot.CombatMaximumAge))
                throw Rejected("recognition-stale");
            CombatActionScope.Current?.Check();
            if (!release) _heldESource = frame.FrameStamp;
            _confirmedSource = frame.FrameStamp;
        }
        internal CombatScriptExecutionPurpose Purpose { get; init; }
        private CaptureFrameFence? _pathingInputFence;
        public bool ReportsInputReceipts => true;
        private Guid? _lastAtomicInputId;
        internal bool HasSelection => _selection != null;
        public bool HasObservationRequest => _selection != null && _selectionResult == null;
        internal CombatControlRequest? PendingControl { get; private set; }
        internal ICombatHostInputDevice? ControlDevice => io.ControlDevice;
        internal TimeProvider ControlClock => io.Clock;
        internal ValueTask ControlDelayAsync(int milliseconds, CancellationToken ct) => new(io.DelayAsync(milliseconds, ct));
        internal bool ReleasePhysicalInputForControl() => _input?.TryReleaseInput(() =>
        {
            io.ReleaseInput();
            _atomicObservationId = null;
        }) == true;
        internal (CaptureFrameStamp Source, CombatControlObservation Control) ObserveControlFrame()
        {
            ClearCapture();
            _capture = CaptureFreshFrame();
            return _capture != null && io.IsCombatHud(_capture)
                ? (_capture.FrameStamp, io.ReadControl(_capture)) : default;
        }
        internal void CompleteControlRecovery()
        {
            PendingControl = null;
            _confirmedActor = null;
            _atomicObservationId = null;
            ClearCapture();
        }
        private bool ControlInterrupted(ImageRegion? frame, CombatFlowAction action)
        {
            if (frame == null || !frame.FrameStamp.IsFresh(io.Clock, UiSnapshot.CombatMaximumAge) || !io.IsCombatHud(frame)) return false;
            var control = frame.ReadOnce((io, typeof(CombatControlObservation)), () => io.ReadControl(frame));
            if (ReferenceEquals(_selectionAction, action)) ObserveSelectionControl(frame, control);
            if (!control.IsObserved)
            {
                _captureFailureReason = "control-not-ready: 本帧控制检查尚未完成，保持原意图但不准入输入";
                return true;
            }
            if (!control.KeyboardBreakoutRequested) return false;
            _confirmedActor = null;
            _captureFailureReason = "control-interrupted: 观察到明确挣脱提示，不继续施放/原子宏";
            PendingControl ??= new(Guid.NewGuid(), action.BattleId,
                io.Clock.GetTimestamp() + (long)(Math.Min(4, Math.Max(0, action.RemainingBudget)) * io.Clock.TimestampFrequency));
            return true;
        }
        internal IEnumerable<string> ActorNames => io.Actors.Select(actor => actor.Name);
        private ILogger Logger => io.Logger;
        internal ILogger DiagnosticLogger => io.Logger;
        internal IDisposable BeginExclusive() => io.BeginExclusive(allowPassiveObservation: true);
        internal async ValueTask RunHostOperationAsync(Guid battleId, Func<CancellationToken, ValueTask> operation, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            _input ??= io.InputCoordinator.TryAcquire(battleId, ReleaseOwnedInput)
                ?? throw new InvalidOperationException("另一场战斗仍持有输入，禁止宿主接管");
            using var input = _input.EnterOperation();
            using var observation = io.BeginExclusive(allowPassiveObservation: false);
            InvalidateActorConfirmation();
            await operation(ct);
        }
        private NativeCombatActor? FindActor(string actor) => io.Actors.FirstOrDefault(item => item.Name == actor);
        private string? CurrentActor()
        {
            var frame = CurrentFrame();
            if (frame == null) return null;
            var index = ReadActive(frame);
            return io.Actors.FirstOrDefault(actor => actor.Index == index)?.Name;
        }
        private ImageRegion? _capture;
        private readonly DiagnosticEvidenceScope? _evidence = DiagnosticEvidenceScope.Current;
        private readonly HashSet<Guid> _evidenceAttempts = new();
        private readonly Dictionary<(string Function, string Actor), object?> _observations = new();
        private HashSet<int>? _sideBurstReady;
        private CombatSkillAttempts? _attempts;
        private CombatInputCoordinator.Session? _input;
        private bool _disposed;
        private string? _confirmedActor;
        private CaptureFrameStamp _confirmedSource;
        private bool _prepared;
        private Task? _visionPreparation;
        private Task? _commonVisionPreparation;
        internal bool IsPreparingVision => !_prepared && (_commonVisionPreparation != null || _visionPreparation != null);
        private CancellationTokenSource? _visionPreparationCancellation;
        private long _visionPreparationStarted;
        private bool _visionPreparationYielded;
        private long? _initialObservationDeadline;
        private bool _initialObservationAdmitted;
        internal bool NeedsBurstVision { get; set; } = true;
        private bool _preparedCapture;
        private Guid? _atomicObservationId;
        private string? _captureFailureReason;
        private int _captureOverruns;
        private long _stepStarted = io.Clock.GetTimestamp();
        private readonly AvatarActiveCheckContext _activeContext = new();
        private AvatarSelectionProtocol.Continuation<ImageRegion>? _selection;
        private AvatarActiveCheckContext? _selectionContext;
        private CombatFlowAction? _selectionAction;
        private CombatFlowAction? _readinessAction;
        private AvatarSelectionProtocol.Result<ImageRegion>? _selectionResult;
        private bool _selectionTerminal;
        private bool _selectionUnconfirmedCaptured, _selectionDeadlineCaptured;
        private bool _selectionBlockedCaptured;
        private CombatControlObservation _selectionControl;
        private CaptureFrameStamp _selectionControlSource;
        private string? _lastSelectionTrace;
        private int _selectionTraceCount;
        private string? _selectionActor;
        private bool _selectionObservationOnly;
        private Guid _selectionBattle;
        internal (Guid Goal, long Deadline)? SelectionAssistance => !_selectionObservationOnly && _selection is { CanAssist: true } goal &&
            PendingControl == null && _input != null && !_selectionTerminal &&
            !io.Actors.Any(actor => _attempts?.HasUnresolved(actor.Name, Method.Skill) == true || _attempts?.HasUnresolved(actor.Name, Method.Burst) == true)
                ? (goal.GoalId, goal.DeadlineTimestamp) : null;
        internal async ValueTask<bool> RunSelectionOperationAsync(CombatBattleHostInput request, Func<CancellationToken, ValueTask> operation, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            InvalidOperationException Rejected(string code)
            {
                var unresolved = _attempts == null ? "not-created" : string.Join(",", io.Actors.Where(actor =>
                    _attempts.HasUnresolved(actor.Name, Method.Skill) || _attempts.HasUnresolved(actor.Name, Method.Burst)).Select(actor => actor.Name));
                var detail = $"battle={_selectionBattle} request={request.RequestId} requestedGoal={request.SelectionGoal} " +
                    $"controlPending={PendingControl != null} inputOwner={_input != null} terminal={_selectionTerminal} unresolvedActors={unresolved} " +
                    (_selection?.DescribeAssistanceSource(request.Source) ?? "selection=missing");
                try { io.Logger.LogWarning("SELECTION_ASSIST_REJECT code={Code} {Detail}", code, detail); } catch { }
                var error = new InvalidOperationException($"选角辅助的新帧/键释放/目标边界不成立 [{code}]：{detail}");
                error.Data["CombatFailureCode"] = code;
                return error;
            }
            if (_selection is not { } selection || selection.GoalId != request.SelectionGoal) throw Rejected("selection-goal-mismatch");
            if (SelectionAssistance == null) throw Rejected("selection-owner-unavailable");
            if (!request.Source.IsKnown || request.Source.SessionId != selection.ObservedSource.SessionId ||
                request.Source.TimestampFrequency != io.Clock.TimestampFrequency)
                throw Rejected("selection-source-identity-mismatch");
            if (!selection.CanAssistFrom(request.Source))
            {
                // 同一有效目标的输入前/过期帧只等待，不发送辅助移动，不刷新原期限。
                try { Logger.LogDebug("SELECTION_ASSIST_DEFER request={Request} {Source}",
                    request.RequestId, selection.DescribeAssistanceSource(request.Source)); } catch { }
                return false;
            }
            if (!_input!.TryReleaseInput(io.ReleaseInput)) throw Rejected("selection-release-failed");
            await RunHostOperationAsync(_selectionBattle, operation, ct);
            return true;
        }
        internal void ObserveSelectionAssistance(CombatBattleHostInput request, CombatBattleHostInputResult receipt)
        {
            if (receipt.Status == CombatBattleHostInputStatus.Unknown)
            {
                _selectionTerminal = true;
                TaskExecutionScope.StopUnconfirmedCombat("选角辅助移动结果未知，停止输入且不重复脉冲");
            }
            if (receipt.Status == CombatBattleHostInputStatus.Sent && receipt.CompletedTimestamp is { } completed)
                _selection?.ObserveAssistance(request.Source, completed);
        }
        private sealed record ExpiredRecovery(CombatFlowAction Action, CombatSkillAttempt Attempt, NativeCombatActor Actor)
        {
            public CombatSkillObservation? First { get; set; }
        }
        private ExpiredRecovery? _expiredRecovery;
        private AvatarSelectionProtocol.Continuation<ImageRegion>? _defeatSelection;
        private NativeCombatActor? _defeatActor;
        internal bool HasDefeatProbe => _defeatSelection != null;

        private void CancelDefeatProbe()
        {
            _defeatSelection?.Dispose();
            _defeatSelection = null;
            _defeatActor = null;
        }

        internal void AdvanceDefeatProbe(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            TaskExecutionScope.ThrowIfFailed();
            if (_defeatSelection == null || _defeatActor == null || _input == null) return;
            using var operation = _input.EnterOperation();
            using var exclusive = io.BeginExclusive(allowPassiveObservation: true);
            using var recoveryBudget = CombatActionScope.Suspend();
            using var result = _defeatSelection.Advance(ct);
            if (result.AwaitingObservation) return;
            var actor = _defeatActor;
            CancelDefeatProbe();
            if (result.NeedsRecovery) io.ResolveSelectionRecovery(actor, result, ct);
        }

        public ValueTask AdvanceObservationAsync(CancellationToken ct)
        {
            using var realtime = new RecognitionReadinessScope();
            try { return AdvanceObservationCore(ct); }
            catch (RecognitionNotReadyException error)
            {
                _captureFailureReason = error.Message;
                ClearCapture();
                return ValueTask.CompletedTask;
            }
            catch (CombatActionInterruptedException)
            {
                ct.ThrowIfCancellationRequested();
                // 观察到提交之间维护/条件可能变化。保留原目标的输入事实，
                // 只请求退役；本步不重发，后续仍由原fence和期限决定能否交接。
                _selection?.RequestRetirement();
                io.ReleaseInput();
                ClearCapture();
                return ValueTask.CompletedTask;
            }
        }

        private ValueTask AdvanceObservationCore(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_selectionTerminal) TaskExecutionScope.StopUnconfirmedCombat("本场选角/辅助输入已进入未确认终态");
            if (_selection == null || _selectionResult != null || _selectionAction == null) return ValueTask.CompletedTask;
            var action = _selectionAction;
            _input ??= io.InputCoordinator.TryAcquire(action.BattleId, ReleaseOwnedInput)
                ?? throw new InvalidOperationException("上一场输入尚未退出，禁止推进观察需求");
            using var operation = _input.EnterOperation();
            using var exclusive = io.BeginExclusive(allowPassiveObservation: true);
            // 已撤回/到期的目标仍须按自己的输入事实收束；不借新动作的CanStart延长旧期限。
            var canContinue = action.CanStart && action.CanContinue && !_selection.RetirementRequested;
            if (!canContinue) _selection.RequestRetirement();
            using var scope = canContinue ? new CombatActionScope(action, ct) : null;
            if (!_selection.IsExpired)
            {
                if (ControlInterrupted(CurrentFrame(), action))
                {
                    TraceSelection("control-awaiting");
                    return ValueTask.CompletedTask;
                }
                if (_selectionContext is { NeedsLayoutPreparation: true } context && CurrentFrame() is { } layoutFrame)
                {
                    var preparation = io.PrepareActorObservation(layoutFrame, context, ct);
                    if (preparation != AvatarLayoutPreparation.NotRequested)
                    {
                        action.Trace("layout-prepare", $"result={preparation} sourceSequence={layoutFrame.FrameStamp.Sequence}");
                        ClearCapture();
                        return ValueTask.CompletedTask;
                    }
                }
            }
            var result = _selection.Advance(ct, canContinue && !_selectionObservationOnly);
            try
            {
                if (result.NeedsRecovery)
                {
                    // 无物理切人时旧before-selection通道不会保存此帧；在恢复消费它之前补齐原始原因图。
                    try
                    {
                        if (result.BorrowFrame is { } recoveryFrame)
                            _evidence?.TryCapture(recoveryFrame, "selection:" + _selection.GoalId,
                                "hud-unavailable-before-recovery",
                                $"battle={action.BattleId} target={_selectionActor} submitted={_selection.HasSubmittedInput} reason={_selection.Reason} purpose={Purpose}; original frame before non-combat recovery; no input authorization", Logger);
                    }
                    catch { /* 诊断不能改变恢复结果。 */ }
                    using var nonCombat = CombatActionScope.Suspend();
                    using var recoveryRecognition = new RecognitionReadinessScope(nonBlocking: false);
                    io.ResolveSelectionRecovery(FindActor(_selectionActor!)!, result, ct);
                }
                CheckSelectionOutcome(result);
            }
            catch { result.Dispose(); throw; }
            if (!result.NeedsRecovery && result.TakeFrame() is { } frame)
            {
                ClearCapture();
                _capture = frame; // 同一步的条件/技能入口复用，不再重复截图。
            }
            TraceSelection(result.State.ToString());
            if (result.AwaitingObservation) result.Dispose();
            else _selectionResult = result;
            return ValueTask.CompletedTask;
        }

        private AvatarSelectionProtocol.Result<ImageRegion> SelectActorStep(CombatFlowAction action, NativeCombatActor actor, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (_selection != null && (_selectionActor != actor.Name || _selectionBattle != action.BattleId))
            {
                _selection.RequestRetirement();
                if (_selectionResult == null) return new(false, false, default, null, null, null, awaitingObservation: true);
                CheckSelectionOutcome(_selectionResult);
                if (_selectionResult.State is not (AvatarSelectionProtocol.Outcome.Ready or AvatarSelectionProtocol.Outcome.RetiredWithoutCompletion or AvatarSelectionProtocol.Outcome.Unavailable))
                    TaskExecutionScope.StopUnconfirmedCombat("选角原期限耗尽且未取得可交接的新帧，禁止由新目标覆盖");
                CancelSelection();
                _confirmedActor = null;
                ClearCapture();
            }
            if (_selectionResult != null)
            {
                var result = _selectionResult;
                _selectionResult = null; // 原需求终态只交付一次，frame所有权随result转移。
                CancelSelection();
                return result;
            }
            if (_selection == null)
            {
                var context = new AvatarActiveCheckContext();
                _selectionContext = context;
                _selectionAction = action;
                _selectionActor = actor.Name;
                _selectionBattle = action.BattleId;
                _selectionObservationOnly = IsObservationOnlyPathingAttack(action);
                _selection = new(actor.Index, SwitchAttempts,
                    TimeSpan.FromSeconds(action.RemainingBudget),
                    TakeSelectionFrame, frame => frame.FrameStamp, io.IsCombatHud,
                    frame => frame.ReadOnce((io, typeof(AvatarActiveCheckContext)), () => io.ReadActive(frame, context)),
                    frame => new ImageRegion(frame.SrcMat.Clone(), 0, 0) { FrameStamp = frame.FrameStamp },
                    (index, request) =>
                    {
                        if (_selectionObservationOnly) throw new CombatActionInterruptedException();
                        var receipt = io.SelectActor(index, request, ct);
                        if (receipt.Status is CombatBattleHostInputStatus.Sent or CombatBattleHostInputStatus.Unknown || receipt.NativeSubmitted > 0)
                            action.RecordInputSubmission(request.Id);
                        action.Trace("switch-submission", $"goal={_selection?.GoalId} pulse={request.Id} source={request.Source.Sequence} index={index} status={receipt.Status} reason={receipt.Reason}");
                        if (receipt.Status == CombatBattleHostInputStatus.NotSent && _selection is { HasSubmittedInput: false } notSent)
                            _evidence?.ForgetBefore("selection", notSent.GoalId.ToString("N"));
                        return receipt;
                    }, io.Clock, canObserve: frame =>
                    {
                        var control = frame.ReadOnce((io, typeof(CombatControlObservation)), () => io.ReadControl(frame));
                        return ObserveSelectionControl(frame, control);
                    }, release: io.ReleaseInput, beforeSubmit: (frame, request) =>
                    {
                        if (_selectionObservationOnly) throw new CombatActionInterruptedException();
                        if (_selection is { HasSubmittedInput: false } goal)
                            _evidence?.RememberBefore(frame, "selection", goal.GoalId.ToString("N"),
                                $"battle={action.BattleId} goal={goal.GoalId} pulse={request.Id} target={actor.Name}/{actor.Index} originalDeadline={goal.DeadlineTimestamp}; before native submission");
                    });
            }
            return new(false, false, default, null, null, null, awaitingObservation: true);
        }

        private bool ObserveSelectionControl(ImageRegion frame, CombatControlObservation control)
        {
            _selectionControl = control;
            _selectionControlSource = frame.FrameStamp;
            var allowed = control.IsObserved && !control.KeyboardBreakoutRequested &&
                control.Motion != MotionStatus.Climb && (control.Motion != MotionStatus.Fly || _selectionObservationOnly);
            if (!allowed && _selection is { HasSubmittedInput: false } goal && _selectionAction is { } action)
            {
                var phase = action.RemainingBudget <= .2 && !_selectionDeadlineCaptured ? "deadline"
                    : (goal.ElapsedSeconds >= 1.5 || control.KeyboardBreakoutRequested) && !_selectionBlockedCaptured ? "blocked-before-input" : null;
                if (phase != null)
                {
                    // 协议Unknown和外层control早退都在持有原帧时取证；正挣脱提示在恢复前记录一次。
                    var detail = $"battle={action.BattleId} goal={goal.GoalId} target={_selectionActor} submitted=False " +
                        $"controlObserved={control.IsObserved} motion={control.Motion} keyboardBreakout={control.KeyboardBreakoutRequested} " +
                        $"purpose={Purpose} command={DescribeCommand(action.Command)} deadline={goal.DeadlineTimestamp} remaining={action.RemainingBudget:F3}";
                    _evidence?.TryCapture(frame, "selection:" + goal.GoalId, phase, detail, Logger);
                    if (phase == "deadline") _selectionDeadlineCaptured = true;
                    else _selectionBlockedCaptured = true;
                }
            }
            return allowed;
        }

        private void TraceSelection(string state)
        {
            if (_selection is not { } goal || _selectionAction is not { } action) return;
            var source = goal.ObservedSource;
            var detail = $"battle={action.BattleId} goal={goal.GoalId} target={_selectionActor} actual={goal.ObservedIndex?.ToString() ?? "unknown"} state={state} reason={goal.Reason} source={source.SessionId}/{source.Sequence} sourceKnown={source.IsKnown} originalDeadline={goal.DeadlineTimestamp} remaining={action.RemainingBudget:F3}s " +
                $"controlObserved={_selectionControl.IsObserved} motion={_selectionControl.Motion} keyboardBreakout={_selectionControl.KeyboardBreakoutRequested} controlSource={_selectionControlSource.SessionId}/{_selectionControlSource.Sequence} " +
                $"purpose={Purpose} command={DescribeCommand(action.Command)} pathingPrimitive={PathingPrimitiveInput.Supports(action.Command)} atomic={action.InAtomicScope} observationOnly={_selectionObservationOnly}";
            action.Trace("selection", detail);
            var changed = state + ":" + goal.Reason + ":" + goal.ObservedIndex + ":" + _selectionControl;
            if (changed != _lastSelectionTrace && _selectionTraceCount++ < 16)
            {
                _lastSelectionTrace = changed;
                try { Logger.LogDebug("SELECTION_GOAL {Detail}", detail); } catch { }
            }
            if (goal.HasSubmittedInput && state != nameof(AvatarSelectionProtocol.Outcome.Ready) && _capture != null)
            {
                var phase = action.RemainingBudget <= .2 && !_selectionDeadlineCaptured ? "deadline"
                    : goal.ElapsedSeconds >= 1.5 && !_selectionUnconfirmedCaptured ? "unconfirmed" : null;
                if (phase != null)
                {
                    _evidence?.CaptureFault(_capture, "selection", goal.GoalId.ToString("N"), phase, detail, Logger);
                    if (phase == "deadline") _selectionDeadlineCaptured = true;
                    else _selectionUnconfirmedCaptured = true;
                }
            }
        }

        private static string DescribeCommand(CombatCommand command) =>
            $"{command.Name} {command.Method.Alias[0]}({string.Join(",", command.Args ?? [])})";

        private void CheckSelectionOutcome(AvatarSelectionProtocol.Result<ImageRegion> result)
        {
            if (result.State == AvatarSelectionProtocol.Outcome.UnconfirmedTerminal)
            {
                _selectionTerminal = true;
                TaskExecutionScope.StopUnconfirmedCombat($"切人输入结果未知且原目标 {_selectionActor} 未确认，禁止跳过或向其他角色继续输入");
            }
        }

        private ImageRegion? TakeSelectionFrame()
        {
            if (_capture == null) return CaptureFreshFrame();
            var frame = _capture;
            _capture = null;
            _observations.Clear();
            _sideBurstReady = null;
            _preparedCapture = false;
            return frame;
        }

        public void CancelObservation(CombatFlowAction action)
        {
            if (ReferenceEquals(_readinessAction, action))
            {
                _readinessAction = null;
                if (action.RemainingBudget <= 0)
                {
                    // 调度可能直接跨过最后一次观察；在原命令收束处记录，不再采图或识别。
                    try
                    {
                        var request = "skill-readiness:" + action.CommandId;
                        var detail = $"battle={action.BattleId} command={action.CommandId} phase=deadline reason={action.DiagnosticReason} remaining={action.RemainingBudget:F3}";
                        Logger.LogDebug("SKILL_READINESS_END {Detail}", detail);
                        if (_capture != null && _evidence != null)
                            _evidence.TryCapture(_capture, request, "deadline", "latest-existing-frame; " + detail, Logger);
                        else Logger.LogDebug("EVIDENCE_CAPTURE_MISSING request={Request} phase=deadline reason={Reason}",
                            request, _capture == null ? "no-existing-frame" : "no-run-scope");
                    }
                    catch { /* 取证失败不得覆盖原动作结果。 */ }
                }
            }
            if (ReferenceEquals(_selectionAction, action))
            {
                // 调度器可能先发现动作到期；不能绕过选角目标的未确认终态。
                if (!_selectionTerminal && _selection is { HasUnconfirmedSubmission: true, IsExpired: true })
                {
                    _selectionTerminal = true;
                    TaskExecutionScope.StopUnconfirmedCombat($"切人输入结果未知且原目标 {_selectionActor} 到期未确认，禁止当作可选跳过");
                }
                if (_selection is { HasSubmittedInput: false } or { IsClosed: true, HasUnconfirmedSubmission: false }) CancelSelection();
                else _selection?.RequestRetirement();
            }
            if (ReferenceEquals(_expiredRecovery?.Action, action)) _expiredRecovery = null;
        }

        private void CancelSelection()
        {
            if (_selection is { } goal) _evidence?.ForgetBefore("selection", goal.GoalId.ToString("N"));
            _selectionResult?.Dispose();
            _selectionResult = null;
            _selection?.Dispose();
            _selection = null;
            _selectionContext = null;
            _selectionAction = null;
            _selectionActor = null;
            _selectionObservationOnly = false;
            _selectionUnconfirmedCaptured = _selectionDeadlineCaptured = false;
            _selectionBlockedCaptured = false;
            _selectionControl = default;
            _selectionControlSource = default;
            _lastSelectionTrace = null;
            _selectionTraceCount = 0;
        }

        internal async ValueTask<bool> PrepareAsync(CancellationToken ct, double availableSeconds)
        {
            if (_prepared && io.IsVisionPrepared) return true;
            if (_prepared)
            {
                // 仅重做已失效模型/尺寸的准备，不重建动作、选角目标、游标或其期限。
                _prepared = _initialObservationAdmitted = false;
                _visionPreparation = _commonVisionPreparation = null;
                _visionPreparationCancellation?.Dispose();
                _visionPreparationCancellation = null;
                _initialObservationDeadline = io.Clock.GetTimestamp() + (long)(Math.Min(CombatFlowPolicy.ActionTimeoutSeconds,
                    Math.Max(0, availableSeconds)) * io.Clock.TimestampFrequency);
                ClearCapture();
            }
            _stepStarted = io.Clock.GetTimestamp();
            ct.ThrowIfCancellationRequested();
            _initialObservationDeadline ??= _stepStarted + (long)(CombatFlowPolicy.ActionTimeoutSeconds * io.Clock.TimestampFrequency);
            _visionPreparationCancellation ??= CancellationTokenSource.CreateLinkedTokenSource(ct);
            _commonVisionPreparation ??= io.PrepareCommonVisionAsync(_visionPreparationCancellation.Token);
            CheckInitialObservationDeadline();
            if (!_commonVisionPreparation.IsCompleted) return false;
            await _commonVisionPreparation;
            if (_visionPreparation != null)
            {
                if (io.Clock.GetTimestamp() >= _initialObservationDeadline)
                    throw new InvalidOperationException("战斗视觉准备耗尽原准入期限，禁止通过迟到完成重开预算");
                if (!_visionPreparation.IsCompleted) return false;
                await _visionPreparation; // 已完成：只消费结果，不在短步里等待模型工作。
                ct.ThrowIfCancellationRequested();
                if (_visionPreparationYielded) ClearCapture(); // 模型样本不作为就绪后的游戏证据。
                _initialObservationAdmitted = _prepared = true;
                try { Logger.LogInformation("战斗视觉准备完成，冷等待墙钟 {Milliseconds:F1}ms；仍计入首次有效动作期限",
                    System.Diagnostics.Stopwatch.GetElapsedTime(_visionPreparationStarted).TotalMilliseconds); } catch { }
                return true;
            }
            CheckInitialObservationDeadline();
            // 控制提示优先于不必要的Q模型准备。只复用原动作的受限恢复入口，不在准备阶段发键。
            using var exclusive = io.BeginExclusive(allowPassiveObservation: true);
            var frame = CaptureFreshFrame();
            if (frame == null)
            {
                CheckInitialObservationDeadline();
                // 已进入执行器后，由原动作/控制恢复期限处理缺帧，不能再拦在准备入口重开预算。
                return _initialObservationAdmitted;
            }
            var held = false;
            try
            {
                CheckInitialObservationDeadline();
                var hud = io.IsCombatHud(frame);
                var controlled = hud && io.ReadControl(frame).KeyboardBreakoutRequested;
                CheckInitialObservationDeadline();
                if (!frame.FrameStamp.IsFresh(io.Clock, UiSnapshot.CombatMaximumAge)) return _initialObservationAdmitted;
                ClearCapture();
                _capture = frame;
                _preparedCapture = true;
                held = true;
                // 交给既有动作/恢复边界；保留本帧，首次短步不再重复采图。
                if (!hud || controlled)
                {
                    _initialObservationAdmitted = true;
                    return true;
                }
            }
            finally { if (!held) frame.Dispose(); }
            try { Logger.LogInformation("战斗视觉准备：按当前策略初始化模型 needsBurst={NeedsBurst}，完成前不准入战斗输入", NeedsBurstVision); } catch { }
            _visionPreparationStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            _visionPreparation = PrepareOwnedVisionAsync(frame, _visionPreparationCancellation.Token);
            if (!_visionPreparation.IsCompleted)
            {
                _visionPreparationYielded = true;
                ClearCapture();
                return false;
            }
            await _visionPreparation;
            ct.ThrowIfCancellationRequested();
            CheckInitialObservationDeadline();
            _initialObservationAdmitted = true;
            _prepared = true;
            try
            {
                Logger.LogInformation("战斗视觉准备完成，耗时 {Milliseconds:F1}ms；准备耗时独立于战斗判定延迟",
                    System.Diagnostics.Stopwatch.GetElapsedTime(_visionPreparationStarted).TotalMilliseconds);
            }
            catch { }
            return true;
        }

        private async Task PrepareOwnedVisionAsync(ImageRegion frame, CancellationToken ct)
        {
            using var sample = new ImageRegion(frame.SrcMat.Clone(), 0, 0) { FrameStamp = frame.FrameStamp };
            using var preparation = new RecognitionReadinessScope(nonBlocking: false);
            await io.PrepareVisionAsync(NeedsBurstVision, sample, ct).ConfigureAwait(false);
        }

        internal async Task PrepareVisionBeforeEntryAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var preparation = new RecognitionReadinessScope(nonBlocking: false);
            await io.PrepareCommonVisionAsync(ct);
            using var sample = io.Capture() ?? throw new InvalidOperationException("缺少视觉准备样本，尚未完成战斗准备");
            await PrepareOwnedVisionAsync(sample, ct);
            ct.ThrowIfCancellationRequested();
            // 这里只准备模型，不创建选角/输入、也不发布任何游戏Ready事实。
        }

        private void CheckInitialObservationDeadline()
        {
            if (!_initialObservationAdmitted && io.Clock.GetTimestamp() >= _initialObservationDeadline)
                throw new InvalidOperationException($"战斗视觉准备耗尽所属动作准入期限（最多{CombatFlowPolicy.ActionTimeoutSeconds}秒），禁止继续输入；{_captureFailureReason ?? "no-fresh-initial-observation"}");
        }

        public void InvalidateActorConfirmation()
        {
            _confirmedActor = null;
            _atomicObservationId = null;
            ClearCapture();
        }

        public void BeginStep()
        {
            _stepStarted = io.Clock.GetTimestamp();
            if (!HasSelection && (_preparedCapture || _atomicObservationId != null) && _capture?.FrameStamp.IsFresh(io.Clock, UiSnapshot.CombatMaximumAge) == true)
                _preparedCapture = false;
            else ClearCapture();
        }
        public void CheckDefeated(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            TaskExecutionScope.ThrowIfFailed();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (HasDefeatProbe) { AdvanceDefeatProbe(ct); return; }
            using var recoveryBudget = CombatActionScope.Suspend();
            // 不能凭截图失败后的缓存帧取得恢复许可。
            using var capture = CaptureFreshFrame();
            if (capture == null) return;
            if (!io.IsCombatHud(capture))
            {
                ct.ThrowIfCancellationRequested();
                ReleaseHeldInput();
                io.CheckDefeated(capture, ct);
                return;
            }
            if (_attempts == null || !io.IsMainUi(capture)) return;
            // 原角色倒下后游戏可能自动换人，不会自行弹出复苏框。每个在途请求只探查一次，
            // 仅重新选择原角色；必须由真实复苏框取得恢复许可，不清空旧请求或伪造施放成功。
            var pending = _attempts.TakeInactiveActorProbe(actor =>
                FindActor(actor) is { } avatar ? io.IsActorActive(avatar, capture) : null);
            if (pending == null) return;
            ct.ThrowIfCancellationRequested();
            TaskExecutionScope.ThrowIfFailed();
            Logger.LogWarning("未决技能角色 {Actor} 已不在前台，有界重选以核实复苏状态，request={Request}",
                pending.Actor, pending.AttemptId);
            ReleaseHeldInput();
            _confirmedActor = null;
            if (FindActor(pending.Actor) is { } target)
            {
                var context = new AvatarActiveCheckContext();
                _defeatActor = target;
                _defeatSelection = new(target.Index, 4, TimeSpan.FromSeconds(1), CaptureFreshFrame,
                    frame => frame.FrameStamp, io.IsCombatHud,
                    frame => frame.ReadOnce((io, typeof(AvatarActiveCheckContext)), () => io.ReadActive(frame, context)),
                    frame => new ImageRegion(frame.SrcMat.Clone(), 0, 0) { FrameStamp = frame.FrameStamp },
                    (index, request) => io.SelectActor(index, request, ct), io.Clock);
            }
        }
        public bool HasPendingSkill(CombatFlowAction action) =>
            _attempts?.HasUnresolved(action.Command.Name, action.Command.Method) == true;

        public void ReleaseHeldInput()
        {
            if (_disposed || _input == null) return;
            // 释放仍受本场输入所有权保护；不由旧场的裸回调释放新持有者。
            // 并发关闭时活动输入尚未退出则交由 Session.Dispose 的完成边界释放。
            _input.TryReleaseInput(() =>
            {
                CancelDefeatProbe();
                _expiredRecovery = null;
                _atomicObservationId = null;
                ReleaseHeldEOwner();
                ClearCapture();
                io.ReleaseInput();
            });
        }

        public ValueTask PrepareObservationAsync(CombatFlowAction action, string function, CancellationToken ct)
        {
            PrepareObservationCore(action, function, ct);
            return ValueTask.CompletedTask;
        }

        public ValueTask<CombatObservationPreparation> PrepareObservationStepAsync(CombatFlowAction action, string function, CancellationToken ct) =>
            ValueTask.FromResult(PrepareObservationCore(action, function, ct));

        private CombatObservationPreparation PrepareObservationCore(CombatFlowAction action, string function, CancellationToken ct)
        {
            using var realtime = new RecognitionReadinessScope();
            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            action.CaptureDiagnostics = CombatFlowDiagnosticWriter.IsEnabled(Logger);
            action.Trace("prepare-observation", function);
            if (action.CaptureDiagnostics && _attempts != null)
                action.DiagnosticAttemptId = _attempts.GetDiagnosticAttemptId(action.Command.Name, action.Command.Method);
            if (!action.CanStart || FindActor(action.Command.Name) is not { } avatar) return CombatObservationPreparation.Unavailable;
            using var exclusive = io.BeginExclusive(allowPassiveObservation: true);
            try
            {
                if (ControlInterrupted(CurrentFrame(), action))
                {
                    action.DiagnosticReason = _captureFailureReason;
                    return CombatObservationPreparation.AwaitingObservation;
                }
                if (!CanReuseActor(avatar))
                {
                    _confirmedActor = null;
                    using var selection = SelectActorStep(action, avatar, ct);
                    if (selection.AwaitingObservation) return CombatObservationPreparation.AwaitingObservation;
                    if (selection.NeedsRecovery)
                    {
                        return CombatObservationPreparation.Unavailable;
                    }
                    if (selection.Confirmed)
                    {
                        _confirmedActor = avatar.Name;
                        _confirmedSource = selection.Source;
                        action.ReportActiveActor(avatar.Name);
                        if (selection.TakeFrame() is { } selectedFrame)
                        {
                            ClearCapture();
                            _capture = selectedFrame;
                        }
                    }
                }
                if (_confirmedActor == avatar.Name && action.CanStart)
                {
                    action.ReportActiveActor(avatar.Name);
                    _ = Observe(function, [], avatar.Name);
                    _preparedCapture = _capture != null;
                    return CombatObservationPreparation.Ready;
                }
            }
            catch (RecognitionNotReadyException) { return CombatObservationPreparation.AwaitingObservation; }
            catch (CombatActionInterruptedException) { }
            return CombatObservationPreparation.Unavailable;
        }

        public async ValueTask<CombatSkillRecovery> RecoverExpiredSkillStepAsync(CombatFlowAction action, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            action.CaptureDiagnostics = CombatFlowDiagnosticWriter.IsEnabled(Logger);
            var command = action.Command;
            if (action.CaptureDiagnostics && _attempts != null)
                action.DiagnosticAttemptId = _attempts.GetDiagnosticAttemptId(command.Name, command.Method);
            var actorName = command.Name == CombatScriptParser.CurrentAvatarName ? CurrentActor() : command.Name;
            if (_attempts == null || !action.CanStart || action.IsConfirmationOnly ||
                command.Method != Method.Skill && command.Method != Method.Burst ||
                actorName == null || _attempts.GetState(actorName, command.Method, action.Now) != CombatSkillAttemptState.Expired ||
                FindActor(actorName) is not { } avatar)
            {
                CancelObservation(action);
                return new(CombatObservationPreparation.Unavailable);
            }
            var attempt = _attempts.GetAttempt(actorName, command.Method)!;
            if (_expiredRecovery == null || !ReferenceEquals(_expiredRecovery.Action, action) || _expiredRecovery.Attempt != attempt)
            {
                if (_expiredRecovery != null) CancelObservation(_expiredRecovery.Action);
                _expiredRecovery = new(action, attempt, avatar);
            }
            var recovery = _expiredRecovery;
            // 选角阶段自己持有输入操作；返回后才借用下一阶段，不能嵌套 EnterOperation。
            if (command.Name != CombatScriptParser.CurrentAvatarName)
            {
                var prepared = await PrepareObservationStepAsync(action, command.Method == Method.Skill ? "e-ready" : "q-ready", ct);
                if (prepared != CombatObservationPreparation.Ready)
                {
                    if (prepared == CombatObservationPreparation.Unavailable) CancelObservation(action);
                    return new(prepared);
                }
            }
            if (!action.CanStart || _input == null) { CancelObservation(action); return new(CombatObservationPreparation.Unavailable); }
            using var operation = _input.EnterOperation();
            using var exclusive = io.BeginExclusive(allowPassiveObservation: true);
            using var scope = new CombatActionScope(action, ct);

            CombatSkillObservation Read()
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    var capture = CurrentFrame();
                    if (capture == null || !io.IsCombatHud(capture) || io.IsActorActive(avatar, capture) != true) return Sample(action, capture, null, null);
                    if (command.Method == Method.Skill)
                    {
                        var cd = io.ReadSkillCooldown(avatar, capture);
                        var ready = io.IsSkillReady(avatar, capture, cd);
                        return Sample(action, capture, cd > 0 ? true : ready ? false : null, ready);
                    }
                    var burst = io.ReadBurst(capture, active: true);
                    return Sample(action, capture, burst.CoolingDown, burst.Ready);
                }
                catch (Exception error) when (error is not OperationCanceledException and not CombatNotFinishedException
                    and not CombatRecoveryCompletedException and not CombatActionInterruptedException)
                {
                    Logger.LogDebug(error, "恢复探测截图/技能识别不可用，保留Unknown");
                    return Sample(action, null, null, null);
                }
            }

            var second = Read();
            _preparedCapture = false; // 下一 Step 必须取得下一源帧，不能反复复用本次准备帧。
            ct.ThrowIfCancellationRequested();
            if (!action.CanStart) { CancelObservation(action); return new(CombatObservationPreparation.Unavailable); }
            var reset = recovery.First is { } first
                ? _attempts.TryReleaseExpiredReady(avatar.Name, command.Method, first, second) : null;
            if (reset != null)
            {
                action.DiagnosticReason = $"{avatar.Name} 双源帧新就绪，退役过期请求 {reset.AttemptId}；不补记旧成功";
                _expiredRecovery = null;
                return new(CombatObservationPreparation.Ready, reset);
            }
            if (!second.SourceAcceptedFresh || second.Ready != true || second.CoolingDown != false)
                recovery.First = null;
            else recovery.First ??= second;
            action.DiagnosticReason = $"{avatar.Name} 过期请求跨帧复核，等待双新帧就绪；保留原请求和期限";
            await YieldAsync(ct);
            return new(CombatObservationPreparation.AwaitingObservation);
        }

        public object? Observe(string function, IReadOnlyList<object?> args, string actor)
        {
            using var realtime = new RecognitionReadinessScope();
            if (_capture != null && !_capture.FrameStamp.IsFresh(io.Clock, UiSnapshot.CombatMaximumAge)) ClearCapture();
            if (function == "q-energy-sample") return null; // 当前没有可验证的能量增量传感器。
            if (function == "last-check") return io.LastFinishCheckAge;
            if (function is not ("in-party" or "onfield" or "e-ready" or "e-cd" or "q-ready" or "q-cd" or "q-energy-low" or "low-hp")) return null;
            var target = args.FirstOrDefault()?.ToString() ?? actor;
            if (string.IsNullOrWhiteSpace(target) || target == CombatScriptParser.CurrentAvatarName)
            {
                _capture ??= CaptureFreshFrame();
                if (_capture == null) return null;
                var activeIndex = ReadActive(_capture);
                target = io.Actors.FirstOrDefault(avatar => avatar.Index == activeIndex)?.Name ?? "";
            }
            if (_observations.TryGetValue((function, target), out var cached)) return cached;
            object? result;
            try { result = ObserveCurrent(function, target); }
            catch (RecognitionNotReadyException) { return null; }
            catch (Exception exception) when (exception is not (OperationCanceledException or CombatActionInterruptedException))
            {
                Logger.LogDebug(exception, "战斗观测 {Function}({Actor}) 不可用，保留 Unknown", function, target);
                result = null;
            }
            if (_capture != null && !_capture.FrameStamp.IsFresh(io.Clock, UiSnapshot.CombatMaximumAge))
            {
                ClearCapture();
                return null;
            }
            _observations[(function, target)] = result;
            return result;
        }

        private object? ObserveCurrent(string function, string target)
        {
            var avatar = FindActor(target);
            if (function == "in-party") return avatar != null;
            if (avatar == null) return null;
            _capture ??= CaptureFreshFrame();
            if (_capture == null) return null;
            if (!io.IsCombatHud(_capture)) return null;
            var activeIndex = ReadActive(_capture);
            if (activeIndex <= 0) return null;
            if (!_observations.TryGetValue(("onfield", target), out var observedActive))
                _observations[("onfield", target)] = observedActive = activeIndex == avatar.Index;
            var active = observedActive is true;
            if (function == "onfield") return active;
            if (function is "e-ready" or "e-cd")
            {
                if (active)
                {
                    var cd = io.ReadSkillCooldown(avatar, _capture);
                    _observations[("e-cd", target)] = cd > 0 ? cd : null;
                    var ready = io.IsSkillReady(avatar, _capture, cd);
                    _observations[("e-ready", target)] = ready;
                    return function == "e-cd" ? cd > 0 ? cd : null : ready;
                }
                // 后台的已知正 CD 能证明尚未就绪；计时归零不能证明当前 UI 真正可施放。
                return io.TryGetKnownSkillCooldown(target, out var known) && known > 0
                    ? function == "e-ready" ? false : known : null;
            }
            if (!active)
            {
                if (function != "q-ready") return null;
                if (_sideBurstReady == null)
                {
                    // 旧侧栏算法会原地调整像素，必须隔离，不能污染本步的其他观测。
                    _sideBurstReady = io.ReadSideBurstReady(_capture);
                }
                return _sideBurstReady.Contains(avatar.Index) ? true : null;
            }
            if (function == "low-hp") return io.ReadLowHp(_capture);
            if (function is "q-ready" or "q-cd" or "q-energy-low")
            {
                var burst = io.ReadBurst(_capture, active: true);
                _observations[("q-cd", target)] = burst.CoolingDown;
                _observations[("q-energy-low", target)] = burst.EnergyFull.HasValue ? !burst.EnergyFull.Value : null;
                _observations[("q-ready", target)] = burst.Ready ? true :
                    burst.CoolingDown == true || burst.EnergyFull == false ? false : null;
                return _observations[(function, target)];
            }
            // 尚无可靠观测的状态保持 Unknown，不能通过旧 bool API 把异常折叠为 false。
            return null;
        }

        public async ValueTask<CombatFlowResult> ExecuteAsync(CombatFlowAction action, CancellationToken ct)
        {
            using var realtime = new RecognitionReadinessScope();
            action.IsAwaitingUnsentBurstReadiness = false;
            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            action.CaptureDiagnostics = CombatFlowDiagnosticWriter.IsEnabled(Logger);
            var command = action.Command;
            if (!action.CanStart)
            {
                ReleaseHeldEOwner();
                action.DiagnosticReason = "动作预算或前置条件不再满足";
                return CombatFlowResult.Skipped;
            }
            if (Purpose == CombatScriptExecutionPurpose.Pathing && command.LegacyOutcomePolicy &&
                action.AtomicObservationId == null && (command.Method != Method.Jump || !action.InAtomicScope) &&
                (PathingPrimitiveInput.Supports(command) ||
                    PathingPrimitiveInput.RequiresCannonScene(command)))
                return await ExecutePathingPrimitiveAsync(action, ct);
            // 条件准备取得的帧仍属于本步/本输入owner；方法边界本身不是画面失效事件。
            if (_capture != null && !_capture.FrameStamp.IsFresh(io.Clock, UiSnapshot.CombatMaximumAge)) ClearCapture();
            var observationOnly = IsObservationOnlyPathingAttack(action);
            var name = observationOnly ? action.ObservationOnlyActor ??= CurrentActor() :
                command.Name == CombatScriptParser.CurrentAvatarName ? CurrentActor() : command.Name;
            if (name == null || FindActor(name) is not { } avatar) return CombatFlowResult.Failed;
            _attempts ??= new(action.BattleId);
            if (action.IsConfirmationOnly && _attempts.GetAttempt(name, command.Method)?.AttemptId != action.PendingAttempt?.AttemptId)
                return CombatFlowResult.Skipped;
            if (action.CaptureDiagnostics)
                action.DiagnosticAttemptId = _attempts.GetDiagnosticAttemptId(name, command.Method);
            var hasUnresolved = _attempts.HasUnresolved(name, command.Method);
            action.Trace("dispatch", $"unresolved={hasUnresolved} confirmedActor={_confirmedActor}");
            // 先允许原请求领取迟到证据。就绪/CD 门禁只控制新输入，不能挡住已发送请求的确认。
            if (!hasUnresolved && command.Method == Method.Burst &&
                !(_selection is { IsClosed: false } && _selectionActor == name && _selectionBattle == action.BattleId) &&
                Observe("q-ready", [], name) is not true)
            {
                _observations.TryGetValue(("q-energy-low", name), out var lowEnergy);
                _observations.TryGetValue(("q-cd", name), out var cooling);
                if (lowEnergy is true || cooling is true)
                {
                    action.DiagnosticReason = $"skill-readiness:known-unavailable burstCooling={cooling ?? "unknown"} energyLow={lowEnergy ?? "unknown"}";
                    if (command.LegacyOutcomePolicy && !command.HasFlag("required")) return CombatFlowResult.Skipped;
                    return CombatFlowResult.Deferred;
                }
                // 未知不能在共用准入之前退出；先核实当前角色，再按原动作期限等待。
            }
            if (!hasUnresolved && command.Method == Method.Skill && command.Args?.Contains("fast") == true
                && io.TryGetKnownSkillCooldown(name, out var cd) && cd > 0)
            {
                action.DiagnosticReason = $"fast 动作仍有已知 E 冷却 {cd:F2}s";
                return command.LegacyOutcomePolicy ? CombatFlowResult.Skipped : CombatFlowResult.Deferred;
            }
            _input ??= io.InputCoordinator.TryAcquire(action.BattleId, ReleaseOwnedInput)
                ?? throw new InvalidOperationException("上一场输入尚未退出或释放，禁止新的战斗输入接管");
            if (!action.IsConfirmationOnly && _attempts.GetState(name, command.Method, action.Now) == CombatSkillAttemptState.Expired)
            {
                var recovery = await RecoverExpiredSkillStepAsync(action, ct);
                if (recovery.State == CombatObservationPreparation.AwaitingObservation) return CombatFlowResult.AwaitingObservation;
                if (recovery.State == CombatObservationPreparation.Unavailable) return CombatFlowResult.Failed;
                hasUnresolved = false;
                // 本步只完成复核；下一 Step 用新帧重新准入，不叠加选角/施放。
                return CombatFlowResult.AwaitingObservation;
            }
            using var operation = _input.EnterOperation();
            using var exclusive = io.BeginExclusive(allowPassiveObservation:
                command.Method == Method.Skill || command.Method == Method.Burst || command.Method == Method.Attack ||
                command.Method == Method.Charge || command.Method == Method.Wait);
            using var scope = new CombatActionScope(action, ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                if (action.HeldESpanId != null && (command.Method == Method.Wait || command.Method == Method.KeyUp || command.Method == Method.MoveBy))
                {
                    // A held span never invokes selection: losing evidence ends ownership instead.
                    scope.Check();
                    if (command.Method == Method.MoveBy)
                    {
                        ObserveHeldE(action, avatar, allowSameSource: true);
                        scope.Check();
                        return SubmitPrimitive(action, avatar, command, _heldESource, ct, requireCompleteReceipt: true);
                    }
                    if (command.Method == Method.KeyUp)
                    {
                        ObserveHeldE(action, avatar, allowSameSource: true, release: true);
                        scope.Check();
                        if (!action.TryBeginInput()) throw new CombatActionInterruptedException();
                        ReleaseHeldEOwner();
                        return CombatFlowResult.Succeeded;
                    }
                    if (!action.TryBeginInput()) throw new CombatActionInterruptedException();
                    var holdSeconds = double.Parse(command.Args![0], System.Globalization.CultureInfo.InvariantCulture);
                    var initial = true;
                    await scope.WaitHeldWithObservationAsync((int)Math.Ceiling(holdSeconds * 1000),
                        () =>
                        {
                            // 首片沿用down的fresh许可，不把同源读取登记成新的观察。
                            ObserveHeldE(action, avatar, allowSameSource: initial);
                            initial = false;
                        }, io.DelayAsync);
                    return CombatFlowResult.Succeeded;
                }
                if (!hasUnresolved && ControlInterrupted(CurrentFrame(), action))
                {
                    action.DiagnosticReason = _captureFailureReason;
                    io.ReleaseInput();
                    return action.AtomicObservationId != null && action.AtomicObservationId == _lastAtomicInputId
                        ? CombatFlowResult.Deferred : CombatFlowResult.AwaitingObservation;
                }
                if (_atomicObservationId != action.AtomicObservationId)
                {
                    if (_atomicObservationId != null) ClearCapture();
                    _atomicObservationId = action.AtomicObservationId;
                }
                var pendingCd = 0d;
                var pendingObservation = "frame-or-hud-unavailable";
                CombatSkillObservation recoverySample = default;
                var recoveryControlValid = false;
                CombatFlowResult? Reconcile() => ReconcilePendingSkill(_attempts, action, name, () =>
                {
                    var capture = CurrentFrame();
                    if (capture == null || !io.IsCombatHud(capture)) return Sample(action, capture, null, null);
                    var active = ReadActive(capture) == avatar.Index;
                    pendingObservation = $"actorActive={active} source={capture.FrameStamp.SessionId}/{capture.FrameStamp.Sequence}";
                    if (!active) return Sample(action, capture, null, null);
                    var observedCd = command.Method == Method.Skill ? io.ReadSkillCooldown(avatar, capture) : 0;
                    pendingCd = observedCd;
                    var skillReady = command.Method == Method.Skill && io.IsSkillReady(avatar, capture, observedCd);
                    var burst = command.Method == Method.Burst ? io.ReadBurst(capture, active) : default;
                    bool? cooling = command.Method == Method.Skill
                        ? observedCd > 0 ? true : skillReady ? false : null : burst.CoolingDown;
                    var sample = Sample(action, capture, cooling, command.Method == Method.Skill ? skillReady : burst.Ready);
                    recoverySample = sample;
                    var control = capture.ReadOnce((io, typeof(CombatControlObservation)), () => io.ReadControl(capture));
                    recoveryControlValid = control.IsObserved && !control.KeyboardBreakoutRequested &&
                        control.Motion is not (MotionStatus.Climb or MotionStatus.Fly);
                    pendingObservation += $" cd={observedCd:F3} cooling={sample.CoolingDown} ready={sample.Ready}";
                    action.Trace("pending-observation", pendingObservation);
                    return GatePendingSkillObservation(sample, active);
                });
                var observedAttempt = hasUnresolved ? _attempts.GetAttempt(name, command.Method) : null;
                var pendingResult = hasUnresolved ? Reconcile() : null;
                if (observedAttempt != null)
                {
                    var request = observedAttempt.AttemptId.ToString("N");
                    if (pendingResult != CombatFlowResult.Succeeded && _capture != null &&
                        action.Now - observedAttempt.InputAt >= 1)
                        _evidence?.CaptureFault(_capture, "skill", request,
                            observedAttempt.Deadline - action.Now <= .2 ? "deadline" : "unconfirmed",
                            $"battle={action.BattleId} command={action.CommandId} syntax={DescribeCommand(command)} purpose={Purpose} actor={name} inputAt={observedAttempt.InputAt:F3} deadline={observedAttempt.Deadline:F3} now={action.Now:F3} reason={action.DiagnosticReason} {pendingObservation}", Logger);
                    if (pendingResult is CombatFlowResult.Succeeded or CombatFlowResult.Failed)
                    {
                        _evidence?.ForgetBefore("skill", request);
                        _evidenceAttempts.Remove(observedAttempt.AttemptId);
                    }
                }
                if (pendingResult != null)
                {
                    if (pendingResult == CombatFlowResult.Pending &&
                        _attempts.TryClaimRecovery(action, recoverySample, recoveryControlValid) is { } pulse)
                    {
                        CombatSkillInput.SendRecovery(_attempts, action, pulse,
                            (request, begin) => io.SubmitInput(avatar, command, request, begin, ct), ct, io.Clock);
                        ClearCapture();
                        action.DiagnosticReason = "原attempt单次恢复脉冲已领取，仍等待输入后实际施放证据";
                    }
                    if (pendingResult == CombatFlowResult.Succeeded && command.Method == Method.Skill)
                        io.ConfirmSkill(avatar, pendingCd,
                            io.Clock.GetUtcNow().UtcDateTime.AddSeconds(-(action.Now - action.EffectiveInputAt!.Value)));
                    return pendingResult.Value;
                }
                if (action.IsConfirmationOnly) return CombatFlowResult.Skipped;
                // 未决Q的动画可能暂时遮住角色栏；上面的纯观察不能再次切人或进入恢复。
                // atomic 内纯等待没有新物理输入，保留连续输入所有权，由下一输入重新核实新帧。
                var atomicWait = action.CanReuseConfirmedActor && command.Method == Method.Wait && _confirmedActor == name;
                if (!atomicWait && (observationOnly && !action.ObservationOnlyActorConfirmed || !CanReuseActor(avatar)))
                {
                    _confirmedActor = null;
                    using var selection = SelectActorStep(action, avatar, ct);
                    if (selection.AwaitingObservation)
                    {
                        action.DiagnosticReason = "选角准备等待下一源帧，保留原命令和截止时间";
                        return CombatFlowResult.AwaitingObservation;
                    }
                    if (selection.NeedsRecovery)
                    {
                        action.DiagnosticReason = "异常HUD已移交非战斗观察，尚未确认恢复";
                        return CombatFlowResult.Deferred;
                    }
                    if (!selection.Confirmed)
                    {
                        action.DiagnosticReason = "在切换窗口内未取得连续两帧角色确认";
                        return CombatFlowResult.Failed;
                    }
                    _confirmedActor = name;
                    _confirmedSource = selection.Source;
                    if (observationOnly) action.ObservationOnlyActorConfirmed = true;
                    if (selection.TakeFrame() is { } selectedFrame)
                    {
                        ClearCapture();
                        _capture = selectedFrame;
                    }
                }
                if (!atomicWait) action.ReportActiveActor(name);
                // 已确认的旧施放槽只用于冷却互斥。必须先取得目标的新观察才能释放旧槽，
                // 不能把它当未决请求一直在别的角色前台返回 Deferred。
                var cooldownResult = !hasUnresolved ? Reconcile() : null;
                if (cooldownResult != null)
                {
                    if (cooldownResult == CombatFlowResult.Deferred && command.LegacyOutcomePolicy &&
                        command.Method == Method.Skill && command.HasFlag("fast")) return CombatFlowResult.Skipped;
                    if (cooldownResult == CombatFlowResult.Deferred && command.Method == Method.Skill && command.HasFlag("wait"))
                    {
                        action.DiagnosticReason = "显式等待原E冷却结束，保留原命令和截止时间，不内联等待或重复记账";
                        return CombatFlowResult.AwaitingObservation;
                    }
                    return cooldownResult.Value;
                }
                if (command.Method == Method.Skill || command.Method == Method.Burst)
                {
                    var ready = Observe(command.Method == Method.Skill ? "e-ready" : "q-ready", [], name);
                    var activeIndex = _capture == null ? 0 : ReadActive(_capture);
                    if (ready is not true || _capture == null || activeIndex != avatar.Index)
                    {
                        // 只读本步缓存，不为日志或分类再做OCR/模型推理。E图标未就绪不等于正CD。
                        _observations.TryGetValue(("e-cd", name), out var skillCooldown);
                        _observations.TryGetValue(("q-cd", name), out var burstCooling);
                        _observations.TryGetValue(("q-energy-low", name), out var energyLow);
                        var knownUnavailable = command.Method == Method.Skill
                            ? skillCooldown is double positive && positive > 0
                            : burstCooling is true || energyLow is true;
                        var gate = activeIndex != avatar.Index ? "actor-unconfirmed" :
                            knownUnavailable ? "known-unavailable" : "unknown-readiness";
                        action.DiagnosticReason = "skill-readiness:" + gate;
                        var gateSource = _capture?.FrameStamp ?? default;
                        if (command.Method == Method.Burst && ready == null && !knownUnavailable &&
                            !hasUnresolved && activeIndex == avatar.Index && _capture != null &&
                            gateSource.IsFresh(io.Clock, UiSnapshot.CombatMaximumAge))
                        {
                            var readinessControl = _capture.ReadOnce((io, typeof(CombatControlObservation)), () => io.ReadControl(_capture));
                            action.IsAwaitingUnsentBurstReadiness = readinessControl.IsObserved &&
                                !readinessControl.KeyboardBreakoutRequested &&
                                readinessControl.Motion is not (MotionStatus.Fly or MotionStatus.Climb);
                        }
                        var detail = $"gate={gate} ready={ready ?? "unknown"} active={activeIndex} target={avatar.Index} " +
                            $"cooldown={skillCooldown ?? "unknown"} burstCooling={burstCooling ?? "unknown"} energyLow={energyLow ?? "unknown"} " +
                            $"sourceKnown={gateSource.IsKnown} source={gateSource.SessionId}/{gateSource.Sequence} " +
                            $"sourceAgeMs={(gateSource.IsKnown && gateSource.TimestampFrequency == io.Clock.TimestampFrequency ? io.Clock.GetElapsedTime(gateSource.CapturedTimestamp).TotalMilliseconds.ToString("F1") : "unknown")} " +
                            $"evidenceFailure={_captureFailureReason ?? "none"} remaining={action.RemainingBudget:F3}";
                        action.Trace("skill-readiness", detail);
                        if (_capture != null)
                            _evidence?.TryCapture(_capture, "skill-readiness:" + action.CommandId,
                                action.RemainingBudget <= .2 ? "deadline" : "unready", detail, Logger);
                        if (command.LegacyOutcomePolicy && command.Method == Method.Skill && command.HasFlag("fast") && knownUnavailable)
                            return CombatFlowResult.Skipped;
                        if (command.Method == Method.Skill && !command.HasFlag("fast") &&
                            (command.HasFlag("wait") || Purpose == CombatScriptExecutionPurpose.Pathing && command.LegacyOutcomePolicy))
                        {
                            action.DiagnosticReason = "E尚未取得就绪证据，保留路径/显式等待的原命令与截止时间，下帧复核；不重放已完成位移";
                            _readinessAction = action;
                            return CombatFlowResult.AwaitingObservation;
                        }
                        if (!knownUnavailable || activeIndex != avatar.Index)
                        {
                            _readinessAction = action;
                            await YieldAsync(ct);
                            return CombatFlowResult.AwaitingObservation;
                        }
                        return CombatFlowResult.Deferred;
                    }
                    _readinessAction = null;
                    var source = _capture.FrameStamp;
                    // 选角/控制准入已在本帧读取，ReadOnce复用同一结果而非再做识别。
                    var inputControl = _capture.ReadOnce((io, typeof(CombatControlObservation)), () => io.ReadControl(_capture));
                    var inputContext = $"syntax={DescribeCommand(command)} hold={command.HasFlag("hold")} purpose={Purpose} " +
                        $"actor={name}/{avatar.Index} controlObserved={inputControl.IsObserved} motion={inputControl.Motion} " +
                        $"keyboardBreakout={inputControl.KeyboardBreakoutRequested} source={source.SessionId}/{source.Sequence}";
                    action.Trace("skill-input-context", inputContext);
                    Guid? submittingAttempt = null;
                    var sent = CombatSkillInput.Send(_attempts, action, name, source,
                        (request, begin) => io.SubmitInput(avatar, command, request, () =>
                        {
                            begin();
                            if (action.PendingAttempt is { } attempt && _capture != null)
                            {
                                submittingAttempt = attempt.AttemptId;
                                if (_evidence?.RememberBefore(_capture, "skill", attempt.AttemptId.ToString("N"),
                                    $"battle={action.BattleId} command={action.CommandId} {inputContext} deadline={action.AbsoluteDeadline:F3}; before native submission") == true)
                                    _evidenceAttempts.Add(attempt.AttemptId);
                            }
                        }, ct), ct, io.Clock);
                    if (sent != CombatFlowResult.Pending && submittingAttempt is { } unsubmitted)
                    {
                        _evidence?.ForgetBefore("skill", unsubmitted.ToString("N"));
                        _evidenceAttempts.Remove(unsubmitted);
                    }
                    ClearCapture();
                    return sent;
                }
                if (command.Method == Method.Wait)
                {
                    if (!action.TryBeginInput()) return CombatFlowResult.Skipped;
                    var seconds = double.Parse(command.Args![0], System.Globalization.CultureInfo.InvariantCulture);
                    if (action.AtomicObservationId != null)
                        await scope.WaitWithObservationAsync((int)Math.Ceiling(seconds * 1000), () =>
                        {
                            ClearCapture();
                            if (_atomicObservationId != action.AtomicObservationId || ControlInterrupted(CurrentFrame(), action) || !CanReuseActor(avatar))
                            {
                                InvalidateActorConfirmation();
                                throw new CombatActionInterruptedException();
                            }
                            scope.Check();
                        }, io.DelayAsync);
                    else await scope.WaitAsync((int)Math.Ceiling(seconds * 1000), io.DelayAsync);
                    return CombatFlowResult.Succeeded;
                }
                var arguments = command.Args?.Where(argument => argument is not ("required" or "refresh")) ?? [];
                var primitive = new CombatCommand(command.Name, command.Method.Alias[0] + "(" + string.Join(",", arguments) + ")");
                var primitiveFrame = CurrentFrame();
                if (primitiveFrame == null) return CombatFlowResult.AwaitingObservation;
                if (action.HeldESpanId is { } span && command.Method == Method.KeyDown)
                {
                    var physical = io.HeldEPhysicalKey();
                    if (physical == null || _heldE != null) throw new CombatActionInterruptedException();
                    var request = new CombatNativeInputRequest(action.InputRequestId, primitiveFrame.FrameStamp,
                        checked(io.Clock.GetTimestamp() + (long)(action.RemainingBudget * io.Clock.TimestampFrequency)));
                    CombatBattleHostInputResult receipt;
                    try
                    {
                        receipt = io.SubmitHeldE(physical.Value, true, request, () =>
                        {
                            scope.Check();
                            if (io.HeldEPhysicalKey() != physical || !action.TryBeginInput())
                                throw new CombatInputNotAdmittedException();
                        }, ct);
                    }
                    catch { io.ReleaseHeldE(physical.Value); throw; }
                    if (receipt.Status is CombatBattleHostInputStatus.Sent or CombatBattleHostInputStatus.Unknown || receipt.NativeSubmitted > 0)
                        action.RecordInputSubmission(request.Id);
                    if (receipt.Status != CombatBattleHostInputStatus.Sent || receipt.NativeRequested is not > 0 ||
                        receipt.NativeSubmitted != receipt.NativeRequested || receipt.Error != null)
                    {
                        io.ReleaseHeldE(physical.Value);
                        throw receipt.Error ?? new InvalidOperationException("E持键未取得完整原生回执，禁止继续或重放");
                    }
                    _heldE = new(action.BattleId, span, name, physical.Value);
                    _heldESource = primitiveFrame.FrameStamp;
                    return CombatFlowResult.Succeeded;
                }
                if (observationOnly)
                {
                    var control = primitiveFrame.ReadOnce((io, typeof(CombatControlObservation)), () => io.ReadControl(primitiveFrame));
                    if (!action.CanContinue || !io.IsCombatHud(primitiveFrame) || ReadActive(primitiveFrame) != avatar.Index ||
                        primitiveFrame.FrameStamp.SessionId != _confirmedSource.SessionId ||
                        primitiveFrame.FrameStamp != _confirmedSource && !primitiveFrame.FrameStamp.IsAfter(_confirmedSource) ||
                        !primitiveFrame.FrameStamp.IsFresh(io.Clock, UiSnapshot.CombatMaximumAge) || !control.IsObserved ||
                        control.KeyboardBreakoutRequested || control.Motion == MotionStatus.Climb)
                    {
                        action.ObservationOnlyActorConfirmed = false;
                        return CombatFlowResult.AwaitingObservation;
                    }
                }
                var submitted = SubmitPrimitive(action, avatar, primitive, primitiveFrame.FrameStamp, ct);
                if (submitted != CombatFlowResult.Succeeded) return submitted;
                if (action.AtomicObservationId != null) _lastAtomicInputId = action.AtomicObservationId;
                // 原始按键可能包含角色切换；下一条宏原语不能复用按键前的角色身份。
                if ((command.Method == Method.KeyPress || command.Method == Method.KeyDown || command.Method == Method.KeyUp) &&
                    command.Args?.FirstOrDefault() is not ("VK_LBUTTON" or "VK_RBUTTON" or "VK_MBUTTON"))
                    InvalidateActorConfirmation();
                ct.ThrowIfCancellationRequested();
                return CombatFlowResult.Succeeded;
            }
            catch (RecognitionNotReadyException error)
            {
                action.DiagnosticReason = error.Message;
                return action.IsConfirmationOnly ? CombatFlowResult.Pending : CombatFlowResult.AwaitingObservation;
            }
            catch (CombatActionInterruptedException)
            {
                ReleaseHeldEOwner();
                if (action.DiagnosticReason?.StartsWith("held-e:", StringComparison.Ordinal) != true)
                    action.DiagnosticReason = _captureFailureReason ?? $"动作被维护/条件/预算边界中断，剩余预算 {action.RemainingBudget:F3}s";
                // 先在本场仍拥有输入时结束持续键/宏，再让调度器转移；不伪造动作完成。
                io.ReleaseInput();
                if (action.HeldESpanId != null) return CombatFlowResult.Failed;
                if (action.RemainingBudget <= 0) return CombatFlowResult.Failed;
                if (action.InputAt != null && (command.Method == Method.Skill || command.Method == Method.Burst))
                {
                    return CombatFlowResult.Pending;
                }
                return CombatFlowResult.Deferred;
            }
            catch
            {
                ReleaseHeldEOwner();
                InvalidateActorConfirmation();
                io.ReleaseInput();
                throw;
            }
        }

        private CombatFlowResult SubmitPrimitive(CombatFlowAction action, NativeCombatActor? actor,
            CombatCommand command, CaptureFrameStamp source, CancellationToken ct, CannonUiObservation scene = default,
            bool requireCompleteReceipt = false)
        {
            var request = new CombatNativeInputRequest(action.InputRequestId, source,
                checked(io.Clock.GetTimestamp() + (long)(action.RemainingBudget * io.Clock.TimestampFrequency)));
            void Begin()
            {
                if ((command.Method == Method.Jump || requireCompleteReceipt) && !action.CanContinue)
                    throw new CombatInputNotAdmittedException();
                if (!action.TryBeginInput()) throw new CombatInputNotAdmittedException();
            }
            var receipt = actor == null ? io.SubmitPathingInput(command, request, Begin, ct, scene)
                : io.SubmitInput(actor, command, request, Begin, ct);
            if (receipt.Status is CombatBattleHostInputStatus.Sent or CombatBattleHostInputStatus.Unknown || receipt.NativeSubmitted > 0)
                action.RecordInputSubmission(request.Id);
            else action.ClearUnsubmittedInput();
            ct.ThrowIfCancellationRequested();
            if (receipt.Status == CombatBattleHostInputStatus.NotSent) return CombatFlowResult.AwaitingObservation;
            if (requireCompleteReceipt && (receipt.Status != CombatBattleHostInputStatus.Sent || receipt.NativeRequested is not > 0 ||
                receipt.NativeRequested != receipt.NativeSubmitted))
                TaskExecutionScope.StopUnconfirmedCombat("持键瞄准输入未取得完整回执，停止区间且禁止重放");
            if (receipt.Status == CombatBattleHostInputStatus.Unknown)
                TaskExecutionScope.StopUnconfirmedCombat("原始输入结果未知，禁止重放未完成指令");
            if (receipt.Status == CombatBattleHostInputStatus.Failed)
                throw receipt.Error ?? new InvalidOperationException(receipt.Reason);
            if (receipt.Error != null) throw receipt.Error;
            if (actor == null)
            {
                if (receipt.CompletedTimestamp is not { } completed || completed < source.CapturedTimestamp || completed > io.Clock.GetTimestamp())
                    throw new InvalidOperationException("路径输入回执时间边界无效");
                _pathingInputFence = new(source, completed);
            }
            return CombatFlowResult.Succeeded;
        }

        private async ValueTask<CombatFlowResult> ExecutePathingPrimitiveAsync(CombatFlowAction action, CancellationToken ct)
        {
            _input ??= io.InputCoordinator.TryAcquire(action.BattleId, ReleaseOwnedInput)
                ?? throw new InvalidOperationException("前一次输入尚未退役，路径交互不能接管");
            using var operation = _input.EnterOperation();
            using var exclusive = io.BeginExclusive(allowPassiveObservation: false);
            using var scope = new CombatActionScope(action, ct);
            try
            {
                var frame = CurrentFrame();
                if (frame == null || _pathingInputFence is { } fence && !fence.Accepts(frame.FrameStamp))
                    return CombatFlowResult.AwaitingObservation;
                if (action.Command.Method == Method.Jump)
                {
                    if (!io.IsMainUi(frame) || !io.IsCombatHud(frame)) return CombatFlowResult.AwaitingObservation;
                    var control = frame.ReadOnce((io, typeof(CombatControlObservation)), () => io.ReadControl(frame));
                    scope.Check();
                    if (!control.IsObserved || control.KeyboardBreakoutRequested ||
                        !frame.FrameStamp.IsFresh(io.Clock, UiSnapshot.CombatMaximumAge)) return CombatFlowResult.AwaitingObservation;
                }
                else
                {
                    // 纯等待没有新的输入完成事实，不能消费前一次Jump的来源会话约束。
                    if (action.Command.Method != Method.Wait) _pathingInputFence = null;
                    if (ControlInterrupted(frame, action)) return CombatFlowResult.AwaitingObservation;
                }
                action.Trace("pathing-primitive", $"source={frame.FrameStamp.Sequence} actorRequired=false; 不推断战斗/交互目标已完成");
                if (action.Command.Method == Method.Wait)
                {
                    if (!action.TryBeginInput()) return CombatFlowResult.Skipped;
                    var duration = double.Parse(action.Command.Args![0], System.Globalization.CultureInfo.InvariantCulture);
                    await scope.WaitAsync(checked((int)Math.Ceiling(duration * 1000)), io.DelayAsync);
                    return CombatFlowResult.Succeeded;
                }
                CannonUiObservation scene = default;
                if (PathingPrimitiveInput.RequiresCannonScene(action.Command))
                {
                    scene = io.ReadCannonScene(frame);
                    if (!scene.CanFire || !scene.IsFor(frame.FrameStamp) ||
                        !frame.FrameStamp.IsFresh(io.Clock, UiSnapshot.CombatMaximumAge))
                    {
                        action.DiagnosticReason = "当前源帧未确认大炮发射能力，保留原期限且不发送RETURN";
                        return CombatFlowResult.AwaitingObservation;
                    }
                    action.Trace("pathing-scene", $"cannon-fire source={scene.Source.Sequence}");
                }
                var result = SubmitPrimitive(action, null, action.Command, frame.FrameStamp, ct, scene);
                InvalidateActorConfirmation();
                return result;
            }
            catch (RecognitionNotReadyException error)
            {
                action.DiagnosticReason = error.Message;
                return CombatFlowResult.AwaitingObservation;
            }
            catch
            {
                InvalidateActorConfirmation();
                io.ReleaseInput();
                throw;
            }
        }

        public async ValueTask YieldAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ClearCapture();
            // 本步采图/识别已跨过帧节拍时直接交还宿主，不能再叠加固定50ms空等。
            // 纯缓存/无动作的快步仍保留节流，避免没有新帧时忙轮询。
            var remaining = 50 - io.Clock.GetElapsedTime(_stepStarted).TotalMilliseconds;
            if (remaining > 0) await io.DelayAsync((int)Math.Ceiling(remaining), ct);
        }

        public ValueTask WaitAfterFailedPassAsync(CancellationToken ct) => new(io.DelayAsync(1000, ct));

        private void ClearCapture()
        {
            _capture?.Dispose();
            _capture = null;
            _observations.Clear();
            _sideBurstReady = null;
            _preparedCapture = false;
        }
        private ImageRegion? CaptureFreshFrame()
        {
            var started = io.Clock.GetTimestamp();
            var frame = io.Capture();
            var elapsed = io.Clock.GetElapsedTime(started).TotalMilliseconds;
            _captureFailureReason = null;
            if (elapsed > 150)
            {
                _captureFailureReason = $"战斗采图/判定超过150ms：{elapsed:F1}ms；丢弃迟到结果，不授权后续输入";
                if (++_captureOverruns <= 3 || (_captureOverruns & (_captureOverruns - 1)) == 0)
                    Logger.LogWarning("{Reason}，累计 {Count} 次", _captureFailureReason, _captureOverruns);
                frame?.Dispose();
                return null;
            }
            if (frame == null) return null;
            if (frame.SrcMat.Empty() || !frame.FrameStamp.IsFresh(io.Clock, UiSnapshot.CombatMaximumAge))
            {
                _captureFailureReason = "战斗源帧未知或超过150ms新鲜度，不能授权后续输入";
                frame.Dispose();
                return null;
            }
            return frame;
        }

        private bool IsObservationOnlyPathingAttack(CombatFlowAction action) =>
            Purpose == CombatScriptExecutionPurpose.Pathing && action.Command.LegacyOutcomePolicy &&
            action.Command.Name == CombatScriptParser.CurrentAvatarName && action.Command.Method == Method.Attack &&
            !action.InAtomicScope;

        private bool CanReuseActor(NativeCombatActor avatar)
        {
            if (HasSelection) return false;
            if (_confirmedActor != avatar.Name || !_confirmedSource.IsKnown || _input == null) return false;
            _ = CurrentFrame();
            if (_capture == null || _capture.FrameStamp.SessionId != _confirmedSource.SessionId || !io.IsCombatHud(_capture)) return false;
            if (_capture.FrameStamp != _confirmedSource && !_capture.FrameStamp.IsAfter(_confirmedSource)) return false;
            if (ReadActive(_capture) != avatar.Index || !_capture.FrameStamp.IsFresh(io.Clock, UiSnapshot.CombatMaximumAge)) return false;
            _confirmedSource = _capture.FrameStamp;
            return true;
        }

        private int ReadActive(ImageRegion frame) => frame.ReadOnce((io, typeof(AvatarActiveCheckContext)),
            () => io.ReadActive(frame, _activeContext));

        private ImageRegion? CurrentFrame()
        {
            if (_capture != null && !_capture.FrameStamp.IsFresh(io.Clock, UiSnapshot.CombatMaximumAge)) ClearCapture();
            return _capture ??= CaptureFreshFrame();
        }

        private CombatSkillObservation Sample(CombatFlowAction action, ImageRegion? frame, bool? cooling, bool? ready) =>
            new CombatSkillObservation(action.BattleId, 0, action.Now, cooling, ready)
                .WithSource(frame?.FrameStamp ?? default, io.Clock);
        private void ReleaseOwnedInput()
        {
            Exception? failure = null;
            void Retire(Action cleanup)
            {
                try { cleanup(); }
                catch (Exception error) { failure ??= error; }
            }
            try { Retire(ReleaseHeldEOwner); }
            finally
            {
                // 物理松键失败仍须退休图像/观察/技能槽，但不能据此释放输入协调器所有权。
                Retire(CancelSelection);
                Retire(CancelDefeatProbe);
                _expiredRecovery = null;
                Retire(ClearCapture);
                Retire(() => _attempts?.Dispose());
            }
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
            io.ReleaseInput();
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _readinessAction = null;
            foreach (var attempt in _evidenceAttempts) _evidence?.ForgetBefore("skill", attempt.ToString("N"));
            _evidenceAttempts.Clear();
            try
            {
                if (_input != null) _input.Dispose();
                else { CancelSelection(); CancelDefeatProbe(); _expiredRecovery = null; ClearCapture(); }
            }
            finally
            {
                // 输入先释放；样本继续归受管准备任务所有，退役等待它的finally，不早释放Mat。
                _visionPreparationCancellation?.Cancel();
                try
                {
                    _commonVisionPreparation?.GetAwaiter().GetResult();
                    _visionPreparation?.GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) { }
                catch (Exception error) { try { Logger.LogDebug(error, "视觉准备退役，保留原任务结果"); } catch { } }
                finally { _visionPreparationCancellation?.Dispose(); }
            }
        }
    }
}
