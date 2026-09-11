using System;
using System.Collections.Generic;
using System.Linq;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

public readonly record struct CombatSkillObservation(Guid BattleId, long FrameId, double CapturedAt,
    bool? CoolingDown, bool? Ready);
public sealed record CombatSkillAttempt(Guid AttemptId, Guid BattleId, string Actor, Method Skill,
    string CommandId, double InputAt, double Deadline);
internal enum CombatSkillAttemptState { Empty, Pending, Confirmed, Expired }

/// <summary>在途槽按物理技能归属，不按脚本行或 press/hold 分配。Unknown 不释放槽。</summary>
public sealed class CombatSkillAttempts(Guid battleId) : IDisposable
{
    private sealed class Slot(CombatSkillAttempt attempt)
    {
        public CombatSkillAttempt Attempt { get; } = attempt;
        public bool SawCooldown;
        public bool Confirmed;
        public bool CreditTaken;
        public bool InactiveActorProbed;
        public long FrameId = -1;
        public double ObservedAt = attempt.InputAt;
        public double? ReadySince;
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
                slot.ReadySince = null;
                slot.SawCooldown = true;
                return ConfirmSlot(slot, sample.CapturedAt);
            }
            // 冷却周期完成可释放；若原输入从未获确认，过期后需两帧明确就绪，
            // 仅结束旧冲突，不补记施放成功。旧帧和 Unknown 均不能授权重发。
            if (sample.CoolingDown == false && sample.Ready == true)
            {
                if (slot.SawCooldown) _slots.Remove((actor, skill));
                else if (sample.CapturedAt >= slot.Attempt.Deadline)
                {
                    slot.ReadySince ??= sample.CapturedAt;
                    if (sample.CapturedAt - slot.ReadySince.Value >= 0.2) _slots.Remove((actor, skill));
                }
            }
            else slot.ReadySince = null;
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
            var confirmed = ConfirmSlot(slot, confirmedAt);
            if (confirmed) slot.CreditTaken = true; // 即时调用已经拿到了 Succeeded，不能在下一轮再领取。
            return confirmed;
        }
    }

    // A release is new readiness evidence, not a late success receipt. The
    // removed attempt is returned once so separate goals cannot reuse it.
    internal CombatSkillAttempt? TryReleaseExpiredReady(string actor, Method skill,
        CombatSkillObservation first, CombatSkillObservation second)
    {
        lock (_gate)
        {
            if (_closed || !_slots.TryGetValue((actor, skill), out var slot) || slot.CreditTaken ||
                first.BattleId != battleId || second.BattleId != battleId ||
                first.FrameId <= slot.FrameId || second.FrameId <= first.FrameId ||
                !double.IsFinite(first.CapturedAt) || !double.IsFinite(second.CapturedAt) ||
                first.CapturedAt <= slot.ObservedAt || first.CapturedAt < slot.Attempt.Deadline ||
                second.CapturedAt - first.CapturedAt < 0.2 ||
                first.Ready != true || second.Ready != true || first.CoolingDown != false || second.CoolingDown != false)
                return null;
            _slots.Remove((actor, skill));
            return slot.Attempt;
        }
    }

    internal bool HasUnresolved(string actor, Method skill)
    {
        lock (_gate) return !_closed && _slots.TryGetValue((actor, skill), out var slot) && !slot.CreditTaken;
    }

    internal CombatSkillAttempt? TakeInactiveActorProbe(Func<string, bool?> isActive)
    {
        lock (_gate)
        {
            if (_closed) return null;
            foreach (var slot in _slots.Values)
            {
                if (slot.CreditTaken || slot.Confirmed || slot.InactiveActorProbed || isActive(slot.Attempt.Actor) != false)
                    continue;
                slot.InactiveActorProbed = true;
                return slot.Attempt;
            }
            return null;
        }
    }

    internal CombatSkillAttemptState GetState(string actor, Method skill, double now)
    {
        lock (_gate)
        {
            if (_closed) return CombatSkillAttemptState.Expired;
            if (!_slots.TryGetValue((actor, skill), out var slot)) return CombatSkillAttemptState.Empty;
            if (slot.CreditTaken) return CombatSkillAttemptState.Confirmed;
            return !double.IsFinite(now) || now >= slot.Attempt.Deadline
                ? CombatSkillAttemptState.Expired : CombatSkillAttemptState.Pending;
        }
    }

    public CombatSkillAttempt? TakeConfirmation(string actor, Method skill, string commandId, double now)
    {
        lock (_gate)
        {
            if (_closed || !_slots.TryGetValue((actor, skill), out var slot) || !slot.Confirmed || slot.CreditTaken
                || slot.Attempt.CommandId != commandId || !double.IsFinite(now)
                || now < slot.ObservedAt || now >= slot.Attempt.Deadline) return null;
            slot.CreditTaken = true;
            return slot.Attempt;
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
