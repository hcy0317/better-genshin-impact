using System;
using System.Linq;
using BetterGenshinImpact.GameTask.AutoFight;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatRuntimeMetricsTests
{
    [Fact]
    public void MetricSeries_ReportsDeterministicCountP50AndP95()
    {
        var series = new CombatMetricSeries();

        series.Record(TimeSpan.FromMilliseconds(10));
        series.Record(TimeSpan.FromMilliseconds(20));
        series.Record(TimeSpan.FromMilliseconds(100));

        var snapshot = series.Snapshot();

        Assert.Equal(3, snapshot.Count);
        Assert.Equal(20, snapshot.P50Milliseconds);
        Assert.Equal(100, snapshot.P95Milliseconds);
    }

    [Fact]
    public void MetricSeries_RetainsOnlyTheConfiguredCapacity()
    {
        var series = new CombatMetricSeries(capacity: 3);

        foreach (var value in new[] { 10, 20, 30, 40, 50 })
        {
            series.Record(TimeSpan.FromMilliseconds(value));
        }

        var snapshot = series.Snapshot();

        Assert.Equal(3, snapshot.Count);
        Assert.Equal(40, snapshot.P50Milliseconds);
        Assert.Equal(50, snapshot.P95Milliseconds);
    }

    [Fact]
    public void FailureTraceBuffer_RetainsOnlyTheConfiguredCapacity()
    {
        var trace = new CombatFailureTraceBuffer<string>(3);
        var origin = DateTime.UtcNow;

        trace.Add(origin, "one");
        trace.Add(origin.AddSeconds(1), "two");
        trace.Add(origin.AddSeconds(2), "three");
        trace.Add(origin.AddSeconds(3), "four");

        Assert.Equal(["two", "three", "four"], trace.Snapshot().Select(x => x.Value));
    }

    [Fact]
    public void FailureTraceBuffer_ReturnsOnlyTheRequestedFailureWindow()
    {
        var trace = new CombatFailureTraceBuffer<string>(8);
        var failureAt = DateTime.UtcNow;
        trace.Add(failureAt.AddSeconds(-4), "too-old");
        trace.Add(failureAt.AddSeconds(-3), "before");
        trace.Add(failureAt, "failure");
        trace.Add(failureAt.AddSeconds(2), "after");
        trace.Add(failureAt.AddSeconds(3), "too-new");

        var window = trace.SnapshotWindow(
            failureAt,
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(2));

        Assert.Equal(["before", "failure", "after"], window.Select(x => x.Value));
    }
}
