using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace BetterGenshinImpact.GameTask.AutoFight.SkillData;

public sealed class SkillMechanicsRule
{
    public string SkillId { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public Dictionary<string, string> Dependencies { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, SkillFormFact> Forms { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>独立于原始数值源的可更新规则包；匹配全部依赖原文后才绑定到当前事实版本。</summary>
public sealed class SkillMechanicsRulePack
{
    public int SchemaVersion { get; set; } = 1;
    public string Version { get; set; } = "";
    public List<SkillMechanicsRule> Rules { get; set; } = [];

    public static string Fingerprint(SkillFact fact)
    {
        // 数值曲线可以独立更新；原文或标签/参数映射变化必须重新核验语义。
        var source = JsonConvert.SerializeObject(new
        {
            fact.CharacterKey, fact.Slot, fact.EnglishDescription,
            Metrics = fact.Metrics.OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => new { Key = item.Key, item.Value.Label, item.Value.SourceParameter, item.Value.Unit })
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
    }

    public void Validate()
    {
        if (SchemaVersion != 1 || string.IsNullOrWhiteSpace(Version) || Version.Length > 128 || Rules == null || Rules.Count is 0 or > 2048)
            throw new InvalidDataException("机制规则包版本或数量无效");
        if (Rules.Any(rule => rule == null) || Rules.Select(rule => rule.SkillId).Distinct(StringComparer.Ordinal).Count() != Rules.Count)
            throw new InvalidDataException("机制规则包包含空项或重复技能");
        foreach (var rule in Rules)
        {
            if (string.IsNullOrWhiteSpace(rule.SkillId) || !IsSource(rule.SourceUrl) || rule.Dependencies == null ||
                !rule.Dependencies.ContainsKey(rule.SkillId) || rule.Dependencies.Any(item => string.IsNullOrWhiteSpace(item.Key) ||
                    item.Value == null || !Regex.IsMatch(item.Value, "^[0-9a-f]{64}$")) || rule.Forms == null || rule.Forms.Count == 0)
                throw new InvalidDataException("机制规则缺少有效来源或依赖指纹：" + rule.SkillId);
            foreach (var (name, form) in rule.Forms)
            {
                if (name is not ("press" or "hold") || form == null || form.Effects == null || form.Refreshes == null ||
                    form.MaximumCharges is < 1 or > 16 || form.ResourceProduction == null ||
                    form.ResourceProduction.Any(resource => resource == null || string.IsNullOrWhiteSpace(resource.Resource) ||
                        string.IsNullOrWhiteSpace(resource.Condition) || resource.Count is < 1) ||
                    form.Effects.Any(effect => effect == null || string.IsNullOrWhiteSpace(effect.Id) || string.IsNullOrWhiteSpace(effect.DurationMetric) ||
                        effect.Scope is not ("actor" or "party" or "target" or "range")) ||
                    form.Effects.Select(effect => effect.Id).Distinct(StringComparer.Ordinal).Count() != form.Effects.Count ||
                    form.DefaultEffect != null && !form.Effects.Any(effect => effect.Id == form.DefaultEffect))
                    throw new InvalidDataException("机制规则形态或效果无效：" + rule.SkillId);
                foreach (var effect in form.Effects)
                    if (effect.DurationSkillId != null && !rule.Dependencies.ContainsKey(effect.DurationSkillId) ||
                        effect.RequiredPassiveSkillId != null && !rule.Dependencies.ContainsKey(effect.RequiredPassiveSkillId) ||
                        effect.MinimumAscension is < 0 or > 6 || effect.MinimumConstellation is < 0 or > 6)
                        throw new InvalidDataException("持续时间来源必须列入规则依赖：" + rule.SkillId);
                foreach (var relation in form.Refreshes)
                    if (relation == null || string.IsNullOrWhiteSpace(relation.EffectId) ||
                        !rule.Dependencies.ContainsKey(relation.ProducerSkillId) ||
                        relation.RequiredPassiveSkillId != null && !rule.Dependencies.ContainsKey(relation.RequiredPassiveSkillId) ||
                        relation.MinimumAscension is < 0 or > 6 || relation.MinimumConstellation is < 0 or > 6 ||
                        !string.IsNullOrEmpty(relation.SourceUrl) && !IsSource(relation.SourceUrl))
                        throw new InvalidDataException("刷新关系必须绑定生成端依赖：" + rule.SkillId);
            }
        }
    }

    public Dictionary<string, SkillFact> Apply(IReadOnlyDictionary<string, SkillFact> facts)
    {
        Validate();
        var result = JsonConvert.DeserializeObject<Dictionary<string, SkillFact>>(JsonConvert.SerializeObject(facts))!;
        foreach (var rule in Rules)
        {
            if (!result.TryGetValue(rule.SkillId, out var fact)) continue;
            fact.Forms.Clear();
            fact.MechanicsVersion = Version;
            fact.MechanicsStatus = "needs-review";
            if (rule.Dependencies.Any(item => !facts.TryGetValue(item.Key, out var dependency) ||
                    dependency.Revision != fact.Revision || Fingerprint(dependency) != item.Value)) continue;
            bool HasSeconds(string skillId, string metric) => facts.TryGetValue(skillId, out var source) &&
                source.Metrics.TryGetValue(metric, out var value) && value.Unit == "seconds" && value.Values.Length > 0 &&
                value.Values.All(number => double.IsFinite(number) && number >= 0);
            if (rule.Forms.Values.Any(form => form.CooldownMetric != null && !HasSeconds(fact.Id, form.CooldownMetric) ||
                    form.Effects.Any(effect => !HasSeconds(effect.DurationSkillId ?? fact.Id, effect.DurationMetric)))) continue;
            fact.Forms = JsonConvert.DeserializeObject<Dictionary<string, SkillFormFact>>(JsonConvert.SerializeObject(rule.Forms))!;
            foreach (var relation in fact.Forms.Values.SelectMany(form => form.Refreshes))
            {
                relation.Revision = fact.Revision;
                relation.ProducerRevision = facts[relation.ProducerSkillId].Revision;
                if (string.IsNullOrEmpty(relation.SourceUrl)) relation.SourceUrl = rule.SourceUrl;
            }
            fact.MechanicsStatus = "verified-source";
        }
        return result;
    }

    private static bool IsSource(string? source) => Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme == "https";
}
