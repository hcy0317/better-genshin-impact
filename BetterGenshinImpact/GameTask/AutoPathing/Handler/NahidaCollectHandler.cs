using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using Microsoft.Extensions.Logging;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.AutoPathing.Handler;

/// <summary>
/// 使用纳西妲长按E技能进行收集，360°球形无死角扫码，`type = target`的情况才有效
/// </summary>
public class NahidaCollectHandler : IActionHandler
{
    private readonly BetterGenshinImpact.GameTask.AutoFight.Script.Flow.INativeCombatIo? _nativeIo;
    public NahidaCollectHandler() { }
    internal NahidaCollectHandler(BetterGenshinImpact.GameTask.AutoFight.Script.Flow.INativeCombatIo nativeIo) => _nativeIo = nativeIo;

    public async Task RunAsync(CancellationToken ct, WaypointForTrack? waypointForTrack = null, object? config = null)
    {
        var io = await NativeActionHandler.ResolveAsync(_nativeIo, ct);
        const string actor = "纳西妲";
        if (!System.Linq.Enumerable.Any(io.Actors, item => item.Name == actor))
            throw new InvalidOperationException("队伍中没有纳西妲，采集未执行");
        var commands = new System.Collections.Generic.List<BetterGenshinImpact.GameTask.AutoFight.Script.CombatCommand>();
        void Add(string command) => commands.Add(new(actor, command));
        var x = (int)(400 * io.DpiScale);
        var y = (int)(-30 * io.DpiScale);
        Add("moveby(0,10000)");
        Add("wait(0.2)");
        Add("keydown(E)");
        Add("wait(0.2)");
        for (var index = 0; index < 15; index++) { Add($"moveby({x},500)"); Add("wait(0.03)"); }
        for (var remaining = 59; remaining >= 0; remaining--)
        {
            if (remaining == 40) y -= (int)(20 * io.DpiScale);
            Add($"moveby({x},{y})");
            Add("wait(0.03)");
        }
        Add("keyup(E)");
        Add("wait(0.2)");
        Add("wait(0.8)");
        Add("click(middle)");
        Add("wait(1)");
        // 原扫描轨迹作为一个E的物理输入，仍由统一协议在新帧CD上确认；没有手工成功记账。
        await NativeActionHandler.ExecuteAsync(io, NativeActionHandler.WithConfirmedSkill(actor, commands, hold: true), ct);
    }
}
