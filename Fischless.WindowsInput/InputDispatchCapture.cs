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
    private int _transportCalls, _transportAcknowledged;

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
    public int TransportCalls => Volatile.Read(ref _transportCalls);
    public int TransportAcknowledged => Volatile.Read(ref _transportAcknowledged);
    public bool HasDispatch => NativeCalls > 0 || TransportCalls > 0;
    public bool HasCompleteReceipt => HasDispatch && !Uncertain && Requested == Submitted &&
        TransportCalls == TransportAcknowledged;
    public static bool IsCapturing => Active.Value != null;

    /// <summary>
    /// Records a non-Win32 transport acknowledgement separately from native input counts.
    /// The transport invokes admit immediately before posting and returns only after acknowledgement.
    /// </summary>
    public static void DispatchTransport(Action<Action> dispatchAndWait)
    {
        var capture = Active.Value;
        var entered = false;
        void Admit()
        {
            for (var item = capture; item != null; item = item._previous)
            {
                if (item._disposed) throw new ObjectDisposedException(nameof(InputDispatchCapture));
                if (!item.HasDispatch) item._beforeFirstNative?.Invoke();
            }
            entered = true;
            for (var item = capture; item != null; item = item._previous)
                Interlocked.Increment(ref item._transportCalls);
        }
        try
        {
            dispatchAndWait(Admit);
            if (!entered) throw new InvalidOperationException("Transport did not enter dispatch admission.");
            for (var item = capture; item != null; item = item._previous)
                Interlocked.Increment(ref item._transportAcknowledged);
        }
        catch
        {
            if (entered) capture?.MarkUncertain();
            throw;
        }
    }

    internal static InputDispatchCapture? BeforeNative()
    {
        var capture = Active.Value;
        for (var item = capture; item != null; item = item._previous)
        {
            if (item._disposed) throw new ObjectDisposedException(nameof(InputDispatchCapture));
            if (!item.HasDispatch) item._beforeFirstNative?.Invoke();
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
