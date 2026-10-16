using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ClearScript;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using BetterGenshinImpact.GameTask.Common;

namespace BetterGenshinImpact.Core.Script;

/// <summary>每次脚本独有的取消与Promise退休边界，不取消整次一条龙，也不拥有游戏输入。</summary>
internal sealed class ScriptAsyncLifetime : IDisposable
{
    private static readonly AsyncLocal<ScriptAsyncLifetime?> Active = new();
    private readonly ScriptAsyncLifetime? _previous;
    private readonly CancellationTokenSource _cancellation;
    private readonly ILogger _logger;
    private readonly Guid _id = Guid.NewGuid();
    private readonly RuntimeStallDiagnostics _retirement;
    private readonly object _gate = new();
    private readonly List<ManagedOperation> _operations = [];
    private object? _controller;
    private Task? _closing;
    private volatile bool _closed;
    private bool _disposed;

    internal static ScriptAsyncLifetime? Current => Active.Value;
    internal static Guid? DiagnosticId => Active.Value?._id;
    internal CancellationToken Token { get { Check(); return _cancellation.Token; } }

    internal ScriptAsyncLifetime(CancellationToken parent, ILogger? logger = null, TimeProvider? diagnosticsClock = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _retirement = new(_logger, "script-retirement", _id.ToString("N"), diagnosticsClock);
        _previous = Active.Value;
        _previous?.Check();
        _cancellation = _previous == null ? CancellationTokenSource.CreateLinkedTokenSource(parent)
            : CancellationTokenSource.CreateLinkedTokenSource(parent, _previous.Token);
        Active.Value = this;
    }

    internal void Check()
    {
        if (_closed || _disposed) throw new OperationCanceledException("所属脚本已经停止准入，拒绝迟到的异步操作");
    }

