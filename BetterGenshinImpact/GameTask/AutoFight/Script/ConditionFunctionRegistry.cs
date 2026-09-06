using System;
using System.Collections.Generic;

namespace BetterGenshinImpact.GameTask.AutoFight.Script;

/// <summary>函数词法名与增强编译器签名共用登记表；不改变旧布尔执行器的返回政策。</summary>
internal static class ConditionFunctionRegistry
{
    private static readonly Dictionary<string, (int Min, int Max)> Signatures = new(StringComparer.OrdinalIgnoreCase)
    {
        ["last-exec"] = (1, 3), ["q-ready"] = (0, 1), ["q-energy-low"] = (0, 1), ["q-cd"] = (0, 1),
        ["e-ready"] = (0, 1), ["e-cd"] = (0, 1), ["low-hp"] = (0, 1), ["battle-time"] = (1, 2),
        ["in-party"] = (1, 1), ["onfield"] = (0, 1), ["t"] = (0, 0), ["since"] = (0, 1), ["count"] = (0, 3),
        ["min"] = (1, 64), ["max"] = (1, 64), ["last-check"] = (0, 0), ["record-exists"] = (1, 1),
        ["record-age"] = (1, 1), ["record-active"] = (1, 1), ["record-remaining"] = (1, 1),
        ["succeeded"] = (1, 1), ["round-odd"] = (0, 0), ["call-index"] = (1, 1), ["odd"] = (1, 1)
    };

    public static IEnumerable<string> Names => Signatures.Keys;

    public static void Validate(string name, int count)
    {
        if (!Signatures.TryGetValue(name, out var signature)) throw new FormatException("未知条件函数：" + name);
        if (count < signature.Min || count > signature.Max)
            throw new FormatException($"条件函数 {name} 需要 {signature.Min}..{signature.Max} 个参数，实际为 {count}");
    }
}
