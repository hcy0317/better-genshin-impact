using BetterGenshinImpact.GameTask.Common;
using Fischless.GameCapture;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class HealingObservationTests
{
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
