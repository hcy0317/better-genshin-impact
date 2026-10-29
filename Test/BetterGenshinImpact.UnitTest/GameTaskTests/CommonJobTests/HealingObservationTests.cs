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
    public async Task UnknownHealthNeverConfirmsRecoveryWithinOriginalDeadline()
    {
        Assert.True(BetterGenshinImpact.Helpers.ApplicationHostBootstrapGuard.IsProhibited);
        Assert.Null(System.Windows.Application.Current);
        using var task = BetterGenshinImpact.GameTask.TaskExecutionScope.BeginOwned();
        var clock = new FakeTimeProvider();
        var producer = new CaptureFrameSource(clock);
        var before = producer.Next();
        var fence = new CaptureFrameFence(before, clock.GetTimestamp());
        // 第一帧必须严格晚于输入；否则陈旧帧拒绝会掩盖unknown健康准入缺陷。
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var started = clock.GetTimestamp();
        var reads = 0;

        var confirmed = await HealingObservation.WaitAsync(fence, () =>
        {
            reads++;
            var frame = new HealingFrame(producer.Next(), true, null);
            Assert.True(fence.Accepts(frame.Source));
            Assert.True(frame.Source.IsFresh(clock, TimeSpan.FromSeconds(2)));
            return frame;
        }, milliseconds =>
        {
            Assert.Equal(100, milliseconds);
            clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
            return Task.CompletedTask;
        }, default, clock);

        Assert.False(confirmed);
        Assert.Equal(TimeSpan.FromSeconds(2), clock.GetElapsedTime(started));
        Assert.Equal(20, reads);
        Assert.Null(System.Windows.Application.Current);
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
