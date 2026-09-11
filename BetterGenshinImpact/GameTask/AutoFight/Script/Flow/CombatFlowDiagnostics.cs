using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

public sealed record CombatFlowDiagnosticEvent(double At, string Actor, string Action, int SourceLine,
    string ReportedResult, bool InputStarted, double Milliseconds)
{
    public long Sequence { get; init; }
    public string? CommandId { get; init; }
    public Guid? AttemptId { get; init; }
    public double? InputAt { get; init; }
    public double? EffectiveInputAt { get; init; }
    public double? ConfirmationDeadline { get; init; }
    public double RemainingBudget { get; init; }
    public IReadOnlyList<string> Observations { get; init; } = Array.Empty<string>();
    public string? Reason { get; init; }
}

public sealed record CombatFlowStatistics(long CoreSteps, long CompletedPasses, long FailedPasses,
    long GameActionCalls, long GameObservationCalls, long PreparationCalls, long YieldCalls,
    double CoreStepMilliseconds, double GameActionMilliseconds, double GameObservationMilliseconds,
    double PreparationMilliseconds, double YieldMilliseconds, IReadOnlyList<CombatFlowDiagnosticEvent> RecentEvents);

/// <summary>有界诊断，不参与条件、效果授权或下一场状态；调用次数不是命中/施放成功次数。</summary>
internal sealed class CombatFlowDiagnostics
{
    private readonly object _gate = new();
    private readonly CombatFlowDiagnosticEvent?[] _events = new CombatFlowDiagnosticEvent[64];
    private int _eventCount, _next;
    private long _eventSequence;
    private long _steps, _passes, _failedPasses, _actions, _observations, _preparations, _yields;
    private double _stepMs, _actionMs, _observationMs, _preparationMs, _yieldMs;

    public void Step(double milliseconds, CombatFlowStep? result)
    {
        lock (_gate)
        {
            _steps++; _stepMs += milliseconds;
            if (result?.RoundCompleted == true)
            {
                _passes++;
                if (result.Value.Result == CombatFlowResult.Failed) _failedPasses++;
            }
        }
    }

    public void Action(CombatFlowAction action, string reportedResult, double milliseconds)
    {
        lock (_gate)
        {
            _actions++; _actionMs += milliseconds;
            Append(action, reportedResult, milliseconds);
        }
    }

    private void Append(CombatFlowAction action, string reportedResult, double milliseconds)
    {
        Append(new(action.Now, action.Command.Name, action.Command.Method.Alias[0],
            action.Command.SourceLine, reportedResult, action.InputAt != null, milliseconds)
        {
            Reason = action.DiagnosticReason, CommandId = action.CommandId,
            AttemptId = action.DiagnosticAttemptId, InputAt = action.InputAt,
            EffectiveInputAt = action.EffectiveInputAt, RemainingBudget = action.RemainingBudget,
            Observations = action.DiagnosticSamples
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
        _events[_next] = entry with { Sequence = ++_eventSequence };
        _next = (_next + 1) % _events.Length;
        _eventCount = Math.Min(_eventCount + 1, _events.Length);
    }

    public void Observe(double milliseconds) { lock (_gate) { _observations++; _observationMs += milliseconds; } }
    public void Prepare(double milliseconds, CombatFlowAction? action = null)
    {
        lock (_gate)
        {
            _preparations++; _preparationMs += milliseconds;
            if (action?.CaptureDiagnostics == true) Append(action, "Observation", milliseconds);
        }
    }
    public void Yield(double milliseconds) { lock (_gate) { _yields++; _yieldMs += milliseconds; } }

    public CombatFlowStatistics Snapshot()
    {
        lock (_gate)
        {
            var recent = new CombatFlowDiagnosticEvent[_eventCount];
            for (var index = 0; index < _eventCount; index++)
                recent[index] = _events[(_next - _eventCount + index + _events.Length) % _events.Length]!;
            return new(_steps, _passes, _failedPasses, _actions, _observations, _preparations, _yields,
                _stepMs, _actionMs, _observationMs, _preparationMs, _yieldMs, Array.AsReadOnly(recent));
        }
    }
}

/// <summary>仅测量已有游戏边界调用，不额外截图/识别/输入，也不把缓存查询计为实际 OCR。</summary>
internal sealed class DiagnosticCombatGame(ICombatFlowGame game, CombatFlowDiagnostics diagnostics) : ICombatFlowGame
{
    public void BeginStep() => game.BeginStep();
    public void CheckDefeated(CancellationToken ct) => game.CheckDefeated(ct);
    public bool HasPendingSkill(CombatFlowAction action) => game.HasPendingSkill(action);
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
        try { await game.PrepareObservationAsync(action, function, ct).ConfigureAwait(false); }
        finally { diagnostics.Prepare(Stopwatch.GetElapsedTime(start).TotalMilliseconds, action); }
    }
    public async ValueTask<CombatFlowResult> ExecuteAsync(CombatFlowAction action, CancellationToken ct)
    {
        var start = Stopwatch.GetTimestamp();
        var outcome = "Exception";
        try
        {
            var result = await game.ExecuteAsync(action, ct).ConfigureAwait(false);
            outcome = result.ToString();
            return result;
        }
        catch (OperationCanceledException) { outcome = "Cancelled"; throw; }
        finally { diagnostics.Action(action, outcome, Stopwatch.GetElapsedTime(start).TotalMilliseconds); }
    }
    public async ValueTask YieldAsync(CancellationToken ct)
    {
        var start = Stopwatch.GetTimestamp();
        try { await game.YieldAsync(ct).ConfigureAwait(false); }
        finally { diagnostics.Yield(Stopwatch.GetElapsedTime(start).TotalMilliseconds); }
    }
}
