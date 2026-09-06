using System;
using System.Collections.Generic;
using System.Linq;

namespace BetterGenshinImpact.GameTask.AutoFight.SkillData;

public sealed class SkillMetric
{
    public string Unit { get; set; } = "seconds";
    public string Label { get; set; } = "";
    public string SourceParameter { get; set; } = "";
    public double[] Values { get; set; } = [];
    public string SourceKind { get; set; } = "source";
    public string? OverrideReason { get; set; }
}

public sealed class SkillFact
{
    public string Id { get; set; } = "";
    public string CharacterKey { get; set; } = "";
    public string Character { get; set; } = "";
    public string Slot { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string EnglishDescription { get; set; } = "";
    public Dictionary<string, SkillMetric> Metrics { get; set; } = new();
    public Dictionary<string, SkillFormFact> Forms { get; set; } = new(StringComparer.Ordinal);
    public string MechanicsStatus { get; set; } = "unreviewed";
    public string? MechanicsVersion { get; set; }
    public string SourceUrl { get; set; } = "";
    public string Revision { get; set; } = "";
}

/// <summary>从一次数据库读事务得到的离线快照。编译策略时解析数值，战斗中不热加载。</summary>
public sealed class SkillCatalogSnapshot(IReadOnlyDictionary<string, SkillFact> skills,
    IReadOnlyDictionary<string, CharacterSkillProfile>? profiles = null)
{
    public IReadOnlyDictionary<string, SkillFact> Skills { get; } = skills;
    public IReadOnlyDictionary<string, CharacterSkillProfile> Profiles { get; } = profiles ?? new Dictionary<string, CharacterSkillProfile>();
    public bool IsApplicable(string characterKey, int? minimumAscension, int? minimumConstellation, string? requiredPassiveSkillId = null)
    {
        Profiles.TryGetValue(characterKey, out var profile);
        return (minimumAscension == null || profile?.Ascension >= minimumAscension) &&
               (minimumConstellation == null || profile?.Constellation >= minimumConstellation) &&
               (requiredPassiveSkillId == null || profile?.UnlockedPassives.Contains(requiredPassiveSkillId) == true);
    }

    public double Resolve(string skillId, string metric, int? level = null)
    {
        if (!Skills.TryGetValue(skillId, out var skill)) throw new InvalidOperationException("技能数据库缺少 " + skillId + "，请先同步");
        level ??= Profiles.TryGetValue(skill.CharacterKey, out var profile) ? profile.TalentLevels.GetValueOrDefault(skill.Slot, 1) : 1;
        if (level is < 1 or > 15) throw new ArgumentOutOfRangeException(nameof(level), "天赋等级必须为 1..15");
        if (!skill.Metrics.TryGetValue(metric, out var value)) throw new InvalidOperationException($"技能 {skillId} 没有数值项 {metric}");
        if (value.Unit != "seconds") throw new InvalidOperationException($"{skillId}/{metric} 不是秒数，不能用作时间");
        var index = value.Values.Length == 1 ? 0 : level.Value - 1;
        if (index >= value.Values.Length) throw new InvalidOperationException($"{skillId}/{metric} 缺少等级 {level} 的数据");
        return value.Values[index];
    }
}

public sealed class SkillCatalogSeed
{
    public string Revision { get; set; } = "";
    public DateTimeOffset RetrievedAt { get; set; }
    public List<SkillFact> Skills { get; set; } = [];
}
