using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

internal sealed record CombatCallContext(Guid Id, string Name, double EnteredAt);

/// <summary>游戏边界必须在切人/等待之后、发送真实输入之前取得一次准入。</summary>
public sealed class CombatFlowAction
{
    private sealed class CommandIdentity { public string Value { get; } = Guid.NewGuid().ToString("N"); }
    private static readonly ConditionalWeakTable<CombatCommand, CommandIdentity> CommandIdentities = new();
    private double? _confirmedInputAt;
    private double _confirmationDeadline = double.PositiveInfinity;
    private readonly CombatFlowContext _context;
    private readonly Func<bool> _validate;
    private readonly double _deadline;
    private readonly Func<bool>? _shouldYield;
    private readonly Func<bool>? _continuation;
    private readonly Guid _legacySubmissionId = Guid.NewGuid();
    private HashSet<Guid>? _reportedSubmissions;
    internal Guid InputRequestId => _legacySubmissionId;
    internal int SubmissionCount => _reportedSubmissions?.Count ?? 0;

    internal void ClearUnsubmittedInput(CombatSkillAttempt? attempt = null)
    {
        if (_reportedSubmissions?.Contains(InputRequestId) == true)
            throw new InvalidOperationException("已提交输入不能回退为未发送");
        if (attempt != null)
        {
            if (PendingAttempt?.AttemptId != attempt.AttemptId) throw new InvalidOperationException("未发送请求身份不一致");
            PendingAttempt = null;
        }
        InputAt = null;
        DiagnosticAttemptId = null;
    }

    internal void RecordInputSubmission(Guid requestId)
    {
        if (requestId == Guid.Empty) throw new ArgumentException("输入回执缺少请求身份", nameof(requestId));
        _reportedSubmissions ??= new();
        if (_reportedSubmissions.Add(requestId)) _context.RecordInputAttempt();
    }

    internal void RecordLegacyInput()
    {
        if (InputAt != null && Command.Method != Method.Wait) RecordInputSubmission(_legacySubmissionId);
    }

    internal CombatFlowAction(CombatCommand command, CombatFlowContext context, Func<bool> validate, double deadline,
        Func<bool>? shouldYield = null, Func<bool>? continuation = null, bool canReuseConfirmedActor = false,
        CombatSkillAttempt? confirmationAttempt = null, IReadOnlyList<CombatCallContext>? callPath = null,
        Guid? atomicObservationId = null)
    {
        Command = command;
        CommandId = CommandIdentities.GetValue(command, _ => new()).Value;
        _context = context;
        _validate = validate;
        _deadline = deadline;
        _shouldYield = shouldYield;
        _continuation = continuation;
        CanReuseConfirmedActor = canReuseConfirmedActor;
        IsConfirmationOnly = confirmationAttempt != null;
        PendingAttempt = confirmationAttempt;
        CallPath = callPath ?? Array.Empty<CombatCallContext>();
        AtomicObservationId = atomicObservationId;
    }

    public CombatCommand Command { get; }
    public string CommandId { get; }
    internal IReadOnlyList<CombatCallContext> CallPath { get; }
    internal Guid? AtomicObservationId { get; }
    internal CombatSkillAttempt? PendingAttempt { get; private set; }
    internal bool IsConfirmationOnly { get; }
    internal double AbsoluteDeadline => Math.Min(_deadline, _confirmationDeadline);
    internal bool RegisterPendingAttempt(CombatSkillAttempt attempt)
    {
        if (IsConfirmationOnly || PendingAttempt != null || attempt.AttemptId == Guid.Empty ||
            attempt.BattleId != BattleId || attempt.CommandId != CommandId || attempt.Skill != Command.Method ||
            attempt.InputAt != InputAt || attempt.Deadline > AbsoluteDeadline ||
            Command.Name != CombatScriptParser.CurrentAvatarName && attempt.Actor != Command.Name) return false;
        PendingAttempt = attempt;
        return true;
    }
    internal bool CanReuseConfirmedActor { get; }
    internal string? DiagnosticReason { get; set; }
    internal bool CaptureDiagnostics { get; set; }
    internal Guid? DiagnosticAttemptId { get; set; }
    private Queue<string>? _diagnosticSamples;
    internal IReadOnlyList<string> DiagnosticSamples => _diagnosticSamples == null
        ? Array.Empty<string>() : Array.AsReadOnly(_diagnosticSamples.ToArray());
    internal void Trace(string phase, string detail)
    {
        if (!CaptureDiagnostics) return;
        // 只缓存已取得的值；不能把截图或待执行的识别闭包带出动作生命周期。
        detail = detail.Replace("\r", "\\r").Replace("\n", "\\n");
        if (detail.Length > 200) detail = detail[..200];
        _diagnosticSamples ??= new();
        // 容纳 Q 的初次识别、输入调用/返回和最多 16 次现有确认采样。
        if (_diagnosticSamples.Count == 32) _diagnosticSamples.Dequeue();
        _diagnosticSamples.Enqueue($"{Now:F3}s budget={RemainingBudget:F3}s {phase}: {detail}");
    }
    public Guid BattleId => _context.BattleId;
    public double Now => _context.Now;
    public double? InputAt { get; private set; }
    /// <summary>已确认原请求的时点；与本步是否发送输入分别记录。</summary>
    public double? EffectiveInputAt => _confirmedInputAt ?? InputAt;
    public double RemainingBudget => Math.Max(0, Math.Min(_deadline, _confirmationDeadline) - _context.Now);
    public bool CanStart => _context.IsOpen && RemainingBudget > 0 && _validate();
    public bool CanContinue => _context.IsOpen && RemainingBudget > 0 && _continuation?.Invoke() != false && _shouldYield?.Invoke() != true;

    public void ReportActiveActor(string actor) => _context.ObserveActiveActor(actor);

    public bool TryBeginInput()
    {
        if (IsConfirmationOnly || EffectiveInputAt != null || !CanStart) return false;
        InputAt = _context.Now;
        return true;
    }

    internal bool AcceptConfirmation(CombatSkillAttempt attempt)
    {
        if (EffectiveInputAt != null || !CanStart || attempt.BattleId != BattleId || attempt.CommandId != CommandId
            || IsConfirmationOnly && attempt.AttemptId != PendingAttempt?.AttemptId
            || attempt.Skill != Command.Method || attempt.InputAt > Now || Now >= attempt.Deadline
            || Command.Name != CombatScriptParser.CurrentAvatarName && attempt.Actor != Command.Name) return false;
        _confirmedInputAt = attempt.InputAt;
        _confirmationDeadline = attempt.Deadline;
        return true;
    }
}
