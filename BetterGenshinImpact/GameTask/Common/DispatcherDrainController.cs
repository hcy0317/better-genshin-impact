using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Fischless.GameCapture.Graphics;

namespace BetterGenshinImpact.GameTask.Common;

/// <summary>停止接收→排空→释放。UI和回调内部的调用方只请求停止，不同步自等待。</summary>
internal sealed class DispatcherDrainController
{
    private readonly object _gate = new();
    private readonly FrameCallbackLifetime _callbacks = new();
    private readonly Action _quiesce;
    private readonly Func<Task> _release;
    private Task? _stop;
    private bool _started;
    internal DispatcherDrainController(Action quiesce, Action release)
        : this(quiesce, () => { release(); return Task.CompletedTask; }) { }
    internal DispatcherDrainController(Action quiesce, Func<Task> release)
    { _quiesce = quiesce; _release = release; _callbacks.BeginStop(); }
    internal bool IsCurrentCallback => _callbacks.IsCurrentCallback;
    internal bool IsStopping => _callbacks.IsStopping;
    internal bool TryEnter() => _callbacks.TryEnter();
    internal void Exit() => _callbacks.Exit();
    internal void Start()
    {
        PrepareStart();
        Activate();
    }
    internal void PrepareStart()
    {
        lock (_gate)
        {
            if (_started && !_callbacks.IsStopping) throw new InvalidOperationException("调度器已经启动");
            if (_stop != null && !_stop.IsCompletedSuccessfully) throw new InvalidOperationException("上一次停止尚未成功排空");
            _stop = null;
            _started = true;
        }
    }
    internal void Activate()
    {
        lock (_gate)
        {
            if (!_started || _stop != null) throw new InvalidOperationException("启动已取消或尚未准备");
            _callbacks.Reset();
        }
    }
    internal Task StopAsync()
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_stop != null && !_stop.IsFaulted) return _stop;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _stop = completion.Task;
            _callbacks.BeginStop();
        }
        Exception? failure = null;
        try { _quiesce(); } catch (Exception error) { failure = error; }
        _ = CompleteStopAsync(completion, failure);
        return completion.Task;
    }
    private async Task CompleteStopAsync(TaskCompletionSource completion, Exception? failure)
    {
        // 发布停止任务并释放启动/停止锁之后才做可能调用UI的资源清理。
        await Task.Yield();
        try { await DrainAsync(failure).ConfigureAwait(false); completion.TrySetResult(); }
        catch (Exception error) { completion.TrySetException(error); }
    }
    private async Task DrainAsync(Exception? quiesceFailure)
    {
        await _callbacks.WaitForCallbacksAsync().ConfigureAwait(false);
        List<Exception> failures = [];
        if (quiesceFailure != null) failures.Add(quiesceFailure);
        try { await _release().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        if (failures.Count != 0) throw new AggregateException("调度器停止清理失败", failures);
    }
}
