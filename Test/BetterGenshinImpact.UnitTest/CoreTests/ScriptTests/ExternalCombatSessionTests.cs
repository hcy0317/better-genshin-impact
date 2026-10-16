using BetterGenshinImpact.Core.Input;
using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.Core.Script.Dependence;
using BetterGenshinImpact.GameTask;
using Fischless.GameCapture;
using Microsoft.ClearScript.V8;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Vanara.PInvoke;

namespace BetterGenshinImpact.UnitTest.CoreTests.ScriptTests;

[Collection("ScriptAsyncRetirement")]
public class ExternalCombatSessionTests
{
    [Theory]
    [InlineData("cancel")]
    [InlineData("timeout")]
    [InlineData("retire")]
    public async Task PublicSessionStopInterruptsOwnedLongWorkAndJoinsItBeforeReturning(string stop)
    {
        using var lifetime = new ScriptAsyncLifetime(default);
        var clock = new FakeTimeProvider();
        var inputs = new InputSessionOwnership(new RecordingBackend());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var bailout = new ManualResetEventSlim();
        var exited = false;
        var dispatcher = new Dispatcher(new object(), null!, NullLogger<Dispatcher>.Instance,
            new(clock, inputs.Acquire, (_, owner, bind, ct) =>
            {
                bind(Guid.NewGuid());
                try
                {
                    owner.Invoke(() =>
                    {
                        entered.TrySetResult();
                        WaitHandle.WaitAny([ct.WaitHandle, bailout.WaitHandle]);
                        ct.ThrowIfCancellationRequested();
                    });
                    return Task.CompletedTask;
                }
                finally { exited = true; }
            }));
        var frames = new CaptureFrameSource(clock);
        var session = dispatcher.CreateCombatSession("钟离 attack(3600)", "tower", frames.Next(), 0, 2);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromMilliseconds(50));
        Assert.True(session.SubmitProgress(session.SessionId, "tower", frames.Next(), 0, 2, false));
        var stopping = Task.Run(async () =>
        {
            if (stop == "cancel") session.Cancel();
            else if (stop == "timeout") clock.Advance(TimeSpan.FromSeconds(30));
            else await lifetime.CloseAsync();
        });
        try
        {
            await stopping.WaitAsync(TimeSpan.FromMilliseconds(500));
            var result = await session.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(stop == "timeout" ? "TimedOut" : "Cancelled", result.Status);
            Assert.True(exited);
            using var next = inputs.Acquire();
        }
        finally
        {
            bailout.Set(); // A failing old implementation must not leave a blocked test thread.
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            await session.DisposeAsync();
            await lifetime.CloseAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealV8EarlyReturnOrThrowClosesAndJoinsAnUnwaitedSession(bool mainFails)
    {
        using var task = TaskExecutionScope.BeginOwned();
        using var lifetime = new ScriptAsyncLifetime(default);
        var clock = new FakeTimeProvider();
        var inputs = new InputSessionOwnership(new RecordingBackend());
        var cleaning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new Dispatcher(new object(), null!, NullLogger<Dispatcher>.Instance,
            new(clock, inputs.Acquire, async (_, _, bind, ct) =>
            {
                bind(Guid.NewGuid());
                try { await Task.Delay(Timeout.Infinite, ct); }
                finally { cleaning.TrySetResult(); await release.Task; }
            }));
        using var engine = new V8ScriptEngine(V8ScriptEngineFlags.EnableTaskPromiseConversion);
        engine.AddHostObject("dispatcher", dispatcher);
        engine.AddHostObject("source", new CaptureFrameSource(clock).Next());
        engine.Script.mainFails = mainFails;
        var running = ScriptOutcomeHost.RunAsync(engine, () => engine.Evaluate("""
            dispatcher.CreateCombatSession('钟离 attack(.1)', 'tower', source, 0, 2);
            if (mainFails) throw new Error('main-failure');
            """), default, lifetime: lifetime);
        try
        {
            await cleaning.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(running.IsCompleted);
            Assert.Equal(2, engine.Evaluate("1+1"));
        }
        finally { release.TrySetResult(); }
        var error = await Record.ExceptionAsync(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
        if (mainFails) Assert.Contains("main-failure", error!.Message);
        else Assert.Null(error);
        using var next = inputs.Acquire();
    }

    [Fact]
    public async Task ReleaseFailureOverridesCompletionAndIsObservedByScriptRetirement()
    {
        using var lifetime = new ScriptAsyncLifetime(default);
        var clock = new FakeTimeProvider();
        var frames = new CaptureFrameSource(clock);
        var inputs = new InputSessionOwnership(new RecordingBackend { FailRelease = true });
        var dispatcher = new Dispatcher(new object(), null!, NullLogger<Dispatcher>.Instance,
            new(clock, inputs.Acquire, (_, _, bind, ct) => { bind(Guid.NewGuid()); return Task.Delay(Timeout.Infinite, ct); }));
        var session = dispatcher.CreateCombatSession("钟离 attack(.1)", "tower", frames.Next(), 0, 2);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        session.SubmitProgress(session.SessionId, "tower", frames.Next(), 1, 2, false);
        clock.Advance(TimeSpan.FromMilliseconds(210));
        session.SubmitProgress(session.SessionId, "tower", frames.Next(), 1, 2, false);
        Assert.Equal("Failed", (await session.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5))).Status);
        await Assert.ThrowsAsync<IOException>(() => lifetime.CloseAsync());
        Assert.Throws<InvalidOperationException>(() => inputs.Acquire());
    }

