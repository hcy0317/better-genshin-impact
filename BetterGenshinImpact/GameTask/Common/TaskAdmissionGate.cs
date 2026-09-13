using System;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.GameTask.Common;

/// <summary>令牌初始化与停止准入共用同一边界，停止后的新任务不能覆盖已取消的令牌。</summary>
internal sealed class TaskAdmissionGate
{
    private readonly object _gate = new();
    private int _pauses;
    private bool _closing;
    private int _activities;
    private TaskCompletionSource? _drained;
    internal bool IsClosing { get { lock (_gate) return _closing; } }
    internal void SetClosing(bool closing) { lock (_gate) _closing = closing; }
    internal void Check()
    {
        lock (_gate)
            if (_closing || _pauses > 0) throw new OperationCanceledException("任务正在停止或应用正在关闭，拒绝启动新任务");
    }
    internal void Initialize(Action initialize)
    {
        lock (_gate)
        {
            Check();
            initialize();
        }
    }
    internal IDisposable Pause()
    {
        lock (_gate) _pauses++;
        return new PauseLease(this);
    }
    internal IDisposable EnterActivity()
    {
        lock (_gate)
        {
            Check();
            if (_activities++ == 0) _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return new ActivityLease(this);
        }
    }
    internal Task WaitForActivitiesAsync()
    {
        lock (_gate) return _activities == 0 ? Task.CompletedTask : _drained!.Task;
    }
    private sealed class ActivityLease(TaskAdmissionGate owner) : IDisposable
    {
        private TaskAdmissionGate? _owner = owner;
        public void Dispose()
        {
            var gate = Interlocked.Exchange(ref _owner, null);
            if (gate != null) lock (gate._gate)
                if (--gate._activities == 0) gate._drained?.TrySetResult();
        }
    }
    private sealed class PauseLease(TaskAdmissionGate owner) : IDisposable
    {
        private TaskAdmissionGate? _owner = owner;
        public void Dispose()
        {
            var gate = Interlocked.Exchange(ref _owner, null);
            if (gate != null) lock (gate._gate) gate._pauses--;
        }
    }
}
