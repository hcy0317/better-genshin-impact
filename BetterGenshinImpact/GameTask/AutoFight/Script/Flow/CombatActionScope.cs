using System;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

public sealed class CombatActionInterruptedException() : Exception("动作到达维护或预算边界，交回战斗调度器");

/// <summary>只作用于当前异步执行链；旧原语/宏的等待在此处共享本动作截止时间。</summary>
public sealed class CombatActionScope : IDisposable
{
    private static readonly AsyncLocal<CombatActionScope?> Active = new();
    private readonly CombatActionScope? _previous;
    private readonly CombatFlowAction _action;
    private readonly CancellationToken _ct;
    private bool _disposed;
    public static CombatActionScope? Current => Active.Value;

    /// <summary>异常恢复沿用自己的取消/超时，不继承已经失效的战斗动作预算。</summary>
    internal static IDisposable? Suspend() => Active.Value == null ? null : new Suspension();

    private sealed class Suspension : IDisposable
    {
        private readonly CombatActionScope? _saved = Active.Value;
        private bool _disposed;
        public Suspension() => Active.Value = null;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (Active.Value == null) Active.Value = _saved;
        }
    }

    public CombatActionScope(CombatFlowAction action, CancellationToken ct)
    {
        _action = action;
        _ct = ct;
        _previous = Active.Value;
        Active.Value = this;
    }

    public void Check()
    {
        _ct.ThrowIfCancellationRequested();
        if (!_action.CanContinue) throw new CombatActionInterruptedException();
    }

    public async Task WaitAsync(int milliseconds, Func<int, CancellationToken, Task>? delay = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(milliseconds);
        delay ??= Task.Delay;
        var end = _action.Now + milliseconds / 1000d;
        Check();
        while (_action.Now < end)
        {
            // 调度迟到已经是实际等待的一部分，不能在下一片再次补等。
            var remaining = Math.Min(end - _action.Now, _action.RemainingBudget);
            var slice = Math.Max(1, (int)Math.Ceiling(Math.Min(.05, remaining) * 1000));
            await delay(slice, _ct).ConfigureAwait(false);
            Check();
        }
    }

    public void Sleep(int milliseconds) => WaitAsync(milliseconds).GetAwaiter().GetResult();

    internal async Task WaitWithObservationAsync(int milliseconds, Action observe, Func<int, CancellationToken, Task> delay)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(milliseconds);
        var end = _action.Now + milliseconds / 1000d;
        var observed = false;
        Check();
        while (_action.Now < end)
        {
            var remaining = end - _action.Now;
            if (!observed && remaining <= 0.150000001)
            {
                observed = true;
                // 同一执行链的一次纯观察；原截止不变，其耗时抵扣本来就要等待的时间。
                observe();
                Check();
                continue;
            }
            await delay(Math.Max(1, (int)Math.Ceiling(Math.Min(0.05, remaining) * 1000)), _ct).ConfigureAwait(false);
            Check();
        }
    }
    internal void Trace(string phase, string detail)
    {
        if (!_disposed) _action.Trace(phase, detail);
    }

    internal async Task WaitHeldWithObservationAsync(int milliseconds, Action observe, Func<int, CancellationToken, Task> delay)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(milliseconds);
        var end = _action.Now + milliseconds / 1000d;
        Check();
        observe();
        Check();
        while (_action.Now < end)
        {
            var remaining = Math.Min(end - _action.Now, _action.RemainingBudget);
            if (remaining <= 0) break;
            await delay(Math.Max(1, (int) Math.Ceiling(Math.Min(.05, remaining) * 1000)), _ct).ConfigureAwait(false);
            Check();
            // 期限内最后一片结束即交给配对up；不为松键等待下一帧而延长hold。
            if (_action.Now >= end) break;
            observe();
            Check();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        if (ReferenceEquals(Active.Value, this)) Active.Value = _previous;
    }
}
