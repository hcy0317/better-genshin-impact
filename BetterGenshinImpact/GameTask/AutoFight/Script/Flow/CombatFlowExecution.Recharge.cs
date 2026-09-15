using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

public sealed partial class CombatFlowExecution
{
    private bool RechargeDemandStillRequired(Frame frame)
    {
        foreach (var source in _frames)
        {
            if (!source.RechargeSource || source.HasSkillInput || source.Caller == null) continue;
            if (source.RechargeDemandWithdrawn) return false;
            var receiver = source.Caller.Name;
            if (_game.Observe("q-ready", [], receiver) is true || _game.Observe("q-cd", [], receiver) is true ||
                _game.Observe("q-energy-low", [], receiver) is false)
            {
                source.RechargeDemandWithdrawn = true;
                frame.RechargeDemandWithdrawn = true;
                return false;
            }
        }
        return true;
    }

    private enum RechargeDemandChange { BurstReady, CoolingDown, NotEnergyLow }

    private sealed class RechargePreparation(CombatRechargePlan plan, CombatFlowAction action,
        double deadline, CombatResourceSample? resourceBefore)
    {
        public CombatRechargePlan Plan { get; } = plan;
        public CombatFlowAction Action { get; } = action;
        public double Deadline { get; } = deadline;
        public CombatResourceSample? ResourceBefore { get; } = resourceBefore;
        public int ProducerIndex;
        public CombatFlowAction? Probe;
        public RechargeDemandChange? DemandChange;
    }

    private void CancelRechargePreparation(Frame frame)
    {
        if (frame.PreparingRecharge?.Probe is { } probe) _game.CancelObservation(probe);
        frame.PreparingRecharge = null;
    }

    private async ValueTask<bool> TryBeginRechargeAsync(Frame frame, CombatCommand command, string? keep,
        CombatFlowAction action, CancellationToken ct)
    {
        if (_program.Recharge(command) is not { } plan) return false;
        var burstReady = _game.Observe("q-ready", [], command.Name) is true;
        var low = ConditionEvaluator.Truth(_game.Observe("q-energy-low", [], command.Name));
        var cooling = ConditionEvaluator.Truth(_game.Observe("q-cd", [], command.Name));
        if (frame.PreparingRecharge == null)
        {
            if (burstReady) return false;
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
            frame.PreparingRecharge = new(plan, action, Math.Min(deadline, action.AbsoluteDeadline), ReadEnergySample(command.Name));
        }
        else if (burstReady || cooling == true || low == false)
        {
            // 这是先前已证实需求的有限续态，不把后台Unknown当成新的需求或永久true。
            frame.PreparingRecharge.DemandChange = burstReady ? RechargeDemandChange.BurstReady
                : cooling == true ? RechargeDemandChange.CoolingDown : RechargeDemandChange.NotEnergyLow;
            if (frame.PreparingRecharge.Probe == null) return FinishChangedDemand();
        }

        var pending = frame.PreparingRecharge;
        bool FinishChangedDemand()
        {
            var changed = frame.PreparingRecharge!.DemandChange;
            CancelRechargePreparation(frame);
            if (changed == RechargeDemandChange.BurstReady) return false;
            CompleteNode(frame, command, changed == RechargeDemandChange.CoolingDown ? CombatFlowResult.Deferred : CombatFlowResult.Skipped);
            return true;
        }
        bool CanPrepare() => pending.Action.CanStart && Context.Now < pending.Deadline;
        if (!CanPrepare())
        {
            CompleteNode(frame, command, CombatFlowResult.Failed);
            return true;
        }
        while (pending.ProducerIndex < pending.Plan.Producers.Count)
        {
            var producer = pending.Plan.Producers[pending.ProducerIndex];
            if (producer.Command.IsActiveInRound(Round) && (producer.Condition == null ||
                producer.Condition.EvaluateBoolean((name, args) => Observe(name, args, frame, producer.Command.Name)) == true))
            {
                var observed = ConditionEvaluator.Truth(_game.Observe("e-ready", [], producer.Command.Name));
                if (pending.Probe != null || observed == null)
                {
                    pending.Probe ??= new(producer.Command, Context, CanPrepare, pending.Deadline);
                    var prepared = await _game.PrepareObservationStepAsync(pending.Probe, "e-ready", ct);
                    ct.ThrowIfCancellationRequested();
                    if (prepared == CombatObservationPreparation.AwaitingObservation) return true;
                    if (pending.DemandChange != null)
                    {
                        // 已发出的切人不能靠丢弃C#续态撤销。先取得该请求的终态，禁止E，
                        // 再由原Q按当前帧返回接收者；否则旧切人可能在Q输入后才生效。
                        if (prepared == CombatObservationPreparation.Ready) return FinishChangedDemand();
                        CompleteNode(frame, command, CombatFlowResult.Unknown);
                        return true;
                    }
                    observed = ConditionEvaluator.Truth(_game.Observe("e-ready", [], producer.Command.Name));
                }
                // 当前帧必须证实供能者E就绪；仍由原source在实际输入前重验自身条件。
                if (CanPrepare() && observed == true)
                {
                    var source = CreateFrame(pending.Plan.Source, command, Math.Min(frame.Deadline, pending.Deadline));
                    source.RechargeSource = true;
                    source.ResourceBefore = pending.ResourceBefore;
                    CancelRechargePreparation(frame);
                    _calls[source.Block.Name] = _calls.GetValueOrDefault(source.Block.Name) + 1;
                    _frames.Push(source);
                    return true;
                }
            }
            if (pending.Probe != null) _game.CancelObservation(pending.Probe);
            pending.Probe = null;
            pending.ProducerIndex++;
        }
        CompleteNode(frame, command, CombatFlowResult.Failed);
        return true;
    }
}
