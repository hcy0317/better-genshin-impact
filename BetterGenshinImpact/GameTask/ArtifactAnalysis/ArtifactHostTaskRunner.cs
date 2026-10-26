using System;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.GameTask.ArtifactAnalysis;

/// <summary>把授权、现场核验及执行放在同一次独立任务内；测试可替换真实启动边界。</summary>
public interface IArtifactHostTaskRunner
{
    Task RunAsync(Func<CancellationToken, Task> prepare,
        Func<CancellationToken, Task> action, CancellationToken cancellationToken);
}

public sealed class ArtifactHostTaskRunner : IArtifactHostTaskRunner
{
    public Task RunAsync(Func<CancellationToken, Task> prepare,
        Func<CancellationToken, Task> action, CancellationToken cancellationToken) =>
        new TaskRunner().RunSoloTaskAsync(new HostTask(action), propagateExceptions: true,
            cancellationToken: cancellationToken, waitForTask: true, beforeStart: prepare);

    private sealed class HostTask(Func<CancellationToken, Task> action) : ISoloTask
    {
        public string Name => "网页圣遗物任务";
        public async Task Start(CancellationToken cancellationToken)
        {
            using var scope = ArtifactHostTaskScope.Enter(cancellationToken);
            await action(cancellationToken);
        }
    }
}

/// <summary>只复用真实任务内仍有效的所有权；关闭后的继承上下文不能启动迟到子任务。</summary>
internal sealed class ArtifactHostTaskScope : IDisposable
{
    private static readonly AsyncLocal<ArtifactHostTaskScope?> Active = new();
    private readonly TaskExecutionScope.Guard _guard = TaskExecutionScope.Capture();
    private readonly Guid _taskId;
    private readonly CancellationToken _token;
    private int _closed;

    private ArtifactHostTaskScope(CancellationToken token)
    {
        _taskId = TaskExecutionScope.DiagnosticId
            ?? throw new InvalidOperationException("圣遗物作用域必须属于活动独立任务。");
        _guard.Check();
        token.ThrowIfCancellationRequested();
        if (Active.Value != null) throw new InvalidOperationException("不能嵌套圣遗物宿主任务。");
        _token = token;
    }

    internal static ArtifactHostTaskScope Enter(CancellationToken token)
    {
        var scope = new ArtifactHostTaskScope(token);
        Active.Value = scope;
        return scope;
    }

    private void Check()
    {
        if (Volatile.Read(ref _closed) != 0 || TaskExecutionScope.DiagnosticId != _taskId)
            throw new OperationCanceledException("圣遗物所属任务已结束。");
        _guard.Check();
        _token.ThrowIfCancellationRequested();
    }

    internal static async Task RunSoloTaskAsync(ISoloTask task, CancellationToken cancellationToken,
        bool requireContinuousOwnership = false)
    {
        if (Active.Value is not { } scope)
        {
            if (requireContinuousOwnership)
                throw new InvalidOperationException("圣遗物现场核验和锁定执行必须属于同一次宿主任务。");
            await new TaskRunner().RunSoloTaskAsync(task, propagateExceptions: true,
                cancellationToken: cancellationToken, waitForTask: true);
            return;
        }
        scope.Check();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(scope._token, cancellationToken);
        linked.Token.ThrowIfCancellationRequested();
        await task.Start(linked.Token);
        // 终态的取消由最外层 TaskRunner 处理，保留原生方案提交后前向恢复语义。
        scope._guard.Check();
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _closed, 1);
        if (ReferenceEquals(Active.Value, this)) Active.Value = null;
    }
}
