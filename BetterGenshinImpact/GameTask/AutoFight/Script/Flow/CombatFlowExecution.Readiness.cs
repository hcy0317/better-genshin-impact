using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoFight.Config;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

public sealed partial class CombatFlowExecution
{
    private Frame? _selectionFrame;

    internal async ValueTask<bool?> EvaluateConditionAsync(ConditionEvaluator.CompiledCondition condition,
        string actor, CancellationToken ct)
    {
        var frame = _frames.TryPeek(out var active) ? active : _selectionFrame ??= CreateFrame(_root);
        var result = await EvaluateWithPreparationAsync(condition, actor, frame, ct);
        if (frame.PreparingCondition == null && ReferenceEquals(frame, _selectionFrame)) _selectionFrame = null;
        return result;
    }

    private void CancelConditionPreparation(Frame frame)
    {
        if (frame.PreparingCondition is { } pending) _game.CancelObservation(pending.Action);
        frame.PreparingCondition = null;
    }

    private void CancelBurstPreparation(Frame frame)
    {
        if (frame.PreparingBurst is { } pending) _game.CancelObservation(pending);
        frame.PreparingBurst = null;
    }

    private void CancelFeedPreparation(Frame frame)
    {
        if (frame.PendingFeed is { } pending) _game.CancelObservation(pending);
        frame.PendingFeed = null;
        frame.FeedParentCommand = null;
    }

    private async ValueTask<bool?> EvaluateWithPreparationAsync(ConditionEvaluator.CompiledCondition condition,
        string actor, Frame frame, CancellationToken ct, CombatFlowBlock? child = null, bool allowPreparation = true,
        double? commandStartedAt = null, double? commandDeadline = null)
    {
        string? unknownActor = null;
        string? unknownFunction = null;
        object? Resolve(string function, IReadOnlyList<object?> args)
        {
            var value = Observe(function, args, frame, actor, !_roundStarted && _hasRootRounds ? Round + 1 : Round);
            if (value == null && function is "e-ready" or "low-hp" or "q-ready" or "q-energy-low" or "q-cd" && unknownActor == null)
            {
                var target = args.FirstOrDefault()?.ToString() ?? actor;
                if (DefaultAutoFightConfig.CombatAvatarAliasToNameMap.TryGetValue(target, out var canonical)) target = canonical;
                if (!string.IsNullOrWhiteSpace(target) && target != CombatScriptParser.CurrentAvatarName)
                { unknownActor = target; unknownFunction = function; }
            }
            return value;
        }
        bool CanPrepare()
        {
            if (_closed || IsAtomic || Context.Now >= frame.Deadline || !LiveRequirementsHold(frame)) return false;
            var guarded = child == null ? frame : CreateFrame(child, deadline: frame.Deadline);
            if (!RequirementsHold(guarded)) return false;
            return !guarded.Block.Atomic ||
                guarded.Block.EntryCoverageRecords.All(record => Context.Remaining(record) is { } remaining &&
                    remaining > guarded.Block.EstimatedSeconds + CombatFlowPolicy.RecoverySeconds) &&
                RequirementsFit(guarded, guarded.Block.EstimatedSeconds + CombatFlowPolicy.RecoverySeconds);
        }
        async ValueTask<bool?> Advance(ConditionPreparation preparation)
        {
            if (!CanPrepare() || !preparation.Action.CanStart)
            {
                CancelConditionPreparation(frame);
                return null;
            }
            var prepared = await _game.PrepareObservationStepAsync(preparation.Action, preparation.Function, ct);
            ct.ThrowIfCancellationRequested();
            if (prepared == CombatObservationPreparation.AwaitingObservation) return null;
            frame.PreparingCondition = null;
            _game.BeginStep();
            if (!preparation.Action.CanStart) return null;
            if (Observe(preparation.Function, [preparation.Actor], frame, preparation.Actor) is bool)
                _episodes.Resolve(preparation.Goal);
            // 只完成原来的这一次准备，再重验原条件；不能跨Step隐式切第二个角色拼接旧帧。
            return condition.EvaluateBoolean((function, args) => Observe(function, args, frame, actor,
                !_roundStarted && _hasRootRounds ? Round + 1 : Round));
        }
        if (frame.PreparingCondition is { } pending)
        {
            if (!ReferenceEquals(pending.Condition, condition)) CancelConditionPreparation(frame);
            else return await Advance(pending);
        }
        var result = condition.EvaluateBoolean(Resolve);
        if (result != null || !allowPreparation || unknownActor == null || !CanPrepare()) return result;
        var targetActor = unknownActor;
        var goal = "condition:" + unknownFunction + ":" + targetActor;
        if (_battle.NextConditionProbe.TryGetValue(goal, out var next) && Context.Now < next) return null;
        if (!_episodes.TrySpend(goal, Context.Now, CombatFlowPolicy.EpisodeTimeoutSeconds,
                CombatFlowPolicy.EpisodeAttempts, out var deadline)) return null;
        ct.ThrowIfCancellationRequested();
        _battle.NextConditionProbe[goal] = Context.Now + 1;
        var probe = new CombatFlowAction(new CombatCommand(targetActor, "e"), Context, CanPrepare,
            Math.Min(commandDeadline ?? double.PositiveInfinity, Math.Min(frame.Deadline, Math.Min(deadline, Context.Now + 3))),
            continuation: CanPrepare);
        _battle.ConditionPreparationVersion++;
        frame.PreparingCondition = new(condition, goal, targetActor, unknownFunction!, probe, commandStartedAt ?? Context.Now);
        return await Advance(frame.PreparingCondition);
    }
}
