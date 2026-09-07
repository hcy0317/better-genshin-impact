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
        Check();
        while (milliseconds > 0)
        {
            var slice = Math.Min(50, milliseconds);
            await delay(slice, _ct).ConfigureAwait(false);
            milliseconds -= slice;
            Check();
        }
    }

    public void Sleep(int milliseconds) => WaitAsync(milliseconds).GetAwaiter().GetResult();
    public void Dispose() { if (ReferenceEquals(Active.Value, this)) Active.Value = _previous; }
}
