using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoFight.Config;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

public enum CombatFlowResult { Succeeded, SatisfiedExisting, Skipped, Failed, Unknown, Pending, Transferred, Deferred }
public readonly record struct CombatFlowStep(CombatFlowResult Result, bool RoundCompleted);

/// <summary>游戏观测/输入及等待边界，不在模拟测试中替换解析器或流程状态。</summary>
public interface ICombatFlowGame
{
    void BeginStep() { }
    void CheckDefeated(CancellationToken ct) { ct.ThrowIfCancellationRequested(); }
    void ReleaseHeldInput() { }
    CombatScopeObservation? ObserveScope() => null;
    bool HasPendingSkill(CombatFlowAction action) => false;
    ValueTask PrepareObservationAsync(CombatFlowAction action, string function, CancellationToken ct) => ValueTask.CompletedTask;
    ValueTask<CombatSkillAttempt?> TryRecoverExpiredSkillAsync(CombatFlowAction action, CancellationToken ct) => ValueTask.FromResult<CombatSkillAttempt?>(null);
    ValueTask<CombatFlowResult> ExecuteAsync(CombatFlowAction action, CancellationToken ct);
    object? Observe(string function, IReadOnlyList<object?> args, string actor);
    ValueTask YieldAsync(CancellationToken ct);
    ValueTask WaitAfterFailedPassAsync(CancellationToken ct) => new(Task.Delay(1000, ct));
}

public sealed partial class CombatFlowExecution : IDisposable
{
    private sealed record CoverageRequest(long Generation, long EffectVersion, double Seconds);
    private sealed record WatchProducer(CombatFlowBlock Block, int Index);
    private sealed class Frame(CombatFlowBlock block, CombatCommand? caller = null)
    {
        public CombatFlowBlock Block { get; } = block;
        public CombatCommand? Caller { get; } = caller;
        public int Index;
        public CombatFlowResult Result = CombatFlowResult.Succeeded;
        public HashSet<string> Succeeded { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Episodes { get; } = new(StringComparer.Ordinal);
        public double Deadline = double.PositiveInfinity;
        public long PassId = 1;
        public bool SuppressCallerSuccess;
        public bool FailureHandled;
        public bool InputAbandoned;
        public bool AtomicAdmitted;
        public bool RechargeSource;
        public HashSet<string> CaughtFor { get; } = new(StringComparer.Ordinal);
        public CombatResourceSample? ResourceBefore;
        public CombatCommand? PendingCommand;
        public CombatSkillAttempt? PendingAttempt;
        public int PendingIndex;
        public double PendingDeadline;
        public bool PendingNeedsFirstObservation;
        public bool PendingEndReported;
        public string? WatchFor;
        public double WatchDemand;
    }

    private readonly CombatFlowProgram _program;
    private readonly ICombatFlowGame _game;
    private readonly CombatFlowBattleState _battle;
    private readonly CombatFlowBlock _root;
    private readonly bool _rootLoops;
    private readonly bool _hasRootRounds;
    private readonly bool _yieldAtRootBoundaries;
    private readonly bool _ownsBattle;
    private readonly JsonAction? _jsonAction;
    private readonly string? _requiredOpening;
    private readonly Stack<Frame> _frames = new();
    private readonly HashSet<string> _once;
    private readonly Dictionary<string, int> _calls;
    private readonly Dictionary<string, WatchProducer> _watchProducers = new(StringComparer.Ordinal);
    private readonly CombatFlowEpisodes _episodes;
    private readonly Dictionary<string, CoverageRequest> _coverageRequests = new(StringComparer.Ordinal);
    private bool _closed;
    private bool _roundStarted;
    private bool _acted;
    private bool _madeProgress;
    public CombatFlowContext Context { get; }
    public CombatFlowStatistics RuntimeStatistics => _battle.Diagnostics.Snapshot();
    public int Round { get; private set; }
    public bool IsAtomic => _frames.Any(frame => frame.Block.Atomic && frame.AtomicAdmitted);
    // 宿主菜单保护只读本场游标和原期限，不为判断属性追加截图/识别。
    public bool HasPendingConfirmation => !_closed && Context.IsOpen && _frames.Any(frame =>
        frame.PendingAttempt != null && frame.Index == frame.PendingIndex && frame.Result == CombatFlowResult.Succeeded &&
        Context.Now < Math.Min(frame.PendingDeadline, frame.Deadline));
    public bool IsAtRootBoundary => !_frames.Any(frame => frame.PendingAttempt != null) &&
        (!_roundStarted || _frames.Count == 1 && !IsAtomic);
    internal bool NeedsCompletion => _roundStarted && _frames.TryPeek(out var frame) &&
        (frame.Index >= frame.Block.Nodes.Count || frame.Result is CombatFlowResult.Failed or CombatFlowResult.Transferred or CombatFlowResult.Deferred);
    public bool LastRoundHadAction => _acted;
    internal bool MadeProgressInRound => _madeProgress;
    public bool TakeFinishCheckRequest() => _battle.TakeFinishCheckRequest();

    public CombatFlowExecution(CombatFlowProgram program, ICombatFlowGame game, TimeProvider? clock = null)
        : this(program, game, new(clock), program.Root, program.Loop, hasRootRounds: true)
    {
        _ownsBattle = true;
    }

    internal CombatFlowExecution(CombatFlowProgram program, ICombatFlowGame game, CombatFlowBattleState battle,
        CombatFlowBlock root, bool rootLoops, bool hasRootRounds, bool yieldAtRootBoundaries = false, JsonAction? jsonAction = null)
    {
        _program = program;
        _game = new DiagnosticCombatGame(game, battle.Diagnostics);
        _battle = battle;
        Context = battle.Context;
        _once = battle.Once;
        _calls = battle.Calls;
        _episodes = battle.Episodes;
        _root = root;
        _rootLoops = rootLoops;
        _hasRootRounds = hasRootRounds;
        _yieldAtRootBoundaries = yieldAtRootBoundaries;
        _jsonAction = jsonAction;
        var entry = root.Nodes.FirstOrDefault()?.Command;
        _requiredOpening = entry?.Method == Method.Call && entry.HasFlag("required") && entry.Options.GetValueOrDefault("once") == "battle"
            ? entry.Args![0] : null;
    }

