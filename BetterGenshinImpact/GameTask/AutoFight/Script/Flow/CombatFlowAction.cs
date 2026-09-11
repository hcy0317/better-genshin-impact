using System;
using System.Runtime.CompilerServices;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

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

    internal CombatFlowAction(CombatCommand command, CombatFlowContext context, Func<bool> validate, double deadline,
        Func<bool>? shouldYield = null, Func<bool>? continuation = null, bool canReuseConfirmedActor = false)
    {
        Command = command;
        CommandId = CommandIdentities.GetValue(command, _ => new()).Value;
        _context = context;
        _validate = validate;
        _deadline = deadline;
        _shouldYield = shouldYield;
        _continuation = continuation;
        CanReuseConfirmedActor = canReuseConfirmedActor;
    }

    public CombatCommand Command { get; }
    public string CommandId { get; }
    internal bool CanReuseConfirmedActor { get; }
    internal string? DiagnosticReason { get; set; }
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
        if (EffectiveInputAt != null || !CanStart) return false;
        InputAt = _context.Now;
        return true;
    }

    internal bool AcceptConfirmation(CombatSkillAttempt attempt)
    {
        if (EffectiveInputAt != null || !CanStart || attempt.BattleId != BattleId || attempt.CommandId != CommandId
            || attempt.Skill != Command.Method || attempt.InputAt > Now || Now >= attempt.Deadline
            || Command.Name != CombatScriptParser.CurrentAvatarName && attempt.Actor != Command.Name) return false;
        _confirmedInputAt = attempt.InputAt;
        _confirmationDeadline = attempt.Deadline;
        return true;
    }
}
