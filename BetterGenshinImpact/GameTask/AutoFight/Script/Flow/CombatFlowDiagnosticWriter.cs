using System;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

/// <summary>只输出已有标量证据，不参与战斗状态或输入准入。</summary>
internal sealed class CombatFlowDiagnosticWriter(ILogger logger)
{
    private long _lastSequence;

    internal static bool IsEnabled(ILogger logger)
    {
        try { return logger.IsEnabled(LogLevel.Debug); }
        catch { return false; }
    }

    public void Write(Guid battleId, CombatFlowStatistics statistics, string boundary)
    {
        if (!IsEnabled(logger)) return;
        foreach (var entry in statistics.RecentEvents)
        {
            if (entry.Sequence <= _lastSequence) continue;
            try
            {
                if (entry.Sequence > _lastSequence + 1)
                    logger.LogDebug("FIGHT_TRACE_GAP battle={Battle} boundary={Boundary} overwritten={Count}",
                        battleId, boundary, entry.Sequence - _lastSequence - 1);
                logger.LogDebug("{Marker} battle={Battle} boundary={Boundary} seq={Sequence} t={At:F3} line={Line} actor={Actor} action={Action} result={Result} inputAdmitted={Input} ms={Milliseconds:F1} command={Command} attempt={Attempt} inputAt={InputAt} effectiveInputAt={EffectiveInputAt} remaining={Remaining:F3} deadline={Deadline} reason={Reason} samples={Samples}",
                    entry.ReportedResult == "PendingEnded" ? "FIGHT_PENDING_END" : "FIGHT_ACTION",
                    battleId, boundary, entry.Sequence, entry.At, entry.SourceLine, entry.Actor, entry.Action,
                    entry.ReportedResult, entry.InputStarted, entry.Milliseconds, entry.CommandId, entry.AttemptId,
                    entry.InputAt, entry.EffectiveInputAt, entry.RemainingBudget, entry.ConfirmationDeadline, entry.Reason,
                    string.Join(" | ", entry.Observations));
            }
            catch { /* 诊断接收器故障不能改变动作、取消或释放输入。 */ }
            // 失败的日志也有界放弃，避免每个失败边界反复打同一份日志。
            _lastSequence = entry.Sequence;
        }
    }
}
