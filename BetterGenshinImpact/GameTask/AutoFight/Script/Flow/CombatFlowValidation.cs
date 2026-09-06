using System;
using System.Collections.Generic;
using System.Linq;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

internal static partial class CombatFlowValidation
{
    public static void Validate(CombatFlowProgram program, CombatScript script)
    {
        var records = new HashSet<string>(StringComparer.Ordinal);
        foreach (var command in script.CombatCommands)
        {
            ValidateActionArguments(command);
            if (command.Method == Method.Burst)
            {
                if (command.HasFlag("recharge") != command.Options.ContainsKey("from"))
                    throw command.Error("recharge 必须显式指定 from 供能来源");
                if (!command.HasFlag("recharge") && (command.Options.ContainsKey("attempts") || command.Options.ContainsKey("no-progress")))
                    throw command.Error("Q 的 attempts/no-progress 需要 recharge 过程");
            }
            if (command.Options.TryGetValue("record", out var producer)) records.Add(CombatFlowContext.NormalizeName(producer));
            if (command.Method == Method.Record) records.Add(CombatFlowContext.NormalizeName(command.Args!.Single()));
            if (command.Options.TryGetValue("once", out var once) && once != "battle") throw command.Error("once 仅支持 battle");
            if (once == "battle" && command.HasFlag("required") && (command.RoundParity != null || command.ActivatingRound.Count != 0))
                throw command.Error("required 的 once 开场不能用 round 过滤绕过；请移除 round，使用 once=battle 控制重复");
            if (command.Options.TryGetValue("resume", out var resume) && resume is not ("next" or "entry"))
                throw command.Error("resume 仅支持 next/entry");
            if (command.Options.ContainsKey("timeout")) _ = CombatFlowPolicy.Timeout(command);
            if (command.Options.ContainsKey("attempts")) _ = CombatFlowPolicy.Attempts(command);
            if (command.Options.ContainsKey("no-progress")) _ = CombatFlowPolicy.NoProgress(command);
            if (command.Options.ContainsKey("before")) _ = CombatFlowProgram.Number(command, "before");
            if (command.Options.ContainsKey("maintain") && (command.HasFlag("refresh") || command.Options.ContainsKey("refresh")))
                throw command.Error("maintain 与 refresh 不能组合");
            if (command.HasFlag("fast") && (command.HasFlag("refresh") || command.Options.ContainsKey("refresh")))
                throw command.Error("fast 与 refresh 不能组合");
            if (command.Options.TryGetValue("maintain", out var maintain) && command.Options.GetValueOrDefault("record") != maintain)
                throw command.Error("maintain 必须对应本动作的同名 record 生成端");
            if (command.Options.TryGetValue("watch", out var watch) && command.Options.GetValueOrDefault("record") != watch)
                throw command.Error("watch 必须对应同名 record 生成端");
            if (command.Options.TryGetValue("watch-mode", out var mode) &&
                (mode is not ("jump" or "call") || !command.Options.ContainsKey("watch")))
                throw command.Error("watch-mode 需要具名 watch，且仅支持 jump/call");
            if ((command.Options.GetValueOrDefault("watch-mode") == "call") != command.Options.ContainsKey("watch-target"))
                throw command.Error("watch-mode=call 必须指定 watch-target 维护片段");
            if (command.Options.TryGetValue("refresh", out var refresh) &&
                (command.Options.GetValueOrDefault("keep") != refresh || command.Options.GetValueOrDefault("record") == refresh))
                throw command.Error("关系式 refresh 必须消费同名 keep，不能再由 record 重复写入");
        }
        program.RecordNames.UnionWith(records);
        foreach (var command in script.CombatCommands)
            foreach (var key in new[] { "keep", "maintain", "watch" })
                if (command.Options.TryGetValue(key, out var name) && !records.Contains(CombatFlowContext.NormalizeName(name)))
                    throw command.Error("未声明记录：" + name);
        foreach (var block in program.Blocks.Values.Append(program.Root))
        {
            var resultIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in block.Nodes)
                if (node.Command.Options.TryGetValue("id", out var id) && !resultIds.Add(id))
                    throw node.Command.Error("同一片段内重复的动作结果 id：" + id);
            foreach (var (condition, source) in block.Nodes.Select(node => (node.Condition, node.Command)).Append((block.Requires, block.Declaration)))
                if (condition != null)
                    ValidateConditionReferences(program, block, condition, source!);
        }

        ValidateCallGraph(program);
        ValidateRecordDependencies(program);
        ValidateOpeningWatchTargets(program);
    }

    internal static void ValidateConditionReferences(CombatFlowProgram program, CombatFlowBlock block,
        ConditionEvaluator.CompiledCondition condition, CombatCommand source)
    {
        foreach (var reference in condition.NamedReferences)
        {
            var name = CombatFlowContext.NormalizeName(reference.Name);
            if (name.StartsWith('$')) throw source.Error("条件不能寻址编译器的保留命名空间");
            var declared = reference.Function switch
            {
                "succeeded" => block.Nodes.Any(node => node.Command.Options.GetValueOrDefault("id") == name),
                "call-index" => program.Blocks.ContainsKey(name),
                _ => program.RecordNames.Contains(name)
            };
            if (!declared) throw source.Error("条件中引用了未声明的记录、当前片段结果或片段名：" + name);
        }
    }
}
