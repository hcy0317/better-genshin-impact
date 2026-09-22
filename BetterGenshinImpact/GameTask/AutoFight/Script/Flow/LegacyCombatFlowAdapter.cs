using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

internal sealed record LegacyGuardianOptions(string Actor, bool Hold, GuardianCoverageMode CoverageMode,
    double? Duration, bool Burst = false, bool SkipGuardianBody = false)
{
    internal static LegacyGuardianOptions? From(AutoFightParam param, IEnumerable<NativeCombatActor> actors) =>
        int.TryParse(param.GuardianAvatar, out var index) && actors.FirstOrDefault(actor => actor.Index == index) is { } actor
            ? new(actor.Name, param.GuardianAvatarHold, param.GuardianCoverageMode, param.GuardianShieldDurationSeconds,
                param.BurstEnabled, param.GuardianCombatSkip) : null;
}

/// <summary>只在普通格式入口规范化兼容政策；增强语法的整队和required要求不参与模板过滤。</summary>
internal static class LegacyCombatFlowAdapter
{
    internal static CombatScript Prepare(IReadOnlyList<CombatCommand> commands, IEnumerable<string> party,
        bool loop, CombatScriptExecutionMode mode, LegacyGuardianOptions? guardian = null)
    {
        var available = ValidateParty(commands, party, mode);
        var enhanced = commands.Any(command => command.RequiresFlow);
        var selected = commands.Where(command => enhanced || command.Method.IsFlowControl ||
                command.Name == CombatScriptParser.CurrentAvatarName || available.Contains(command.Name))
            .Select(command => new CombatCommand(command) { LegacyOutcomePolicy = !enhanced }).ToList();
        if (selected.Count == 0) throw Failure("NO_APPLICABLE_ACTOR");
        if (!enhanced) selected = ProtectHeldInputSpans(selected, "$legacy-txt");
        if (!enhanced && guardian != null) selected = ApplyGuardian(selected, guardian, includeDeclaration: true);
        if (!enhanced && loop)
            selected.Insert(0, new CombatCommand("", "strategy(loop=battle)") { IsCompilerGenerated = true });
        return new(selected.Where(command => !command.Method.IsFlowControl).Select(command => command.Name).ToHashSet(StringComparer.Ordinal), selected);
    }

    internal static HashSet<string> ValidateParty(IReadOnlyList<CombatCommand> commands, IEnumerable<string> party,
        CombatScriptExecutionMode mode)
    {
        if (commands.Count == 0) throw Failure("EMPTY_FRAGMENT");
        var available = party.ToHashSet(StringComparer.Ordinal);
        if (available.Count == 0) throw Failure("PARTY_NOT_INITIALIZED");
        var named = commands.Where(command => !command.Method.IsFlowControl && command.Name != CombatScriptParser.CurrentAvatarName)
            .Select(command => command.Name).ToHashSet(StringComparer.Ordinal);
        var enhanced = commands.Any(command => command.RequiresFlow);
        if ((enhanced || mode == CombatScriptExecutionMode.RequiredSequence) && !named.IsSubsetOf(available))
            throw Failure("REQUIRED_ACTOR_MISSING:" + string.Join(",", named.Except(available)));
        var hasCurrentActorAction = commands.Any(command => !command.Method.IsFlowControl &&
            command.Name == CombatScriptParser.CurrentAvatarName);
        if (!enhanced && !hasCurrentActorAction && named.Count > 0 && !named.Overlaps(available))
            throw Failure($"NO_APPLICABLE_ACTOR: 路线所需角色=[{string.Join(",", named)}]，当前队伍=[{string.Join(",", available)}]");
        return available;
    }

