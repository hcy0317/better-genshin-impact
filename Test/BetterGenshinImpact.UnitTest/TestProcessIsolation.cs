using System.Runtime.CompilerServices;
using BetterGenshinImpact.Helpers;

namespace BetterGenshinImpact.UnitTest;

internal static class TestProcessIsolation
{
    [ModuleInitializer]
    internal static void Initialize() => ApplicationHostBootstrapGuard.ProhibitForCurrentProcess();
}
