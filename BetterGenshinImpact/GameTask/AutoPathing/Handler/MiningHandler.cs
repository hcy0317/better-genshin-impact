using System;
using System.Threading;
using System.Linq;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.Common.Job;
using Microsoft.Extensions.Logging;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.AutoPathing.Handler;

/// <summary>
/// 挖矿并拾取
/// </summary>
public class MiningHandler : IActionHandler
{
    private static readonly string[] MiningActions =
    [
        "钟离 e(hold,wait)",
        "莉奈娅 moveby(0,2000),wait(0.5),charge(0.6),wait(0.5),click(middle),wait(0.1)",
        "爱诺 attack(0.8)",
        "诺艾尔 attack(1.25)",
        "玛薇卡 attack(0.20),j,wait(0.5),attack(0.6)",
        "迪希雅 attack(0.6),mousedown,wait(2.1),mouseup,j",
        "娜维娅 attack(1.25)",
        "辛焱 attack(0.28),jump,wait(0.8),attack(0.28),jump,wait(0.8),attack(0.28),jump,wait(0.8)",
        "重云 attack(0.28),jump,wait(0.8),attack(0.28),jump,wait(0.8),attack(0.28),jump,wait(0.8)",
        "荒泷一斗 attack(0.1),charge(1.9),j,wait(0.5),attack(0.2)",
        "基尼奇 attack(0.28),jump,wait(0.8),attack(0.28),jump,wait(0.8),attack(0.28),jump,wait(0.8),attack(0.28),jump,wait(0.8)",
        "菲米尼 attack(0.28),jump,wait(0.8),attack(0.28),jump,wait(0.8),attack(0.28),jump,wait(0.8),attack(0.28),jump,wait(0.8)",
        "卡维 attack(0.28),jump,wait(0.8),attack(0.28),jump,wait(0.8),attack(0.28),jump,wait(0.8),attack(0.28),jump,wait(0.8)",
        "优菈 attack(0.28),jump,wait(0.8),attack(0.28),jump,wait(0.8),attack(0.28),jump,wait(0.8),attack(0.28),jump,wait(0.8)",
        "嘉明 attack(0.28),jump,wait(0.8),attack(0.28),jump,wait(0.8),attack(0.28),jump,wait(0.8),attack(0.28),jump,wait(0.8)",
        "多莉 attack(2.0)",
        "北斗 attack(0.28),jump,wait(0.8),attack(0.28),jump,wait(0.8),attack(0.28),jump,wait(0.8),attack(0.28),jump,wait(0.8)",
        "早柚 attack(0.23),j,wait(0.6),attack(0.23),j,wait(0.6),attack(0.23),j,wait(0.6),attack(0.23),j,wait(0.6)",
        "迪卢克 charge(3.15),j",
        "坎蒂丝 e(hold,wait)",
        "雷泽 e(hold,wait)",
        "凝光 attack(4.0)"
    ];
    

    private ScanPickTask? _scanPickTask;
    private readonly INativeCombatIo? _nativeIo;

    public MiningHandler() { }

    // 与生产相同的入口；替换的只有游戏I/O，不替换选角、预算或执行器。
    internal MiningHandler(INativeCombatIo nativeIo) => _nativeIo = nativeIo;

    internal static string? SelectMiningAction(Func<string, bool> hasAvatar)
    {
        foreach (var action in MiningActions)
            if (hasAvatar(action[..action.IndexOf(' ')])) return action;
        return null;
    }

    public async Task RunAsync(CancellationToken ct, WaypointForTrack? waypointForTrack = null, object? config = null)
    {
        ct.ThrowIfCancellationRequested();
        var io = _nativeIo;
        if (io == null)
        {
            var combatScenes = await RunnerContext.Instance.GetCombatScenes(ct);
            if (combatScenes == null)
                throw new InvalidOperationException("队伍识别未初始化成功，挖矿未执行，不能标记路线完成");
            io = new NativeCombatIo(combatScenes);
        }

        // 挖矿
        await MiningAsync(io, ct);
        ct.ThrowIfCancellationRequested();


        if (waypointForTrack is { ActionParams: not null }
            && waypointForTrack.ActionParams.Contains("disablePickupAround",
                StringComparison.InvariantCultureIgnoreCase))
        {
            await Delay(1000, ct);

            // 拾取
            await (_scanPickTask ??= new ScanPickTask()).Start(ct);
        }
    }

    private static async Task MiningAsync(INativeCombatIo io, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var selected = SelectMiningAction(name => io.Actors.Any(actor => actor.Name == name));
        if (selected == null) throw new InvalidOperationException("当前队伍没有可执行的挖矿动作，不能标记路线完成");
        var miningAction = CombatScriptParser.ParseContext(selected);
        using var runner = NativeCombatFlowRunner.Create(miningAction.CombatCommands, io, loop: false,
            purpose: CombatScriptExecutionPurpose.Pathing);
        var outcome = CombatScriptExecutor.FromFlowResult(await runner.RunRoundAsync(ct));
        ct.ThrowIfCancellationRequested();
        if (outcome.Kind != CombatExecutionKind.Completed)
        {
            var name = selected[..selected.IndexOf(' ')];
            io.Logger.LogWarning("挖矿角色 {Name} 未确认执行，停止当前挖矿动作，不回退其他角色普攻：{Outcome}/{Reason}",
                name, outcome.Kind, outcome.Reason);
            throw new InvalidOperationException($"挖矿角色 {name} 未确认执行，停止当前挖矿动作；本路线未完成：{outcome.Kind}/{outcome.Reason}");
        }
    }
}
