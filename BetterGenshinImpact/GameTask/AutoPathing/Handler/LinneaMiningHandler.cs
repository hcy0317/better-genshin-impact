using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.Common.Job;
using Microsoft.Extensions.Logging;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.AutoPathing.Handler;

/// <summary>
/// 莉奈娅Yolo挖矿
/// 格式: "射箭次数,大循环次数" 或 "射箭次数"
/// 例: "3" 或 "3,10" 或 "mines=3,rounds=10"
/// </summary>
public class LinneaMiningHandler : IActionHandler
{
    private readonly INativeCombatIo? _nativeIo;
    public LinneaMiningHandler() { }
    internal LinneaMiningHandler(INativeCombatIo nativeIo) => _nativeIo = nativeIo;
    public async Task RunAsync(CancellationToken ct, WaypointForTrack? waypointForTrack = null, object? config = null)
    {
        var (mineCount, scanRounds) = ParseParams(waypointForTrack?.ActionParams);

        var io = await NativeActionHandler.ResolveAsync(_nativeIo, ct);
        if (!System.Linq.Enumerable.Any(io.Actors, actor => actor.Name == "莉奈娅"))
        {
            throw new InvalidOperationException("队伍中没有莉奈娅，专用挖矿未执行");
        }
        await NativeActionHandler.WithSelectedActorAsync(io, "莉奈娅", async token =>
        {
            await io.DelayAsync(500, token);
            await new LinneaMiningTask(scanRounds, mineCount).Start(token);
        }, ct);
    }

    private static (int mineCount, int scanRounds) ParseParams(string? actionParams)
    {
        if (string.IsNullOrEmpty(actionParams)) return (LinneaMiningTask.DefaultMineCount, LinneaMiningTask.DefaultScanRounds);

        var parts = actionParams.Split(',');
        var mineCount = -1;
        var scanRounds = -1;

        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith("mines=", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(trimmed.AsSpan("mines=".Length), out var m))
            {
                mineCount = Clamp(m, 1, 999);
            }
            else if (trimmed.StartsWith("rounds=", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(trimmed.AsSpan("rounds=".Length), out var r))
            {
                scanRounds = Clamp(r, 1, 999);
            }
            else if (int.TryParse(trimmed, out var num))
            {
                if (mineCount == -1)
                    mineCount = Clamp(num, 1, 999);
                else if (scanRounds == -1)
                    scanRounds = Clamp(num, 1, 999);
            }
        }

        if (mineCount == -1) mineCount = LinneaMiningTask.DefaultMineCount;
        if (scanRounds == -1) scanRounds = LinneaMiningTask.DefaultScanRounds;
        if (scanRounds < mineCount) scanRounds = mineCount;

        return (mineCount, scanRounds);
    }

    private static int Clamp(int value, int min, int max) => value <= 0 ? min : value > max ? max : value;
}
