using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Model;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using Microsoft.Extensions.Logging;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.AutoPathing.Handler;

/// <summary>
/// 元素采集
/// </summary>
public class ElementalCollectHandler(ElementalType elementalType) : IActionHandler
{
    private readonly INativeCombatIo? _nativeIo;
    internal ElementalCollectHandler(ElementalType elementalType, INativeCombatIo nativeIo) : this(elementalType) => _nativeIo = nativeIo;

    public async Task RunAsync(CancellationToken ct, WaypointForTrack? waypointForTrack = null, object? config = null)
    {
        ct.ThrowIfCancellationRequested();
        var io = await NativeActionHandler.ResolveAsync(_nativeIo, ct);

        // 筛选出对应元素的角色列表
        var elementalCollectAvatars = ElementalCollectAvatarConfigs.Lists.Where(x => x.ElementalType == elementalType).ToList();
        // 循环遍历角色列表
        foreach (var combatScenesAvatar in io.Actors)
        {
            // 判断是否为对应元素的角色
            var elementalCollectAvatar = elementalCollectAvatars.FirstOrDefault(x => x.Name == combatScenesAvatar.Name);
            if (elementalCollectAvatar == null)
            {
                continue;
            }

            if (!elementalCollectAvatar.NormalAttack && !elementalCollectAvatar.ElementalSkill)
                throw new InvalidOperationException("该元素采集角色没有声明可执行动作：" + combatScenesAvatar.Name);
            var action = elementalCollectAvatar.NormalAttack ? "attack(0.1)" : "e(wait)";
            await NativeActionHandler.ExecuteAsync(io, combatScenesAvatar.Name + " " + action, ct);
            return;
        }
        throw new InvalidOperationException($"当前队伍没有可执行{elementalType.ToChinese()}元素采集的角色，路线未完成");
    }
}

public class ElementalCollectAvatar(string name, ElementalType elementalType, bool normalAttack, bool elementalSkill)
{
    public string Name { get; set; } = name;
    public ElementalType ElementalType { get; set; } = elementalType;
    public bool NormalAttack { get; set; } = normalAttack;

    public bool ElementalSkill { get; set; } = elementalSkill;

    // public CombatAvatar Info => DefaultAutoFightConfig.CombatAvatarMap[Name];

    public DateTime LastUseSkillTime { get; set; } = DateTime.MinValue;
}

public class ElementalCollectAvatarConfigs
{
    public static List<ElementalCollectAvatar> Lists { get; set; } =
    [
        // 水
        new ElementalCollectAvatar("芭芭拉", ElementalType.Hydro, true, true),
        new ElementalCollectAvatar("莫娜", ElementalType.Hydro, true, false),
        new ElementalCollectAvatar("珊瑚宫心海", ElementalType.Hydro, true, true),
        new ElementalCollectAvatar("玛拉妮", ElementalType.Hydro, true, false),
        new ElementalCollectAvatar("那维莱特", ElementalType.Hydro, true, true),
        new ElementalCollectAvatar("芙宁娜", ElementalType.Hydro, true, false),
        new ElementalCollectAvatar("妮露", ElementalType.Hydro, false, true),
        new ElementalCollectAvatar("坎蒂斯", ElementalType.Hydro, false, true),
        new ElementalCollectAvatar("行秋", ElementalType.Hydro, false, true),
        new ElementalCollectAvatar("神里绫人", ElementalType.Hydro, false, true),
        // 雷
        new ElementalCollectAvatar("丽莎", ElementalType.Electro, true, true),
        new ElementalCollectAvatar("八重神子", ElementalType.Electro, true, false),
        new ElementalCollectAvatar("瓦雷莎", ElementalType.Electro, true, false),
        new ElementalCollectAvatar("雷电将军", ElementalType.Electro, false, true),
        new ElementalCollectAvatar("久岐忍", ElementalType.Electro, false, true),
        new ElementalCollectAvatar("北斗", ElementalType.Electro, false, true),
        new ElementalCollectAvatar("菲谢尔", ElementalType.Electro, false, true),
        new ElementalCollectAvatar("雷泽", ElementalType.Electro, false, true),
        // 风
        new ElementalCollectAvatar("砂糖", ElementalType.Anemo, true, true),
        new ElementalCollectAvatar("鹿野院平藏", ElementalType.Anemo, true, true),
        new ElementalCollectAvatar("流浪者", ElementalType.Anemo, true, false),
        new ElementalCollectAvatar("闲云", ElementalType.Anemo, true, false),
        new ElementalCollectAvatar("蓝砚", ElementalType.Anemo, true, false),
        new ElementalCollectAvatar("枫原万叶", ElementalType.Anemo, false, true),
        new ElementalCollectAvatar("珐露珊", ElementalType.Anemo, false, true),
        new ElementalCollectAvatar("琳妮特", ElementalType.Anemo, false, true),
        new ElementalCollectAvatar("温迪", ElementalType.Anemo, false, true),
        new ElementalCollectAvatar("琴", ElementalType.Anemo, false, true),
        new ElementalCollectAvatar("早柚", ElementalType.Anemo, false, true),
        // 火
        new ElementalCollectAvatar("烟绯", ElementalType.Pyro, true, true),
        new ElementalCollectAvatar("迪卢克", ElementalType.Pyro, false,true),
        new ElementalCollectAvatar("可莉", ElementalType.Pyro, true, true),
        new ElementalCollectAvatar("班尼特", ElementalType.Pyro, false, true),
        new ElementalCollectAvatar("香菱", ElementalType.Pyro, false, true),
        new ElementalCollectAvatar("托马", ElementalType.Pyro,false, true),
        new ElementalCollectAvatar("胡桃", ElementalType.Pyro, false, true),
        new ElementalCollectAvatar("迪希雅", ElementalType.Pyro, false, true),
        new ElementalCollectAvatar("夏沃蕾", ElementalType.Pyro, false, true),
        new ElementalCollectAvatar("辛焱", ElementalType.Pyro, false, true),
        new ElementalCollectAvatar("林尼", ElementalType.Pyro, false, true),
        new ElementalCollectAvatar("宵宫", ElementalType.Pyro, false, true),
    ];

    public static ElementalCollectAvatar? Get(string name, ElementalType type) => Lists.FirstOrDefault(x => x.Name == name && x.ElementalType == type);

    public static List<string> GetAvatarNameList(ElementalType type) => Lists.Where(x => x.ElementalType == type).Select(x => x.Name).ToList();
}
