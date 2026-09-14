using System.Collections.Concurrent;
using System.Windows.Threading;
using BetterGenshinImpact.GameTask.Common;
using Vanara.PInvoke;

namespace BetterGenshinImpact.UnitTest.CoreTests.CaptureTests;

public class WinEventHookOwnerTests
{
    [Fact]
    public async Task RepeatedNativeFailureKeepsDiagnosticsBoundedWithoutGivingUpOwnership()
    {
        using var owner = new OwnerMessageLoop();
        var native = new ControlledNativeHooks { RemainingUnhookFailures = 80 };
        var observations = new ConcurrentQueue<WinEventHookObservation>();
        var hooks = await owner.Dispatcher.InvokeAsync(() =>
        {
            var result = new WinEventHookOwner(owner.Dispatcher, (_, _, _, _, _, _, _) => { },
                observations.Enqueue, native: native);
            result.Register();
            return result;
        }).Task;
        for (var i = 0; i < 40; i++)
            await Assert.ThrowsAsync<AggregateException>(() => hooks.ReleaseAsync());
        Assert.Equal(2, native.LiveHandles.Count);
        await hooks.ReleaseAsync();
        Assert.Empty(native.LiveHandles);
        Assert.InRange(observations.Count, 4, 65);
        Assert.Equal("released", observations.Last().Phase);
        Assert.Equal(20, observations.Last().Suppressed);
    }

    [Fact]
    public async Task PartialRegistrationFailureStillReleasesTheOwnedHookWhenDiagnosticsThrow()
    {
        using var owner = new OwnerMessageLoop();
        var native = new ControlledNativeHooks { FailSecondRegistration = true };
        WinEventHookOwner? hooks = null;
        await owner.Dispatcher.InvokeAsync(() =>
        {
            hooks = new WinEventHookOwner(owner.Dispatcher, (_, _, _, _, _, _, _) => { },
                _ => throw new InvalidOperationException("logging unavailable"), native: native);
            Assert.Throws<System.ComponentModel.Win32Exception>(hooks.Register);
        }).Task;
        await hooks!.ReleaseAsync();
        Assert.Empty(native.LiveHandles);
        Assert.Equal(1, native.SuccessfulReleases);
    }

    [Fact]
    public async Task UnhookFailureRetainsOnlyTheFailedHandleForTheNextOwnerAttempt()
    {
        using var owner = new OwnerMessageLoop();
        var native = new ControlledNativeHooks { RemainingUnhookFailures = 1 };
        var hooks = await owner.Dispatcher.InvokeAsync(() =>
        {
            var result = new WinEventHookOwner(owner.Dispatcher, (_, _, _, _, _, _, _) => { }, native: native);
            result.Register();
            return result;
        }).Task;

        await Assert.ThrowsAsync<AggregateException>(() => hooks.ReleaseAsync());
        Assert.Single(native.LiveHandles);
        await hooks.ReleaseAsync();
        await hooks.ReleaseAsync();
        Assert.Empty(native.LiveHandles);
        Assert.Equal(2, native.SuccessfulReleases);
    }

