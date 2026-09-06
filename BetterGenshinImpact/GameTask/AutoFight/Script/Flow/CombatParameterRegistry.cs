using System;
using System.Collections.Generic;
using System.Linq;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

/// <summary>公共/动作参数的单一登记入口。动作位置参数仍归动作校验，工程默认值归 CombatFlowPolicy。</summary>
internal static class CombatParameterRegistry
{
    private static readonly string[] ActionOptions =
        ["record", "keep", "maintain", "before", "timing", "id", "if", "watch", "watch-mode", "watch-target", "timeout"];

    public static bool IsCommandFlag(string parameter) => parameter.Equals("required", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyCollection<string> OptionsFor(CombatCommand command)
    {
        var method = command.Method;
        if (method == Method.Timing) return ["cd", "duration"];
        if (method == Method.Strategy) return ["loop"];
        if (method == Method.Segment) return command.Args?.FirstOrDefault() == "end"
            ? ["record"] : ["name", "requires", "onfail", "timeout", "if"];
        if (method == Method.Call) return ["once", "if", "id", "resume", "timeout", "attempts"];
        if (method == Method.Branch) return ["if", "then", "else", "unknown", "id"];
        if (method == Method.Record) return ["duration", "timing"];
        if (method == Method.Return || method == Method.Round) return [];
        if (method == Method.JumpTo) return ["if", "timeout", "attempts"];
        if (method == Method.Skill) return [.. ActionOptions, "feed", "refresh", "effect"];
        if (method == Method.Burst) return [.. ActionOptions, "from", "attempts", "no-progress", "refresh", "effect"];
        return ActionOptions;
    }

    public static void Validate(CombatCommand command)
    {
        if (command.Method == Method.Round) throw command.Error("round 必须位于动作组开头；请使用 | 分组或在声明片段内另起一行");
        var allowed = OptionsFor(command);
        foreach (var key in command.Options.Keys)
            if (!allowed.Contains(key, StringComparer.OrdinalIgnoreCase)) throw command.Error("未知或不适用于此处的参数：" + key);
        foreach (var flag in command.Flags)
            if (!IsCommandFlag(flag) || command.Method == Method.Record || command.Method == Method.Timing ||
                command.Method == Method.Strategy || command.Method == Method.Return ||
                command.Method == Method.Segment && command.Args?.Contains("end") == true)
                throw command.Error("不适用于此命令的标志：" + flag);
        var args = command.Args ?? [];
        if (command.Method == Method.Call || command.Method == Method.JumpTo || command.Method == Method.Record || command.Method == Method.Timing)
        {
            if (args.Count != 1 || string.IsNullOrWhiteSpace(args[0])) throw command.Error(command.Method.Alias[0] + " 需要一个非空名称");
        }
        else if (command.Method == Method.Return || command.Method == Method.Branch || command.Method == Method.Strategy)
        {
            if (args.Count != 0) throw command.Error(command.Method.Alias[0] + " 不接受位置参数");
        }
        else if (command.Method == Method.Segment)
        {
            if (args.Count == 0 || args.Distinct(StringComparer.Ordinal).Count() != args.Count ||
                args[0] is not ("start" or "end") || args[0] == "end" && args.Count != 1 ||
                args.Skip(1).Any(arg => arg is not ("define" or "atomic")))
                throw command.Error("segment 只接受 start[,define,atomic] 或 end");
        }
        else if (command.Method == Method.Skill)
        {
            if (args.Any(arg => arg is not ("hold" or "fast" or "wait" or "refresh")) || args.Distinct().Count() != args.Count)
                throw command.Error("未知或重复 E 参数");
        }
        else if (command.Method == Method.Burst && (args.Any(arg => arg != "recharge") || args.Count > 1))
            throw command.Error("未知或重复 Q 参数");
    }
}
