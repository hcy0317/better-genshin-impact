using System.Collections.Generic;

namespace BetterGenshinImpact.GameTask.AutoFight.Script;

using System.Linq;

public class CombatScript(HashSet<string> avatarNames, List<CombatCommand> combatCommands)
{
    public string Name { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public HashSet<string> AvatarNames { get; set; } = avatarNames;
    public List<CombatCommand> CombatCommands { get; set; } = combatCommands;
    public bool HasFlowCommands => CombatCommands.Any(command => command.RequiresFlow);

    public bool IsAvailableForParty(IEnumerable<string> party)
    {
        var available = party.ToHashSet(System.StringComparer.Ordinal);
        if (HasFlowCommands)
        {
            _ = SelectForParty(available); // 无角色流程允许执行；缺少增强动作角色必须明确失败。
            return true;
        }
        return AvatarNames.Contains(CombatScriptParser.CurrentAvatarName) || available.Overlaps(AvatarNames);
    }

    public List<CombatCommand> SelectForParty(IEnumerable<string> party)
    {
        var available = party.ToHashSet(System.StringComparer.Ordinal);
        if (!HasFlowCommands) return CombatCommands.Where(command => available.Contains(command.Name)).ToList();
        var required = CombatCommands.Where(command => !command.Method.IsFlowControl &&
            command.Name != CombatScriptParser.CurrentAvatarName).Select(command => command.Name).ToHashSet(System.StringComparer.Ordinal);
        if (!required.IsSubsetOf(available))
            throw new System.InvalidOperationException("增强策略缺少所需角色：" + string.Join("、", required.Except(available)));
        // 声明/标记/调用不属于角色过滤对象；删掉它们会绕过 required 开场和静态校验。
        return CombatCommands.ToList();
    }

    /// <summary>
    /// 用于记录和队伍角色匹配到的数量
    /// </summary>
    public int MatchCount { get; set; } = 0;
}
