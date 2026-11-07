using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using Fischless.GameCapture;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class SelectionRefusalTests
{
    private sealed record Frame(CaptureFrameStamp Source) : IDisposable { public void Dispose() { } }

    [Theory]
    [InlineData("unknown")]
    [InlineData("persistent")]
    [InlineData("foreign")]
    [InlineData("stale")]
    [InlineData("deadline")]
    public void RefusalRecoveryCannotBorrowUnknownOrInvalidClearEvidence(string fault)
    {
        var clock = new FakeTimeProvider(); var source = new CaptureFrameSource(clock); var requests = 0;
        using var goal = new AvatarSelectionProtocol.Continuation<Frame>(3, 10, TimeSpan.FromSeconds(1),
            () => new(source.Next()), f => f.Source, _ => true, _ => 1, f => new(f.Source),
            (_, _) => { requests++; return new(CombatBattleHostInputStatus.Sent, clock.GetTimestamp()); }, clock,
            selectionRefusal: _ => SelectionRefusalCue.GameRefusedSelection);
        var deadline = goal.DeadlineTimestamp;
        using (goal.Advance(default)) { }
        clock.Advance(TimeSpan.FromMilliseconds(80)); using (goal.Advance(default)) { }
        clock.Advance(TimeSpan.FromMilliseconds(80)); using (goal.Advance(default)) { }
        Assert.True(goal.RefusalRecoveryPending);
        Assert.True(goal.RecordRefusalRelease(goal.ObservedSource, clock.GetTimestamp()));
        clock.Advance(TimeSpan.FromMilliseconds(70)); var frame = source.Next();
        var cue = fault == "unknown" ? SelectionRefusalCue.Unknown : fault == "persistent" ? SelectionRefusalCue.GameRefusedSelection : SelectionRefusalCue.Absent;
        if (fault == "foreign") frame = new CaptureFrameSource(clock).Next();
        if (fault == "stale") clock.Advance(TimeSpan.FromMilliseconds(160));
        if (fault == "deadline") clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(goal.CompleteRefusalRecovery(frame, cue));
        Assert.Equal(deadline, goal.DeadlineTimestamp); Assert.Equal(1, requests);
        clock.Advance(TimeSpan.FromSeconds(2));
        using var ended = goal.Advance(default);
        Assert.False(ended.Confirmed); Assert.False(ended.AwaitingObservation); Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CueAbsenceAfterCompletedReleaseOnlyResumesOriginalTargetObservation(bool cancel)
    {
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        var cue = SelectionRefusalCue.GameRefusedSelection;
        var active = 1;
        var requests = 0;
        using var goal = new AvatarSelectionProtocol.Continuation<Frame>(3, 10, TimeSpan.FromSeconds(8),
            () => new(source.Next()), f => f.Source, _ => true, _ => active, f => new(f.Source),
            (_, _) => { requests++; return new(CombatBattleHostInputStatus.Sent, clock.GetTimestamp()); }, clock,
            selectionRefusal: _ => cue);
        var id = goal.GoalId; var deadline = goal.DeadlineTimestamp;
        using (goal.Advance(default)) { }
        clock.Advance(TimeSpan.FromMilliseconds(80)); using (goal.Advance(default)) { }
        clock.Advance(TimeSpan.FromMilliseconds(80)); using (goal.Advance(default)) { }
        Assert.True(goal.RefusalRecoveryPending);
        var refusedSource = goal.ObservedSource;
        Assert.True(goal.RecordRefusalRelease(refusedSource, clock.GetTimestamp()));
        cue = SelectionRefusalCue.Absent;
        clock.Advance(TimeSpan.FromMilliseconds(59));
        Assert.False(goal.CompleteRefusalRecovery(source.Next(), cue));
        clock.Advance(TimeSpan.FromMilliseconds(2));
        Assert.True(goal.CompleteRefusalRecovery(source.Next(), cue));
        Assert.Equal(id, goal.GoalId); Assert.Equal(deadline, goal.DeadlineTimestamp);
        Assert.Equal(1, requests); Assert.False(goal.IsClosed);
        using var cancellation = new CancellationTokenSource();
        if (cancel)
        {
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => goal.Advance(cancellation.Token));
            Assert.Equal(1, requests);
            return;
        }
        clock.Advance(TimeSpan.FromMilliseconds(10));
        using (var stillWrong = goal.Advance(default)) Assert.False(stillWrong.Confirmed);
        active = 3;
        clock.Advance(TimeSpan.FromMilliseconds(10));
        using (var first = goal.Advance(default)) Assert.False(first.Confirmed);
        clock.Advance(TimeSpan.FromMilliseconds(10));
        using var selected = goal.Advance(default);
        Assert.True(selected.Confirmed);
        Assert.Equal(1, requests);
    }

    [Fact]
    public void SentSelectionThenFreshGameRefusalStopsRepeatedSelectionRequests()
    {
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        var requests = 0;
        using var goal = new AvatarSelectionProtocol.Continuation<Frame>(3, 10, TimeSpan.FromSeconds(8),
            () => new(source.Next()), f => f.Source, _ => true, _ => 1, f => new(f.Source),
            (_, _) => { requests++; return new(CombatBattleHostInputStatus.Sent, clock.GetTimestamp()); }, clock,
            selectionRefusal: _ => SelectionRefusalCue.GameRefusedSelection);
        var id = goal.GoalId;
        var deadline = goal.DeadlineTimestamp;
        using (goal.Advance(default)) { }
        clock.Advance(TimeSpan.FromMilliseconds(80));
        using (goal.Advance(default)) { }
        clock.Advance(TimeSpan.FromMilliseconds(300));
        using var refused = goal.Advance(default);
        Assert.Equal(1, requests);
        Assert.Equal(AvatarSelectionProtocol.Outcome.RefusalRecovery, refused.State);
        Assert.False(refused.Confirmed);
        Assert.Equal(id, goal.GoalId);
        Assert.Equal(deadline, goal.DeadlineTimestamp);
    }
}
