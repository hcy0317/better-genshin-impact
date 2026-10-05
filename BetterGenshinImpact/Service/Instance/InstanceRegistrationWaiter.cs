using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.Service.Instance;

internal static class InstanceRegistrationWaiter
{
    internal static async Task<T> WaitAsync<T>(Func<T?> readLiveRegistration,
        TimeSpan timeout, string timeoutMessage, CancellationToken cancellationToken) where T : class
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (readLiveRegistration() is { } registration) return registration;
            var remaining = timeout - watch.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            await Task.Delay(remaining < TimeSpan.FromMilliseconds(250) ? remaining : TimeSpan.FromMilliseconds(250),
                cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        throw new TimeoutException(timeoutMessage);
    }
}
