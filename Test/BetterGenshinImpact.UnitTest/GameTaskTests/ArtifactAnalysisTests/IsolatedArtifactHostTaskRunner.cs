using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.ArtifactAnalysis;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.ArtifactAnalysisTests;

// 不构造 TaskRunner，不读取 App/DI，也不调用真实游戏启动或输入。
internal sealed class IsolatedArtifactHostTaskRunner(SemaphoreSlim? semaphore = null) : IArtifactHostTaskRunner
{
    public int Starts { get; private set; }
    public Func<Task>? BeforePrepare { get; init; }

    public async Task RunAsync(Func<CancellationToken, Task> prepare,
        Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        if (semaphore != null)
            await TaskRunnerStartupGate.TryAcquireAsync(semaphore, () => { }, cancellationToken, waitForTask: true);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BeforePrepare != null) await BeforePrepare();
            await prepare(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Starts++;
            using var task = TaskExecutionScope.BeginOwned();
            using var scope = ArtifactHostTaskScope.Enter(cancellationToken);
            await action(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            semaphore?.Release();
        }
    }
}
