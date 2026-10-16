using BetterGenshinImpact.GameTask.Common;
using Fischless.GameCapture;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class HealingObservationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryEntryWaitsForDelayedHealingBeforeRestartingTheOriginalSegment(bool remainsLow)
    {
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        var reads = 0;
        var retries = 0;
        var attempts = 0;
        var error = await Record.ExceptionAsync(() => BetterGenshinImpact.GameTask.AutoPathing.PathExecutor.ExecuteSegmentWithRetriesAsync(
            async () =>
            {
                if (++attempts > 1) return;
                await BetterGenshinImpact.GameTask.AutoPathing.PathExecutor.ConfirmHealingRestartAsync(source.Next(),
                    () => Task.CompletedTask,
                    () => { reads++; return new(source.Next(), true, remainsLow || reads < 4); },
                    ms => { clock.Advance(TimeSpan.FromMilliseconds(ms)); return Task.CompletedTask; }, default, clock);
            }, _ => retries++, () => { }, default));
        if (remainsLow)
        {
            Assert.NotNull(error);
            Assert.Equal(0, retries);
        }
        else
        {
            Assert.Null(error);
            Assert.Equal(1, retries);
            Assert.Equal(2, attempts);
        }
        Assert.InRange(reads, 4, 21);
    }

    [Fact]
    public async Task HealingChecksNewPixelsInsteadOfReusingTheLowHpFrame()
    {
        var clock = new FakeTimeProvider();
        var producer = new CaptureFrameSource(clock);
        var before = producer.Next();
        var healingCompleted = clock.GetTimestamp();
        var reads = 0;
        var confirmed = await HealingObservation.WaitAsync(new(before, healingCompleted), () =>
        {
            reads++;
            return reads == 1 ? new(before, true, true) : new(producer.Next(), true, false);
        }, ms => { clock.Advance(TimeSpan.FromMilliseconds(ms)); return Task.CompletedTask; }, default, clock);
        Assert.True(confirmed);
        Assert.Equal(2, reads);
    }
}
