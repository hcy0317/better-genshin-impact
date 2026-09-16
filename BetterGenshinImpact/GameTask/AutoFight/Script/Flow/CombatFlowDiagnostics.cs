using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

public sealed record CombatFlowDiagnosticEvent(double At, string Actor, string Action, int SourceLine,
    string ReportedResult, bool InputStarted, double Milliseconds)
{
    public long Sequence { get; init; }
    public bool? InputSubmitted { get; init; }
    public long FirstSequence { get; init; }
    public double? FirstAt { get; init; }
    public double TotalSampleMilliseconds { get; init; }
    public long SampleCount => FirstSequence > 0 ? Sequence - FirstSequence + 1 : 1;
    public string? CommandId { get; init; }
    public Guid? AttemptId { get; init; }
    public double? InputAt { get; init; }
    public double? EffectiveInputAt { get; init; }
    public double? ConfirmationDeadline { get; init; }
    public double? ActionDeadline { get; init; }
    public double RemainingBudget { get; init; }
    public IReadOnlyList<string> Observations { get; init; } = Array.Empty<string>();
    public string? Reason { get; init; }
    internal IReadOnlyList<CombatCallContext> CallPath { get; init; } = Array.Empty<CombatCallContext>();
}

public sealed record CombatFlowStatistics(long CoreSteps, long CompletedPasses, long FailedPasses,
    long GameActionCalls, long GameObservationCalls, long PreparationCalls, long YieldCalls,
    double CoreStepMilliseconds, double GameActionMilliseconds, double GameObservationMilliseconds,
    double PreparationMilliseconds, double YieldMilliseconds, IReadOnlyList<CombatFlowDiagnosticEvent> RecentEvents)
{
    internal IReadOnlyDictionary<string, CombatMetricPopulation> Populations { get; init; } =
        new Dictionary<string, CombatMetricPopulation>();
}

/// <summary>有界诊断，不参与条件、效果授权或下一场状态；调用次数不是命中/施放成功次数。</summary>
internal sealed class CombatFlowDiagnostics
{
    private readonly object _gate = new();
    private readonly CombatFlowDiagnosticEvent?[] _events = new CombatFlowDiagnosticEvent[64];
    private int _eventCount, _next;
    private long _eventSequence;
    private long _steps, _passes, _failedPasses, _actions, _observations, _preparations, _yields;
    private double _stepMs, _actionMs, _observationMs, _preparationMs, _yieldMs;
    private readonly CombatRuntimeMetrics _metrics = new();

    public void Step(double milliseconds, CombatFlowStep? result)
    {
        lock (_gate)
        {
            _steps++; _stepMs += milliseconds;
            _metrics.Record("step-total", TimeSpan.FromMilliseconds(milliseconds));
            if (result?.RoundCompleted == true)
            {
                _passes++;
                if (result.Value.Result == CombatFlowResult.Failed) _failedPasses++;
            }
        }
    }

    public void Action(CombatFlowAction action, string reportedResult, double milliseconds, bool? submitted = null)
    {
        lock (_gate)
        {
            _actions++; _actionMs += milliseconds;
            _metrics.Record("action-including-physical-wait", TimeSpan.FromMilliseconds(milliseconds));
            Append(action, reportedResult, milliseconds, submitted);
        }
    }

    private void Append(CombatFlowAction action, string reportedResult, double milliseconds, bool? submitted = null)
    {
        Append(new(action.Now, action.Command.Name, action.Command.Method.Alias[0],
            action.Command.SourceLine, reportedResult, action.InputAt != null, milliseconds)
        {
            Reason = action.DiagnosticReason, CommandId = action.CommandId,
            AttemptId = action.DiagnosticAttemptId ?? action.PendingAttempt?.AttemptId, InputAt = action.InputAt,
            EffectiveInputAt = action.EffectiveInputAt, RemainingBudget = action.RemainingBudget,
            ActionDeadline = action.AbsoluteDeadline,
            InputSubmitted = submitted,
            Observations = action.DiagnosticSamples, CallPath = action.CallPath
        });
    }

