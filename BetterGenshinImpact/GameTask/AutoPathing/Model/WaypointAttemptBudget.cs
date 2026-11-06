using System;
using System.Threading;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Common;

namespace BetterGenshinImpact.GameTask.AutoPathing.Model;

// New explicit, stricter node owner. Healing and navigation return borrow this deadline, never restart it.
internal sealed class WaypointAttemptBudget
{
    internal Guid Owner { get; } = Guid.NewGuid();
    internal int CurrentIndex { get; }
    internal DateTimeOffset Deadline { get; }
    internal WaypointAttemptBudget(int currentIndex, DateTimeOffset now, TimeSpan limit)
    {
        CurrentIndex = currentIndex;
        if (UiOperation.Current is { } parent && parent.Remaining < limit) limit = parent.Remaining;
        Deadline = now + limit;
    }
    internal void Check(TimeProvider clock, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        TaskExecutionScope.ThrowIfFailed();
        UiOperation.Current?.Check();
        if (clock.GetUtcNow() >= Deadline) throw new TimeoutException("原节点owner期限已耗尽，禁止回血投影或继续动作");
    }
}
