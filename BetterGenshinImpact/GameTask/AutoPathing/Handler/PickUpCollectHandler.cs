using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using Microsoft.Extensions.Logging;
using static BetterGenshinImpact.GameTask.Common.TaskControl;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using System.Linq;
using System.Collections.Generic;
using BetterGenshinImpact.GameTask.AutoFight.Config;

namespace BetterGenshinImpact.GameTask.AutoPathing.Handler;

    /// <summary>
/// 使用万叶或琴团通过战技吸取拾取物品，优先万叶，如果没有万叶则使用琴团
/// </summary>
public class PickUpCollectHandler : IActionHandler
{
    /// <summary>
    /// 新增命令时，以角色名称开头(必填)，“-”后定义动作(必填，用于预解析，不能单写角色名称)，空格后定义参数(必填)
    /// 1、"action": "pick_up_collect","action_params":为空或不填，在队伍中寻找CharacterNames中第一个找到的角色，找到就会执行PickUpActions第一个找到的相关角色命令。
    /// 2、"action": "pick_up_collect","action_params":"琴"，只填角色名称（或别名），会执行PickUpActions第一个找到的相关角色命令。
    /// 3、"action": "pick_up_collect","action_params":"琴-短E"，填了具体角色和动作，直接找PickUpActions找到该命令执行。
    /// </summary>
    public static readonly string[] PickUpActions =
    [
        "枫原万叶-长E attack(0.08),keydown(E),wait(0.8),keyup(E),attack(0.5)",
        "枫原万叶-短E attack(0.08),keydown(E),wait(0.47),keyup(E),attack(0.5)",
        "琴-短E wait(0.1),keydown(E),wait(0.4),moveby(1000,0),wait(0.2),moveby(1000,0),wait(0.2),moveby(1000,0),wait(0.2),moveby(1000,-3500),wait(1.8),keyup(E),wait(0.3),click(middle)",
        // 战后只扫转一轮，沿用短 E 的 2.8 秒持有预算，避免技能结束后仍执行长串转镜。
        "琴-长E wait(0.1),click(middle),keydown(E),wait(0.4),moveby(1000,0),wait(0.2),moveby(1000,0),wait(0.2),moveby(1000,0),wait(0.2),moveby(1000,3500),wait(1.8),keyup(E),wait(0.3),click(middle),wait(0.3)",
    ];
    
    // 预解析所有角色名
    private static readonly HashSet<string> CharacterNames = new HashSet<string>(
        PickUpActions
            .Select(action => action.Split(' ')[0]) 
            .Select(GetBaseCharacterName)            
            .Distinct()
    );
    
    private readonly BetterGenshinImpact.GameTask.AutoFight.Script.Flow.INativeCombatIo? _nativeIo;
    public PickUpCollectHandler() { }
    internal PickUpCollectHandler(BetterGenshinImpact.GameTask.AutoFight.Script.Flow.INativeCombatIo nativeIo) => _nativeIo = nativeIo;

