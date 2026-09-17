using System;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using static BetterGenshinImpact.GameTask.Common.TaskControl;
using BetterGenshinImpact.Core.Config;

namespace BetterGenshinImpact.GameTask.AutoPathing.Handler;

/// <summary>
/// 触发普通攻击
/// </summary>
[Obsolete]
public class NormalAttackHandler : IActionHandler
{
    private readonly INativeCombatIo? _nativeIo;
    public NormalAttackHandler() { }
    internal NormalAttackHandler(INativeCombatIo nativeIo) => _nativeIo = nativeIo;
    // Attack(0)使用用户的普攻键绑定且仅点一次，内部已等待200ms；余下800ms保持原总等待。
    public async Task RunAsync(CancellationToken ct, WaypointForTrack? waypointForTrack = null, object? config = null) =>
        await NativeActionHandler.ExecuteAsync(await NativeActionHandler.ResolveAsync(_nativeIo, ct), "attack(0),wait(0.8)", ct);
}
