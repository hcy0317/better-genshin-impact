using Fischless.GameCapture.Graphics;
using Xunit;

namespace BetterGenshinImpact.UnitTest.CoreTests.CaptureTests;

public class WgcDispatchLifecycleTests
{
    [Fact]
    public async Task RunningTimeoutKeepsResourcesOwnedUntilTheNativeActionExits()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var exited = new ManualResetEventSlim();
        using var cleaned = new ManualResetEventSlim();
        var disposed = 0;
        var usedDisposedResource = 0;
        using var owner = new WgcCaptureLifetime(() => { }, () =>
        {
            Interlocked.Increment(ref disposed);
            cleaned.Set();
        }, TimeSpan.FromMilliseconds(80));
        var operation = Task.Factory.StartNew(() => Record.Exception(() => owner.Invoke(() =>
        {
            entered.Set();
            try
            {
                release.Wait();
                if (Volatile.Read(ref disposed) != 0) Interlocked.Increment(ref usedDisposedResource);
            }
            finally { exited.Set(); }
        })), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            Assert.IsType<TimeoutException>(await operation.WaitAsync(TimeSpan.FromSeconds(3)));
            owner.Stop();
            Assert.Equal(0, Volatile.Read(ref disposed));
            release.Set();
            Assert.True(exited.Wait(TimeSpan.FromSeconds(3)));
            Assert.True(cleaned.Wait(TimeSpan.FromSeconds(3)));
            Assert.Equal(1, Volatile.Read(ref disposed));
            Assert.Equal(0, Volatile.Read(ref usedDisposedResource));
        }
        finally
        {
            release.Set();
            Assert.True(exited.Wait(TimeSpan.FromSeconds(3)));
        }
    }

    [Fact]
    public async Task RetiringOwnerBlocksReplacementUntilLateNativeWritesAndCleanupExit()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var exited = new ManualResetEventSlim();
        using var cleaned = new ManualResetEventSlim();
        var generationField = 1;
        using var old = new WgcCaptureLifetime(() => { }, () => cleaned.Set(), TimeSpan.FromMilliseconds(80));
        var operation = Task.Factory.StartNew(() => Record.Exception(() => old.Invoke(() =>
        {
            entered.Set();
            try { release.Wait(); Volatile.Write(ref generationField, 10); }
            finally { exited.Set(); }
        })), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            Assert.IsType<TimeoutException>(await operation.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.False(old.Stop());
            Assert.Throws<InvalidOperationException>(() =>
                WgcCaptureLifetime.CreateAfterRetirement(old, () => { }, () => { }));
            Assert.Throws<InvalidOperationException>(() => old.Invoke(() => generationField = 99));
            Assert.Equal(1, Volatile.Read(ref generationField));
            release.Set();
            Assert.True(cleaned.Wait(TimeSpan.FromSeconds(3)));
            Assert.True(SpinWait.SpinUntil(() => old.IsRetired, TimeSpan.FromSeconds(3)));
            using var next = WgcCaptureLifetime.CreateAfterRetirement(old, () => { }, () => { });
            next.Invoke(() => generationField = 2);
            Assert.Equal(2, Volatile.Read(ref generationField));
        }
        finally { release.Set(); Assert.True(exited.Wait(TimeSpan.FromSeconds(3))); }
    }

    [Fact]
    public async Task QueuedActionIsCanceledBeforeTheNativeActionStarts()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var exited = new ManualResetEventSlim();
        using var queuedFinished = new ManualResetEventSlim();
        using var cleaned = new ManualResetEventSlim();
        var queuedExecutions = 0;
        Exception? queuedFailure = null;
        using var owner = new WgcCaptureLifetime(() => { }, () => cleaned.Set());
        var running = Task.Factory.StartNew(() => Record.Exception(() => owner.Invoke(() =>
        {
            entered.Set();
            try { release.Wait(); }
            finally { exited.Set(); }
        })), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var queuedCaller = new Thread(() =>
        {
            queuedFailure = Record.Exception(() => owner.Invoke(() => Interlocked.Increment(ref queuedExecutions)));
            queuedFinished.Set();
        }) { IsBackground = true };
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            queuedCaller.Start();
            // 观察调用线程确实在同步派发中等待，排除“Stop 先发生，根本未排队”的假阳性。
            Assert.True(SpinWait.SpinUntil(() =>
                (queuedCaller.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(1)));
            Assert.False(owner.Stop(waitForCompletion: false));
            Assert.True(queuedFinished.Wait(TimeSpan.FromSeconds(1)));
            Assert.IsType<InvalidOperationException>(queuedFailure);
            Assert.Equal(0, Volatile.Read(ref queuedExecutions));
            release.Set();
            Assert.IsType<InvalidOperationException>(await running.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.True(cleaned.Wait(TimeSpan.FromSeconds(3)));
            Assert.Equal(0, Volatile.Read(ref queuedExecutions));
        }
        finally
        {
            release.Set();
            Assert.True(exited.Wait(TimeSpan.FromSeconds(3)));
            if ((queuedCaller.ThreadState & System.Threading.ThreadState.Unstarted) == 0)
                Assert.True(queuedCaller.Join(TimeSpan.FromSeconds(3)));
        }
    }

    [Fact]
    public void CaptureLeaseKeepsReadbackResourcesAliveWithoutExtendingStopWait()
    {
        using var cleaned = new ManualResetEventSlim();
        var disposed = 0;
        using var owner = new WgcCaptureLifetime(() => { }, () =>
        {
            Interlocked.Increment(ref disposed); cleaned.Set();
        }, TimeSpan.FromMilliseconds(80));
        owner.Invoke(() => { });
        using var lease = owner.TryAcquireCapture();
        Assert.NotNull(lease);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(owner.Stop());
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Equal(0, Volatile.Read(ref disposed));
        Assert.Null(owner.TryAcquireCapture());
        Assert.Throws<InvalidOperationException>(() =>
            WgcCaptureLifetime.CreateAfterRetirement(owner, () => { }, () => { }));
        lease.Dispose();
        lease.Dispose();
        Assert.True(cleaned.Wait(TimeSpan.FromSeconds(3)));
        Assert.Equal(1, Volatile.Read(ref disposed));
    }

    [Fact]
    public void SuccessfulStopRunsNativeAndDeviceCleanupOnceOnTheOwningPump()
    {
        var pump = 0;
        var nativeCleanupThread = 0;
        var deviceCleanupThread = 0;
        var nativeCleanup = 0;
        var deviceCleanup = 0;
        using var owner = new WgcCaptureLifetime(() =>
        {
            nativeCleanupThread = Environment.CurrentManagedThreadId;
            Interlocked.Increment(ref nativeCleanup);
        }, () =>
        {
            deviceCleanupThread = Environment.CurrentManagedThreadId;
            Interlocked.Increment(ref deviceCleanup);
        });
        owner.Invoke(() => pump = Environment.CurrentManagedThreadId);
        Assert.True(owner.Stop());
        Assert.True(owner.Stop());
        Assert.True(owner.IsRetired);
        Assert.Equal(pump, nativeCleanupThread);
        Assert.Equal(pump, deviceCleanupThread);
        Assert.Equal(1, nativeCleanup);
        Assert.Equal(1, deviceCleanup);
    }

    [Fact]
    public void NativeActionExceptionStillReleasesOwnedResources()
    {
        using var cleaned = new ManualResetEventSlim();
        var failure = new InvalidOperationException("native initialization failed");
        var nativeCleanup = 0;
        using var owner = new WgcCaptureLifetime(() => Interlocked.Increment(ref nativeCleanup), () => cleaned.Set());
        Assert.Same(failure, Record.Exception(() => owner.Invoke(() => throw failure)));
        Assert.True(cleaned.Wait(TimeSpan.FromSeconds(3)));
        Assert.True(SpinWait.SpinUntil(() => owner.IsRetired, TimeSpan.FromSeconds(3)));
        Assert.Equal(1, nativeCleanup);
    }

    [Fact]
    public void NativeCleanupExceptionStillReleasesDeviceAndBlocksUnsafeReplacement()
    {
        var failure = new InvalidOperationException("native cleanup failed");
        var deviceCleanup = 0;
        using var owner = new WgcCaptureLifetime(() => throw failure, () => Interlocked.Increment(ref deviceCleanup));
        owner.Invoke(() => { });
        Assert.False(owner.Stop());
        Assert.Same(failure, owner.CleanupError);
        Assert.Equal(1, deviceCleanup);
        Assert.Throws<InvalidOperationException>(() =>
            WgcCaptureLifetime.CreateAfterRetirement(owner, () => { }, () => { }));
    }

    [Fact]
    public async Task NativeFailureAfterTimeoutStillExecutesDeferredCleanup()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var exited = new ManualResetEventSlim();
        using var cleaned = new ManualResetEventSlim();
        using var owner = new WgcCaptureLifetime(() => { }, () => cleaned.Set(), TimeSpan.FromMilliseconds(80));
        var operation = Task.Factory.StartNew(() => Record.Exception(() => owner.Invoke(() =>
        {
            entered.Set();
            try { release.Wait(); throw new InvalidOperationException("late native failure"); }
            finally { exited.Set(); }
        })), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            Assert.IsType<TimeoutException>(await operation.WaitAsync(TimeSpan.FromSeconds(3)));
            release.Set();
            Assert.True(cleaned.Wait(TimeSpan.FromSeconds(3)));
            Assert.True(SpinWait.SpinUntil(() => owner.IsRetired, TimeSpan.FromSeconds(3)));
        }
        finally { release.Set(); Assert.True(exited.Wait(TimeSpan.FromSeconds(3))); }
    }

    [Fact]
    public void PumpCloseCallbackCanRetireWithoutTakingTheCallersCaptureLock()
    {
        var callerGate = new object();
        using var cleaned = new ManualResetEventSlim();
        using var owner = new WgcCaptureLifetime(() => { }, () => cleaned.Set());
        lock (callerGate)
        {
            Assert.Throws<InvalidOperationException>(() => owner.Invoke(() =>
            {
                Assert.False(owner.Stop(waitForCompletion: false));
            }));
        }
        Assert.True(cleaned.Wait(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void DeviceCleanupExceptionFinishesRetirementAndRejectsNewOwnership()
    {
        var failure = new InvalidOperationException("device cleanup failed");
        var releases = 0;
        using var owner = new WgcCaptureLifetime(() => { }, () =>
        {
            Interlocked.Increment(ref releases);
            throw failure;
        });
        owner.Invoke(() => { });
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(owner.Stop());
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Same(failure, owner.CleanupError);
        Assert.Equal(1, releases);
        Assert.False(owner.Stop());
        Assert.Throws<InvalidOperationException>(() =>
            WgcCaptureLifetime.CreateAfterRetirement(owner, () => { }, () => { }));
    }
}