    public async Task RunAsync(CancellationToken ct, WaypointForTrack? waypointForTrack = null, object? config = null)
    {
        var io = await NativeActionHandler.ResolveAsync(_nativeIo, ct);
        var requested = waypointForTrack?.ActionParams;
        var names = string.IsNullOrWhiteSpace(requested)
            ? new[] { PickUpActions.Select(action => action.Split(' ')[0]).FirstOrDefault(header =>
                io.Actors.Any(actor => actor.Name == GetBaseCharacterName(header)))
                ?? throw new InvalidOperationException("队伍没有可执行聚物动作的角色，路线未完成") }
            : requested.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        names = SelectAlternativeNames(names, io.Actors.Select(actor => actor.Name));
        if (names.Length == 0) throw new InvalidOperationException("聚物动作参数为空，路线未完成");
        foreach (var raw in names)
        {
            ct.ThrowIfCancellationRequested();
            var dash = raw.IndexOf('-');
            var baseName = dash < 0 ? raw : raw[..dash];
            var actor = DefaultAutoFightConfig.AvatarAliasToStandardName(baseName);
            var header = actor + (dash < 0 ? "" : raw[dash..]);
            var selected = PickUpActions.FirstOrDefault(action => dash < 0
                ? GetBaseCharacterName(action.Split(' ')[0]) == actor
                : action.StartsWith(header + " ", StringComparison.Ordinal));
            if (selected == null || !io.Actors.Any(item => item.Name == actor))
                throw new InvalidOperationException("没有可执行的聚物角色/动作：" + raw);
            var commands = CombatScriptParser.ParseContext(actor + selected[selected.IndexOf(' ')..]).CombatCommands;
            await NativeActionHandler.ExecuteAsync(io,
                NativeActionHandler.WithConfirmedSkill(actor, commands, selected.Split(' ')[0].EndsWith("长E", StringComparison.Ordinal)), ct);
        }
    }

    private static string GetBaseCharacterName(string fullActionName)
    {
        var dash = fullActionName.IndexOf('-');
        return dash > 0 ? fullActionName[..dash] : fullActionName;
    }

    internal static string[] SelectAlternativeNames(string[] names, IEnumerable<string> party)
    {
        string Actor(string name) => DefaultAutoFightConfig.AvatarAliasToStandardName(GetBaseCharacterName(name));
        if (!names.Any(name => Actor(name) == "枫原万叶") || !names.Any(name => Actor(name) == "琴")) return names;
        var available = party.ToHashSet(StringComparer.Ordinal);
        var excluded = available.Contains("枫原万叶") ? "琴" : available.Contains("琴") ? "枫原万叶" : null;
        return excluded == null ? names : names.Where(name => Actor(name) != excluded).ToArray();
    }

    internal static async Task RunAfterBattleAsync(Avatar picker, bool doublePickup, CancellationToken ct)
    {
        var io = new BetterGenshinImpact.GameTask.AutoFight.Script.Flow.NativeCombatIo(picker.CombatScenes);
        if (picker.Name == "枫原万叶")
        {
            var body = "keyup(VK_LBUTTON),wait(0.01),keydown(E),wait(0.8),keyup(E),wait(0.05)," +
                string.Join(",", Enumerable.Repeat("keyup(VK_LBUTTON),wait(0.01),keydown(VK_LBUTTON),wait(0.035),keyup(VK_LBUTTON),wait(0.05)", 6)) + ",wait(1.5)";
            var commands = CombatScriptParser.ParseContext(picker.Name + " " + body).CombatCommands;
            await NativeActionHandler.ExecuteAsync(io, NativeActionHandler.WithConfirmedSkill(picker.Name, commands, hold: true), ct);
            return;
        }
        if (picker.Name != "琴") throw new InvalidOperationException("不支持的战后聚物角色：" + picker.Name);
        var found = !doublePickup;
        void ObservePickup()
        {
            if (found) return;
            using var image = CaptureToRectArea();
            using var icon = image.Find(BetterGenshinImpact.GameTask.AutoPick.Assets.AutoPickAssets.Get(image,
                BetterGenshinImpact.GameTask.TaskContext.Instance().Config.AutoPickConfig.PickKey).PickRo);
            found = icon.IsExist();
        }
        var selected = PickUpActions.First(action => action.StartsWith("琴-长E ", StringComparison.Ordinal));
        for (var attempt = 0; attempt < (doublePickup ? 2 : 1); attempt++)
        {
            var commands = CombatScriptParser.ParseContext("琴" + selected[selected.IndexOf(' ')..]).CombatCommands;
            await NativeActionHandler.ExecuteAsync(io,
                NativeActionHandler.WithConfirmedSkill("琴", commands, hold: true, ObservePickup), ct, ObservePickup);
            if (found) break;
        }
    }
}
