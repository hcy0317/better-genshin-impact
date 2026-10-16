using BetterGenshinImpact.Core.Input;
using Vanara.PInvoke;
using BetterGenshinImpact.GameTask.Runtime.WebPage;
using System.ComponentModel;

namespace BetterGenshinImpact.UnitTest.CoreTests.InputTests;

public class InputSessionOwnershipTests
{
    [Fact]
    public async Task RevocationAllowsTheAlreadyRunningOwnersFinallyToReleaseButNeverSendAgain()
    {
        var device = new RecordingBackend();
        var inputs = new InputSessionOwnership(device);
        var owner = inputs.Acquire();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = Task.Run(() => owner.Invoke(() =>
        {
            inputs.Backend.Foreground.Keyboard.KeyDown(User32.VK.VK_E);
            try
            {
                entered.TrySetResult();
                release.Wait();
                Assert.Throws<InvalidOperationException>(() => inputs.Backend.Foreground.Keyboard.KeyDown(User32.VK.VK_Q));
            }
            finally { inputs.Backend.Foreground.Keyboard.KeyUp(User32.VK.VK_E); }
        }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        owner.CloseAdmission();
        release.Set();
        Exception? failure;
        try { failure = await Record.ExceptionAsync(() => running.WaitAsync(TimeSpan.FromSeconds(5))); }
        finally { owner.Dispose(); }
        Assert.Null(failure);
        Assert.False(device.Channel.IsKeyDown(User32.VK.VK_E));
        Assert.Equal(1, device.Channel.Presses);
    }

    [Fact]
    public async Task RevocationDoesNotWaitForBlockingOwnerWorkAndRetirementMustWaitForItsExit()
    {
        var inputs = new InputSessionOwnership(new RecordingBackend());
        var owner = inputs.Acquire();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = Task.Run(() => owner.Invoke(() => { entered.TrySetResult(); release.Wait(); }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var closing = Task.Run(owner.CloseAdmission);
        try
        {
            await closing.WaitAsync(TimeSpan.FromMilliseconds(500));
            Assert.False(running.IsCompleted);
            Assert.Throws<InvalidOperationException>(owner.Dispose);
            Assert.Throws<InvalidOperationException>(() => inputs.Acquire());
        }
        finally
        {
            release.Set();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
            await closing.WaitAsync(TimeSpan.FromSeconds(5));
            owner.Dispose();
        }
        using var next = inputs.Acquire();
    }

    [Fact]
    public void CancelledWindowClosingMustNotReleaseAnActiveSessionBeforeRuntimeDrain()
    {
        var inputs = new InputSessionOwnership(new RecordingBackend());
        using (var owner = inputs.Acquire())
        {
            // This is the production Closing handler's seam, without constructing a WPF window.
            WebPageGameWindow.ReleaseForHostClosing(new CancelEventArgs(true), inputs.Backend.ReleaseAll);
            Assert.Throws<InvalidOperationException>(inputs.Backend.ReleaseAll);
            owner.CloseAdmission();
        }
        WebPageGameWindow.ReleaseForHostClosing(new CancelEventArgs(false), inputs.Backend.ReleaseAll);
        using var next = inputs.Acquire();
    }

    [Fact]
    public void LegacyTaskOrHeldKeyPreventsAcquisitionAndRevocationStillAllowsOnlyCleanup()
    {
        var device = new RecordingBackend();
        var inputs = new InputSessionOwnership(device);
        using (inputs.EnterLegacyOperation()) Assert.Throws<InvalidOperationException>(() => inputs.Acquire());
        inputs.Backend.Foreground.Keyboard.KeyDown(User32.VK.VK_E);
        Assert.Throws<InvalidOperationException>(() => inputs.Acquire());
        inputs.Backend.Foreground.Keyboard.KeyUp(User32.VK.VK_E);
        using var owner = inputs.Acquire();
        owner.CloseAdmission();
        Assert.Throws<InvalidOperationException>(() => owner.Invoke(() => inputs.Backend.Foreground.Keyboard.KeyPress(User32.VK.VK_Q)));
        Assert.Throws<InvalidOperationException>(() => inputs.EnterLegacyOperation());
        owner.ReleaseInput(() => inputs.Backend.ReleaseAll());
        Assert.Equal(1, device.Channel.Presses);
    }

    [Fact]
    public void ReleaseFailurePoisonsOwnershipAndCannotBeRetriedIntoANewOwner()
    {
        var device = new RecordingBackend { FailRelease = true };
        var inputs = new InputSessionOwnership(device);
        var owner = inputs.Acquire();
        Assert.Throws<IOException>(owner.Dispose);
        device.FailRelease = false;
        Assert.Throws<InvalidOperationException>(() => inputs.Acquire());
        Assert.Throws<InvalidOperationException>(owner.Dispose);
        Assert.Throws<InvalidOperationException>(() => inputs.Backend.Foreground.Keyboard.KeyPress(User32.VK.VK_Q));
        Assert.Equal(0, device.Channel.Presses);
    }

    [Fact]
    public async Task CachedChannelsAndBackendCannotBypassTheOwnerAndAsyncWorkCannotInheritIt()
    {
        var device = new RecordingBackend();
        var inputs = new InputSessionOwnership(device);
        var cachedBackend = inputs.Backend;
        var cachedKeyboard = cachedBackend.Foreground.Keyboard;
        using (var owner = inputs.Acquire())
        {
            Assert.Throws<InvalidOperationException>(() => cachedKeyboard.KeyPress(User32.VK.VK_Q));
            Assert.Throws<InvalidOperationException>(cachedBackend.ReleaseAll);
            Assert.Throws<InvalidOperationException>(() => inputs.Acquire());
            owner.Invoke(() => cachedKeyboard.KeyPress(User32.VK.VK_Q));
            Task? escaped = null;
            owner.Invoke(() => escaped = Task.Run(() => cachedKeyboard.KeyPress(User32.VK.VK_E)));
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await escaped!);
            Assert.Equal(1, device.Channel.Presses);
        }
        cachedKeyboard.KeyPress(User32.VK.VK_E);
        Assert.Equal(2, device.Channel.Presses);
    }

    private sealed class RecordingBackend : IInputBackend
    {
        internal bool FailRelease;
        internal RecordingChannel Channel { get; } = new();
        public InputBackendKind Kind => InputBackendKind.Win32;
        public IInputChannel Foreground => Channel;
        public IInputChannel Background => Channel;
        public void ReleaseAll()
        {
            if (FailRelease) throw new IOException("release failed");
            Channel.ReleaseAll();
        }
        public void Dispose() { }
    }

    private sealed class RecordingChannel : InputChannelBase
    {
        internal int Presses;
        protected override void OnKeyDown(User32.VK key) => Presses++;
        protected override void OnKeyUp(User32.VK key) { }
        protected override void OnMouseButton(InputMouseButton button, bool down) { }
        protected override void OnMoveBy(int x, int y) { }
        protected override void OnMoveTo(double x, double y) { }
        protected override void OnScroll(int clicks) { }
    }
}
