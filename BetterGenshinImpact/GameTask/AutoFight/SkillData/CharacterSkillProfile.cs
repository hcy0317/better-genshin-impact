using System;
using System.Collections.Generic;
using System.Linq;

namespace BetterGenshinImpact.GameTask.AutoFight.SkillData;

/// <summary>用户确认的角色条件，null 为未知；装备说明不自动推导未核验的机制。</summary>
public sealed class CharacterSkillProfile
{
    public string CharacterKey { get; set; } = "";
    public int? Ascension { get; set; }
    public int? Constellation { get; set; }
    public Dictionary<string, int> TalentLevels { get; set; } = new(StringComparer.Ordinal);
    public List<string> UnlockedPassives { get; set; } = [];
    public string Equipment { get; set; } = "";
    public string Reason { get; set; } = "";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(CharacterKey) || string.IsNullOrWhiteSpace(Reason) || Ascension is < 0 or > 6 ||
            Constellation is < 0 or > 6 || TalentLevels == null || TalentLevels.Any(item =>
                item.Key is not ("a" or "e" or "q") || item.Value is < 1 or > 15) || UnlockedPassives == null ||
            UnlockedPassives.Any(id => string.IsNullOrWhiteSpace(id) || !id.StartsWith(CharacterKey + ".passive", StringComparison.Ordinal)) ||
            Equipment?.Length > 2000 || Reason.Length > 2000)
            throw new ArgumentException("角色覆盖需有效角色键、等级/命座范围和原因");
    }
}
