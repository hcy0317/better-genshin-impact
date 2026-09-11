using System;
using System.Collections.Generic;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

/// <summary>多根 JSON 与单根 TXT 使用相同的本场状态；根游标/局部成功仍各自隔离。</summary>
internal sealed class CombatFlowBattleState(TimeProvider? clock) : IDisposable
{
    public CombatFlowContext Context { get; } = new(clock);
    public HashSet<string> Once { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> Calls { get; } = new(StringComparer.Ordinal);
    public CombatFlowEpisodes Episodes { get; } = new();
    public CombatConditionHistory History { get; } = new();
    public CombatFlowDiagnostics Diagnostics { get; } = new();
    public Dictionary<string, double> NextConditionProbe { get; } = new(StringComparer.Ordinal);
    public long ConditionPreparationVersion { get; set; }
    public bool FinishCheckRequested { get; set; }
    public bool TakeFinishCheckRequest()
    {
        var requested = FinishCheckRequested;
        FinishCheckRequested = false;
        return requested;
    }
    public void Dispose() { Context.Dispose(); Once.Clear(); Calls.Clear(); Episodes.Clear(); History.Clear(); NextConditionProbe.Clear(); FinishCheckRequested = false; }
}
