using System;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

/// <summary>只输出已有标量证据，不参与战斗状态或输入准入。</summary>
internal sealed class CombatFlowDiagnosticWriter(ILogger logger, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private long _lastPeriodicTimestamp = (clock ?? TimeProvider.System).GetTimestamp();
    private long _lastSequence;

    internal static bool IsEnabled(ILogger logger)
    {
        try { return logger.IsEnabled(LogLevel.Debug); }
        catch { return false; }
    }

    public void WritePeriodic(Guid battleId, Func<CombatFlowStatistics> readStatistics)
    {
        if (!IsEnabled(logger)) return;
        var now = _clock.GetTimestamp();
        if (_clock.GetElapsedTime(_lastPeriodicTimestamp, now).TotalSeconds < 30) return;
        // 失败的周期也消耗窗口，不在每个战斗步骤重试日志接收器。
        _lastPeriodicTimestamp = now;
        var latestSequence = _lastSequence;
        try
        {
            var statistics = readStatistics();
            var events = statistics.RecentEvents;
            if (events.Count > 0) latestSequence = Math.Max(_lastSequence, events[^1].Sequence);
            var first = Math.Max(0, events.Count - 8);
            while (first < events.Count && events[first].Sequence <= _lastSequence) first++;
            var sampled = events.Count - first;
            var dropped = latestSequence - _lastSequence - sampled;
            logger.LogDebug("FIGHT_PROGRESS battle={Battle} steps={Steps} passes={Passes} failedPasses={FailedPasses} actionCalls={Actions} observationCalls={Observations} latestSeq={Sequence} sampled={Sampled} dropped={Dropped} actualHits=unknown",
                battleId, statistics.CoreSteps, statistics.CompletedPasses, statistics.FailedPasses,
                statistics.GameActionCalls, statistics.GameObservationCalls, latestSequence, sampled, dropped);
            for (var index = first; index < events.Count; index++)
            {
                var entry = events[index];
                // 周期仅输出标量；详细观察样本仍留给原有失败/结束边界。
                logger.LogDebug("{Marker} battle={Battle} boundary=periodic seq={Sequence} t={At:F3} line={Line} actor={Actor} action={Action} result={Result} inputAdmitted={Input} ms={Milliseconds:F1} command={Command} attempt={Attempt} inputAt={InputAt} effectiveInputAt={EffectiveInputAt} remaining={Remaining:F3} deadline={Deadline}",
                    entry.ReportedResult == "PendingEnded" ? "FIGHT_PENDING_END" : "FIGHT_ACTION",
                    battleId, entry.Sequence, entry.At, entry.SourceLine, entry.Actor, entry.Action,
                    entry.ReportedResult, entry.InputStarted, entry.Milliseconds, entry.CommandId, entry.AttemptId,
                    entry.InputAt, entry.EffectiveInputAt, entry.RemainingBudget, entry.ConfirmationDeadline);
            }
        }
        catch { /* 快照/日志故障不改变业务结果、取消或输入释放。 */ }
        finally
        {
            // 已声明采样丢弃的历史不在后续边界重复输出；下一周期继续新序号。
            _lastSequence = latestSequence;
        }
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
