using BetterGenshinImpact.GameTask.AutoFight.Model;
using Fischless.GameCapture;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class AvatarSelectionProtocolTests
{
    [Fact]
    public void RepeatedCopiesOfOneFrameCannotConfirmSelectionOrSendAnotherInput()
    {
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock).Next();
        var frames = new List<ObservedFrame>();
        using (var selection = AvatarSelectionProtocol.Select(1, 4,
            () => { var frame = new ObservedFrame(source); frames.Add(frame); return frame; },
            frame => frame.Source, _ => true, _ => 1, frame => new ObservedFrame(frame.Source),
            _ => throw new InvalidOperationException("不能重新选择已在场的角色"),
            ms => clock.Advance(TimeSpan.FromMilliseconds(ms)), default, clock: clock))
        {
            Assert.False(selection.Confirmed);
            Assert.False(selection.NeedsRecovery);
        }
        Assert.All(frames, frame => Assert.True(frame.Disposed));
    }

    private sealed class ObservedFrame(CaptureFrameStamp source) : IDisposable
    {
        public CaptureFrameStamp Source { get; } = source;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
