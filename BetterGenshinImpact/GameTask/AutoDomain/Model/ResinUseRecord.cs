using System;
using System.Collections.Generic;
using System.Linq;
using BetterGenshinImpact.GameTask.AutoStygianOnslaught;

namespace BetterGenshinImpact.GameTask.AutoDomain.Model;

/// <summary>
/// 树脂使用记录
/// 用于自动秘境指定树脂刷取次数时候，计算剩余刷取次数
/// </summary>
public class ResinUseRecord
{
    public string Name { get; set; }
    
    public int RemainCount { get; set; }
    
    public int MaxCount { get; set; }

    public ResinUseRecord(string name, int maxCount)
    {
        Name = name;
        RemainCount = maxCount;
        MaxCount = maxCount;
    }

    public static List<ResinUseRecord> BuildFromDomainParam(AutoDomainParam taskParam)
    {
        List<ResinUseRecord> list = [];
        if (taskParam.SpecifyResinUse)
        {
            if (taskParam.CondensedResinUseCount > 0)
            {
                list.Add(new ResinUseRecord("浓缩树脂", taskParam.CondensedResinUseCount));
            }
            if (taskParam.OriginalResin40UseCount > 0)
            {
                list.Add(new ResinUseRecord("原粹树脂40", taskParam.OriginalResin40UseCount));
            }
            if (taskParam.OriginalResin20UseCount > 0)
            {
                list.Add(new ResinUseRecord("原粹树脂20", taskParam.OriginalResin20UseCount));
            }
            if (taskParam.OriginalResinUseCount > 0)
            {
                list.Add(new ResinUseRecord("原粹树脂", taskParam.OriginalResinUseCount));
            }
            if (taskParam.TransientResinUseCount > 0)
            {
                list.Add(new ResinUseRecord("须臾树脂", taskParam.TransientResinUseCount));
            }
            if (taskParam.FragileResinUseCount > 0)
            {
                list.Add(new ResinUseRecord("脆弱树脂", taskParam.FragileResinUseCount));
            }

            if (list.Count == 0)
            {
                throw new Exception("你选择了指定树脂刷取次数，请至少配置一种树脂的刷取次数！");
            }

            list = OrderByPriority(list, taskParam.ResinPriorityList);
        }

        return list;
    }
    
    public static List<ResinUseRecord> BuildFromDomainParam(AutoStygianOnslaughtParam taskParam)
    {
        List<ResinUseRecord> list = [];
        if (taskParam.SpecifyResinUse)
        {
            if (taskParam.CondensedResinUseCount > 0)
            {
                list.Add(new ResinUseRecord("浓缩树脂", taskParam.CondensedResinUseCount));
            }
            if (taskParam.OriginalResinUseCount > 0)
            {
                list.Add(new ResinUseRecord("原粹树脂", taskParam.OriginalResinUseCount));
            }
            if (taskParam.TransientResinUseCount > 0)
            {
                list.Add(new ResinUseRecord("须臾树脂", taskParam.TransientResinUseCount));
            }
            if (taskParam.FragileResinUseCount > 0)
            {
                list.Add(new ResinUseRecord("脆弱树脂", taskParam.FragileResinUseCount));
            }

            if (list.Count == 0)
            {
                throw new Exception("你选择了指定树脂刷取次数，请至少配置一种树脂的刷取次数！");
            }

            list = OrderByPriority(list, taskParam.ResinPriorityList);
        }

        return list;
    }

    private static List<ResinUseRecord> OrderByPriority(
        List<ResinUseRecord> records,
        IReadOnlyList<string>? resinPriorityList)
    {
        if (resinPriorityList == null || resinPriorityList.Count == 0)
        {
            return records;
        }

        var priority = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < resinPriorityList.Count; index++)
        {
            var name = resinPriorityList[index]?.Trim();
            if (!string.IsNullOrEmpty(name))
            {
                priority.TryAdd(name, index);
            }
        }

        return records
            .Select((record, index) => new
            {
                Record = record,
                OriginalIndex = index,
                Priority = priority.GetValueOrDefault(GetPriorityName(record.Name), int.MaxValue)
            })
            .OrderBy(item => item.Priority)
            .ThenBy(item => item.OriginalIndex)
            .Select(item => item.Record)
            .ToList();
    }

    private static string GetPriorityName(string resinName)
    {
        return resinName.StartsWith("原粹树脂", StringComparison.Ordinal)
            ? "原粹树脂"
            : resinName;
    }
}