    [Fact]
    public async Task CallbackDrainAndRepeatedStopCannotUnhookTheNextRegistration()
    {
        using var owner = new OwnerMessageLoop();
        var native = new ControlledNativeHooks();
        var first = await owner.Dispatcher.InvokeAsync(() =>
        {
            var hooks = new WinEventHookOwner(owner.Dispatcher, (_, _, _, _, _, _, _) => { }, native: native);
            hooks.Register();
            return hooks;
        }).Task;
        var drain = new DispatcherDrainController(() => { }, first.ReleaseAsync);
        drain.Start();
        Assert.True(drain.TryEnter());
        var stopped = drain.StopAsync();
        try
        {
            await owner.Dispatcher.InvokeAsync(() => { }).Task;
            Assert.False(stopped.IsCompleted);
            Assert.Equal(2, native.LiveHandles.Count);
            Assert.Throws<InvalidOperationException>(drain.Start);
        }
        finally { drain.Exit(); }
        await stopped.WaitAsync(TimeSpan.FromSeconds(5));
        var next = await owner.Dispatcher.InvokeAsync(() =>
        {
            var hooks = new WinEventHookOwner(owner.Dispatcher, (_, _, _, _, _, _, _) => { }, native: native);
            hooks.Register();
            return hooks;
        }).Task;
        try
        {
            await first.ReleaseAsync();
            await drain.StopAsync();
            Assert.Equal(2, native.LiveHandles.Count);
            // Awaiting shutdown on the owner must keep its message loop available.
            await owner.Dispatcher.InvokeAsync(async () => await next.ReleaseAsync()).Task.Unwrap()
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(native.LiveHandles);
        }
        finally { await next.ReleaseAsync(); }
    }

    [Fact]
    public async Task ForeignThreadShutdownUnhooksOnTheLiveOwnerMessageLoop()
    {
        using var owner = new OwnerMessageLoop();
        var observations = new ConcurrentQueue<WinEventHookObservation>();
        WinEventHookOwner? hooks = null;
        try
        {
            await owner.Dispatcher.InvokeAsync(() =>
            {
                // Observe only this test process; never register against or send input to the game.
                hooks = new WinEventHookOwner(owner.Dispatcher, (_, _, _, _, _, _, _) => { },
                    observations.Enqueue, (uint)Environment.ProcessId);
                hooks.Register();
            }).Task;

            await Task.Run(() => hooks!.ReleaseAsync()).WaitAsync(TimeSpan.FromSeconds(5));

            var released = observations.Where(item => item.Phase == "unregister").ToArray();
            Assert.Equal(2, released.Length);
            Assert.All(released, item =>
            {
                Assert.True(item.Succeeded);
                Assert.Equal(item.OwnerThreadId, item.CurrentThreadId);
                Assert.Equal(0, item.Win32Error);
            });
        }
        finally
        {
            if (hooks != null) await hooks.ReleaseAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class ControlledNativeHooks : IWinEventHookApi
    {
        private int _registrationCalls;
        public bool FailSecondRegistration { get; init; }
        public int RemainingUnhookFailures { get; set; }
        public int SuccessfulReleases { get; private set; }
        public Dictionary<User32.HWINEVENTHOOK, uint> LiveHandles { get; } = [];
        public uint CurrentThreadId => Kernel32.GetCurrentThreadId();

        public (User32.HWINEVENTHOOK Handle, int Error) Register(uint first, uint last,
            User32.WinEventProc callback, uint processId)
        {
            if (++_registrationCalls == 2 && FailSecondRegistration) return (default, 5);
            var handle = new User32.HWINEVENTHOOK((nint)_registrationCalls);
            LiveHandles.Add(handle, CurrentThreadId);
            return (handle, 0);
        }

        public (bool Succeeded, int Error) Unregister(User32.HWINEVENTHOOK handle)
        {
            if (!LiveHandles.TryGetValue(handle, out var owner) || owner != CurrentThreadId) return (false, 6);
            if (RemainingUnhookFailures > 0)
            {
                RemainingUnhookFailures--;
                return (false, 6);
            }
            LiveHandles.Remove(handle);
            SuccessfulReleases++;
            return (true, 0);
        }
    }

    private sealed class OwnerMessageLoop : IDisposable
    {
        private readonly Thread _thread;
        public Dispatcher Dispatcher { get; }

        public OwnerMessageLoop()
        {
            var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
            _thread = new Thread(() =>
            {
                ready.SetResult(Dispatcher.CurrentDispatcher);
                Dispatcher.Run();
            }) { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            Dispatcher = ready.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            Assert.True(_thread.Join(TimeSpan.FromSeconds(5)), "Hook owner message loop did not drain");
        }
    }
}
