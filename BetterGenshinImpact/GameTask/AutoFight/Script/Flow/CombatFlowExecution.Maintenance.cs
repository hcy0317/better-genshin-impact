using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

public sealed partial class CombatFlowExecution
{
    private readonly Dictionary<string, int> _maintenanceAttemptsSinceProgress = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (long Generation, long EffectVersion)> _deferredMaintenance = new(StringComparer.Ordinal);
    public string? LastMaintenanceDecision { get; private set; }

    private async ValueTask<double?> TrySpendCoverageAsync(string name, CombatCommand command,
        double ancestorDeadline, double demand, CancellationToken ct)
    {
        var goal = "coverage:" + name;
        if (_program.Timing(command)?.Duration is { } full && full <= demand) return null;
        if (_episodes.TrySpend(goal, Context.Now, CombatFlowPolicy.EpisodeTimeoutSeconds,
                CombatFlowPolicy.EpisodeAttempts, out var deadline)) return deadline;
        LastMaintenanceDecision = $"维护目标 {name} 的原预算已耗尽，等待物理技能新就绪证据";
        if (IsAtomic || Context.Now >= ancestorDeadline || !_episodes.CanTrySkillReset(goal, Context.Now) ||
            command.Method != Method.Skill && command.Method != Method.Burst) return null;

        var probe = new CombatFlowAction(command, Context, () => !_closed,
            Math.Min(ancestorDeadline, Context.Now + 3));
        var reset = await _game.TryRecoverExpiredSkillAsync(probe, ct);
        ct.ThrowIfCancellationRequested();
        if (probe.DiagnosticReason is { } reason) LastMaintenanceDecision = $"维护目标 {name}：{reason}";
        if (reset == null || !probe.CanStart || _closed || Context.Now >= ancestorDeadline || reset.BattleId != Context.BattleId ||
            reset.Actor != command.Name || reset.Skill != command.Method || reset.Deadline > Context.Now ||
            !_episodes.TryReopenAfterSkillReset(goal, reset.AttemptId, Context.Now)) return null;
        LastMaintenanceDecision = $"维护目标 {name} 已取得双新帧就绪证据，释放过期请求 {reset.AttemptId} 并有界重新准入；未补记旧施放成功";
        return _episodes.TrySpend(goal, Context.Now, CombatFlowPolicy.EpisodeTimeoutSeconds,
            CombatFlowPolicy.EpisodeAttempts, out deadline) ? deadline : null;
    }

    private void ReportMaintenanceProgress(CombatCommand command)
    {
        var channel = command.Options.GetValueOrDefault("watch") ?? command.Options.GetValueOrDefault("maintain");
        if (channel != null)
        {
            // 单个生成端成功不代表多个窗口的共同输出前提已经满足。
            _maintenanceAttemptsSinceProgress[channel] = _maintenanceAttemptsSinceProgress.GetValueOrDefault(channel) + 1;
        }
        else if (command.Method != Method.Wait || CombatFlowPolicy.ActionSeconds(command) > 0)
        {
            _maintenanceAttemptsSinceProgress.Clear();
        }
    }

    private void RefreshDeferredMaintenance()
    {
        foreach (var (name, previous) in _deferredMaintenance.ToArray())
            if (Context.Find(name) is { } current && Context.Remaining(name) > 0 &&
                (current.Generation != previous.Generation || current.EffectVersion != previous.EffectVersion))
            {
                _deferredMaintenance.Remove(name);
                _maintenanceAttemptsSinceProgress.Remove(name);
                LastMaintenanceDecision = "维护通道收到新的有效来源证据，可以重新评估覆盖：" + name;
            }
    }

    private void DeferConflictingMaintenance(string requested)
    {
        if (_maintenanceAttemptsSinceProgress.GetValueOrDefault(requested) < CombatFlowPolicy.EpisodeAttempts) return;
        var candidates = _maintenanceAttemptsSinceProgress.Keys
            .Where(name => !_deferredMaintenance.ContainsKey(name) && _watchProducers.ContainsKey(name)).ToArray();
        if (candidates.Length < 2) return;
        var deferred = candidates.OrderBy(name =>
        {
            var producer = _watchProducers[name];
            var command = producer.Block.Nodes[producer.Index].Command;
            return CombatFlowPolicy.MaintenancePriority(command, _program.RecordSource(command));
        }).ThenByDescending(name => name, StringComparer.Ordinal).First();
        var record = Context.Find(deferred)!;
        _deferredMaintenance[deferred] = (record.Generation, record.EffectVersion);
        _coverageRequests.Remove(deferred);
        // 只暂停主动维护，不伪造物理效果消失，也不删除已发生事件；消费者仍按真实剩余窗口准入。
        LastMaintenanceDecision = $"多个维护窗口未取得共同进展，暂停较低优先级维护 {deferred} 并运行保底；等待该通道新的有效来源证据";
    }
}
