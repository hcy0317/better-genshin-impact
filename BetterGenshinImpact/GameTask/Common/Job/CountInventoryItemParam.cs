using BetterGenshinImpact.GameTask.Model.GameUI;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BetterGenshinImpact.GameTask.Common.Job;

/// <summary>
/// 背包物品计数任务参数。
/// <para><see cref="GridScreenName"/> 可选：传入时按指定页面扫描；为 null 时按 item.csv 的 page 自动分组多页扫描。</para>
/// <para><see cref="StopByItemSort"/> 默认 false；显式置 true 时，若当前页所有目标物品都有有效 sort_order，则按目标最大 sort_order 提前结束本页扫描。</para>
/// </summary>
public class CountInventoryItemParam
{
    public GridScreenName? GridScreenName { get; set; }

    /// <summary>兼容旧脚本的单物品调用，仍返回单个数量。</summary>
    public string? ItemName { get; set; }

    public List<string> ItemNames { get; set; } = [];

    private ItemIconRecognitionMode? _iconRecognitionMode;
    public ItemIconRecognitionMode IconRecognitionMode
    {
        get => _iconRecognitionMode ?? ItemIconRecognitionMode.GridIcon;
        set => _iconRecognitionMode = value;
    }

    /// <summary>
    /// 是否启用按物品排序提前停止；默认 false。详见类注释。
    /// </summary>
    public bool StopByItemSort { get; set; }

    /// <summary>贵重道具多物品计数的可选覆盖证明；只有完整扫描才确认缺项为零。</summary>
    public bool IncludeScanEvidence { get; set; }

    /// <summary>
    /// 供脚本创建后逐项赋值；参数校验在任务消费参数时执行。
    /// </summary>
    public CountInventoryItemParam()
    {
    }

    internal void UseDefaultIconRecognitionMode(ItemIconRecognitionMode mode) => _iconRecognitionMode ??= mode;
    public IEnumerable<string>? GetItemNamesOrNull() => ItemNames.Count > 0 ? ItemNames : null;

    public void Validate()
    {
        ItemNames ??= [];
        bool hasItemName = !string.IsNullOrWhiteSpace(ItemName);
        if (!hasItemName) ItemName = null;
        bool hasItemNames = ItemNames.Count > 0;

        if (hasItemName && hasItemNames)
            throw new ArgumentException($"参数{nameof(ItemName)}和{nameof(ItemNames)}不能同时使用");

        if (!hasItemName && !hasItemNames)
        {
            throw new ArgumentException($"参数{nameof(ItemNames)}不能为空");
        }

        if (ItemNames.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException($"参数{nameof(ItemNames)}不能包含空名称");
        }
        if (IncludeScanEvidence && (hasItemName || GridScreenName != Model.GameUI.GridScreenName.PreciousItems ||
            IconRecognitionMode != ItemIconRecognitionMode.Item))
            throw new ArgumentException("扫描覆盖证明仅支持贵重道具页的Item模式多物品计数");
    }
}
