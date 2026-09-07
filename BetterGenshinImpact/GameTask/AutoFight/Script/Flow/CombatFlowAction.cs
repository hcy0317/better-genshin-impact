using System;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

/// <summary>游戏边界必须在切人/等待之后、发送真实输入之前取得一次准入。</summary>
public sealed class CombatFlowAction
{
    private readonly CombatFlowContext _context;
    private readonly Func<bool> _validate;
    private readonly double _deadline;
    private readonly Func<bool>? _shouldYield;
    private readonly Func<bool>? _continuation;

    internal CombatFlowAction(CombatCommand command, CombatFlowContext context, Func<bool> validate, double deadline,
        Func<bool>? shouldYield = null, Func<bool>? continuation = null, bool canReuseConfirmedActor = false)
    {
        Command = command;
        _context = context;
        _validate = validate;
        _deadline = deadline;
        _shouldYield = shouldYield;
        _continuation = continuation;
        CanReuseConfirmedActor = canReuseConfirmedActor;
    }

    public CombatCommand Command { get; }
    internal bool CanReuseConfirmedActor { get; }
    internal string? DiagnosticReason { get; set; }
    public Guid BattleId => _context.BattleId;
    public double Now => _context.Now;
    public double? InputAt { get; private set; }
    public double RemainingBudget => Math.Max(0, _deadline - _context.Now);
    public bool CanStart => _context.IsOpen && RemainingBudget > 0 && _validate();
    public bool CanContinue => _context.IsOpen && RemainingBudget > 0 && _continuation?.Invoke() != false && _shouldYield?.Invoke() != true;

    public void ReportActiveActor(string actor) => _context.ObserveActiveActor(actor);

    public bool TryBeginInput()
    {
        if (InputAt != null || !CanStart) return false;
        InputAt = _context.Now;
        return true;
    }
}
