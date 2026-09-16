using System.Threading;

namespace Fischless.WindowsInput;

/// <summary>Captures native submission facts for the current execution chain; it never sends input.</summary>
public sealed class InputDispatchCapture : IDisposable
{
    private static readonly AsyncLocal<InputDispatchCapture?> Active = new();
    private readonly InputDispatchCapture? _previous;
    private readonly Action? _beforeFirstNative;
    private bool _disposed;
    private int _calls, _requested, _submitted, _uncertain;

    public InputDispatchCapture(Action? beforeFirstNative = null)
    {
        _previous = Active.Value;
        _beforeFirstNative = beforeFirstNative;
        Active.Value = this;
    }

    public int NativeCalls => Volatile.Read(ref _calls);
    public int Requested => Volatile.Read(ref _requested);
    public int Submitted => Volatile.Read(ref _submitted);
    public bool Uncertain => Volatile.Read(ref _uncertain) != 0;

    internal static InputDispatchCapture? BeforeNative()
    {
        var capture = Active.Value;
        for (var item = capture; item != null; item = item._previous)
        {
            if (item._disposed) throw new ObjectDisposedException(nameof(InputDispatchCapture));
            if (item.NativeCalls == 0) item._beforeFirstNative?.Invoke();
        }
        return capture;
    }

    internal void Begin(int requested)
    {
        for (var item = this; item != null; item = item._previous)
        {
            Interlocked.Increment(ref item._calls);
            Interlocked.Add(ref item._requested, requested);
        }
    }

    internal void Complete(uint submitted)
    {
        for (var item = this; item != null; item = item._previous)
            Interlocked.Add(ref item._submitted, checked((int)submitted));
    }

    internal void MarkUncertain()
    {
        for (var item = this; item != null; item = item._previous)
            Interlocked.Exchange(ref item._uncertain, 1);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (ReferenceEquals(Active.Value, this)) Active.Value = _previous;
    }
}
