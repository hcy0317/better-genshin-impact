using System;
using System.Threading;

namespace BetterGenshinImpact.Helpers;

/// <summary>Headless callers may prohibit application startup, never bypass its security checks.</summary>
internal static class ApplicationHostBootstrapGuard
{
    private static int _prohibited;

    internal static bool IsProhibited => Volatile.Read(ref _prohibited) != 0;

    internal static void ProhibitForCurrentProcess() => Interlocked.Exchange(ref _prohibited, 1);

    internal static void EnsureAllowed()
    {
        if (IsProhibited)
            throw new InvalidOperationException("Application host startup is prohibited in this process. Inject isolated dependencies instead of accessing App services.");
    }
}
