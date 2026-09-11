using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoFight.Config;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

public sealed partial class CombatFlowExecution
{
    internal ValueTask<bool?> EvaluateConditionAsync(ConditionEvaluator.CompiledCondition condition,
        string actor, CancellationToken ct) => EvaluateWithPreparationAsync(condition, actor,
            _frames.TryPeek(out var frame) ? frame : CreateFrame(_root), ct);

    private async ValueTask<bool?> EvaluateWithPreparationAsync(ConditionEvaluator.CompiledCondition condition,
        string actor, Frame frame, CancellationToken ct, CombatFlowBlock? child = null)
    {
        string? unknownActor = null;
        object? Resolve(string function, IReadOnlyList<object?> args)
        {
            var value = Observe(function, args, frame, actor, !_roundStarted && _hasRootRounds ? Round + 1 : Round);
            if (value == null && function == "e-ready" && unknownActor == null)
            {
                var target = args.FirstOrDefault()?.ToString() ?? actor;
                if (DefaultAutoFightConfig.CombatAvatarAliasToNameMap.TryGetValue(target, out var canonical)) target = canonical;
                if (!string.IsNullOrWhiteSpace(target) && target != CombatScriptParser.CurrentAvatarName) unknownActor = target;
            }
            return value;
        }
        var result = condition.EvaluateBoolean(Resolve);
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
        if (result != null || unknownActor == null || !CanPrepare()) return result;
        var targetActor = unknownActor;
        var goal = "condition:e-ready:" + targetActor;
        if (_battle.NextConditionProbe.TryGetValue(goal, out var next) && Context.Now < next) return null;
        if (!_episodes.TrySpend(goal, Context.Now, CombatFlowPolicy.EpisodeTimeoutSeconds,
                CombatFlowPolicy.EpisodeAttempts, out var deadline)) return null;
        ct.ThrowIfCancellationRequested();
        _battle.NextConditionProbe[goal] = Context.Now + 1;
        var probe = new CombatFlowAction(new CombatCommand(targetActor, "e"), Context, CanPrepare,
            Math.Min(frame.Deadline, Math.Min(deadline, Context.Now + 3)), continuation: CanPrepare);
        _battle.ConditionPreparationVersion++;
        await _game.PrepareObservationAsync(probe, "e-ready", ct);
        ct.ThrowIfCancellationRequested();
        _game.BeginStep(); // 不复用切人之前的条件缓存。
        if (!probe.CanStart) return null;
        if (Observe("e-ready", [targetActor], frame, targetActor) is bool) _episodes.Resolve(goal);
        // 同一次求值不连环切换多个角色，也不拼接不同角色的旧帧证据。
        return condition.EvaluateBoolean((function, args) => Observe(function, args, frame, actor,
            !_roundStarted && _hasRootRounds ? Round + 1 : Round));
    }
}
