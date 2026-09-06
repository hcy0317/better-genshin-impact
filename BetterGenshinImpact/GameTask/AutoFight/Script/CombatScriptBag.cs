using BetterGenshinImpact.GameTask.AutoFight.Model;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.AutoFight.Script;

public class CombatScriptBag(List<CombatScript> combatScripts)
{
    private List<CombatScript> CombatScripts { get; set; } = combatScripts;

    public CombatScriptBag(CombatScript combatScript) : this([combatScript])
    {
    }

    public List<CombatCommand> FindCombatScript(ReadOnlyCollection<Avatar> avatars)
    {
        foreach (var combatScript in CombatScripts)
        {
            if (combatScript.HasFlowCommands && combatScript.AvatarNames
                .Where(name => name != CombatScriptParser.CurrentAvatarName)
                .ToHashSet().IsSubsetOf(avatars.Select(avatar => avatar.Name)))
            {
                Logger.LogInformation("匹配到增强战斗脚本：{Name}", combatScript.Name);
                return combatScript.SelectForParty(avatars.Select(avatar => avatar.Name));
            }
            var matchCount = 0;
            foreach (var avatar in avatars)
            {
                if (combatScript.AvatarNames.Contains(avatar.Name))
                {
                    matchCount++;
                }

                if (matchCount != avatars.Count) continue;
                // Logger.LogInformation("匹配到战斗脚本：{Name}，共{Cnt}条指令，涉及角色：{Str}", 
                // combatScript.Name, combatScript.CombatCommands.Count, string.Join(",", combatScript.AvatarNames)); 
                Logger.LogInformation("匹配到战斗脚本：{Name}", combatScript.Name); 
                return combatScript.CombatCommands;
            }

            combatScript.MatchCount = matchCount;
        }

        // 没有找到匹配的战斗脚本
        // 按照匹配数量降序排序
        var fallback = CombatScripts.Where(script => !script.HasFlowCommands)
            .OrderByDescending(script => script.MatchCount).FirstOrDefault();
        if (fallback == null || fallback.MatchCount == 0)
        {
            throw new Exception("未匹配到任何战斗脚本");
        }

        Logger.LogWarning("未完整匹配到四人队伍，使用匹配度最高的队伍：{Name}", fallback.Name);
        return fallback.CombatCommands;
    }
}
