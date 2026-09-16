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
