using System.Collections.Generic;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

/// <summary>
/// 输入协议的保守能力声明，不是技能数值或施放成功证据。
/// 未声明、特殊物理序列和未核验的多段形态一律不授予恢复/可选能力。
/// </summary>
internal sealed record CombatSkillInputCapability(double RecoveryWindowSeconds, bool AllowsOptionalBurst = false,
    double MinimumContinuationSeconds = 0);

internal static class CombatSkillInputCapabilities
{
    // 仅登记已核验的单次输入形态；执行状态机不再包含角色特判。
    // 原有两项保持一秒恢复窗口；钟离Q在原逻辑动作预算内观察并最多恢复一次。
    private static readonly IReadOnlyDictionary<(string Actor, Method Skill, bool Hold), CombatSkillInputCapability> Known =
        new Dictionary<(string, Method, bool), CombatSkillInputCapability>
        {
            [("班尼特", Method.Burst, false)] = new(1, MinimumContinuationSeconds: 2),
            [("钟离", Method.Skill, true)] = new(1, MinimumContinuationSeconds: 2),
            [("钟离", Method.Burst, false)] = new(CombatFlowPolicy.ActionTimeoutSeconds,
                AllowsOptionalBurst: true, MinimumContinuationSeconds: 2)
        };

    internal static CombatSkillInputCapability? Resolve(CombatCommand command) =>
        command.NativeSkillSequence == null &&
        Known.TryGetValue((command.Name, command.Method, command.HasFlag("hold")), out var capability)
            ? capability : null;
}
