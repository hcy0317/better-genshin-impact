using System;
using System.Threading.Tasks;

namespace BetterGenshinImpact.GameTask.Common;

/// <summary>一个在途拥有者和一个最新待处理项。停止只丢弃待处理项，在途原生计算返回后才释放。</summary>
internal sealed class LatestOwnedWork<T>(Action<T> process, Action<Exception> onError) : IAsyncDisposable where T : class, IDisposable
{
    private readonly object _gate = new();
    private T? _pending;
    private bool _closed, _running;
    private Task _worker = Task.CompletedTask;

    internal void Enqueue(T item)
    {
        T? discard;
        lock (_gate)
        {
            if (_closed) discard = item;
            else
            {
                discard = _pending;
                _pending = item;
                if (!_running) { _running = true; _worker = Task.Run(Run); }
            }
        }
        discard?.Dispose();
    }

    private void Run()
    {
        try
        {
            while (true)
            {
                T? item;
                lock (_gate)
                {
                    item = _pending;
                    _pending = null;
                    if (item == null) { _running = false; return; }
                }
                using (item)
                {
                    try { process(item); }
                    catch (Exception error) { onError(error); }
                }
            }
        }
        catch
        {
            T? discard;
            lock (_gate) { _closed = true; _running = false; discard = _pending; _pending = null; }
            discard?.Dispose();
            throw;
        }
    }

    internal void DiscardPending()
    {
        T? discard;
        lock (_gate) { discard = _pending; _pending = null; }
        discard?.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Task worker;
        T? discard;
        lock (_gate) { _closed = true; discard = _pending; _pending = null; worker = _worker; }
        discard?.Dispose();
        return new(worker);
    }
}
