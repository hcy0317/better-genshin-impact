using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace BetterGenshinImpact.GameTask.AutoFight;

internal readonly record struct CombatMetricSnapshot(
    int Count,
    double P50Milliseconds,
    double P95Milliseconds);

internal sealed class CombatMetricSeries
{
    private const int DefaultCapacity = 4096;
    private readonly object _sync = new();
    private readonly int _capacity;
    private readonly Queue<double> _milliseconds;

    internal CombatMetricSeries(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }
        _capacity = capacity;
        _milliseconds = new Queue<double>(capacity);
    }

    internal void Record(TimeSpan elapsed)
    {
        lock (_sync)
        {
            _milliseconds.Enqueue(Math.Max(0, elapsed.TotalMilliseconds));
            while (_milliseconds.Count > _capacity)
            {
                _milliseconds.Dequeue();
            }
        }
    }

    internal CombatMetricSnapshot Snapshot()
    {
        lock (_sync)
        {
            if (_milliseconds.Count == 0)
            {
                return new CombatMetricSnapshot(0, 0, 0);
            }

            var ordered = _milliseconds.Order().ToArray();
            return new CombatMetricSnapshot(
                ordered.Length,
                Percentile(ordered, 0.50),
                Percentile(ordered, 0.95));
        }
    }

    internal void Reset()
    {
        lock (_sync)
        {
            _milliseconds.Clear();
        }
    }

    private static double Percentile(IReadOnlyList<double> ordered, double percentile)
    {
        var index = Math.Clamp(
            (int)Math.Ceiling(percentile * ordered.Count) - 1,
            0,
            ordered.Count - 1);
        return ordered[index];
    }
}

internal sealed class CombatRuntimeMetrics
{
    internal static CombatRuntimeMetrics Shared { get; } = new();

    private readonly ConcurrentDictionary<string, CombatMetricSeries> _series =
        new(StringComparer.Ordinal);

    internal void Record(string name, TimeSpan elapsed)
    {
        _series.GetOrAdd(name, static _ => new CombatMetricSeries()).Record(elapsed);
    }

    internal CombatMetricSnapshot Snapshot(string name)
    {
        return _series.TryGetValue(name, out var series)
            ? series.Snapshot()
            : new CombatMetricSnapshot(0, 0, 0);
    }

    internal void Reset()
    {
        foreach (var series in _series.Values)
        {
            series.Reset();
        }
    }
}

internal readonly record struct CombatFailureTraceEntry<T>(
    DateTime TimestampUtc,
    T Value);

internal sealed class CombatFailureTraceBuffer<T>
{
    private readonly object _sync = new();
    private readonly int _capacity;
    private readonly Queue<CombatFailureTraceEntry<T>> _entries;

    internal CombatFailureTraceBuffer(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
        _entries = new Queue<CombatFailureTraceEntry<T>>(capacity);
    }

    internal void Add(DateTime timestampUtc, T value)
    {
        lock (_sync)
        {
            _entries.Enqueue(new CombatFailureTraceEntry<T>(timestampUtc, value));
            while (_entries.Count > _capacity)
            {
                _entries.Dequeue();
            }
        }
    }

    internal IReadOnlyList<CombatFailureTraceEntry<T>> Snapshot()
    {
        lock (_sync)
        {
            return _entries.ToArray();
        }
    }

    internal IReadOnlyList<CombatFailureTraceEntry<T>> SnapshotWindow(
        DateTime failureAtUtc,
        TimeSpan before,
        TimeSpan after)
    {
        var from = failureAtUtc - before;
        var to = failureAtUtc + after;
        lock (_sync)
        {
            return _entries
                .Where(entry => entry.TimestampUtc >= from && entry.TimestampUtc <= to)
                .ToArray();
        }
    }

    internal void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
        }
    }
}
