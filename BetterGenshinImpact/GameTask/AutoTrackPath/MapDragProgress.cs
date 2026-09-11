using System;

namespace BetterGenshinImpact.GameTask.AutoTrackPath;

internal enum MapDragProgressDecision { Continue, ReanchorSlowly, Stop }

/// <summary>只使用真实识别距离；一次操作最多重新定位并慢速重试一次。</summary>
internal sealed class MapDragProgress
{
    private double _bestDistance = double.PositiveInfinity;
    private int _stagnant;
    private bool _retriedSlowly;

    internal void Reanchor(double observedDistance)
    {
        Validate(observedDistance);
        _bestDistance = observedDistance;
        _stagnant = 0;
    }

    internal MapDragProgressDecision Observe(double? observedDistance)
    {
        if (observedDistance is { } distance)
        {
            Validate(distance);
            if (double.IsPositiveInfinity(_bestDistance)
                || distance <= _bestDistance - Math.Max(1, _bestDistance * 0.01))
            {
                Reanchor(distance);
                return MapDragProgressDecision.Continue;
            }
        }
        if (++_stagnant < 3) return MapDragProgressDecision.Continue;
        if (_retriedSlowly) return MapDragProgressDecision.Stop;
        _retriedSlowly = true;
        _stagnant = 0;
        return MapDragProgressDecision.ReanchorSlowly;
    }

    private static void Validate(double distance)
    {
        if (!double.IsFinite(distance) || distance < 0)
            throw new ArgumentOutOfRangeException(nameof(distance), "地图识别距离必须为有限非负数");
    }
}