    public void PendingEnded(CombatCommand command, CombatSkillAttempt attempt, double now, double deadline, string reason)
    {
        lock (_gate)
        {
            // 生命周期事件不是游戏边界调用、输入或施放成功，不增加 Action 计数。
            Append(new(now, attempt.Actor, command.Method.Alias[0], command.SourceLine, "PendingEnded", false, 0)
            {
                CommandId = attempt.CommandId, AttemptId = attempt.AttemptId, ConfirmationDeadline = deadline,
                RemainingBudget = Math.Max(0, deadline - now), Reason = $"{reason}; originalInputAt={attempt.InputAt:F3}"
            });
        }
    }

    private void Append(CombatFlowDiagnosticEvent entry)
    {
        var sequence = ++_eventSequence;
        var lastIndex = (_next + _events.Length - 1) % _events.Length;
        var previous = _eventCount > 0 ? _events[lastIndex] : null;
        // 同一请求的连续纯等待只更新有界摘要，不能挤掉此前输入或状态转换。
        // 序号仍覆盖每次调用，便于区分聚合与真正丢失的历史；不合并实际输入。
        if (previous != null && (entry.InputSubmitted == false || entry.InputSubmitted == null && !entry.InputStarted) &&
            (previous.InputSubmitted == false || previous.InputSubmitted == null && !previous.InputStarted) &&
            entry.ReportedResult is "AwaitingObservation" or "Observation" or "Pending" &&
            entry.ReportedResult == previous.ReportedResult && entry.CommandId != null &&
            entry.CommandId == previous.CommandId && entry.AttemptId == previous.AttemptId &&
            entry.Actor == previous.Actor && entry.Action == previous.Action &&
            entry.Reason == previous.Reason && entry.ActionDeadline == previous.ActionDeadline &&
            entry.CallPath.SequenceEqual(previous.CallPath))
        {
            _events[lastIndex] = entry with
            {
                Sequence = sequence, FirstSequence = previous.FirstSequence, FirstAt = previous.FirstAt,
                TotalSampleMilliseconds = previous.TotalSampleMilliseconds + entry.Milliseconds
            };
            return;
        }
        _events[_next] = entry with
        {
            Sequence = sequence, FirstSequence = sequence, FirstAt = entry.At,
            TotalSampleMilliseconds = entry.Milliseconds
        };
        _next = (_next + 1) % _events.Length;
        _eventCount = Math.Min(_eventCount + 1, _events.Length);
    }

    public void Observe(double milliseconds)
    { lock (_gate) { _observations++; _observationMs += milliseconds; _metrics.Record("observation", TimeSpan.FromMilliseconds(milliseconds)); } }
    public void Prepare(double milliseconds, CombatFlowAction? action = null, bool? submitted = null)
    {
        lock (_gate)
        {
            _preparations++; _preparationMs += milliseconds;
            _metrics.Record("action-preparation", TimeSpan.FromMilliseconds(milliseconds));
            if (action?.CaptureDiagnostics == true) Append(action, "Observation", milliseconds, submitted);
        }
    }
    public void Yield(double milliseconds)
    { lock (_gate) { _yields++; _yieldMs += milliseconds; _metrics.Record("explicit-yield", TimeSpan.FromMilliseconds(milliseconds)); } }

    public CombatFlowStatistics Snapshot()
    {
        lock (_gate)
        {
            var recent = new CombatFlowDiagnosticEvent[_eventCount];
            for (var index = 0; index < _eventCount; index++)
                recent[index] = _events[(_next - _eventCount + index + _events.Length) % _events.Length]!;
            return new(_steps, _passes, _failedPasses, _actions, _observations, _preparations, _yields,
                _stepMs, _actionMs, _observationMs, _preparationMs, _yieldMs, Array.AsReadOnly(recent))
            { Populations = _metrics.SnapshotPopulation() };
        }
    }
}

