using BetterGenshinImpact.Core.Simulator;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using Vanara.PInvoke;
using static BetterGenshinImpact.GameTask.Common.TaskControl;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Simulator.Extensions;

namespace BetterGenshinImpact.GameTask.AutoPathing.Handler;

/// <summary>
/// 触发元素战技
/// </summary>
[Obsolete]
public class ElementalSkillHandler : IActionHandler
{
    private readonly INativeCombatIo? _nativeIo;
    public ElementalSkillHandler() { }
    internal ElementalSkillHandler(INativeCombatIo nativeIo) => _nativeIo = nativeIo;
    public async Task RunAsync(CancellationToken ct, WaypointForTrack? waypointForTrack = null, object? config = null) =>
        await NativeActionHandler.ExecuteAsync(await NativeActionHandler.ResolveAsync(_nativeIo, ct), "e(wait),wait(1)", ct);
}
