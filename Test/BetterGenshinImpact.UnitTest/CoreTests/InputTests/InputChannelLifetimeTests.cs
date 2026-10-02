using BetterGenshinImpact.Core.Input;
using Vanara.PInvoke;

namespace BetterGenshinImpact.UnitTest.CoreTests.InputTests;

public class InputChannelLifetimeTests
{
    [Fact]
    public void ReleaseAttemptsOtherKeysAndRetainsOnlyFailedKeysForRetry()
    {
        var channel = new RecordingChannel { FailedRelease = User32.VK.VK_E };
        channel.Keyboard.KeyDown(User32.VK.VK_E).KeyDown(User32.VK.VK_Q);
        Assert.Throws<AggregateException>(channel.ReleaseAll);
        Assert.Equal(2, channel.Releases.Count);
        Assert.True(channel.IsKeyDown(User32.VK.VK_E));
        Assert.False(channel.IsKeyDown(User32.VK.VK_Q));
        channel.FailedRelease = null;
        channel.Releases.Clear();
        channel.ReleaseAll();
        Assert.Equal(new[] { User32.VK.VK_E }, channel.Releases);
        Assert.False(channel.IsKeyDown(User32.VK.VK_E));
    }

    [Theory]
    [InlineData(User32.VK.VK_E)]
    [InlineData(User32.VK.VK_LBUTTON)]
    public void FailedExplicitUpRetainsThePressedRecord(User32.VK key)
    {
        var channel = new RecordingChannel { FailedRelease = key };
        channel.Keyboard.KeyDown(key);
        Assert.Throws<InvalidOperationException>(() => channel.Keyboard.KeyUp(key));
        Assert.True(channel.IsKeyDown(key));
        channel.FailedRelease = null;
        channel.ReleaseAll();
        Assert.False(channel.IsKeyDown(key));
    }

    private sealed class RecordingChannel : InputChannelBase
    {
        internal User32.VK? FailedRelease;
        internal readonly List<User32.VK> Releases = [];
        protected override void OnKeyDown(User32.VK key) { }
        protected override void OnKeyUp(User32.VK key)
        {
            Releases.Add(key);
            if (key == FailedRelease) throw new InvalidOperationException("fake release failure");
        }
        protected override void OnMouseButton(InputMouseButton button, bool down)
        {
            if (!down) OnKeyUp(ToVirtualKey(button));
        }
        protected override void OnMoveBy(int dx, int dy) { }
        protected override void OnMoveTo(double x, double y) { }
        protected override void OnScroll(int clicks) { }
    }
}
