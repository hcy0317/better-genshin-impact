namespace BetterGenshinImpact.GameTask.AutoDomain;

internal static class AutoDomainResinPreflightPolicy
{
    internal static bool ShouldCheckMapResin(
        bool specifyResinUse,
        int transientResinUseCount,
        int fragileResinUseCount)
    {
        return !specifyResinUse
               || (transientResinUseCount <= 0 && fragileResinUseCount <= 0);
    }

    internal static bool HasClaimableResin(int originalResin, int condensedResin)
    {
        return condensedResin > 0 || originalResin >= 20;
    }
}
