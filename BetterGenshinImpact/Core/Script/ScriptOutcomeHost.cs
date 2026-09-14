using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.LogParse;
using Microsoft.ClearScript;

namespace BetterGenshinImpact.Core.Script;

public enum ScriptOutcomeKind { Completed, Skipped, Deferred, NeedsReconcile, Failed, Cancelled }

public sealed record ScriptExecutionResult(ScriptOutcomeKind Kind, string Reason)
{
    public string? TaskName { get; init; }
    public IReadOnlyList<ScriptExecutionResult> Children { get; init; } = Array.Empty<ScriptExecutionResult>();

    internal void ApplyTo(ExecutionRecord record)
    {
        record.Outcome = Kind.ToString();
        record.OutcomeReason = Reason;
        record.IsSuccessful = Kind == ScriptOutcomeKind.Completed && TaskExecutionScope.Failure == null;
    }
    internal void ThrowIfFailure()
    {
        TaskExecutionScope.ThrowIfFailed();
        if (Kind == ScriptOutcomeKind.Cancelled) throw new OperationCanceledException("[BGI_TASK_CANCELLED] " + Reason);
        if (Kind == ScriptOutcomeKind.Failed) throw new InvalidOperationException("[BGI_SCRIPT_FAILED] " + Reason);
    }
    internal void ThrowIfIncomplete()
    {
        ThrowIfFailure();
        if (Kind is ScriptOutcomeKind.Deferred or ScriptOutcomeKind.NeedsReconcile)
            throw new InvalidOperationException($"[BGI_SCRIPT_INCOMPLETE] {Kind}: {Reason}");
    }
}

/// <summary>父调度保留各子结果；后续完成只能增加进展，不能抹掉先前未闭合的行动。</summary>
internal sealed class ScriptOutcomeAccumulator
{
    private readonly List<ScriptExecutionResult> _children = [];

    internal void Add(string taskName, ScriptExecutionResult result) =>
        _children.Add(result with { TaskName = taskName });

    internal ScriptExecutionResult Complete()
    {
        var kind = ScriptOutcomeKind.Skipped;
        foreach (var child in _children)
            if (Priority(child.Kind) > Priority(kind)) kind = child.Kind;
        return new(kind, _children.Count == 0 ? "NO_ACTIONS" : $"CHILDREN_{kind.ToString().ToUpperInvariant()}")
        {
            Children = Array.AsReadOnly(_children.ToArray())
        };
    }

    private static int Priority(ScriptOutcomeKind kind) => kind switch
    {
        ScriptOutcomeKind.Skipped => 0,
        ScriptOutcomeKind.Completed => 1,
        ScriptOutcomeKind.Deferred => 2,
        ScriptOutcomeKind.NeedsReconcile => 3,
        ScriptOutcomeKind.Failed => 4,
        ScriptOutcomeKind.Cancelled => 5,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}

/// <summary>每次脚本独有的终态入口；不暴露重置/重开，迟到回调不能修改下一脚本。</summary>
public sealed class ScriptOutcomeReporter
{
    private readonly object _gate = new();
    private readonly CancellationToken _ct;
    private readonly TaskExecutionScope.Guard _guard;
    private bool _closed, _required;
    private ScriptExecutionResult? _result;
    internal ScriptOutcomeReporter(CancellationToken ct)
    { _ct = ct; _guard = TaskExecutionScope.Capture(); }

    public void Check()
    {
        if (_ct.IsCancellationRequested) throw new OperationCanceledException("[BGI_TASK_CANCELLED] 用户已取消自动化", _ct);
        _guard.Check();
        lock (_gate) ObjectDisposedException.ThrowIf(_closed, this);
    }
    public void RequireExplicitOutcome()
    {
        Check();
        lock (_gate) { ObjectDisposedException.ThrowIf(_closed, this); _required = true; }
    }
    public void Report(string kind, string reason)
    {
        Check();
        if (!Enum.TryParse<ScriptOutcomeKind>(kind, true, out var outcome) ||
            !string.Equals(Enum.GetName(outcome), kind, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("未知脚本结果种类：" + kind);
        reason ??= "";
        if (reason.Length > 2048) reason = reason[..2048];
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_result != null) throw new InvalidOperationException("脚本终态只能报告一次");
            _result = new(outcome, reason);
        }
    }
    internal ScriptExecutionResult Finish()
    {
        Check();
        lock (_gate)
        {
            _closed = true;
            return _result ?? (_required
                ? new(ScriptOutcomeKind.NeedsReconcile, "SCRIPT_OUTCOME_MISSING")
                : new(ScriptOutcomeKind.Completed, "LEGACY_NORMAL_RETURN"));
        }
    }
    internal void Close() { lock (_gate) _closed = true; }
}

internal static class ScriptOutcomeHost
{
    internal static async Task<ScriptExecutionResult> RunAsync(IScriptEngine engine, Func<object?> evaluate, CancellationToken ct)
    {
        var reporter = new ScriptOutcomeReporter(ct);
        try
        {
            reporter.Check();
            engine.AddHostObject("taskResult", reporter);
            var result = evaluate();
            if (result is Task task) await task;
            return reporter.Finish();
        }
        finally { reporter.Close(); }
    }
}
