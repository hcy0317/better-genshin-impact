using System;
using System.Text.RegularExpressions;
using System.Threading;

namespace BetterGenshinImpact.GameTask.AutoFight.Model;

public readonly record struct BurstObservation(bool? EnergyFull, bool? CoolingDown)
{
    public bool Ready => EnergyFull == true && CoolingDown == false;
    public static BurstObservation FromClassifier(string? label, double confidence)
    {
        if (label == null || !double.IsFinite(confidence) || confidence <= .7) return default;
        var match = Regex.Match(label.Trim(), @"^energy\s+([01])\s+cd\s+([01])$", RegexOptions.IgnoreCase);
        return match.Success ? new(match.Groups[1].Value == "1", match.Groups[2].Value == "1") : default;
    }
}

public enum BurstCastResult { Confirmed, NotReady, Unknown, Unconfirmed }

/// <summary>释放前就绪、释放后同一角色进入冷却才算成功；编号消失或 Unknown 不算成功。</summary>
public static class BurstCastProtocol
{
    public static BurstCastResult TryCast(Func<BurstObservation> observe, Action press,
        Action<int> delay, CancellationToken ct, TimeProvider? timeProvider = null, double timeoutSeconds = 2.4, int maxSamples = 16,
        Func<bool>? tryBeginInput = null)
    {
        var clock = timeProvider ?? TimeProvider.System;
        var started = clock.GetTimestamp();
        if (!double.IsFinite(timeoutSeconds) || timeoutSeconds <= 0 || timeoutSeconds > 10 || maxSamples is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
        var budget = TimeSpan.FromSeconds(timeoutSeconds);
        ct.ThrowIfCancellationRequested();
        var before = observe();
        if (!before.Ready)
            return before.EnergyFull == false || before.CoolingDown == true
                ? BurstCastResult.NotReady : BurstCastResult.Unknown;
        if (clock.GetElapsedTime(started) >= budget) return BurstCastResult.Unknown;
        ct.ThrowIfCancellationRequested();
        if (tryBeginInput?.Invoke() == false) return BurstCastResult.Unknown;
        press();
        for (var i = 0; i < maxSamples; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (clock.GetElapsedTime(started) >= budget) break;
            delay(150);
            ct.ThrowIfCancellationRequested();
            if (clock.GetElapsedTime(started) >= budget) break;
            var after = observe();
            if (clock.GetElapsedTime(started) >= budget) break;
            if (after.CoolingDown == true) return BurstCastResult.Confirmed;
        }
        return BurstCastResult.Unconfirmed;
    }
}
