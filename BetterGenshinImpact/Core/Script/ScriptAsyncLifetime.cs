using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ClearScript;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BetterGenshinImpact.Core.Script;

/// <summary>每次脚本独有的取消与Promise退休边界，不取消整次一条龙，也不拥有游戏输入。</summary>
internal sealed class ScriptAsyncLifetime : IDisposable
{
    private static readonly AsyncLocal<ScriptAsyncLifetime?> Active = new();
    private readonly ScriptAsyncLifetime? _previous;
    private readonly CancellationTokenSource _cancellation;
    private readonly ILogger _logger;
    private readonly Guid _id = Guid.NewGuid();
    private object? _controller;
    private Task? _closing;
    private volatile bool _closed;
    private bool _disposed;

    internal static ScriptAsyncLifetime? Current => Active.Value;
    internal CancellationToken Token { get { Check(); return _cancellation.Token; } }

    internal ScriptAsyncLifetime(CancellationToken parent, ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
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
        if (_closing != null) return _closing;
        _closed = true;
        return _closing = CloseCoreAsync();
    }

    private async Task CloseCoreAsync()
    {
        Trace("retiring");
        if (_controller is { } value) ((dynamic)value).close();
        Exception? cancellationFailure = null;
        try { await _cancellation.CancelAsync().ConfigureAwait(false); }
        catch (Exception failure) { cancellationFailure = failure; }
        if (_controller is { } controller)
        {
            var drain = ((dynamic)controller).drain();
            if (drain is Task pending)
            {
                using var warningCancellation = new CancellationTokenSource();
                var warning = Task.Delay(TimeSpan.FromSeconds(10), warningCancellation.Token);
                try
                {
                    if (await Task.WhenAny(pending, warning).ConfigureAwait(false) != pending)
                        Trace("waiting-in-flight", warning: true);
                    await pending.ConfigureAwait(false);
                }
                finally
                {
                    await warningCancellation.CancelAsync().ConfigureAwait(false);
                    try { await warning.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (warningCancellation.IsCancellationRequested) { }
                }
            }
            else if (drain != null) throw new InvalidOperationException("异步脚本宿主未启用Task/Promise转换，无法确认退休");
            ((dynamic)controller).restore();
            (_controller as IDisposable)?.Dispose();
            _controller = null;
        }
        Trace("retired");
        if (cancellationFailure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cancellationFailure).Throw();
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
        if (_disposed) return;
        _disposed = true;
        _closed = true;
        if (ReferenceEquals(Active.Value, this)) Active.Value = _previous;
        (_controller as IDisposable)?.Dispose();
        _cancellation.Dispose();
    }
}
