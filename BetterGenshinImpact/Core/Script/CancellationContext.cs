using System;
using BetterGenshinImpact.Model;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.Core.Script;

public class CancellationContext : Singleton<CancellationContext>
{
    private readonly object _sync = new();
    private CancellationEpoch _current = new();
    private bool _cleared;
    private readonly AsyncLocal<RunState?> _ambientRun = new();
    private RunState? _activeRun;
    private CancellationEpoch EffectiveEpoch => _ambientRun.Value?.Epoch ?? _current;

    public CancellationTokenSource Cts { get { lock (_sync) return EffectiveEpoch.Source; } }
    public bool IsManualStop { get { lock (_sync) return EffectiveEpoch.ManualStop; } }
    public bool IsCancellationRequested { get { lock (_sync) return (_ambientRun.Value != null || !_cleared) && EffectiveEpoch.Token.IsCancellationRequested; } }

    public CancellationToken GetTokenOrNone()
    {
        lock (_sync) return _ambientRun.Value == null && _cleared ? CancellationToken.None : EffectiveEpoch.Token;
    }

    public void Set()
    {
        CancellationEpoch old;
        lock (_sync)
        {
            CheckRunAccess();
            if (_activeRun != null) return; // 子任务沿用整次run代际，不清除父取消。
            old = _current;
            _current = new();
            _cleared = false;
        }
        old.Retire();
    }

    public void ManualCancel() => Cancel(manual: true);
    public void Cancel() => Cancel(manual: false);
    public Task CancelAsync()
    {
        lock (_sync) return _ambientRun.Value == null && _cleared
            ? Task.CompletedTask : EffectiveEpoch.CancelAsync(manual: false);
    }
    private void Cancel(bool manual)
    {
        CancellationEpoch epoch;
        lock (_sync)
        {
            if (_ambientRun.Value == null && _cleared) return;
            epoch = EffectiveEpoch;
        }
        epoch.Cancel(manual);
    }

    public void Clear()
    {
        CancellationEpoch epoch;
        lock (_sync)
        {
            // 子任务/迟到的旧run清理都不能释放整次run或下一代的令牌。
            if (_ambientRun.Value != null || _activeRun != null) return;
            if (_cleared) return;
            _cleared = true;
            epoch = _current;
        }
        epoch.Retire();
    }

    internal void CheckRunAccess(bool checkCancellation = false)
    {
        lock (_sync)
        {
            var caller = _ambientRun.Value;
            if (caller?.Closing == true) throw new OperationCanceledException("所属运行已经停止，拒绝迟到任务");
            if (_activeRun != null && !ReferenceEquals(caller, _activeRun))
                throw new InvalidOperationException("另一整次运行仍持有任务所有权");
            if (checkCancellation && caller != null) caller.Epoch.Token.ThrowIfCancellationRequested();
        }
    }

    internal void CheckLifecycleAccess()
    {
        lock (_sync)
            if (_ambientRun.Value is { } caller && !ReferenceEquals(caller, _activeRun))
                throw new OperationCanceledException("旧运行不能关闭后续运行的资源");
    }

    internal RunLease EnterRun(bool reset = true)
    {
        CancellationEpoch old;
        RunState run;
        lock (_sync)
        {
            CheckRunAccess();
            if (_activeRun != null) throw new InvalidOperationException("不能在活动run中启动第二个run");
            if (!reset && _cleared) throw new InvalidOperationException("取消代际已退役，不能附加任务");
            old = _current;
            if (reset) _current = new();
            _cleared = false;
            _activeRun = run = new(_current);
            _ambientRun.Value = run;
        }
        if (reset) old.Retire();
        return new RunLease(this, run);
    }

    internal RunLease? EnterTaskRun(bool reset = true)
    {
        lock (_sync)
        {
            CheckRunAccess();
            return _activeRun != null ? null : EnterRun(reset);
        }
    }

    internal sealed class RunState(CancellationEpoch epoch)
    {
        internal readonly CancellationEpoch Epoch = epoch;
        internal bool Closing;
    }

    internal sealed class RunLease(CancellationContext owner, RunState run) : IAsyncDisposable
    {
        private readonly object _gate = new();
        private Task? _finished;
        internal Task DrainAsync()
        {
            lock (owner._sync) run.Closing = true;
            return run.Epoch.CancelAsync(manual: false);
        }
        public ValueTask DisposeAsync()
        {
            // 非async入口：在调用者的ExecutionContext里撤销ambient，不留一个已关闭的run。
            if (ReferenceEquals(owner._ambientRun.Value, run)) owner._ambientRun.Value = null;
            lock (_gate) return new(_finished ??= FinishAsync());
        }
        private async Task FinishAsync()
        {
            try { await DrainAsync().ConfigureAwait(false); }
            finally
            {
                run.Epoch.Retire();
                lock (owner._sync)
                {
                    if (ReferenceEquals(owner._activeRun, run))
                    {
                        owner._activeRun = null;
                        owner._cleared = true;
                    }
                }
            }
        }
    }

    /// <summary>代际拥有CTS；取消回调还在运行时只请求退役，不抢先Dispose。</summary>
    internal sealed class CancellationEpoch
    {
        private readonly object _gate = new();
        private bool _cancelIssued, _cancelling, _retiring, _disposed;
        private TaskCompletionSource? _cancelled;
        public CancellationTokenSource Source { get; } = new();
        public CancellationToken Token { get; }
        private bool _manualStop;
        public bool ManualStop { get { lock (_gate) return _manualStop; } }
        public CancellationEpoch() => Token = Source.Token;

        public void Cancel(bool manual)
        {
            Task pending;
            lock (_gate)
            {
                _manualStop |= manual;
                if (_cancelIssued) return;
                pending = CancelAsync(manual);
            }
            pending.GetAwaiter().GetResult();
        }

        public Task CancelAsync(bool manual)
        {
            lock (_gate)
            {
                _manualStop |= manual;
                if (_cancelled != null) return _cancelled.Task;
                if (_retiring || _disposed) return Task.CompletedTask;
                _cancelIssued = _cancelling = true;
                _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _ = CancelCoreAsync(_cancelled);
                return _cancelled.Task;
            }
        }

        private async Task CancelCoreAsync(TaskCompletionSource done)
        {
            Exception? failure = null;
            try { await Source.CancelAsync().ConfigureAwait(false); }
            catch (Exception error) { failure = error; }
            finally
            {
                lock (_gate)
                {
                    _cancelling = false;
                    TryDispose();
                }
            }
            if (failure == null) done.TrySetResult(); else done.TrySetException(failure);
        }

        public void Retire()
        {
            lock (_gate)
            {
                _retiring = true;
                TryDispose();
            }
        }

        private void TryDispose()
        {
            if (!_retiring || _cancelling || _disposed) return;
            _disposed = true;
            Source.Dispose();
        }
    }
}
