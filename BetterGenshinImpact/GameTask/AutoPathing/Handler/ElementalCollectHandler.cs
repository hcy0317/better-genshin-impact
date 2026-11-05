using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoFight.Config;
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
    public static List<ElementalCollectAvatar> Lists { get; } = DefaultAutoFightConfig.CombatAvatarMap.Values
        .Where(x => x.ElementalType is not null && (x.NormalAttack || x.ElementalSkill))
        .Select(x => new ElementalCollectAvatar(x.Name, x.ElementalType!.Value, x.NormalAttack, x.ElementalSkill))
        .ToList();

    public static ElementalCollectAvatar? Get(string name, ElementalType type) => Lists.FirstOrDefault(x => x.Name == name && x.ElementalType == type);

    public static List<string> GetAvatarNameList(ElementalType type) => Lists.Where(x => x.ElementalType == type).Select(x => x.Name).ToList();
}