    internal bool? EvaluateCondition(ConditionEvaluator.CompiledCondition condition, string actor) =>
        condition.EvaluateBoolean((name, args) => Observe(name, args, _frames.TryPeek(out var frame) ? frame : new(_root), actor,
            !_roundStarted && _hasRootRounds ? Round + 1 : Round));

    public async ValueTask<CombatFlowResult> RunRoundAsync(CancellationToken ct = default)
    {
        while (true)
        {
            var step = await StepAsync(ct);
            if (step.RoundCompleted) return step.Result;
        }
    }

    public ValueTask<CombatFlowStep> StepAsync(CancellationToken ct = default) => StepAsync(ct, beginObservationFrame: true);

    internal async ValueTask<CombatFlowStep> StepAsync(CancellationToken ct, bool beginObservationFrame)
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        CombatFlowStep? result = null;
        try
        {
            result = await StepCoreAsync(ct, beginObservationFrame);
            return result.Value;
        }
        catch (Exception error)
        {
            foreach (var frame in _frames)
                RecordPendingEnd(frame, error is OperationCanceledException ? "cancelled" : "exception");
            throw;
        }
        finally { _battle.Diagnostics.Step(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds, result); }
    }

    private async ValueTask<CombatFlowStep> StepCoreAsync(CancellationToken ct, bool beginObservationFrame)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        ct.ThrowIfCancellationRequested();
        if (beginObservationFrame) _game.BeginStep();
        if (Context.HasScopedEffects && _game.ObserveScope() is { } scope) Context.ObserveScope(scope);
        RefreshDeferredMaintenance();
        if (!_roundStarted)
        {
            _roundStarted = true;
            _acted = false;
            _madeProgress = false;
            if (_hasRootRounds) Round++;
            _frames.Push(CreateFrame(_root));
        }
        for (var transitions = 0; transitions < 64; transitions++)
        {
            ct.ThrowIfCancellationRequested();
            // 新物理请求只优先确认一次；Unknown 后下个 Step 恢复维护优先级。
            var firstConfirmation = _frames.TryPeek(out var pendingFrame) && pendingFrame.PendingNeedsFirstObservation;
            if (firstConfirmation) pendingFrame!.PendingNeedsFirstObservation = false;
            if (!firstConfirmation || pendingFrame!.PendingAttempt == null ||
                pendingFrame.Index != pendingFrame.PendingIndex || Context.Now >= pendingFrame.PendingDeadline ||
                _frames.Any(active => active.Result != CombatFlowResult.Succeeded || Context.Now >= active.Deadline || !RequirementsHold(active)))
                await ApplyDueWatchAsync(ct);
            foreach (var displaced in _frames.Where(active => active.PendingCommand != null && active.Index != active.PendingIndex))
                AbandonPending(displaced);
            if (_frames.FirstOrDefault(active => active.Block.Atomic && active.AtomicAdmitted &&
                    active.Result == CombatFlowResult.Succeeded && !RequirementsHold(active)) is { } invalidAtomic)
                foreach (var active in _frames)
                {
                    active.Result = CombatFlowResult.Failed;
                    if (active == invalidAtomic) break;
                }
            var frame = _frames.Peek();
            if (frame.Block.Atomic && !frame.AtomicAdmitted)
            {
                frame.AtomicAdmitted = true;
                if (frame.Block.EntryCoverageRecords.Any(record => Context.Remaining(record) is not { } remaining ||
                        remaining <= frame.Block.EstimatedSeconds + CombatFlowPolicy.RecoverySeconds) ||
                    !RequirementsFit(frame, frame.Block.EstimatedSeconds + CombatFlowPolicy.RecoverySeconds))
                    frame.Result = CombatFlowResult.Failed;
            }
            if (Context.Now >= frame.Deadline) frame.Result = CombatFlowResult.Failed;
            if (!RequirementsHold(frame)) frame.Result = CombatFlowResult.Failed;
            if (frame.Index >= frame.Block.Nodes.Count || frame.Result is CombatFlowResult.Failed or CombatFlowResult.Transferred or CombatFlowResult.Deferred)
            {
                if (frame.Result == CombatFlowResult.Succeeded && !frame.SuppressCallerSuccess &&
                    (frame.RechargeSource && frame.Caller != null && !frame.CaughtFor.Contains(frame.Caller.Name) ||
                     frame.WatchFor is { } watched && (Context.Remaining(watched) is not { } coverage || coverage <= frame.WatchDemand)))
                    frame.Result = CombatFlowResult.Failed;
                if (frame.Result is CombatFlowResult.Failed or CombatFlowResult.Transferred or CombatFlowResult.Deferred && !frame.InputAbandoned)
                {
                    RecordPendingEnd(frame, "frame-" + frame.Result.ToString().ToLowerInvariant());
                    _game.ReleaseHeldInput();
                    frame.InputAbandoned = true;
                }
                if (frame.Result is CombatFlowResult.Failed or CombatFlowResult.Deferred && TryBeginRecovery(frame)) continue;
                var result = FinishFrame();
                if (_frames.Count == 0)
                {
                    _roundStarted = false;
                    if (!_madeProgress) await _game.YieldAsync(ct);
                    return new(result, true);
                }
                if (_yieldAtRootBoundaries && _frames.Count == 1) return new(result, false);
                continue;
            }
            var node = frame.Block.Nodes[frame.Index++];
            var command = node.Command;
            var confirming = ReferenceEquals(frame.PendingCommand, command) && frame.PendingAttempt != null;
            if (confirming && Context.Now >= frame.PendingDeadline)
            {
                CompleteNode(frame, command, CombatFlowResult.Failed);
                continue;
            }
            if (!confirming && !command.IsActiveInRound(Round))
            {
                // 普通 round 仍是路线筛选；未完成 once 的关键开场不能借筛选假装已完成。
                if (_frames.Any(active => active.Caller?.Options.GetValueOrDefault("once") == "battle"))
                    CompleteNode(frame, command, CombatFlowResult.Skipped);
                continue;
            }
            if (command.Method == Method.Branch)
            {
                var selected = node.Condition!.EvaluateBoolean((name, args) => Observe(name, args, frame, command.Name));
                var key = selected == true ? "then" : selected == false ? "else" : "unknown";
                if (command.Options.TryGetValue(key, out var target))
                {
                    if (!AdmitAtomicChild(_program.Blocks[target])) continue;
                    if (_frames.Count >= 32) throw new InvalidOperationException("片段调用深度超过上限");
                    _calls[target] = _calls.GetValueOrDefault(target) + 1;
                    _frames.Push(CreateFrame(_program.Blocks[target], command, frame.Deadline));
                }
                else CompleteNode(frame, command, selected == null ? CombatFlowResult.Unknown : CombatFlowResult.Skipped);
                continue;
            }
            var calledBlock = node.Block ?? (command.Method == Method.Call ? _program.Blocks[command.Args![0]] : null);
            if (!confirming && node.Condition != null)
            {
                var alreadyCompleted = calledBlock != null && command.Options.GetValueOrDefault("once") == "battle" && _once.Contains(calledBlock.Name);
                var eligible = alreadyCompleted
                    ? node.Condition.EvaluateBoolean((name, args) => Observe(name, args, frame, command.Name))
                    : await EvaluateWithPreparationAsync(node.Condition, command.Name, frame, ct, calledBlock);
                if (eligible != true)
                {
                    CompleteNode(frame, command, CombatFlowResult.Skipped);
                    continue;
                }
            }
            if (command.Method == Method.Call || node.Block != null)
            {
                var block = node.Block ?? _program.Blocks[command.Args![0]];
                if (command.Options.GetValueOrDefault("once") == "battle" && _once.Contains(block.Name))
                {
                    CompleteNode(frame, command, CombatFlowResult.SatisfiedExisting);
                    continue;
                }
                if (!AdmitAtomicChild(block)) continue;
                if (_frames.Count >= 32) throw new InvalidOperationException("片段调用深度超过上限");
                var child = CreateFrame(block, command, frame.Deadline);
                if (command.Options.GetValueOrDefault("resume") == "entry" || command.Options.ContainsKey("once") ||
                    command.Options.ContainsKey("timeout") || command.Options.ContainsKey("attempts"))
                {
                    var objective = "call:" + block.Name;
                    if (!_episodes.TrySpend(objective, Context.Now, CombatFlowPolicy.Timeout(command),
                            CombatFlowPolicy.Attempts(command), out var deadline))
                    {
                        frame.Episodes.Remove(objective);
                        CompleteNode(frame, command, CombatFlowResult.Failed);
                        continue;
                    }
                    child.Deadline = Math.Min(child.Deadline, deadline);
                    if (command.Options.GetValueOrDefault("resume") == "entry")
                    {
                        frame.Episodes.Add(objective);
                        frame.Deadline = Math.Min(frame.Deadline, deadline);
                    }
                    else child.Episodes.Add(objective);
                }
                _calls[block.Name] = _calls.GetValueOrDefault(block.Name) + 1;
                _frames.Push(child);
                continue;
            }
            if (command.Method == Method.Return)
            {
                if (frame.Block.Nodes.Skip(frame.Index).Any(next => Required(next.Command))) frame.Result = CombatFlowResult.Failed;
                frame.Index = frame.Block.Nodes.Count;
                continue;
            }
            if (command.Method == Method.JumpTo)
            {
                var target = _program.Blocks[command.Args!.Single()];
                var objective = "jump:" + target.Name;
                if (!_episodes.TrySpend(objective, Context.Now, CombatFlowPolicy.Timeout(command),
                        CombatFlowPolicy.Attempts(command), out var deadline))
                {
                    frame.Result = CombatFlowResult.Failed;
                    continue;
                }
                _game.ReleaseHeldInput();
                PopFrame("displaced-jump"); // 不执行旧帧的完成记录、once、返回或 onfail。
                var replacement = CreateFrame(target, frame.Caller, Math.Min(frame.Deadline, deadline));
                replacement.SuppressCallerSuccess = true;
                replacement.WatchFor = frame.WatchFor;
                replacement.WatchDemand = frame.WatchDemand;
                replacement.RechargeSource = frame.RechargeSource;
                replacement.Episodes.Add(objective);
                _calls[target.Name] = _calls.GetValueOrDefault(target.Name) + 1;
                _frames.Push(replacement);
                continue;
            }
            if (command.Method == Method.Record)
            {
                var duration = CombatFlowProgram.Number(command, "duration", true) ?? _program.Timing(command)?.Duration;
                Context.TryRecord(command.Args!.Single(), Context.Now, duration);
                continue;
            }
            if (command.Method == Method.Check)
            {
                _battle.FinishCheckRequested = true;
                CompleteNode(frame, command, CombatFlowResult.Succeeded);
                return new(CombatFlowResult.Succeeded, false);
            }
            if (command.Method.IsFlowControl) throw new InvalidOperationException("流程命令尚未接通：" + command.Method.Alias[0]);
            if ((command.Options.GetValueOrDefault("watch") ?? command.Options.GetValueOrDefault("maintain")) is { } maintenanceChannel &&
                _deferredMaintenance.ContainsKey(maintenanceChannel))
            {
                CompleteNode(frame, command, CombatFlowResult.Deferred);
                continue;
            }
            if (command.Options.TryGetValue("keep", out var keep) && Context.Remaining(keep) is not > 0)
            {
                CompleteNode(frame, command, CombatFlowResult.Skipped);
                continue;
            }
            var actionDeadline = frame.Deadline;
            string? actionEpisode = null;
            if (!confirming && command.Options.TryGetValue("maintain", out var maintain))
            {
                var demand = Math.Max(_program.CoverageAfter(frame.Block, frame.Index, maintain),
                    _coverageRequests.GetValueOrDefault(maintain)?.Seconds ?? 0);
                if (AtomicRemainingSeconds(includeCurrent: false) is { } remainingAtomic && frame.Block.CoverageRecords.Contains(maintain))
                    demand = Math.Max(demand, remainingAtomic + CombatFlowPolicy.RecoverySeconds);
                if (Context.Remaining(maintain) is { } remaining &&
                    remaining > Math.Max(demand, CombatFlowProgram.Number(command, "before") ?? 0))
                {
                    if (command.Options.GetValueOrDefault("watch") == maintain)
                        _watchProducers.TryAdd(maintain, new(frame.Block, frame.Index - 1));
                    _coverageRequests.Remove(maintain);
                    _episodes.Resolve("coverage:" + maintain);
                    CompleteNode(frame, command, CombatFlowResult.SatisfiedExisting);
                    continue;
                }
                var watchFrame = _frames.FirstOrDefault(active => active.WatchFor == maintain);
                var deadline = watchFrame?.Deadline ?? double.PositiveInfinity;
                if (watchFrame == null)
                {
                    var admitted = await TrySpendCoverageAsync(maintain, command, frame.Deadline, demand, ct);
                    if (admitted == null)
                    {
                        CompleteNode(frame, command, CombatFlowResult.Failed);
                        continue;
                    }
                    deadline = admitted.Value;
                }
                actionDeadline = Math.Min(actionDeadline, deadline);
                if (watchFrame == null) actionEpisode = "coverage:" + maintain;
                if (_program.Timing(command)?.Duration is { } fullWindow && fullWindow <= demand)
                {
                    CompleteNode(frame, command, CombatFlowResult.Failed);
                    continue;
                }
            }
            CombatFlowRecord? refreshRecord = null;
            if (command.Options.TryGetValue("refresh", out var refreshName))
            {
                refreshRecord = Context.Find(refreshName);
                if (refreshRecord == null || !_program.CanRefresh(command, refreshRecord) ||
                    Context.Remaining(refreshName) is not { } refreshRemaining ||
                    refreshRemaining <= CombatFlowPolicy.ActionSeconds(command) + CombatFlowPolicy.RecoverySeconds)
                {
                    CompleteNode(frame, command, CombatFlowResult.Unknown);
                    continue;
                }
            }
            var keepRecord = keep == null ? null : Context.Find(keep);
            var requiredWindow = CombatFlowPolicy.CoverageSeconds(command);
            var atomicRemainder = AtomicRemainingSeconds(includeCurrent: true);
            if (atomicRemainder is { } remainingAtomicSeconds)
                requiredWindow = Math.Max(requiredWindow, remainingAtomicSeconds + CombatFlowPolicy.RecoverySeconds);
            bool CanStart()
            {
                if (Context.HasScopedEffects && _game.ObserveScope() is { } latestScope) Context.ObserveScope(latestScope);
                return !_closed && LiveRequirementsHold(frame) &&
                    (atomicRemainder == null || RequirementsFit(frame, atomicRemainder.Value + CombatFlowPolicy.RecoverySeconds)) &&
                    (keep == null || keepRecord != null && Context.CanConsume(keep, keepRecord, requiredWindow));
            }
            bool CanContinue()
            {
                if (Context.HasScopedEffects && _game.ObserveScope() is { } latestScope) Context.ObserveScope(latestScope);
                // 已开始动作只检查仍然成立的条件，不反复索要包含已消耗时长的整份准入余量。
                var live = confirming ? _frames.All(active => active.Result == CombatFlowResult.Succeeded &&
                    Context.Now < active.Deadline && RequirementsHold(active)) : LiveRequirementsHold(frame);
                return !_closed && live && (keep == null || Context.Remaining(keep) > 0);
            }
            var action = new CombatFlowAction(command, Context, confirming ? CanContinue : CanStart,
                confirming ? Math.Min(actionDeadline, frame.PendingDeadline) :
                    Math.Min(actionDeadline, Context.Now + CombatFlowPolicy.ActionTimeout(command, _program.Timing(command))),
                command.Method == Method.Skill || command.Method == Method.Burst ? null : MaintenanceIsDue, CanContinue,
                canReuseConfirmedActor: IsAtomic && (command.Method == Method.Wait || command.Method == Method.MoveBy || command.Method == Method.KeyUp),
                confirmationAttempt: confirming ? frame.PendingAttempt : null);
            if (!action.CanStart)
            {
                CompleteNode(frame, command, CombatFlowResult.Skipped);
                continue;
            }
            var hasPendingSkill = _game.HasPendingSkill(action);
            if (!confirming && !hasPendingSkill && command.Method == Method.Burst && (Required(command) || _program.Recharge(command) != null) &&
                ConditionEvaluator.Truth(_game.Observe("q-ready", [], command.Name)) != true &&
                ConditionEvaluator.Truth(_game.Observe("q-cd", [], command.Name)) != true &&
                (_game.Observe("q-energy-low", [], command.Name) == null || _game.Observe("q-cd", [], command.Name) == null))
            {
                var goal = "observe:q:" + command.Name;
                if (!_episodes.TrySpend(goal, Context.Now, CombatFlowPolicy.Timeout(command), CombatFlowPolicy.Attempts(command), out var deadline))
                {
                    CompleteNode(frame, command, CombatFlowResult.Unknown);
                    continue;
                }
                var preparation = new CombatFlowAction(command, Context, () => action.CanStart,
                    Math.Min(deadline, Context.Now + action.RemainingBudget));
                await _game.PrepareObservationAsync(preparation, "q-ready", ct);
                ct.ThrowIfCancellationRequested();
                if (!action.CanStart)
                {
                    CompleteNode(frame, command, CombatFlowResult.Skipped);
                    continue;
                }
                if (_game.Observe("q-ready", [], command.Name) is true ||
                    _game.Observe("q-energy-low", [], command.Name) is bool && _game.Observe("q-cd", [], command.Name) is bool)
                    _episodes.Resolve(goal);
            }
            if (!confirming && !hasPendingSkill && await TryBeginRechargeAsync(frame, command, keep, action, ct)) continue;
            var actionResult = await _game.ExecuteAsync(action, ct);
            ct.ThrowIfCancellationRequested();
            if (actionEpisode != null && actionResult is CombatFlowResult.Pending or CombatFlowResult.Deferred)
                _episodes.Defer(actionEpisode);
            if (actionResult == CombatFlowResult.Pending && action.PendingAttempt is { } pendingAttempt)
            {
                if (frame.PendingAttempt?.AttemptId != pendingAttempt.AttemptId)
                {
                    frame.PendingNeedsFirstObservation = true;
                    frame.PendingEndReported = false;
                }
                frame.PendingCommand = command;
                frame.PendingAttempt = pendingAttempt;
                frame.PendingDeadline = Math.Min(action.AbsoluteDeadline, pendingAttempt.Deadline);
                frame.PendingIndex = --frame.Index;
                _game.ReleaseHeldInput();
                await _game.YieldAsync(ct);
                return new(actionResult, false);
            }
            if (actionResult == CombatFlowResult.Succeeded && action.EffectiveInputAt == null) actionResult = CombatFlowResult.Unknown;
            if (actionResult == CombatFlowResult.Succeeded)
            {
                _acted = true;
                _madeProgress |= command.Method != Method.Wait || CombatFlowPolicy.ActionSeconds(command) > 0;
                ReportMaintenanceProgress(command);
                if (command.Method == Method.Burst)
                {
                    _episodes.Resolve("burst:" + command.Name);
                    _episodes.Resolve("observe:q:" + command.Name);
                }
                if (command.Options.TryGetValue("record", out var record))
                {
                    // 技能使用保守的调用前时点；普通动作记录完成边界，不将迟到确认当作效果起点。
                    var at = command.Method == Method.Skill || command.Method == Method.Burst ? action.EffectiveInputAt!.Value : Context.Now;
                    var source = _program.RecordSource(command);
                    if (source.Scope is "target" or "range" && _game.ObserveScope() is { } scopeAfterInput) Context.ObserveScope(scopeAfterInput);
                    Context.TryRecord(record, at, _program.Timing(command)?.Duration, Context.BindScope(source, action.EffectiveInputAt!.Value));
                    if (command.Options.GetValueOrDefault("watch") == record)
                        _watchProducers[record] = new(frame.Block, frame.Index - 1);
                    if (Context.Remaining(record) is { } coverage && coverage >
                        Math.Max(_coverageRequests.GetValueOrDefault(record)?.Seconds ?? 0,
                            CombatFlowProgram.Number(command, "before") ?? 0))
                    {
                        _coverageRequests.Remove(record);
                        _episodes.Resolve("coverage:" + record);
                    }
                }
                if (refreshRecord != null && (Context.Remaining(refreshName!) is not > 0 ||
                    !Context.TryRefresh(refreshName!, refreshRecord.Generation, action.EffectiveInputAt!.Value, refreshRecord.Duration!.Value,
                        refreshRecord.EffectVersion)))
                    actionResult = CombatFlowResult.Unknown;
                if (actionResult == CombatFlowResult.Succeeded && _program.Feed(command) is { } feed)
                {
                    // 连续执行接球站位/等待；不把这个估计片段的完成记作粒子已到账。
                    var feedAction = new CombatFlowAction(feed, Context, () => !_closed && LiveRequirementsHold(frame) &&
                        (keep == null || Context.Remaining(keep) > CombatFlowPolicy.CoverageSeconds(feed)),
                        Math.Min(actionDeadline, Context.Now + action.RemainingBudget),
                        continuation: () => !_closed && LiveRequirementsHold(frame) && (keep == null || Context.Remaining(keep) > 0));
                    actionResult = await _game.ExecuteAsync(feedAction, ct);
                    ct.ThrowIfCancellationRequested();
                    if (actionResult == CombatFlowResult.Succeeded && feedAction.InputAt == null) actionResult = CombatFlowResult.Unknown;
                    if (actionResult == CombatFlowResult.Succeeded) frame.CaughtFor.Add(feed.Name);
                }
            }
            CompleteNode(frame, command, actionResult);
            return new(actionResult, false);
        }
        await _game.YieldAsync(ct);
        return new(CombatFlowResult.Skipped, false);
    }

    private CombatFlowResult FinishFrame()
    {
        var frame = PopFrame("frame-" + _frames.Peek().Result.ToString().ToLowerInvariant());
        if (frame.Block.Atomic || _frames.Count == 0) _game.ReleaseHeldInput();
        if (frame.Result == CombatFlowResult.Deferred)
        {
            foreach (var objective in frame.Episodes) _episodes.Defer(objective);
            // call 型 watch 在入栈时单独消费 coverage；等待已知 CD 不是一次新施放失败。
            // 只退还这次尝试，不能把 coverage 放入成功即 Resolve 的 Episodes 或延长原截止时间。
            if (frame.WatchFor != null) _episodes.Defer("coverage:" + frame.WatchFor);
        }
        if (frame.Result == CombatFlowResult.Succeeded)
        {
            foreach (var objective in frame.Episodes) _episodes.Resolve(objective);
            if (frame.Block.CompletionRecord is { } record) Context.TryRecord(record, Context.Now);
            if (!frame.SuppressCallerSuccess && frame.Caller?.Options.GetValueOrDefault("once") == "battle") _once.Add(frame.Block.Name);
        }
        var result = frame.SuppressCallerSuccess && frame.Result == CombatFlowResult.Succeeded
            ? CombatFlowResult.Transferred : frame.Result;
        if (result == CombatFlowResult.Succeeded && frame.RechargeSource && frame.Caller != null && frame.ResourceBefore is { } before &&
            ReadEnergySample(frame.Caller.Name) is { } after && before.FrameId != after.FrameId && after.CapturedAt >= before.CapturedAt)
            _episodes.ReportProgress("burst:" + frame.Caller.Name, after.Value > before.Value + 0.001);
        if (_frames.TryPeek(out var parent) && frame.Caller != null)
        {
            parent.CaughtFor.UnionWith(frame.CaughtFor);
            if (result != CombatFlowResult.Succeeded && frame.Caller.Options.GetValueOrDefault("resume") == "entry")
                parent.Episodes.Remove("call:" + frame.Caller.Args![0]);
            if (frame.RechargeSource && result == CombatFlowResult.Succeeded)
                parent.Index--; // 回到请求 Q 的原命令，重新观测；不把供能成功记成 Q 成功。
            else if (result == CombatFlowResult.Succeeded && frame.Caller.Options.GetValueOrDefault("resume") == "entry")
            {
                parent.Index = 0;
                parent.PassId++;
                parent.Succeeded.Clear();
                parent.CaughtFor.Clear();
            }
            else CompleteNode(parent, frame.Caller, result);
        }
        if (frame.WatchFor != null && result != CombatFlowResult.Succeeded && _frames.TryPeek(out var interrupted))
            interrupted.Result = result is CombatFlowResult.Transferred or CombatFlowResult.Deferred ? result : CombatFlowResult.Failed;
        return result;
    }

    private async ValueTask<bool> TryBeginRechargeAsync(Frame frame, CombatCommand command, string? keep,
        CombatFlowAction action, CancellationToken ct)
    {
        if (_program.Recharge(command) is not { } plan ||
            ConditionEvaluator.Truth(_game.Observe("q-ready", [], command.Name)) == true) return false;
        var low = ConditionEvaluator.Truth(_game.Observe("q-energy-low", [], command.Name));
        var cooling = ConditionEvaluator.Truth(_game.Observe("q-cd", [], command.Name));
        if (low != true || cooling != false)
        {
            CompleteNode(frame, command, cooling == true ? CombatFlowResult.Deferred
                : low == false ? CombatFlowResult.Skipped : CombatFlowResult.Unknown);
            return true;
        }
        if (!_episodes.TrySpend("burst:" + command.Name, Context.Now, CombatFlowPolicy.Timeout(command),
                CombatFlowPolicy.Attempts(command), out var deadline, CombatFlowPolicy.NoProgress(command)))
        {
            CompleteNode(frame, command, CombatFlowResult.Failed);
            return true;
        }
        if (keep != null && Context.Remaining(keep) is { } remaining)
            deadline = Math.Min(deadline, Context.Now + remaining - CombatFlowPolicy.CoverageSeconds(command));
        var ready = false;
        foreach (var producer in plan.Producers)
        {
            if (!producer.Command.IsActiveInRound(Round) || producer.Condition != null &&
                producer.Condition.EvaluateBoolean((name, args) => Observe(name, args, frame, producer.Command.Name)) != true) continue;
            var observed = ConditionEvaluator.Truth(_game.Observe("e-ready", [], producer.Command.Name));
            if (observed == null)
            {
                var preparation = new CombatFlowAction(producer.Command, Context, () => action.CanStart,
                    Math.Min(deadline, Context.Now + action.RemainingBudget));
                await _game.PrepareObservationAsync(preparation, "e-ready", ct);
                ct.ThrowIfCancellationRequested();
                observed = ConditionEvaluator.Truth(_game.Observe("e-ready", [], producer.Command.Name));
            }
            if (action.CanStart && observed == true) { ready = true; break; }
        }
        if (!ready)
        {
            CompleteNode(frame, command, CombatFlowResult.Failed);
            return true;
        }
        var source = CreateFrame(plan.Source, command, Math.Min(frame.Deadline, deadline));
        source.RechargeSource = true;
        source.ResourceBefore = ReadEnergySample(command.Name);
        _calls[plan.Source.Name] = _calls.GetValueOrDefault(plan.Source.Name) + 1;
        _frames.Push(source);
        return true;
    }

    private CombatResourceSample? ReadEnergySample(string actor)
    {
        var sample = _game.Observe("q-energy-sample", [], actor) as CombatResourceSample;
        return sample != null && sample.BattleId == Context.BattleId && sample.Actor == actor &&
            double.IsFinite(sample.CapturedAt) && sample.CapturedAt <= Context.Now && Context.Now - sample.CapturedAt <= 0.5 &&
            double.IsFinite(sample.Value) && sample.Value is >= 0 and <= 1 ? sample : null;
    }

    private bool TryBeginRecovery(Frame frame)
    {
        if (frame.FailureHandled || frame.Block.OnFail is not { } target) return false;
        frame.FailureHandled = true;
        var objective = "onfail:" + target;
        if (!_episodes.TrySpend(objective, Context.Now, CombatFlowPolicy.EpisodeTimeoutSeconds,
                CombatFlowPolicy.EpisodeAttempts, out var deadline)) return false;
        if (_frames.Count >= 32) return false;
        // 恢复段有独立的有界保底权限，但没有把结果回写成原片段成功的调用地址。
        var recovery = CreateFrame(_program.Blocks[target], deadline: deadline);
        recovery.Episodes.Add(objective);
        _calls[target] = _calls.GetValueOrDefault(target) + 1;
        _frames.Push(recovery);
        return true;
    }

    private async ValueTask ApplyDueWatchAsync(CancellationToken ct)
    {
        if (IsAtomic || WaitingForRequiredOpening()) return;
        // 首次开场要求未完成时，维护转移不能越过它。
        if (_frames.Any(frame => frame.Result is CombatFlowResult.Failed or CombatFlowResult.Transferred or CombatFlowResult.Deferred || frame.Caller?.Options.GetValueOrDefault("once") == "battle")) return;
        var root = _frames.Last();
        foreach (var (name, producer) in _watchProducers.OrderBy(pair =>
                     (Context.Remaining(pair.Key) ?? double.PositiveInfinity) -
                     (CombatFlowProgram.Number(pair.Value.Block.Nodes[pair.Value.Index].Command, "before") ?? 0)))
        {
            if (_deferredMaintenance.ContainsKey(name)) continue;
            if (_frames.Any(frame => frame.WatchFor == name)) continue;
            if (Context.Remaining(name) is not { } remaining) continue;
            var sourceCommand = producer.Block.Nodes[producer.Index].Command;
            var callMode = sourceCommand.Options.GetValueOrDefault("watch-mode") == "call";
            // 仅访问当前调用链中可达的维护作用域，不进入未选择分支或未调用定义体。
            var owner = callMode ? _frames.Peek() : _frames.FirstOrDefault(frame => frame.Block == producer.Block) ??
                _frames.FirstOrDefault(frame => frame.Block.WatchPoints.ContainsKey(name));
            if (owner == null) continue;
            var points = owner.Block.WatchPoints.GetValueOrDefault(name) ?? [];
            var before = CombatFlowProgram.Number(producer.Block.Nodes[producer.Index].Command, "before") ?? 0;
            var active = _frames.Peek();
            var demand = _program.CoverageAfter(active.Block, active.Index, name);
            if (_coverageRequests.TryGetValue(name, out var pending))
            {
                var current = Context.Find(name);
                if (current?.Generation != pending.Generation || current.EffectVersion != pending.EffectVersion)
                    _coverageRequests.Remove(name);
                else demand = Math.Max(demand, pending.Seconds);
            }
            if (remaining > Math.Max(before, demand)) continue;
            DeferConflictingMaintenance(name);
            if (_deferredMaintenance.ContainsKey(name)) continue;
            if (demand > 0 && Context.Find(name) is { } requested)
                _coverageRequests[name] = new(requested.Generation, requested.EffectVersion, demand);
            if (callMode)
            {
                var admitted = _frames.Count >= 32 ? null : await TrySpendCoverageAsync(name, sourceCommand, owner.Deadline, demand, ct);
                if (admitted == null)
                {
                    owner.Result = CombatFlowResult.Failed;
                    return;
                }
                var target = _program.Blocks[sourceCommand.Options["watch-target"]];
                _game.ReleaseHeldInput();
                var maintenance = CreateFrame(target, deadline: Math.Min(owner.Deadline, admitted.Value));
                maintenance.WatchFor = name;
                maintenance.WatchDemand = Math.Max(before, demand);
                _calls[target.Name] = _calls.GetValueOrDefault(target.Name) + 1;
                _frames.Push(maintenance);
                return;
            }
            var producerIndex = owner.Block == producer.Block ? Array.IndexOf(points, producer.Index) : -1;
            var next = -1;
            var nextRound = Round;
            // 只检查当前及下一根轮；不可借重复跳转跨越任意未来条件。
            for (var offset = 1; offset <= points.Length && next < 0; offset++)
            {
                var candidate = points[(producerIndex + offset) % points.Length];
                var node = owner.Block.Nodes[candidate];
                if (demand > 0 && _program.Timing(node.Command)?.Duration is { } duration && duration <= demand) continue;
                for (var candidateRound = Round; candidateRound <= Round + (_rootLoops ? 1 : 0); candidateRound++)
                {
                    if (candidateRound == Round && candidate < owner.Index) continue;
                    if (!node.Command.IsActiveInRound(candidateRound)) continue;
                    if (node.Condition != null && node.Condition.EvaluateBoolean((function, args) =>
                            Observe(function, args, owner, node.Command.Name, candidateRound)) != true) continue;
                    next = candidate;
                    nextRound = candidateRound;
                    break;
                }
            }
            if (next < 0)
            {
                if (demand > 0) continue; // 交给消费者的前提/失败分支，不为不可能的窗口反复施放。
                LastMaintenanceDecision = "当前及下一轮没有合法维护点，退出受保护片段并执行其声明恢复：" + name;
                while (_frames.Peek() != owner) PopFrame("displaced-watch-failed");
                owner.Result = CombatFlowResult.Failed;
                return;
            }
            if (_frames.Peek() == owner && owner.Index == next && nextRound == Round) return;
            _game.ReleaseHeldInput();
            if (nextRound != Round && owner != root)
            {
                // 子片段末端跨轮只能走真正的根回边，重新经过根依赖/once 门槛。
                while (_frames.Count > 1) PopFrame("displaced-watch-round");
                Round = nextRound;
                root.Succeeded.Clear();
                root.Index = 0;
                return;
            }
            while (_frames.Peek() != owner) PopFrame("displaced-watch");
            if (nextRound != Round) { Round = nextRound; root.Succeeded.Clear(); }
            owner.Index = next;
            return;
        }
    }

    private object? Observe(string function, IReadOnlyList<object?> args, Frame frame, string actor, int? candidateRound = null)
    {
        var name = args.FirstOrDefault()?.ToString() ?? "";
        if (args.Count == 1 && function is "q-ready" or "q-energy-low" or "q-cd" or "e-ready" or "e-cd" or "low-hp" or "onfield" or "in-party" &&
            DefaultAutoFightConfig.CombatAvatarAliasToNameMap.TryGetValue(name, out var canonical) && canonical != name)
            args = [canonical];
        return function switch
        {
            "record-exists" => Context.Find(name) != null,
            "record-age" => Context.Find(name) is { } record ? Context.Now - record.OccurredAt : null,
            "record-remaining" => Context.Remaining(name),
            "record-active" => Context.Find(name) == null ? false : Context.Remaining(name) is { } remaining ? remaining > 0 : null,
            "succeeded" => (candidateRound == null || candidateRound == Round) && frame.Succeeded.Contains(name),
            "round-odd" => _hasRootRounds ? (candidateRound ?? Round) % 2 != 0 : null,
            "call-index" => _calls.GetValueOrDefault(name),
            "t" => Context.Now,
            "since" or "count" or "last-exec" or "battle-time" =>
                _battle.History.Resolve(function, args, _jsonAction?.Index ?? 0, _jsonAction?.Name ?? "", Context.Now),
            _ => _game.Observe(function, args, actor)
        };
    }

    private bool MaintenanceIsDue()
    {
        if (IsAtomic || WaitingForRequiredOpening() || _frames.Any(frame => frame.Caller?.Options.GetValueOrDefault("once") == "battle")) return false;
        return _watchProducers.Any(pair => !_frames.Any(frame => frame.WatchFor == pair.Key) &&
            !_deferredMaintenance.ContainsKey(pair.Key) &&
            Context.Remaining(pair.Key) is { } remaining && remaining <=
            (CombatFlowProgram.Number(pair.Value.Block.Nodes[pair.Value.Index].Command, "before") ?? 0));
    }

    private bool WaitingForRequiredOpening() => _requiredOpening != null && !_once.Contains(_requiredOpening);

    private double? AtomicRemainingSeconds(bool includeCurrent)
    {
        var atomic = _frames.LastOrDefault(frame => frame.Block.Atomic && frame.AtomicAdmitted && frame.Result == CombatFlowResult.Succeeded);
        if (atomic == null) return null;
        double seconds = 0;
        foreach (var frame in _frames)
        {
            seconds += _program.EstimateRemaining(frame.Block, Math.Max(0, frame.Index - (includeCurrent ? 1 : 0)));
            includeCurrent = false;
            if (frame == atomic) break;
        }
        return seconds;
    }

    private bool AdmitAtomicChild(CombatFlowBlock child)
    {
        var atomic = _frames.LastOrDefault(frame => frame.Block.Atomic && frame.AtomicAdmitted && frame.Result == CombatFlowResult.Succeeded);
        if (atomic == null) return true;
        var seconds = child.EstimatedSeconds;
        var records = new HashSet<string>(child.EntryCoverageRecords, StringComparer.Ordinal);
        foreach (var frame in _frames)
        {
            seconds += _program.EstimateRemaining(frame.Block, frame.Index);
            records.UnionWith(frame.Block.EntryCoverageRecords);
            if (frame == atomic) break;
        }
        if (Context.Now + seconds <= atomic.Deadline && records.All(record => Context.Remaining(record) is { } remaining &&
                remaining > seconds + CombatFlowPolicy.RecoverySeconds)) return true;
        // 还未进入所选分支/调用，也未发其首个输入。整段返回失败，交给声明的恢复或外层保底。
        foreach (var frame in _frames)
        {
            frame.Result = CombatFlowResult.Failed;
            if (frame == atomic) break;
        }
        return false;
    }

    private bool RequirementsHold(Frame frame) => frame.Block.Requires == null ||
        frame.Block.Requires.EvaluateBoolean((name, args) => Observe(name, args, frame,
            frame.Caller?.Name ?? CombatScriptParser.CurrentAvatarName)) == true;

    private bool LiveRequirementsHold(Frame frame) => RequirementsHold(frame) && _frames.All(active =>
        active == frame || !active.Block.Atomic || !active.AtomicAdmitted || active.Result != CombatFlowResult.Succeeded || RequirementsHold(active));

    private bool RequirementsFit(Frame frame, double horizon) => frame.Block.Requires == null ||
        frame.Block.Requires.EvaluateBoolean((name, args) => name switch
        {
            "t" => Context.Now + horizon,
            "record-active" => Context.Find(args[0]!.ToString()!) == null ? false :
                Context.Remaining(args[0]!.ToString()!) is { } remaining ? remaining > horizon : null,
            "record-remaining" => Context.Remaining(args[0]!.ToString()!) is { } remaining ? Math.Max(0, remaining - horizon) : null,
            "record-age" => Context.Find(args[0]!.ToString()!) is { } record ? Context.Now + horizon - record.OccurredAt : null,
            _ => Observe(name, args, frame, frame.Caller?.Name ?? CombatScriptParser.CurrentAvatarName)
        }) == true;

    private Frame CreateFrame(CombatFlowBlock block, CombatCommand? caller = null, double deadline = double.PositiveInfinity) =>
        new(block, caller) { Deadline = Math.Min(deadline, Context.Now + block.Timeout) };

    private static bool Required(CombatCommand command) => command.HasFlag("required") || command.HasFlag("refresh");

    private void CompleteNode(Frame frame, CombatCommand command, CombatFlowResult result)
    {
        if (ReferenceEquals(frame.PendingCommand, command))
        {
            if (result != CombatFlowResult.Succeeded)
                RecordPendingEnd(frame, Context.Now >= frame.PendingDeadline ? "expired" : "command-" + result.ToString().ToLowerInvariant());
            frame.PendingCommand = null;
            frame.PendingAttempt = null;
            frame.PendingNeedsFirstObservation = false;
        }
        if (result == CombatFlowResult.Succeeded && command.Options.TryGetValue("id", out var id)) frame.Succeeded.Add(id);
        if (Required(command) && result is not (CombatFlowResult.Succeeded or CombatFlowResult.SatisfiedExisting))
            frame.Result = result switch
            {
                CombatFlowResult.Transferred => result,
                CombatFlowResult.Pending or CombatFlowResult.Deferred => CombatFlowResult.Deferred,
                _ => CombatFlowResult.Failed
            };
    }

    private void AbandonPending(Frame frame)
    {
        RecordPendingEnd(frame, "displaced-cursor");
        if (frame.PendingCommand is { } command) CompleteNode(frame, command, CombatFlowResult.Deferred);
    }

    private void RecordPendingEnd(Frame frame, string reason)
    {
        if (frame.PendingEndReported || frame.PendingCommand is not { } command || frame.PendingAttempt is not { } attempt) return;
        _battle.Diagnostics.PendingEnded(command, attempt, Context.Now, frame.PendingDeadline, reason);
        frame.PendingEndReported = true;
    }

    private Frame PopFrame(string reason)
    {
        var frame = _frames.Pop();
        RecordPendingEnd(frame, reason);
        return frame;
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        foreach (var frame in _frames) RecordPendingEnd(frame, "dispose");
        try { _game.ReleaseHeldInput(); }
        finally
        {
            if (_ownsBattle) _battle.Dispose();
            _frames.Clear();
            _watchProducers.Clear();
            _coverageRequests.Clear();
            _maintenanceAttemptsSinceProgress.Clear();
            _deferredMaintenance.Clear();
        }
    }
}
