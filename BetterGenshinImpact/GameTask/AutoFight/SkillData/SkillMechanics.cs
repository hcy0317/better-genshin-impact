using System.Collections.Generic;

namespace BetterGenshinImpact.GameTask.AutoFight.SkillData;

/// <summary>来源校订的输入形态；策略仍使用 e/hold，而不是数值档位。</summary>
public sealed class SkillFormFact
{
    public string? CooldownMetric { get; set; }
    public string? DefaultEffect { get; set; }
    public int? MaximumCharges { get; set; }
    public List<SkillResourceProduction> ResourceProduction { get; set; } = [];
    public List<SkillEffectFact> Effects { get; set; } = [];
    public List<SkillRefreshFact> Refreshes { get; set; } = [];
}

public sealed class SkillEffectFact
{
    public string Id { get; set; } = "";
    public string Capability { get; set; } = "";
    public string DurationMetric { get; set; } = "";
    public string? DurationSkillId { get; set; }
    public bool EndsOnSwitch { get; set; }
    public string Scope { get; set; } = "actor";
    public int? MinimumAscension { get; set; }
    public int? MinimumConstellation { get; set; }
    public string? RequiredPassiveSkillId { get; set; }
}

/// <summary>只有与事实版本一致的已校订关系能授权刷新，纯 CD/时长不能推导关系。</summary>
public sealed class SkillRefreshFact
{
    public string EffectId { get; set; } = "";
    public string ProducerSkillId { get; set; } = "";
    public bool Verified { get; set; }
    public string Revision { get; set; } = "";
    public string ProducerRevision { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public int? MinimumAscension { get; set; }
    public int? MinimumConstellation { get; set; }
    public string? RequiredPassiveSkillId { get; set; }
}

/// <summary>机制中的产生条件/数量，不代表本次输入已经产生或接到资源。空集合表示尚未核验。</summary>
public sealed class SkillResourceProduction
{
    public string Resource { get; set; } = "";
    public string Condition { get; set; } = "";
    public int? Count { get; set; }
}
