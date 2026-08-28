using BetterGenshinImpact.GameTask.ArtifactAnalysis;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.ArtifactAnalysisTests;

public class ArtifactExecutionGuardTests
{
    [Fact]
    public void Fingerprint_IsStableAcrossSubstatOrderAndLockChanges()
    {
        var left = Item(0, false,
            new ArtifactSubstatDto("critRate_", 7.8),
            new ArtifactSubstatDto("critDMG_", 14.0));
        var right = Item(0, true,
            new ArtifactSubstatDto("critDMG_", 14.0),
            new ArtifactSubstatDto("critRate_", 7.8));

        Assert.Equal(left.ContentFingerprint, right.ContentFingerprint);
        Assert.Equal("2fff20ac9ec1dbec95328f0c529c842ff7996ebaf853b92a1812fa602f59a8a4",
            left.ContentFingerprint);
    }

    [Fact]
    public void Fingerprint_DistinguishesDormantFourthLineWithoutChangingActiveLegacyHash()
    {
        var active = Item(0, false,
            new ArtifactSubstatDto("critRate_", 7.8),
            new ArtifactSubstatDto("critDMG_", 14.0));
        var dormant = Item(0, false,
            new ArtifactSubstatDto("critRate_", 7.8),
            new ArtifactSubstatDto("critDMG_", 14.0, true));

        Assert.Equal("2fff20ac9ec1dbec95328f0c529c842ff7996ebaf853b92a1812fa602f59a8a4",
            active.ContentFingerprint);
        Assert.Equal("fe179e4e39f948a748465293bb9c6d1adcca18161f64ccd95d88f084dae6aaf2",
            dormant.ContentFingerprint);
        Assert.NotEqual(active.ContentFingerprint, dormant.ContentFingerprint);
    }

    [Fact]
    public void ChangedCount_RequiresRescanWithoutActions()
    {
        var source = Snapshot(Item(0, false));
        var plan = Plan(source, Decision(source.Artifacts[0], true));
        var live = Snapshot(Item(0, false), Item(1, false));

        var result = ArtifactExecutionGuard.Preflight(plan, live);

        Assert.Equal(ArtifactPreflightStatus.RescanRequired, result.Status);
        Assert.Empty(result.Actions);
    }

    [Fact]
    public void ChangedFingerprintOrLockState_AbortsWholeBatch()
    {
        var source = Snapshot(Item(0, false), Item(1, true));
        var plan = Plan(source,
            Decision(source.Artifacts[0], true),
            Decision(source.Artifacts[1], false));
        var changed = Item(1, false, new ArtifactSubstatDto("critDMG_", 21.0));

        var result = ArtifactExecutionGuard.Preflight(
            plan,
            Snapshot(Item(0, false), changed));

        Assert.Equal(ArtifactPreflightStatus.StaleAbort, result.Status);
        Assert.Empty(result.Actions);
    }

    [Fact]
    public void ExactSnapshot_ProducesOnlyRequiredActions()
    {
        var source = Snapshot(Item(0, false), Item(1, true));
        var plan = Plan(source,
            Decision(source.Artifacts[0], true),
            Decision(source.Artifacts[1], true));

        var result = ArtifactExecutionGuard.Preflight(plan, source);

        Assert.Equal(ArtifactPreflightStatus.Ready, result.Status);
        var action = Assert.Single(result.Actions);
        Assert.Equal(0, action.ScanIndex);
        Assert.False(action.ExpectedLocked);
        Assert.True(action.DesiredLocked);
    }

    [Fact]
    public void RepeatedExecution_TargetAlreadyAtDesiredStateProducesNoAction()
    {
        var source = Snapshot(Item(0, false));
        var plan = Plan(source, Decision(source.Artifacts[0], true));
        var alreadyLocked = Snapshot(Item(0, true));

        var result = ArtifactExecutionGuard.Preflight(plan, alreadyLocked);

        Assert.Equal(ArtifactPreflightStatus.Ready, result.Status);
        Assert.Empty(result.Actions);
    }

    private static ArtifactItemDto Item(int index, bool locked, params ArtifactSubstatDto[] substats) =>
        new(index, "GoldenTroupe", "circlet", 20, 5, "critRate_", substats, "Furina", locked);

    private static ArtifactSnapshotDto Snapshot(params ArtifactItemDto[] items) =>
        ArtifactSnapshotDto.Create("102550550", "scan", "CURRENT_INVENTORY_ORDER", "genshin-7.0", items);

    private static ArtifactDecisionPlanDto Plan(ArtifactSnapshotDto source, params ArtifactDecisionDto[] decisions) =>
        new("plan-1", source.Uid, source.ArtifactCount, source.SnapshotDigest, true, decisions);

    private static ArtifactDecisionDto Decision(ArtifactItemDto item, bool desiredLocked) =>
        new(item.ScanIndex, item.ContentFingerprint, item.Locked, desiredLocked);
}
