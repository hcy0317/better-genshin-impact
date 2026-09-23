using BetterGenshinImpact.GameTask.Common.Ui;
using Fischless.GameCapture;
using Microsoft.Extensions.Time.Testing;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class DomainExitRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlreadyOpenExitPromptIsConfirmedOnceAndWaitsForWorld(bool returnMain)
    {
        var io = new RecoveryReplay();
        var result = returnMain
            ? await UiRecovery.ToMainAsync(io, default, requireOverworld: true, clock: io.Clock)
            : await UiRecovery.ExitDomainAsync(io, default, clock: io.Clock);
        Assert.True(result.Matches(UiTarget.Overworld));
        Assert.Equal(new[] { UiAction.ConfirmDomainExit }, io.Actions);
        Assert.Equal(2, io.WorldFrames);
    }

    [Fact]
    public async Task PlainReturnCancelsWithoutLeavingDomain()
    {
        var io = new RecoveryReplay { StayInDomain = true };
        Assert.True((await UiRecovery.ToMainAsync(io, default, clock: io.Clock)).InDomain);
        Assert.Equal(new[] { UiAction.Escape }, io.Actions);
    }

    [Fact]
    public async Task UnrecognizedBlackButtonIsNeverConfirmed()
    {
        var io = new RecoveryReplay { Recognized = false };
        await Assert.ThrowsAsync<TimeoutException>(() => UiRecovery.ExitDomainAsync(io, default, clock: io.Clock));
        Assert.Empty(io.Actions);
    }

    private sealed class RecoveryReplay : IUiDriver
    {
        internal FakeTimeProvider Clock { get; } = new();
        private readonly CaptureFrameSource _source;
        internal List<UiAction> Actions { get; } = [];
        internal int WorldFrames;
        internal bool StayInDomain;
        internal bool Recognized = true;
        private int _afterAction;
        internal RecoveryReplay() => _source = new(Clock);
        public UiSnapshot Capture()
        {
            var stamp = _source.Next();
            var prompt = Actions.Count == 0 || ++_afterAction <= 3;
            if (!prompt) WorldFrames++;
            return new UiSnapshot(stamp.Sequence)
            {
                MainHud = true, InDomain = prompt || StayInDomain, BlackConfirm = prompt,
                DomainExit = new(stamp, prompt && Recognized, new Rect(990, 740, 35, 35))
            }.WithSource(stamp, Clock, UiSnapshot.RecoveryMaximumAge);
        }
        public Task DelayAsync(int milliseconds, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Clock.Advance(TimeSpan.FromMilliseconds(milliseconds)); return Task.CompletedTask; }
        public Task<bool> ActAsync(UiAction action, UiSnapshot observed, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Actions.Add(action); return Task.FromResult(true); }
    }
}