    // 先登记真实任务的完成屏障，再调用启动函数；关闭与注册只在同一把锁内裁决。
    // run只能启动受管C#工作，不能持有或回调V8对象；返回的Task包括其全部清理。
    internal Task RegisterOperation(Func<CancellationToken, Task> run, Action closeAdmission)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(closeAdmission);
        ManagedOperation operation;
        CancellationToken token;
        lock (_gate)
        {
            Check();
            token = _cancellation.Token;
            token.ThrowIfCancellationRequested();
            operation = new(closeAdmission);
            _operations.Add(operation);
        }
        _ = RunOperationAsync(operation, run, token);
        return operation.Completion.Task;
    }

    private static async Task RunOperationAsync(ManagedOperation operation, Func<CancellationToken, Task> run,
        CancellationToken token)
    {
        try
        {
            await run(token).ConfigureAwait(false);
            operation.Completion.TrySetResult();
        }
        catch (Exception error)
        {
            operation.Completion.TrySetException(error);
            _ = operation.Completion.Task.Exception; // 即使JS没有wait，也先观察，退休仍会传播原异常。
        }
    }

    private sealed class ManagedOperation(Action closeAdmission)
    {
        internal Action CloseAdmission { get; } = closeAdmission;
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal void Attach(IScriptEngine engine)
    {
        Check();
        if (_controller != null) throw new InvalidOperationException("脚本异步生命周期已经绑定引擎");
        // 在JS侧等待真正的Promise结算，而不是只等待Task完成就提前Dispose V8。
        _controller = engine.Evaluate("""
            (() => {
                const pending = new Set();
                let closed = false;
                const originalSleep = globalThis.sleep;
                const originalDispatcher = globalThis.dispatcher;
                const sleepDescriptor = Object.getOwnPropertyDescriptor(globalThis, 'sleep');
                const dispatcherDescriptor = Object.getOwnPropertyDescriptor(globalThis, 'dispatcher');
                function track(value) {
                    if (!value || typeof value.then !== 'function') return value;
                    const promise = Promise.resolve(value);
                    pending.add(promise);
                    promise.then(() => pending.delete(promise), () => pending.delete(promise));
                    return promise;
                }
                function check() { if (closed) throw new Error('BGI_SCRIPT_RETIRED'); }
                let scopedSleep;
                if (typeof originalSleep === 'function') {
                    scopedSleep = (...args) => { check(); return track(originalSleep(...args)); };
                    Object.defineProperty(globalThis, 'sleep', { ...sleepDescriptor, value: scopedSleep });
                }
                let scopedDispatcher;
                if (originalDispatcher) {
                    const asyncMethods = new Set(['runtask', 'runcombatscript', 'runautofighttask',
                        'runautodomaintask', 'runautobosstask', 'runautoleylineoutcroptask',
                        'runautostygianonslaughttask', 'runcountinventoryitemtask', 'synccombatskills', 'waitfortask']);
                    scopedDispatcher = new Proxy({}, {
                        get: (_, name) => asyncMethods.has(String(name).toLowerCase())
                            ? (...args) => { check(); return track(originalDispatcher[name](...args)); }
                            : originalDispatcher[name]
                    });
                    Object.defineProperty(globalThis, 'dispatcher', { ...dispatcherDescriptor, value: scopedDispatcher });
                }
                return {
                    pendingCount: () => pending.size,
                    close: () => { closed = true; },
                    drain: function drain() {
                        if (pending.size === 0) return null;
                        return Promise.allSettled(Array.from(pending)).then(drain);
                    },
                    restore: () => {
                        if (scopedSleep) Object.defineProperty(globalThis, 'sleep', sleepDescriptor);
                        if (scopedDispatcher) Object.defineProperty(globalThis, 'dispatcher', dispatcherDescriptor);
                    }
                };
            })()
            """);
    }

    internal Task CloseAsync()
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_closing != null) return _closing;
            _closed = true;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _closing = completion.Task;
        }
        _ = CompleteCloseAsync(completion);
        return completion.Task;
    }

    private async Task CompleteCloseAsync(TaskCompletionSource completion)
    {
        try { await CloseCoreAsync().ConfigureAwait(false); completion.TrySetResult(); }
        catch (Exception error) { completion.TrySetException(error); }
    }

    private async Task CloseCoreAsync()
    {
        Trace("retiring");
        var failures = new List<Exception>();
        ManagedOperation[] operations;
        lock (_gate) operations = _operations.ToArray();
        using (_retirement.Watch("close-admission"))
        {
            foreach (var operation in operations)
                try { operation.CloseAdmission(); }
                catch (Exception error) { failures.Add(error); }
            try { if (_controller is { } value) ((dynamic)value).close(); }
            catch (Exception error) { failures.Add(error); }
        }
        using (_retirement.Watch("cancel-callbacks"))
        {
            try { await _cancellation.CancelAsync().ConfigureAwait(false); }
            catch (Exception failure) { failures.Add(failure); }
        }
        using (_retirement.Watch("native-operation-drain"))
            foreach (var operation in operations)
            {
                try { await operation.Completion.Task.ConfigureAwait(false); }
                catch (OperationCanceledException error) when (_cancellation.IsCancellationRequested &&
                    error.CancellationToken == _cancellation.Token) { }
                catch (Exception error) { failures.Add(error); }
            }
        if (_controller is { } controller)
        {
            object? drain;
            using (_retirement.Watch("promise-drain-call")) drain = ((dynamic)controller).drain();
            if (drain is Task pending)
            {
                using var observedDrain = _retirement.Watch("promise-drain-wait");
                using var warningCancellation = new CancellationTokenSource();
                var warning = Task.Delay(TimeSpan.FromSeconds(10), warningCancellation.Token);
                try
                {
                    if (await Task.WhenAny(pending, warning).ConfigureAwait(false) != pending)
                        Trace("waiting-in-flight", warning: true);
                    try { await pending.ConfigureAwait(false); }
                    catch (Exception error) { failures.Add(error); }
                }
                finally
                {
                    await warningCancellation.CancelAsync().ConfigureAwait(false);
                    try { await warning.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (warningCancellation.IsCancellationRequested) { }
                }
            }
            else if (drain != null) throw new InvalidOperationException("异步脚本宿主未启用Task/Promise转换，无法确认退休");
            using (_retirement.Watch("restore-bindings"))
                try { ((dynamic)controller).restore(); }
                catch (Exception error) { failures.Add(error); }
            using (_retirement.Watch("release-controller"))
                try { (_controller as IDisposable)?.Dispose(); }
                catch (Exception error) { failures.Add(error); }
                finally { _controller = null; }
        }
        Trace("retired");
        if (failures.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException("脚本原生任务与退休失败", failures);
    }

    private void Trace(string state, bool warning = false)
    {
        try
        {
            var pending = _controller == null ? 0 : (int)((dynamic)_controller).pendingCount();
            _logger.Log(warning ? LogLevel.Warning : LogLevel.Debug,
                "SCRIPT_ASYNC_LIFECYCLE script={Script} state={State} pending={Pending}; 在途工作未返回前不释放引擎", _id, state, pending);
        }
        catch { /* 诊断不能改变退休顺序。 */ }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_closing is { IsCompleted: false } || _operations.Any(operation => !operation.Completion.Task.IsCompleted))
                throw new InvalidOperationException("脚本原生任务尚未退出，不得销毁所属生命周期");
            _disposed = true;
            _closed = true;
        }
        if (ReferenceEquals(Active.Value, this)) Active.Value = _previous;
        (_controller as IDisposable)?.Dispose();
        _cancellation.Dispose();
    }
}
