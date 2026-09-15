using System;
using System.Collections.Generic;
using System.Linq;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Model;
using Newtonsoft.Json;

namespace BetterGenshinImpact.GameTask;

internal sealed record OneDragonRunSnapshot(OneDragonFlowConfig Config, List<OneDragonTaskItem> Tasks)
{
    internal static OneDragonRunSnapshot Capture(OneDragonFlowConfig config, IEnumerable<OneDragonTaskItem> tasks) =>
        new(JsonConvert.DeserializeObject<OneDragonFlowConfig>(JsonConvert.SerializeObject(config))
                ?? throw new InvalidOperationException("无法固定本次一条龙配置"),
            tasks.Select(task => new OneDragonTaskItem(task.Name, task.Id)
            { IsEnabled = task.IsEnabled, IsNextTask = task.IsNextTask }).ToList());
}
