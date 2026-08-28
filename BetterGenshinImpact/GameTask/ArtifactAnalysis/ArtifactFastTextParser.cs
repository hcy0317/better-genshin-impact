using BetterGenshinImpact.GameTask.AutoArtifactSalvage;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace BetterGenshinImpact.GameTask.ArtifactAnalysis;

internal static partial class ArtifactFastTextParser
{
    private static readonly IReadOnlyDictionary<string, string> AffixKeys =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["生命值"] = "hp",
            ["攻击力"] = "atk",
            ["防御力"] = "def",
            ["元素精通"] = "eleMas",
            ["元素充能效率"] = "enerRech_",
            ["暴击率"] = "critRate_",
            ["暴击伤害"] = "critDMG_",
            ["治疗加成"] = "heal_",
            ["风元素伤害加成"] = "anemo_dmg_",
            ["冰元素伤害加成"] = "cryo_dmg_",
            ["草元素伤害加成"] = "dendro_dmg_",
            ["雷元素伤害加成"] = "electro_dmg_",
            ["岩元素伤害加成"] = "geo_dmg_",
            ["水元素伤害加成"] = "hydro_dmg_",
            ["物理伤害加成"] = "physical_dmg_",
            ["火元素伤害加成"] = "pyro_dmg_"
        };

    private static readonly IReadOnlyDictionary<string, string> SlotKeys =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["生之花"] = "flower",
            ["死之羽"] = "plume",
            ["时之沙"] = "sands",
            ["空之杯"] = "goblet",
            ["理之冠"] = "circlet"
        };

    internal static ArtifactSubstatDto ParseAffix(
        string nameText,
        string valueText,
        bool dormant = false)
    {
        var canonical = AutoArtifactSalvageTask.ResolveKnownAffixLine(
                            [NormalizeName(nameText)], AffixKeys.Keys)
                        ?? throw new FormatException($"无法识别圣遗物词条：{nameText}");
        var value = ParseNumber(valueText);
        var key = AffixKeys[canonical];
        if (valueText.Contains('%') && key is "hp" or "atk" or "def") key += "_";
        return new ArtifactSubstatDto(key, value, dormant);
    }

    internal static ArtifactSubstatDto ParseAffixLine(string text)
    {
        var normalized = text.Replace('＋', '+').Replace('，', ',').Replace('。', '.').Trim();
        var digitIndex = normalized.IndexOfAny("0123456789".ToCharArray());
        if (digitIndex <= 0) throw new FormatException($"无法拆分圣遗物副词条：{text}");
        var name = normalized[..digitIndex].Trim().TrimStart('·', '•', '-', ' ').TrimEnd('+', ' ');
        return ParseAffix(name, normalized[digitIndex..], IsDormantAffixLine(normalized));
    }

    internal static bool TryParseAffixLine(
        string text,
        out ArtifactSubstatDto? affix)
    {
        try
        {
            affix = ParseAffixLine(text);
            return true;
        }
        catch (FormatException)
        {
            affix = null;
            return false;
        }
    }

    internal static string ParseSlot(string text)
    {
        var canonical = AutoArtifactSalvageTask.ResolveKnownAffixLine(
                            [text], SlotKeys.Keys)
                        ?? throw new FormatException($"无法识别圣遗物部位：{text}");
        return SlotKeys[canonical];
    }

    internal static bool IsEnhancementMaterial(string text) =>
        NormalizeName(text).Contains("圣遗物强化素材", StringComparison.Ordinal);

    internal static bool IsDormantAffixLine(string text)
    {
        var normalized = NormalizeName(text);
        return normalized.Contains("待激活", StringComparison.Ordinal)
               || normalized.Contains("待激话", StringComparison.Ordinal)
               || normalized.Contains("未激活", StringComparison.Ordinal);
    }

    internal static ArtifactSubstatDto[] ApplyDormantFourthLine(
        IReadOnlyList<ArtifactSubstatDto> substats,
        int level,
        string fourthLineText)
    {
        var result = substats.ToArray();
        if (level == 0 && result.Length >= 4 && IsDormantAffixLine(fourthLineText))
        {
            result[3] = result[3] with { Dormant = true };
        }
        return result;
    }

    internal static int ParseLevel(string text)
    {
        var match = NumberPattern().Match(text);
        if (!match.Success || !int.TryParse(match.Value, out var level) || level is < 0 or > 20)
        {
            throw new FormatException($"无法识别圣遗物等级：{text}");
        }
        return level;
    }

    private static string NormalizeName(string value)
    {
        return value.Trim().TrimStart('·', '•', '-', ' ').Replace(" ", string.Empty);
    }

    private static double ParseNumber(string value)
    {
        var normalized = value.Replace(",", string.Empty).Replace("，", string.Empty).Replace("。", ".");
        var match = DecimalPattern().Match(normalized);
        if (!match.Success || !double.TryParse(
                match.Value, NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var number))
        {
            throw new FormatException($"无法识别圣遗物词条数值：{value}");
        }
        return number;
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"\d+(?:\.\d+)?")]
    private static partial Regex DecimalPattern();
}
