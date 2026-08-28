using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace BetterGenshinImpact.Service;

internal static class ArtifactHostExecutionContext
{
    internal static SynchronizationContext CaptureApplicationContext()
    {
        var dispatcher = Application.Current?.Dispatcher
            ?? throw new InvalidOperationException("BetterGI UI Dispatcher 尚未初始化");
        return new DispatcherSynchronizationContext(dispatcher);
    }

    internal static Task RunAsync(SynchronizationContext context, Func<Task> action)
    {
        if (ReferenceEquals(SynchronizationContext.Current, context)) return action();

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Post(async _ =>
        {
            try
            {
                await action();
                completion.TrySetResult();
            }
            catch (OperationCanceledException exception)
            {
                completion.TrySetCanceled(exception.CancellationToken);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }, null);
        return completion.Task;
    }
}
