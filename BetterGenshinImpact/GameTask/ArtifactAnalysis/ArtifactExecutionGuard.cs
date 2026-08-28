using System;
using System.Collections.Generic;
using System.Linq;

namespace BetterGenshinImpact.GameTask.ArtifactAnalysis;

public static class ArtifactExecutionGuard
{
    public static ArtifactPreflightResult Preflight(
        ArtifactDecisionPlanDto plan,
        ArtifactSnapshotDto liveSnapshot)
    {
        if (!plan.Approved)
        {
            return Blocked(ArtifactPreflightStatus.NotApproved, "plan is not approved");
        }
        if (!string.Equals(plan.Uid, liveSnapshot.Uid, StringComparison.Ordinal))
        {
            return Blocked(ArtifactPreflightStatus.StaleAbort, "uid does not match approved plan");
        }
        if (plan.SourceArtifactCount != liveSnapshot.ArtifactCount)
        {
            return Blocked(ArtifactPreflightStatus.RescanRequired,
                "artifact count changed; full rescan and approval are required");
        }

        var liveByIndex = liveSnapshot.Artifacts.ToDictionary(item => item.ScanIndex);
        foreach (var decision in plan.Decisions)
        {
            if (!liveByIndex.TryGetValue(decision.ScanIndex, out var live))
            {
                return Blocked(ArtifactPreflightStatus.StaleAbort,
                    $"target index is missing: {decision.ScanIndex}");
            }
            if (!string.Equals(decision.ExpectedFingerprint, live.ContentFingerprint, StringComparison.Ordinal))
            {
                return Blocked(ArtifactPreflightStatus.StaleAbort,
                    $"target fingerprint changed at index {decision.ScanIndex}");
            }
            if (live.Locked == decision.DesiredLocked) continue;
            if (decision.ExpectedLocked != live.Locked)
            {
                return Blocked(ArtifactPreflightStatus.StaleAbort,
                    $"target lock state changed at index {decision.ScanIndex}");
            }
        }

        var actions = plan.Decisions
            .Where(decision => decision.ExpectedLocked != decision.DesiredLocked)
            .Where(decision => liveByIndex[decision.ScanIndex].Locked != decision.DesiredLocked)
            .Select(decision => new ArtifactExecutionActionDto(
                decision.ScanIndex,
                decision.ExpectedLocked,
                decision.DesiredLocked,
                decision.ExpectedFingerprint))
            .ToArray();
        return new ArtifactPreflightResult(ArtifactPreflightStatus.Ready, actions, []);
    }

    private static ArtifactPreflightResult Blocked(ArtifactPreflightStatus status, string reason) =>
        new(status, [], [reason]);
}