    [Fact]
    public async Task RealPublicInputAndSecondCombatOrPathAreRejectedUntilTheSessionExits()
    {
        using var task = TaskExecutionScope.BeginOwned();
        using var lifetime = new ScriptAsyncLifetime(default);
        var backend = new RecordingBackend();
        using var isolatedInput = InputHub.UseIsolatedBackend(backend);
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        var dispatcher = new Dispatcher(new object(), null!, NullLogger<Dispatcher>.Instance,
            new(clock, InputHub.AcquireSession, (_, _, bind, ct) => { bind(Guid.NewGuid()); return Task.Delay(Timeout.Infinite, ct); }));
        var pathEntered = false;
        var path = new AutoPathingScript(Path.GetTempPath(), null, new LimitedFile(Path.GetTempPath()), (_, _) => { },
            (_, _) => { pathEntered = true; return Task.FromResult(true); }, (_, _) => { });
        using var engine = new V8ScriptEngine(V8ScriptEngineFlags.EnableTaskPromiseConversion | V8ScriptEngineFlags.UseCaseInsensitiveMemberBinding);
        engine.AddHostObject("dispatcher", dispatcher);
        engine.AddHostObject("pathingScript", path);
        engine.AddHostObject("baseline", source.Next());
        engine.AddHostObject("leftButtonDown", TaskExecutionScope.Capture().Bind((Action)GlobalMethod.LeftButtonDown));
        engine.AddHostObject("leftButtonUp", TaskExecutionScope.Capture().Bind((Action)GlobalMethod.LeftButtonUp));
        var cached = InputHub.Backend;
        engine.AddHostObject("cachedRelease", (Action)cached.ReleaseAll);
        await ScriptOutcomeHost.RunAsync(engine, () => engine.Evaluate("""
            (async () => {
                const session = dispatcher.CreateCombatSession('钟离 attack(.1)', 'tower', baseline, 0, 2);
                let rejected = 0;
                try { leftButtonDown(); } catch (_) { rejected++; }
                try { cachedRelease(); } catch (_) { rejected++; }
                try { await dispatcher.RunCombatScript('钟离 attack(.1)'); } catch (_) { rejected++; }
                try { await pathingScript.Run('{}'); } catch (_) { rejected++; }
                if (rejected !== 4) throw new Error('non-owner input admitted: ' + rejected);
                session.Cancel();
                if ((await session.WaitAsync()).Status !== 'Cancelled') throw new Error('cancellation was success');
                leftButtonDown(); leftButtonUp();
            })()
            """), default, lifetime: lifetime);
        Assert.False(pathEntered);
        Assert.Equal(1, backend.Channel.MouseDowns);
    }

