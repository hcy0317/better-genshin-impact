using System;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using Fischless.GameCapture;

namespace BetterGenshinImpact.GameTask.AutoFight;

/// <summary>Per-host thickness hysteresis; never uses remaining red width as distance or renews movement budgets.</summary>
internal sealed class CombatTargetDistance
{
    private CombatBattleObservation _previous;
    private CombatEngagementDemand _demand;
    private bool _approach;
    private long? _closeSince;

    internal bool? Observe(CombatBattleObservation observation, CombatEngagementDemand demand, TimeProvider clock)
    {
        if (demand == CombatEngagementDemand.Unknown || observation.Quality != CombatObservationQuality.Available ||
            !observation.Source.IsFresh(clock, TimeSpan.FromMilliseconds(150)) || observation.Width <= 0 || observation.Height <= 0 ||
            observation.Target is not { Cue: SeekCueKind.HealthBar, SignalCount: 1, Visual: { } visual })
        { Reset(); return null; }
        if (_previous.Source.IsKnown && observation.BattleId == _previous.BattleId &&
            !observation.Source.IsAfter(_previous.Source)) return null;
        var consistent = _previous.Target?.Visual is { } previous && _demand == demand &&
            observation.BattleId == _previous.BattleId && observation.CaptureEpoch == _previous.CaptureEpoch &&
            observation.Scene == _previous.Scene &&
            observation.Width == _previous.Width && observation.Height == _previous.Height &&
            clock.GetElapsedTime(_previous.Source.CapturedTimestamp, observation.Source.CapturedTimestamp).TotalSeconds <= .5 &&
            Math.Abs(previous.X - visual.X) <= observation.Width * .03 &&
            Math.Abs(previous.CenterY - visual.CenterY) <= observation.Height * .03 &&
            Math.Abs(previous.HealthBarTrackWidth - visual.HealthBarTrackWidth) <= observation.Width * .02;
        var normalizedHeight = visual.Height * 1080d / observation.Height;
        var exit = demand == CombatEngagementDemand.Melee ? 6d : 3d;
        var enter = demand == CombatEngagementDemand.Melee ? 4d : 2d;
        if (!consistent) { _approach = normalizedHeight < exit; _closeSince = null; }
        else if (!_approach && normalizedHeight <= enter) { _approach = true; _closeSince = null; }
        if (_approach)
        {
            if (normalizedHeight < exit) _closeSince = null;
            else
            {
                _closeSince ??= observation.Source.CapturedTimestamp;
                if (clock.GetElapsedTime(_closeSince.Value, observation.Source.CapturedTimestamp).TotalMilliseconds >= 200)
                { _approach = false; _closeSince = null; }
            }
        }
        _previous = observation;
        _demand = demand;
        return _approach;
    }

    private void Reset() { _previous = default; _demand = CombatEngagementDemand.Unknown; _closeSince = null; }
}
