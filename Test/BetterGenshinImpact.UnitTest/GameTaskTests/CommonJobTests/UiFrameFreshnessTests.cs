using BetterGenshinImpact.GameTask.Common.Ui;
using Fischless.GameCapture;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class UiFrameFreshnessTests
{
    [Fact]
    public void RecoveryUsesItsOwnAgeLimitAndDoesNotRenewPixelsAtReadTime()
    {
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock).Next();
        var snapshot = new UiSnapshot(999) { MainHud = true };
        var recovery = snapshot.WithSource(source, clock, UiSnapshot.RecoveryMaximumAge);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.True(recovery.Matches(UiTarget.Main));
        Assert.False(snapshot.WithSource(source, clock, UiSnapshot.CombatMaximumAge).Matches(UiTarget.Main));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(recovery.Matches(UiTarget.Main));
    }

    [Fact]
    public void InputFenceRejectsFramesAcquiredBeforeInputCompletedAndOtherSessions()
    {
        var clock = new FakeTimeProvider();
        var producer = new CaptureFrameSource(clock);
        var before = producer.Next();
        clock.Advance(TimeSpan.FromMilliseconds(50));
        var queued = producer.Next();
        clock.Advance(TimeSpan.FromMilliseconds(50));
        var fence = new CaptureFrameFence(before, clock.GetTimestamp());
        Assert.False(fence.Accepts(queued));
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(fence.Accepts(producer.Next()));
        producer.Restart();
        Assert.False(fence.Accepts(producer.Next()));
        Assert.False(fence.Accepts(default));
    }

    [Fact]
    public void ReadingTheSameOldPixelsCannotConfirmACombatReadyUi()
    {
        var clock = new FakeTimeProvider();
        var producer = new CaptureFrameSource(clock);
        var source = producer.Next();
        var features = new UiSnapshot(999) { MainHud = true };
        var combatAge = TimeSpan.FromMilliseconds(150);
        Assert.True(features.WithSource(source, clock, combatAge).Matches(UiTarget.Main));
        clock.Advance(TimeSpan.FromMilliseconds(150));
        Assert.True(features.WithSource(source, clock, combatAge).Matches(UiTarget.Main));
        clock.Advance(TimeSpan.FromMilliseconds(1));

        Assert.False(features.WithSource(source, clock, combatAge).Matches(UiTarget.Main));
        Assert.False(features.WithSource(default, clock, combatAge).Matches(UiTarget.Main));
    }
}
