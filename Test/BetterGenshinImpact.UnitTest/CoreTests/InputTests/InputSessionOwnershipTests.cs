using BetterGenshinImpact.Core.Input;
using Vanara.PInvoke;
using BetterGenshinImpact.GameTask.Runtime.WebPage;
using System.ComponentModel;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoFight;
using Fischless.GameCapture;
using Fischless.WindowsInput;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

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
                var stopped = Assert.Throws<InputSessionOwnership.RevokedException>(
                    () => inputs.Backend.Foreground.Keyboard.KeyDown(User32.VK.VK_Q));
                Assert.Same(owner, stopped.RevokedOwner);
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
        var stopped = Assert.Throws<InputSessionOwnership.RevokedException>(
            () => owner.Invoke(() => inputs.Backend.Foreground.Keyboard.KeyPress(User32.VK.VK_Q)));
        Assert.Same(owner, stopped.RevokedOwner);
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

    [Theory]
    [InlineData("wrong-owner")]
    [InlineData("cleanup-input")]
    [InlineData("released")]
    public void OwnerMisuseIsNotClassifiedAsAnExpectedRevocation(string misuse)
    {
        Assert.True(BetterGenshinImpact.Helpers.ApplicationHostBootstrapGuard.IsProhibited);
        Assert.Null(System.Windows.Application.Current);
        var device = new RecordingBackend();
        var inputs = new InputSessionOwnership(device);
        using var owner = inputs.Acquire();
        owner.CloseAdmission();
        if (misuse == "wrong-owner")
        {
            var otherInputs = new InputSessionOwnership(new RecordingBackend());
            using var otherOwner = otherInputs.Acquire();
            otherOwner.Invoke(() => Assert.Throws<InvalidOperationException>(
                () => inputs.Backend.Foreground.Keyboard.KeyPress(User32.VK.VK_Q)));
        }
        else if (misuse == "cleanup-input")
        {
            owner.ReleaseInput(() => Assert.Throws<InvalidOperationException>(
                () => inputs.Backend.Foreground.Keyboard.KeyPress(User32.VK.VK_Q)));
        }
        else
        {
            owner.Dispose();
            Assert.Throws<InvalidOperationException>(
                () => owner.Invoke(() => inputs.Backend.Foreground.Keyboard.KeyPress(User32.VK.VK_Q)));
        }
        Assert.Equal(0, device.Channel.Presses);
    }

    [Fact]
    public void PoisonRemainsAnOrdinaryFailureInTheRunningOwnersChannelAfterRevocation()
    {
        Assert.True(BetterGenshinImpact.Helpers.ApplicationHostBootstrapGuard.IsProhibited);
        Assert.Null(System.Windows.Application.Current);
        var device = new RecordingBackend();
        var inputs = new InputSessionOwnership(device);
        var owner = inputs.Acquire();
        owner.Invoke(() =>
        {
            device.FailRelease = true;
            Assert.Throws<IOException>(inputs.Backend.ReleaseAll);
            device.FailRelease = false;
            owner.CloseAdmission();
            // The sending owner still matches: Poison must win over Revoked.
            Assert.Throws<InvalidOperationException>(
                () => inputs.Backend.Foreground.Keyboard.KeyPress(User32.VK.VK_Q));
        });
        Assert.Throws<InvalidOperationException>(
            () => owner.Invoke(() => inputs.Backend.Foreground.Keyboard.KeyPress(User32.VK.VK_Q)));
        Assert.Throws<InvalidOperationException>(owner.Dispose);
        Assert.Throws<InvalidOperationException>(() => inputs.Acquire());
        Assert.Equal(0, device.Channel.Presses);
    }

    [Fact]
    public void OwnedCleanupKeepsItsUpReceiptWhenOnlyTheKnownSourceHasAged()
    {
        var clock = new FakeTimeProvider();
        var device = new RecordingBackend();
        device.Channel.RecordNativeUps = true;
        var inputs = new InputSessionOwnership(device);
        var owner = inputs.Acquire();
        try
        {
            owner.Invoke(() => inputs.Backend.Foreground.Keyboard.KeyDown(User32.VK.VK_E));
            Assert.True(device.Channel.IsKeyDown(User32.VK.VK_E));
            owner.CloseAdmission();
            Assert.Throws<InputSessionOwnership.RevokedException>(
                () => owner.Invoke(() => inputs.Backend.Foreground.Keyboard.KeyDown(User32.VK.VK_Q)));
            var cleanupEntered = false;
            owner.ReleaseInput(() => cleanupEntered = true);
            Assert.True(cleanupEntered);
            var source = new CaptureFrameSource(clock).Next();
            var request = new CombatNativeInputRequest(Guid.NewGuid(), source, clock.GetTimestamp() + clock.TimestampFrequency * 3);
            clock.Advance(TimeSpan.FromMilliseconds(200));
            Assert.True(source.IsKnown);
            Assert.Equal(clock.TimestampFrequency, source.TimestampFrequency);
            Assert.True(source.CapturedTimestamp <= clock.GetTimestamp());
            Assert.False(source.IsFresh(clock, TimeSpan.FromMilliseconds(150)));
            Assert.True(request.DeadlineTimestamp > clock.GetTimestamp());
            TaskExecutionScope.ThrowIfFailed();
            Assert.Equal(0, device.Channel.NativeUps);
            Assert.False(device.Channel.IsKeyDown(User32.VK.VK_Q));
            var result = new CombatBattleHostInputResult(CombatBattleHostInputStatus.Unknown);
            // The existing opaque cleanup API is the permission; the kind string is not an admission flag.
            owner.ReleaseInput(() => result = new CombatNativeInput(clock, NullLogger.Instance, () => { })
                .Submit(request, "release-selection-owner", inputs.Backend.ReleaseAll, CancellationToken.None));
            Assert.Equal(CombatBattleHostInputStatus.Sent, result.Status);
            Assert.Null(result.Error);
            Assert.Equal(1, result.NativeRequested);
            Assert.Equal(1, result.NativeSubmitted);
            Assert.NotNull(result.StartedTimestamp);
            Assert.NotNull(result.CompletedTimestamp);
            Assert.Equal(1, device.Channel.NativeUps);
            Assert.False(device.Channel.IsKeyDown(User32.VK.VK_E));
        }
        finally
        {
            // Business assertions precede cleanup: Dispose must not manufacture the expected release.
            try { owner.ReleaseInput(inputs.Backend.ReleaseAll); }
            finally { owner.Dispose(); }
        }
    }

    [Theory]
    [InlineData("unknown-source")]
    [InlineData("wrong-frequency")]
    [InlineData("expired-deadline")]
    [InlineData("cancelled")]
    [InlineData("late-prepare")]
    public void OwnedCleanupKeepsTheOriginalUnconfirmedAdmissionFailures(string kind)
    {
        var clock = new FakeTimeProvider();
        var device = new RecordingBackend();
        device.Channel.RecordNativeUps = true;
        var inputs = new InputSessionOwnership(device);
        var owner = inputs.Acquire();
        using var cancellation = new CancellationTokenSource();
        try
        {
            owner.Invoke(() => inputs.Backend.Foreground.Keyboard.KeyDown(User32.VK.VK_E));
            Assert.True(device.Channel.IsKeyDown(User32.VK.VK_E));
            var source = new CaptureFrameSource(clock).Next();
            if (kind != "late-prepare") clock.Advance(TimeSpan.FromMilliseconds(200));
            if (kind == "unknown-source") source = default;
            if (kind == "wrong-frequency") source = source with { TimestampFrequency = source.TimestampFrequency + 1 };
            var deadline = kind == "expired-deadline" ? clock.GetTimestamp() : clock.GetTimestamp() + clock.TimestampFrequency * 3;
            var request = new CombatNativeInputRequest(Guid.NewGuid(), source, deadline);
            if (kind == "cancelled") cancellation.Cancel();
            var input = new CombatNativeInput(clock, NullLogger.Instance, () =>
            {
                if (kind == "late-prepare") clock.Advance(TimeSpan.FromMilliseconds(200));
            });
            var result = new CombatBattleHostInputResult(CombatBattleHostInputStatus.Unknown);
            void Submit() => owner.ReleaseInput(() => result = input.Submit(request, "release-selection-owner",
                inputs.Backend.ReleaseAll, cancellation.Token));
            if (kind == "cancelled") Assert.ThrowsAny<OperationCanceledException>(Submit);
            else
            {
                Submit();
                Assert.Equal(CombatBattleHostInputStatus.NotSent, result.Status);
                Assert.NotNull(result.Error);
                Assert.Null(result.CompletedTimestamp);
            }
            Assert.Equal(0, device.Channel.NativeUps);
            Assert.True(device.Channel.IsKeyDown(User32.VK.VK_E));
        }
        finally
        {
            try { owner.ReleaseInput(inputs.Backend.ReleaseAll); }
            finally { owner.Dispose(); }
        }
    }

    [Fact]
    public void OrdinarySubmissionCannotBorrowTheCleanupKindToUseAnAgedSource()
    {
        var clock = new FakeTimeProvider();
        var device = new RecordingBackend();
        var inputs = new InputSessionOwnership(device);
        using var owner = inputs.Acquire();
        var source = new CaptureFrameSource(clock).Next();
        var request = new CombatNativeInputRequest(Guid.NewGuid(), source, clock.GetTimestamp() + clock.TimestampFrequency * 3);
        clock.Advance(TimeSpan.FromMilliseconds(200));
        var result = new CombatBattleHostInputResult(CombatBattleHostInputStatus.Unknown);
        owner.Invoke(() => result = new CombatNativeInput(clock, NullLogger.Instance, () => { })
            .Submit(request, "release-selection-owner", () => inputs.Backend.Foreground.Keyboard.KeyDown(User32.VK.VK_Q), default));
        Assert.Equal(CombatBattleHostInputStatus.NotSent, result.Status);
        Assert.Null(result.CompletedTimestamp);
        Assert.False(device.Channel.IsKeyDown(User32.VK.VK_Q));
        Assert.Equal(0, device.Channel.Presses);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(200)]
    public void CleanupSubmissionCannotDisguiseNewDownAsARelease(int sourceAgeMilliseconds)
    {
        var clock = new FakeTimeProvider();
        var device = new RecordingBackend();
        var inputs = new InputSessionOwnership(device);
        using var owner = inputs.Acquire();
        var source = new CaptureFrameSource(clock).Next();
        var request = new CombatNativeInputRequest(Guid.NewGuid(), source, clock.GetTimestamp() + clock.TimestampFrequency * 3);
        clock.Advance(TimeSpan.FromMilliseconds(sourceAgeMilliseconds));
        var result = new CombatBattleHostInputResult(CombatBattleHostInputStatus.Unknown);
        owner.ReleaseInput(() => result = new CombatNativeInput(clock, NullLogger.Instance, () => { })
            .Submit(request, "release-selection-owner", () => inputs.Backend.Foreground.Keyboard.KeyDown(User32.VK.VK_Q), default));
        Assert.NotEqual(CombatBattleHostInputStatus.Sent, result.Status);
        Assert.NotNull(result.Error);
        Assert.Null(result.CompletedTimestamp);
        Assert.Equal(0, device.Channel.Presses);
        Assert.False(device.Channel.IsKeyDown(User32.VK.VK_Q));
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
        internal bool RecordNativeUps;
        internal int NativeUps;
        protected override void OnKeyDown(User32.VK key) => Presses++;
        protected override void OnKeyUp(User32.VK key)
        {
            if (!RecordNativeUps) return;
            var native = new WindowsInputMessageDispatcher(null, events =>
            {
                var input = Assert.Single(events);
                Assert.Equal(User32.KEYEVENTF.KEYEVENTF_KEYUP, input.ki.dwFlags);
                Assert.Equal((ushort)key, (ushort)input.ki.wVk);
                NativeUps++;
                return (uint)events.Length;
            }, () => 0);
            native.DispatchInput([new User32.INPUT(User32.KEYEVENTF.KEYEVENTF_KEYUP, (ushort)key)]);
        }
        protected override void OnMouseButton(InputMouseButton button, bool down) { }
        protected override void OnMoveBy(int x, int y) { }
        protected override void OnMoveTo(double x, double y) { }
        protected override void OnScroll(int clicks) { }
    }
}
