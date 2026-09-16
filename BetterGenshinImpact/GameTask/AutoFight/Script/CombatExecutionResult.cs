using System;

namespace BetterGenshinImpact.GameTask.AutoFight.Script;

public enum CombatScriptExecutionMode { RequiredSequence, LegacyPartyTemplate }
public enum CombatScriptExecutionPurpose { Combat, Pathing }
public enum CombatExecutionKind { Completed, Skipped, Deferred, Failed, Cancelled }

public readonly record struct CombatExecutionResult(CombatExecutionKind Kind, string Reason)
{
    public bool CanContinue => Kind is CombatExecutionKind.Completed or CombatExecutionKind.Skipped;
    public void EnsureCanContinue()
    {
        if (Kind == CombatExecutionKind.Cancelled) throw new OperationCanceledException(Reason);
        if (!CanContinue) throw new InvalidOperationException($"[BGI_COMBAT_FRAGMENT_INCOMPLETE] {Kind}: {Reason}");
    }
}
