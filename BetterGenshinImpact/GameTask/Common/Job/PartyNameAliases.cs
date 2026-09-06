using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace BetterGenshinImpact.GameTask.Common.Job;

/// <summary>本地元素队伍改名后的旧游戏预设兼容；自定义名称仍按原有正则匹配。</summary>
internal static class PartyNameAliases
{
    private static readonly Dictionary<string, string> LegacyNames = new()
    {
        ["火"] = "钟班香万", ["水"] = "钟心那万", ["风"] = "钟芙琴万",
        ["雷"] = "钟纳久万", ["草"] = "钟心纳久", ["冰"] = "钟迪绫万",
        ["岩"] = "钟心娜万", ["矿物"] = "钟纳娜万"
    };

    internal static bool IsMatch(string actual, string requested)
    {
        foreach (var (element, legacy) in LegacyNames)
        {
            if (requested == element || requested == legacy)
                return actual.Trim() == element || actual.Trim() == legacy;
        }
        return Regex.IsMatch(actual, requested);
    }
}
