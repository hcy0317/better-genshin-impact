using System;
using System.Collections.Generic;
using System.Linq;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

/// <summary>与原 JSON 相同的索引/名称/严格自身寻址；单调时钟，区间计数采用二分查找。</summary>
internal sealed class CombatConditionHistory
{
    private readonly Dictionary<int, List<double>> _indices = new();
    private readonly Dictionary<string, List<double>> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(int Index, string Name), List<double>> _selves = new();
    public HashSet<string> KnownNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Record(int index, string name, double now)
    {
        if (!_indices.TryGetValue(index, out var byIndex)) _indices[index] = byIndex = [];
        if (!_names.TryGetValue(name, out var byName)) _names[name] = byName = [];
        var key = (index, name.ToUpperInvariant());
        if (!_selves.TryGetValue(key, out var bySelf)) _selves[key] = bySelf = [];
        byIndex.Add(now); byName.Add(now); bySelf.Add(now);
        KnownNames.Add(name);
    }

    public object? Resolve(string function, IReadOnlyList<object?> args, int index, string name, double now)
    {
        static double? Number(object? value) => value is double number && !double.IsNaN(number) ? number
            : value is int integer ? integer : null;
        if (function == "battle-time")
            return args.Count > 0 && Number(args[0]) is { } seconds
                ? args.Count < 2 || args[1] is not false ? now > seconds : now < seconds : null;
        var targetPosition = function == "last-exec" ? 2 : 0;
        IReadOnlyList<double> events;
        if (args.Count <= targetPosition) events = _selves.GetValueOrDefault((index, name.ToUpperInvariant())) ?? [];
        else if (args[targetPosition] is string targetName)
        {
            if (!KnownNames.Contains(targetName)) return null;
            events = _names.GetValueOrDefault(targetName) ?? [];
        }
        else if (Number(args[targetPosition]) is { } targetIndex && double.IsFinite(targetIndex) &&
                 targetIndex >= int.MinValue && targetIndex <= int.MaxValue)
            events = _indices.GetValueOrDefault((int)targetIndex) ?? [];
        else return null;
        var elapsed = events.Count == 0 ? double.PositiveInfinity : now - events[^1];
        if (function == "since") return elapsed;
        if (function == "last-exec")
            return args.Count > 0 && Number(args[0]) is { } seconds
                ? args.Count < 2 || args[1] is not false ? elapsed > seconds : elapsed < seconds : null;
        if (function != "count") return null;
        var start = args.Count >= 2 ? Number(args[1]) : 0;
        var end = args.Count >= 3 ? Number(args[2]) : now;
        if (start == null || end == null) return null;
        return (double)Math.Max(0, Bound(events, end.Value, inclusive: true) - Bound(events, start.Value, inclusive: false));
    }

    private static int Bound(IReadOnlyList<double> values, double value, bool inclusive)
    {
        var low = 0; var high = values.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (values[middle] < value || inclusive && values[middle] == value) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    public void Clear() { _indices.Clear(); _names.Clear(); _selves.Clear(); KnownNames.Clear(); }
}
