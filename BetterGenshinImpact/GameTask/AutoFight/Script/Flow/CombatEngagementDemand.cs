using BetterGenshinImpact.GameTask.AutoFight.Config;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

internal enum CombatEngagementDemand { Unknown, Melee, Ranged }

internal static class CombatEngagementPolicy
{
    // Only ordinary attacks with a known weapon envelope. Skills/charged attacks and
    // stance-dependent actors need their own evidence; their weapon alone is insufficient.
    internal static CombatEngagementDemand For(CombatCommand? command)
    {
        if (command?.Method != Method.Attack || command.Name is "达达利亚" or "基尼奇" ||
            !DefaultAutoFightConfig.CombatAvatarMap.TryGetValue(command.Name, out var actor))
            return CombatEngagementDemand.Unknown;
        return actor.Weapon switch
        {
            "1" or "11" or "13" => CombatEngagementDemand.Melee,
            "12" => CombatEngagementDemand.Ranged,
            _ => CombatEngagementDemand.Unknown
        };
    }
}
