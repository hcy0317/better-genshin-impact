using System;
using System.Threading.Tasks;

namespace BetterGenshinImpact.Service;

/// <summary>一个关闭请求只执行一次；应用窗口仍可调度时先排空，再进入真正退出。</summary>
internal sealed class ApplicationShutdownSequence(Func<Task> drain, Action close)
{
    private readonly object _gate = new();
    private Task? _request;
    internal bool Prepared { get; private set; }
    internal Task RequestAsync()
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_request?.IsFaulted == true && !Prepared) _request = null;
            if (_request != null) return _request;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _request = completion.Task;
        }
        _ = RunAsync(completion);
        return completion.Task;
    }
    private async Task RunAsync(TaskCompletionSource completion)
    {
        // 让原Closing事件先以Cancel=true返回，避免在同一个关闭回调中重入Window.Close。
        await Task.Yield();
        try
        {
            await drain();
            Prepared = true;
            close();
            completion.TrySetResult();
        }
        catch (Exception error) { completion.TrySetException(error); }
    }
}
