using System;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

/// <summary>能力适配器的直接资源测量；没有相应传感器时必须返回 null，不能从 Q 未亮推导。</summary>
public sealed record CombatResourceSample(Guid BattleId, string Actor, Guid FrameId, double CapturedAt, double Value);
