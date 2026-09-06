using System;
using System.Collections.Generic;
using System.Linq;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

public sealed partial class CombatFlowExecution
{
    private readonly Dictionary<string, int> _maintenanceAttemptsSinceProgress = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (long Generation, long EffectVersion)> _deferredMaintenance = new(StringComparer.Ordinal);
    public string? LastMaintenanceDecision { get; private set; }

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