    internal static List<CombatCommand> ApplyGuardian(IReadOnlyList<CombatCommand> commands, LegacyGuardianOptions guardian,
        bool includeDeclaration)
    {
        // 使用同一流程内核的维护/记录能力，不能在普通入口重开同步盾奶执行器。
        var result = new List<CombatCommand>();
        var strict = guardian.CoverageMode == GuardianCoverageMode.RequireKnownCoverage;
        const string record = "$legacy-guardian";
        if (includeDeclaration && guardian.Duration is > 0)
            result.Add(new("", $"timing({record},duration={guardian.Duration.Value.ToString(CultureInfo.InvariantCulture)})") { IsCompilerGenerated = true });
        void AddBoundary()
        {
            var timing = guardian.Duration is > 0 ? $",timing={record}" : "";
            result.Add(new(guardian.Actor, $"e(fast,{(guardian.Hold ? "hold," : "")}record={record},maintain={record},before=4{timing}{(strict ? ",required" : "")})")
            { IsCompilerGenerated = true, GuardianDurationLimit = guardian.Duration });
            if (guardian.Burst) result.Add(new(guardian.Actor, "q") { IsCompilerGenerated = true });
        }
        var depth = 0;
        string? previous = null;
        foreach (var original in commands)
        {
            var command = new CombatCommand(original)
            { IsCompilerGenerated = original.IsCompilerGenerated || strict || original.Name == guardian.Actor && original.Method == Method.Skill };
            if (command.Method == Method.Segment && command.Args?.FirstOrDefault() == "start")
            {
                if (depth++ == 0) { AddBoundary(); previous = null; }
            }
            else if (command.Method == Method.Segment && command.Args?.FirstOrDefault() == "end") depth--;
            else if (!command.Method.IsFlowControl)
            {
                if (depth == 0 && command.Name != previous) { AddBoundary(); previous = command.Name; }
                if (command.Name == guardian.Actor && guardian.SkipGuardianBody && depth == 0) continue;
                if (command.Name == guardian.Actor && command.Method == Method.Skill)
                    command.Options["if"] = $"!record-active({record})";
                else if (strict) command.Options["keep"] = record;
            }
            result.Add(command);
        }
        return result;
    }

    internal static List<CombatCommand> ProtectHeldInputSpans(IReadOnlyList<CombatCommand> commands, string prefix)
    {
        var result = new List<CombatCommand>();
        for (var index = 0; index < commands.Count; index++)
        {
            if (commands[index].Method != Method.KeyDown) { result.Add(commands[index]); continue; }
            var held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var end = index;
            for (; end < commands.Count; end++)
            {
                var command = commands[end];
                var key = command.Args?.FirstOrDefault() ?? "";
                if (command.Method == Method.KeyDown) held.Add(key);
                if (command.Method == Method.KeyUp) held.Remove(key);
                if (held.Count == 0) break;
            }
            end = Math.Min(end, commands.Count - 1);
            var span = commands.Skip(index).Take(end - index + 1).ToArray();
            // 预算包含原脚本的真实持续时间；不要求用户为兼容迁移增添atomic参数。
            var switches = 1 + span.Zip(span.Skip(1), (previous, next) => previous.Name != next.Name).Count(changed => changed);
            var budget = Math.Max(CombatFlowPolicy.AtomicSeconds, span.Sum(CombatFlowPolicy.ActionSeconds) +
                switches * CombatFlowPolicy.SwitchSeconds + span.Length * .15 + CombatFlowPolicy.RecoverySeconds);
            result.Add(new("", $"segment(start,name={prefix}:{index},atomic,timeout={budget.ToString(CultureInfo.InvariantCulture)})")
            {
                IsCompilerGenerated = true, LegacyOutcomePolicy = true,
                SourceFile = span[0].SourceFile, SourceLine = span[0].SourceLine
            });
            result.AddRange(span);
            result.Add(new("", "segment(end)") { IsCompilerGenerated = true, LegacyOutcomePolicy = true });
            index = end;
        }
        return result;
    }

    private static InvalidOperationException Failure(string reason) =>
        new($"[BGI_COMBAT_FRAGMENT_INCOMPLETE] Failed: {reason}");
}
