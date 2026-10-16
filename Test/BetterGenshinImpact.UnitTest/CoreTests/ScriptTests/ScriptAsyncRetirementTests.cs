using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.GameTask;
using Microsoft.ClearScript.V8;
using Microsoft.Extensions.Logging.Abstractions;
using ScriptDispatcher = BetterGenshinImpact.Core.Script.Dependence.Dispatcher;

namespace BetterGenshinImpact.UnitTest.CoreTests.ScriptTests;

[CollectionDefinition("ScriptAsyncRetirement", DisableParallelization = true)]
public class ScriptAsyncRetirementCollection;

[Collection("ScriptAsyncRetirement")]
public class ScriptAsyncRetirementTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task BindingRestoreFailureCannotReplaceAnAlreadyObservedNativeFailure()
    {
        using var lifetime = new ScriptAsyncLifetime(default);
        using var engine = new V8ScriptEngine(V8ScriptEngineFlags.EnableTaskPromiseConversion);
        engine.AddHostObject("dispatcher", new RegisteredWorkPort(lifetime,
            Task.FromException(new IOException("native-failure-before-restore")), () => { }));
        var error = await Record.ExceptionAsync(() => ScriptOutcomeHost.RunAsync(engine, () => engine.Evaluate("""
            dispatcher.Begin();
            Object.defineProperty(globalThis, 'dispatcher', { writable: false, configurable: false });
            """), default, lifetime: lifetime));
        var combined = Assert.IsType<AggregateException>(error);
        Assert.Contains(combined.Flatten().InnerExceptions, e => e.Message.Contains("native-failure-before-restore"));
        Assert.True(combined.Flatten().InnerExceptions.Count >= 2);
    }

    [Fact]
    public async Task NativeFailureAndAdmissionFailureAreObservedEvenWhenJavascriptDoesNotWait()
    {
        using var lifetime = new ScriptAsyncLifetime(default);
        var operation = lifetime.RegisterOperation(_ => Task.FromException(new IOException("worker-failure")),
            () => throw new InvalidOperationException("admission-failure"));
        var first = lifetime.CloseAsync();
        Assert.Same(first, lifetime.CloseAsync());
        var error = await Assert.ThrowsAsync<AggregateException>(() => first);
        Assert.Contains(error.Flatten().InnerExceptions, e => e.Message == "worker-failure");
        Assert.Contains(error.Flatten().InnerExceptions, e => e.Message == "admission-failure");
        await Assert.ThrowsAsync<IOException>(() => operation);
    }

    [Fact]
    public async Task NativeWorkerWithoutAReturnedPromiseMustExitBeforeTheScriptHostReturns()
    {
        using var owner = TaskExecutionScope.BeginOwned();
        using var lifetime = new ScriptAsyncLifetime(default);
        using var engine = new V8ScriptEngine(V8ScriptEngineFlags.EnableTaskPromiseConversion);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.AddHostObject("dispatcher", new RegisteredWorkPort(lifetime, released.Task, () => closed.TrySetResult()));
        var running = ScriptOutcomeHost.RunAsync(engine,
            () => engine.Evaluate("dispatcher.Begin(); Promise.resolve();"), default, lifetime: lifetime);
        try
        {
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(running.IsCompleted);
            await Assert.ThrowsAsync<OperationCanceledException>(() => lifetime.RegisterOperation(_ => Task.CompletedTask, () => { }));
            Assert.Throws<InvalidOperationException>(lifetime.Dispose);
        }
        finally
        {
            released.TrySetResult();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(2, engine.Evaluate("1+1"));
    }

    public sealed class RegisteredWorkPort
    {
        private readonly ScriptAsyncLifetime _lifetime;
        private readonly Task _completion;
        private readonly Action _close;
        internal RegisteredWorkPort(ScriptAsyncLifetime lifetime, Task completion, Action close)
        { _lifetime = lifetime; _completion = completion; _close = close; }
        public string Begin()
        {
            _lifetime.RegisterOperation(_ => _completion, _close);
            return "registered";
        }
    }

    [Fact]
    public async Task MainFailureAndRetirementFailureAreBothPreservedBeforeEngineRetirement()
    {
        Assert.True(BetterGenshinImpact.Helpers.ApplicationHostBootstrapGuard.IsProhibited);
        using var lifetime = new ScriptAsyncLifetime(default);
        using var engine = new V8ScriptEngine(V8ScriptEngineFlags.EnableTaskPromiseConversion);
        using var callback = lifetime.Token.Register(() => throw new IOException("native-retirement-failure"));
        var error = await Record.ExceptionAsync(() => ScriptOutcomeHost.RunAsync(engine,
            () => engine.Evaluate("throw new Error('original-main-failure')"), default, lifetime: lifetime));
        var combined = Assert.IsType<AggregateException>(error);
        Assert.Contains(combined.Flatten().InnerExceptions, e => e.Message.Contains("original-main-failure"));
        Assert.Contains(combined.Flatten().InnerExceptions, e => e.Message.Contains("native-retirement-failure"));
        Assert.Equal(2, engine.Evaluate("1+1"));
    }

    [Fact]
    public async Task CancellationCallbacksHaveAVisibleRetirementPhaseBeforeTheyReturn()
    {
        var logger = new RetirementLogger();
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        using var lifetime = new ScriptAsyncLifetime(default, logger, clock);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var callback = lifetime.Token.Register(() =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        });
        var closing = lifetime.CloseAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(TimeSpan.FromMilliseconds(2001));
            Assert.True(logger.SlowCancellation.Task.IsCompleted);
            Assert.False(closing.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await closing.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Contains(logger.Messages, message => message.Contains("to=after-cancel-callbacks", StringComparison.Ordinal));
    }

    private sealed class RetirementLogger : Microsoft.Extensions.Logging.ILogger
    {
        internal System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();
        internal TaskCompletionSource SlowCancellation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId id,
            TState state, Exception? error, Func<TState, Exception?, string> format)
        {
            var message = format(state, error);
            Messages.Enqueue(message);
            if (message.Contains("cancel-callbacks-still-running", StringComparison.Ordinal)) SlowCancellation.TrySetResult();
        }
    }

    [Fact]
    public async Task HostJoinTimeoutDoesNotCancelOrPretendToCompleteTheOriginalWork()
    {
        var cancellation = CancellationContext.Instance;
        await using var run = cancellation.EnterRun();
        using var lifetime = new ScriptAsyncLifetime(cancellation.GetTokenOrNone());
        var dispatcher = new ScriptDispatcher(new object(), null!, NullLogger<ScriptDispatcher>.Instance);
        var native = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.False(await dispatcher.WaitForTask(native.Task, 1));
        Assert.False(native.Task.IsCompleted);
        Assert.False(cancellation.GetTokenOrNone().IsCancellationRequested);
        native.SetResult();
        Assert.True(await dispatcher.WaitForTask(native.Task, 1));
        var failure = new IOException("original-native-failure");
        Assert.Same(failure, await Record.ExceptionAsync(() => dispatcher.WaitForTask(Task.FromException(failure), 100)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InFlightNativeWorkRetainsTheEngineAndRejectsLateEffectsUntilItReallyReturns(bool mainFails)
    {
        var cancellation = CancellationContext.Instance;
        await using var run = cancellation.EnterRun();
        using var owner = TaskExecutionScope.BeginOwned();
        using var lifetime = new ScriptAsyncLifetime(cancellation.GetTokenOrNone());
        using var engine = new V8ScriptEngine(V8ScriptEngineFlags.EnableTaskPromiseConversion | V8ScriptEngineFlags.UseCaseInsensitiveMemberBinding);
        var native = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var effects = 0;
        engine.AddHostObject("dispatcher", new NativeWorkPort(native.Task));
        engine.AddHostObject("effect", TaskExecutionScope.Capture().Bind((Action)(() => effects++)));
        engine.Script.mainFails = mainFails;
        var running = ScriptOutcomeHost.RunAsync(engine, () => engine.Evaluate("""
            globalThis.lateEffectRejected = false;
            globalThis.workFinished = false;
            (async () => {
                dispatcher.RunTask().then(() => {
                    try { effect(); } catch (_) { lateEffectRejected = true; }
                    workFinished = true;
                });
                if (mainFails) throw new Error('original-main-failure');
            })();
            """), cancellation.GetTokenOrNone(), lifetime: lifetime);
        try
        {
            await Task.Yield();
            Assert.False(running.IsCompleted);
            Assert.Equal(2, engine.Evaluate("1+1"));
            native.SetResult();
            if (mainFails)
            {
                var error = await Record.ExceptionAsync(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Contains("original-main-failure", error!.Message);
            }
            else Assert.Equal(ScriptOutcomeKind.Completed, (await running.WaitAsync(TimeSpan.FromSeconds(5))).Kind);
            Assert.Equal(true, engine.Evaluate("workFinished"));
            Assert.Equal(true, engine.Evaluate("lateEffectRejected"));
            Assert.Equal(0, effects);
            Assert.False(cancellation.GetTokenOrNone().IsCancellationRequested);
        }
        finally
        {
            native.TrySetResult();
            try { await running.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        }
    }

    public sealed class NativeWorkPort(Task completion)
    {
        public Task RunTask() => completion;
    }

    [Fact]
    public async Task DispatcherChildrenBelongToTheScriptRatherThanTheWholeRun()
    {
        var cancellation = CancellationContext.Instance;
        await using var run = cancellation.EnterRun();
        using var lifetime = new ScriptAsyncLifetime(cancellation.GetTokenOrNone());
        var dispatcher = new ScriptDispatcher(new object(), null!, NullLogger<ScriptDispatcher>.Instance);
        using var child = dispatcher.GetLinkedCancellationTokenSource();
        await lifetime.CloseAsync();
        Assert.True(child.IsCancellationRequested);
        Assert.False(cancellation.GetTokenOrNone().IsCancellationRequested);
        Assert.Throws<OperationCanceledException>(() => dispatcher.GetLinkedCancellationTokenSource());
    }

    [Fact]
    public async Task HostTaskJoinCanConsumeAJavaScriptPromiseWithoutStartingALoserTimerInJavaScript()
    {
        var cancellation = CancellationContext.Instance;
        await using var run = cancellation.EnterRun();
        using var owner = TaskExecutionScope.BeginOwned();
        using var lifetime = new ScriptAsyncLifetime(cancellation.GetTokenOrNone());
        using var engine = new V8ScriptEngine(V8ScriptEngineFlags.EnableTaskPromiseConversion | V8ScriptEngineFlags.UseCaseInsensitiveMemberBinding);
        engine.AddHostObject("dispatcher", new ScriptDispatcher(new object(), null!, NullLogger<ScriptDispatcher>.Instance));
        var result = await ScriptOutcomeHost.RunAsync(engine, () => engine.Evaluate("""
            (async () => {
                if (!await dispatcher.WaitForTask(Promise.resolve('done'), 10000)) throw new Error('join failed');
                taskResult.report('Completed', 'JOINED');
            })();
            """), cancellation.GetTokenOrNone(), lifetime: lifetime);
        Assert.Equal("JOINED", result.Reason);
        Assert.False(cancellation.GetTokenOrNone().IsCancellationRequested);
    }

    [Fact]
    public async Task CompletingTheMainPromiseRetiresItsUnusedTimerBeforeTheEngineCanBeDisposed()
    {
        var cancellation = CancellationContext.Instance;
        await using var run = cancellation.EnterRun();
        using var owner = TaskExecutionScope.BeginOwned();
        using var engine = new V8ScriptEngine(V8ScriptEngineFlags.EnableTaskPromiseConversion | V8ScriptEngineFlags.UseCaseInsensitiveMemberBinding);
        EngineExtend.AddAllGlobalMethod(engine);
        output.WriteLine((string)engine.Evaluate("JSON.stringify(Object.getOwnPropertyDescriptor(globalThis, 'sleep'))"));
        try
        {
            var result = await ScriptOutcomeHost.RunAsync(engine, () => engine.Evaluate("""
                globalThis.timerState = 'pending';
                globalThis.loserTimer = sleep(10000).then(
                    () => { timerState = 'elapsed'; },
                    () => { timerState = 'cancelled'; });
                (async () => { await Promise.race([Promise.resolve(true), loserTimer]); })();
                """), cancellation.GetTokenOrNone());

            Assert.Equal(ScriptOutcomeKind.Completed, result.Kind);
            Assert.Equal("cancelled", engine.Evaluate("timerState"));
            Assert.False(cancellation.GetTokenOrNone().IsCancellationRequested);
        }
        finally
        {
            // Red阶段也必须在V8仍存活时收束计时器，不能把故障泄漏到测试进程退出。
            await cancellation.CancelAsync();
            if (engine.Evaluate("typeof loserTimer === 'undefined' ? null : loserTimer") is Task timer)
                await timer.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
