namespace Fischless.GameCapture.Graphics;

public sealed class FrameCallbackLifetime
{
    private readonly object _sync = new();
    private int _activeCallbacks;
    private bool _stopping;
    private readonly AsyncLocal<int> _depth = new();
    private TaskCompletionSource? _drained;

    public bool IsCurrentCallback => _depth.Value > 0;

    public Task WaitForCallbacksAsync()
    {
        lock (_sync) return _activeCallbacks == 0 ? Task.CompletedTask : _drained!.Task;
    }

    public bool IsStopping
    {
        get
        {
            lock (_sync)
            {
                return _stopping;
            }
        }
    }

    public bool TryEnter()
    {
        lock (_sync)
        {
            if (_stopping)
            {
                return false;
            }

            if (_activeCallbacks == 0) _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _activeCallbacks++;
            _depth.Value++;
            return true;
        }
    }

    public void Exit()
    {
        lock (_sync)
        {
            if (_activeCallbacks <= 0)
            {
                throw new InvalidOperationException("No active frame callback to exit.");
            }

            _activeCallbacks--;
            _depth.Value = Math.Max(0, _depth.Value - 1);
            if (_activeCallbacks == 0)
            {
                _drained?.TrySetResult();
                Monitor.PulseAll(_sync);
            }
        }
    }

    public void BeginStopAndWait()
    {
        BeginStop();
        WaitForCallbacks();
    }

    public void BeginStop()
    {
        lock (_sync)
        {
            _stopping = true;
        }
    }

    public void WaitForCallbacks()
    {
        lock (_sync)
        {
            while (_activeCallbacks > 0)
            {
                Monitor.Wait(_sync);
            }
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            if (_activeCallbacks != 0)
            {
                throw new InvalidOperationException("Cannot reset while frame callbacks are active.");
            }

            _stopping = false;
            _drained = null;
        }
    }
}
