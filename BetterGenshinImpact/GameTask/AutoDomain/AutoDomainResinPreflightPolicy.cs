using System;
using System.Collections.Generic;
using System.Linq;
using BetterGenshinImpact.GameTask.AutoDomain.Model;

namespace BetterGenshinImpact.GameTask.AutoDomain;

internal static class AutoDomainResinPreflightPolicy
{
    internal static ResinUseRecord? SelectNextAvailableResin(
        IEnumerable<ResinUseRecord> records,
        IReadOnlySet<string> unavailableResinNames)
    {
        return records.FirstOrDefault(record => record.RemainCount > 0
            && !unavailableResinNames.Contains(record.Name));
    }

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

    internal static bool ShouldExitSupplementPrompt(
        bool specifyResinUse,
        int transientResinRemainCount,
        int fragileResinRemainCount)
    {
        return !specifyResinUse
               || (transientResinRemainCount <= 0 && fragileResinRemainCount <= 0);
    }

    internal static bool ShouldPrepareSupplementalResinBeforeDomain(
        bool specifyResinUse,
        string? preferredResinName)
    {
        return specifyResinUse
               && preferredResinName is "须臾树脂" or "脆弱树脂";
    }

    internal static bool CanPrepareAnotherSupplementalResin(
        int domainRoundNum,
        int preparedCount)
    {
        return preparedCount < Math.Max(0, domainRoundNum);
    }
}
