using System;
using System.Collections.Generic;
using System.Linq;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

public readonly record struct CombatSkillObservation(Guid BattleId, long FrameId, double CapturedAt,
    bool? CoolingDown, bool? Ready);
public sealed record CombatSkillAttempt(Guid AttemptId, Guid BattleId, string Actor, Method Skill,
    string CommandId, double InputAt, double Deadline);

/// <summary>在途槽按物理技能归属，不按脚本行或 press/hold 分配。Unknown 不释放槽。</summary>
public sealed class CombatSkillAttempts(Guid battleId) : IDisposable
{
    private sealed class Slot(CombatSkillAttempt attempt)
    {
        public CombatSkillAttempt Attempt { get; } = attempt;
        public bool SawCooldown;
        public bool Confirmed;
        public long FrameId = -1;
        public double ObservedAt = attempt.InputAt;
    }
    private readonly object _gate = new();
    private readonly Dictionary<(string Actor, Method Skill), Slot> _slots = new();
    private bool _closed;

    public bool IsOccupied(string actor, Method skill)
    {
        lock (_gate) return _closed || _slots.ContainsKey((actor, skill));
    }

    public CombatSkillAttempt? TryBegin(string actor, Method skill, string commandId, double inputAt, double deadline)
    {
        if (skill != Method.Skill && skill != Method.Burst) throw new ArgumentException("在途槽只适用于物理 E/Q 技能");
        if (!double.IsFinite(inputAt) || !double.IsFinite(deadline) || deadline <= inputAt)
            throw new ArgumentOutOfRangeException(nameof(deadline));
        lock (_gate)
        {
            if (_closed || _slots.ContainsKey((actor, skill))) return null;
            var attempt = new CombatSkillAttempt(Guid.NewGuid(), battleId, actor, skill, commandId, inputAt, deadline);
            _slots.Add((actor, skill), new(attempt));
            return attempt;
        }
    }

    public bool Observe(string actor, Method skill, CombatSkillObservation sample)
    {
        lock (_gate)
        {
            if (_closed || sample.BattleId != battleId || !_slots.TryGetValue((actor, skill), out var slot) ||
                sample.FrameId <= slot.FrameId || !double.IsFinite(sample.CapturedAt) || sample.CapturedAt <= slot.ObservedAt)
                return false;
            slot.FrameId = sample.FrameId;
            slot.ObservedAt = sample.CapturedAt;
            if (sample.CoolingDown == true)
            {
                slot.SawCooldown = true;
                return ConfirmSlot(slot, sample.CapturedAt);
            }
            // 必须先见过本次 CD，再用新的明确就绪证据结束冲突；OCR 空白不等于可重发。
            if (slot.SawCooldown && sample.CoolingDown == false && sample.Ready == true) _slots.Remove((actor, skill));
            return false;
        }
    }

    public bool Confirm(Guid attemptId, double confirmedAt)
    {
        lock (_gate)
        {
            var slot = _slots.Values.FirstOrDefault(value => value.Attempt.AttemptId == attemptId);
            if (_closed || slot == null) return false;
            slot.SawCooldown = true;
            return ConfirmSlot(slot, confirmedAt);
        }
    }

    private static bool ConfirmSlot(Slot slot, double at)
    {
        if (slot.Confirmed || !double.IsFinite(at) || at < slot.Attempt.InputAt || at >= slot.Attempt.Deadline) return false;
        slot.Confirmed = true;
        return true;
    }

    public void Dispose() { lock (_gate) { _closed = true; _slots.Clear(); } }
}
