using System;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

/// <summary>只有已确认的实体识别才提交；没有稳定实体标识时返回 null，不用屏幕坐标伪造目标身份。</summary>
public sealed record CombatScopeObservation(Guid BattleId, long FrameId, double CapturedAt, string? TargetId = null,
    string? RangeId = null, bool? InRange = null, string? SkillId = null, double? ProducedAt = null);
