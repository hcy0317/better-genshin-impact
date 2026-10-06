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
        var (combatScript, matchCount) = SelectCombatScript(avatars.Select(avatar => avatar.Name).ToArray());
        if (matchCount == avatars.Count)
        {
            Logger.LogInformation("匹配到战斗脚本：{Name}", combatScript.Name);
        }
        else
        {
            Logger.LogWarning("未完整匹配到当前队伍，使用匹配度最高的队伍：{Name}", combatScript.Name);
        }

        return combatScript.HasFlowCommands
            ? combatScript.SelectForParty(avatars.Select(avatar => avatar.Name))
            : combatScript.CombatCommands;
    }

    internal (CombatScript Script, int MatchCount) SelectCombatScript(IReadOnlyCollection<string> avatarNames)
    {
        CombatScript? bestScript = null;
        var bestMatchCount = 0;
        CombatScript? bestFlow = null;
        var bestFlowMatchCount = 0;

        foreach (var combatScript in CombatScripts)
        {
            var matchCount = 0;
            foreach (var avatarName in avatarNames)
            {
                if (combatScript.AvatarNames.Contains(avatarName))
                {
                    matchCount++;
                }
            }

            if (combatScript.HasFlowCommands)
            {
                // 增强流程必须完整保留控制语句与所需角色，不得降级成部分匹配。
                try { _ = combatScript.SelectForParty(avatarNames); }
                catch (InvalidOperationException) { continue; }
                if (bestFlow == null || matchCount > bestFlowMatchCount ||
                    matchCount == bestFlowMatchCount && combatScript.AvatarNames.Count < bestFlow.AvatarNames.Count)
                { bestFlow = combatScript; bestFlowMatchCount = matchCount; }
                continue;
            }

            if (matchCount == 0)
            {
                continue;
            }

            // 先比较匹配人数；人数相同时，策略角色越少，匹配比例越高。
            // 即使已覆盖全队也继续比较，避免通用策略抢先覆盖专用策略。
            if (bestScript == null
                || matchCount > bestMatchCount
                || (matchCount == bestMatchCount && combatScript.AvatarNames.Count < bestScript.AvatarNames.Count))
            {
                bestScript = combatScript;
                bestMatchCount = matchCount;
            }
            // 两项相同时保留先遇到的策略，不改变候选列表顺序。
        }

        if (bestFlow != null) return (bestFlow, bestFlowMatchCount);
        if (bestScript == null)
        {
            var error = new Exception("未匹配到任何战斗脚本");
            // 不改变异常类型/文案与候选顺序；Data供调用方核对真实候选和队伍识别结果。
            var detail = DescribeNoMatch(avatarNames);
            error.Data["combatScriptSelection"] = detail;
            try
            {
                GameTask.Common.DiagnosticEvidenceScope.Current?.RequestLatestWindow("strategy:no-match", "strategy-unmatched",
                    detail + "; latest retained source, not a new team capture");
            }
            catch { }
            throw error;
        }

        return (bestScript, bestMatchCount);
    }

    internal string DescribeNoMatch(IReadOnlyCollection<string> avatars)
    {
        static string Safe(string name)
        {
            var value = (name ?? "unknown").Replace("\r", "").Replace("\n", "");
            return value[..Math.Min(value.Length, 40)];
        }
        return $"actualParty=[{string.Join(",", avatars.Take(8).Select(Safe))}] candidates={CombatScripts.Count} " +
            string.Join(";", CombatScripts.Take(12).Select((script, index) =>
                $"candidate={index} flow={script.HasFlowCommands} required=[{string.Join(",", script.AvatarNames.Take(8).Select(Safe))}] matched={script.AvatarNames.Count(avatars.Contains)}"));
    }
}