    [Theory]
    [InlineData("wrong-session")]
    [InlineData("wrong-target")]
    [InlineData("duplicate")]
    [InlineData("stale")]
    [InlineData("regression")]
    [InlineData("unknown")]
    public async Task InvalidProgressCannotCompleteOrRenewTheOriginalSession(string invalid)
    {
        using var lifetime = new ScriptAsyncLifetime(default);
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        var inputs = new InputSessionOwnership(new RecordingBackend());
        var dispatcher = new Dispatcher(new object(), null!, NullLogger<Dispatcher>.Instance,
            new(clock, inputs.Acquire, (_, _, bind, ct) => { bind(Guid.NewGuid()); return Task.Delay(Timeout.Infinite, ct); }));
        var session = dispatcher.CreateCombatSession("钟离 attack(.1)", "tower", source.Next(), 1, 2);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        var first = source.Next();
        Assert.True(session.SubmitProgress(session.SessionId, "tower", first, 2, 2, false));
        clock.Advance(TimeSpan.FromMilliseconds(210));
        var second = invalid == "duplicate" ? first : source.Next();
        if (invalid == "stale") clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(session.SubmitProgress(invalid == "wrong-session" ? "old" : session.SessionId,
            invalid == "wrong-target" ? "other" : "tower", second,
            invalid == "regression" ? 0 : invalid == "unknown" ? -1 : 2, 2, false));
        Assert.False(session.IsCompleted);
        session.Cancel();
        Assert.Equal("Cancelled", (await session.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5))).Status);
        await lifetime.CloseAsync();
    }

    [Fact]
    public async Task RepeatedProgressDoesNotRenewTheOriginalTotalBudget()
    {
        using var lifetime = new ScriptAsyncLifetime(default);
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        var inputs = new InputSessionOwnership(new RecordingBackend());
        var dispatcher = new Dispatcher(new object(), null!, NullLogger<Dispatcher>.Instance,
            new(clock, inputs.Acquire, (_, _, bind, ct) => { bind(Guid.NewGuid()); return Task.Delay(Timeout.Infinite, ct); }));
        var session = dispatcher.CreateCombatSession("钟离 attack(.1)", "tower", source.Next(), 0, 2, 1000);
        clock.Advance(TimeSpan.FromMilliseconds(900));
        Assert.True(session.SubmitProgress(session.SessionId, "tower", source.Next(), 0, 2, false));
        clock.Advance(TimeSpan.FromMilliseconds(101));
        Assert.Equal("TimedOut", (await session.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5))).Status);
        await lifetime.CloseAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealV8CompletionWaitsForRunnerExitAndInputRelease(bool cancelDuringCleanup)
    {
        using var task = TaskExecutionScope.BeginOwned();
        using var lifetime = new ScriptAsyncLifetime(default);
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        var baseline = source.Next();
        clock.Advance(TimeSpan.FromMilliseconds(100));
        var first = source.Next();
        clock.Advance(TimeSpan.FromMilliseconds(210));
        var second = source.Next();
        var backend = new RecordingBackend();
        var inputs = new InputSessionOwnership(backend);
        var cleaning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dependencies = new CombatSessionDependencies(clock, inputs.Acquire, async (script, _, bindBattle, ct) =>
        {
            Assert.All(script.CombatCommands, command =>
            {
                Assert.Equal("Dispatcher.CreateCombatSession", command.SourceEntryPoint);
                Assert.Equal("memory:external-progress", command.SourceIdentity);
                Assert.False(string.IsNullOrEmpty(command.SourceSessionId));
                Assert.Equal(script.SourceTextSha256, command.SourceTextSha256);
            });
            bindBattle(Guid.NewGuid());
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { cleaning.TrySetResult(); await released.Task; }
        });
        var dispatcher = new Dispatcher(new object(), null!, NullLogger<Dispatcher>.Instance, dependencies);
        using var engine = new V8ScriptEngine(V8ScriptEngineFlags.EnableTaskPromiseConversion | V8ScriptEngineFlags.UseCaseInsensitiveMemberBinding);
        engine.AddHostObject("dispatcher", dispatcher);
        engine.AddHostObject("baseline", baseline);
        engine.AddHostObject("first", first);
        engine.AddHostObject("second", second);
        var running = ScriptOutcomeHost.RunAsync(engine, () => engine.Evaluate("""
            (async () => {
                if (dispatcher.readCombatSessionProtocol() !== 'bgi.external-combat.v1') throw new Error('protocol');
                const session = globalThis.activeSession = dispatcher.createCombatSession('钟离 attack(0.1)', 'tower-1', baseline, 0, 2, 30000);
                if (!session.submitProgress(session.sessionId, session.targetId, first, 1, 2, false)) throw new Error('first');
                if (!session.submitProgress(session.sessionId, session.targetId, second, 1, 2, false)) throw new Error('second');
                globalThis.last = await session.waitAsync();
                await session.disposeAsync();
            })()
            """), default, lifetime: lifetime);
        try
        {
            await cleaning.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(running.IsCompleted);
            Assert.Equal(0, backend.Releases);
            if (cancelDuringCleanup) engine.Execute("activeSession.Cancel()");
        }
        finally { released.TrySetResult(); }
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(cancelDuringCleanup ? "Cancelled" : "CompletedByExternalProgress", engine.Evaluate("last.status"));
        Assert.Equal(1, backend.Releases);
        using var next = inputs.Acquire();
    }

    private sealed class RecordingBackend : IInputBackend
    {
        internal readonly RecordingChannel Channel = new();
        internal int Releases;
        internal bool FailRelease;
        public InputBackendKind Kind => InputBackendKind.Win32;
        public IInputChannel Foreground => Channel;
        public IInputChannel Background => Channel;
        public void ReleaseAll() { Releases++; if (FailRelease) throw new IOException("release-failed"); Channel.ReleaseAll(); }
        public void Dispose() { }
    }
    private sealed class RecordingChannel : InputChannelBase
    {
        internal int MouseDowns;
        protected override void OnKeyDown(User32.VK key) { }
        protected override void OnKeyUp(User32.VK key) { }
        protected override void OnMouseButton(InputMouseButton button, bool down) { if (down) MouseDowns++; }
        protected override void OnMoveBy(int x, int y) { }
        protected override void OnMoveTo(double x, double y) { }
        protected override void OnScroll(int clicks) { }
    }
}