/// <summary>仅测量已有游戏边界调用，不额外截图/识别/输入，也不把缓存查询计为实际 OCR。</summary>
internal sealed class DiagnosticCombatGame(ICombatFlowGame game, CombatFlowDiagnostics diagnostics) : ICombatFlowGame
{
    public bool ReportsInputReceipts => game.ReportsInputReceipts;
    public async ValueTask<CombatSkillRecovery> RecoverExpiredSkillStepAsync(CombatFlowAction action, CancellationToken ct)
    {
        var start = Stopwatch.GetTimestamp();
        try { return await game.RecoverExpiredSkillStepAsync(action, ct).ConfigureAwait(false); }
        finally { diagnostics.Prepare(Stopwatch.GetElapsedTime(start).TotalMilliseconds); }
    }
    public void BeginStep() => game.BeginStep();
    public void CheckDefeated(CancellationToken ct) => game.CheckDefeated(ct);
    public bool HasPendingSkill(CombatFlowAction action) => game.HasPendingSkill(action);
    public void CancelObservation(CombatFlowAction action) => game.CancelObservation(action);
    public ValueTask WaitAfterFailedPassAsync(CancellationToken ct) => game.WaitAfterFailedPassAsync(ct);
    public async ValueTask<CombatSkillAttempt?> TryRecoverExpiredSkillAsync(CombatFlowAction action, CancellationToken ct)
    {
        var start = Stopwatch.GetTimestamp();
        try { return await game.TryRecoverExpiredSkillAsync(action, ct).ConfigureAwait(false); }
        finally { diagnostics.Prepare(Stopwatch.GetElapsedTime(start).TotalMilliseconds, action); }
    }
    public void ReleaseHeldInput() => game.ReleaseHeldInput();
    public CombatScopeObservation? ObserveScope()
    {
        var start = Stopwatch.GetTimestamp();
        try { return game.ObserveScope(); }
        finally { diagnostics.Observe(Stopwatch.GetElapsedTime(start).TotalMilliseconds); }
    }
    public object? Observe(string function, IReadOnlyList<object?> args, string actor)
    {
        var start = Stopwatch.GetTimestamp();
        try { return game.Observe(function, args, actor); }
        finally { diagnostics.Observe(Stopwatch.GetElapsedTime(start).TotalMilliseconds); }
    }
    public async ValueTask PrepareObservationAsync(CombatFlowAction action, string function, CancellationToken ct)
    {
        var start = Stopwatch.GetTimestamp();
        var before = action.SubmissionCount;
        try { await game.PrepareObservationAsync(action, function, ct).ConfigureAwait(false); }
        finally { diagnostics.Prepare(Stopwatch.GetElapsedTime(start).TotalMilliseconds, action,
            game.ReportsInputReceipts ? action.SubmissionCount > before : null); }
    }
    public async ValueTask<CombatObservationPreparation> PrepareObservationStepAsync(CombatFlowAction action, string function, CancellationToken ct)
    {
        var start = Stopwatch.GetTimestamp();
        var before = action.SubmissionCount;
        try { return await game.PrepareObservationStepAsync(action, function, ct).ConfigureAwait(false); }
        finally { diagnostics.Prepare(Stopwatch.GetElapsedTime(start).TotalMilliseconds, action,
            game.ReportsInputReceipts ? action.SubmissionCount > before : null); }
    }
    public async ValueTask<CombatFlowResult> ExecuteAsync(CombatFlowAction action, CancellationToken ct)
    {
        var start = Stopwatch.GetTimestamp();
        var before = action.SubmissionCount;
        var outcome = "Exception";
        try
        {
            var result = await game.ExecuteAsync(action, ct).ConfigureAwait(false);
            outcome = result.ToString();
            return result;
        }
        catch (OperationCanceledException) { outcome = "Cancelled"; throw; }
        finally { diagnostics.Action(action, outcome, Stopwatch.GetElapsedTime(start).TotalMilliseconds,
            game.ReportsInputReceipts ? action.SubmissionCount > before : null); }
    }
    public async ValueTask YieldAsync(CancellationToken ct)
    {
        var start = Stopwatch.GetTimestamp();
        try { await game.YieldAsync(ct).ConfigureAwait(false); }
        finally { diagnostics.Yield(Stopwatch.GetElapsedTime(start).TotalMilliseconds); }
    }
}
