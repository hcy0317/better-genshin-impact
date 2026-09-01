using BetterGenshinImpact.GameTask.ArtifactAnalysis;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.ArtifactAnalysisTests;

public class ArtifactNativePlanValidatorTests
{
    [Fact]
    public void ThreeBuildsPerSetAndTwoQuickEquipSlotsAreValid()
    {
        var plan = Plan(
            [Lock("a"), Lock("b"), Lock("c")],
            [Quick("a", 1), Quick("b", 2)],
            sourceBuildCount: 3);

        ArtifactNativePlanValidator.Validate(plan);

        Assert.Equal(3, ArtifactNativePlanValidator.GroupLockSchemes(plan).Single().Schemes.Count);
    }

    [Fact]
    public void FourthBuildForOneSetIsRejected()
    {
        var plan = Plan(
            [Lock("a"), Lock("b"), Lock("c"), Lock("d")],
            [], sourceBuildCount: 4);

        var exception = Assert.Throws<InvalidDataException>(() =>
            ArtifactNativePlanValidator.Validate(plan));

        Assert.Contains("three", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DuplicateQuickEquipSlotForOneCharacterIsRejected()
    {
        var plan = Plan(
            [Lock("a")],
            [Quick("a", 1), Quick("b", 1)],
            sourceBuildCount: 2);

        var exception = Assert.Throws<InvalidDataException>(() =>
            ArtifactNativePlanValidator.Validate(plan));

        Assert.Contains("slot", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DuplicateSetsInAQuickEquipRecipeAreRejected()
    {
        var duplicate = Quick("a", 1) with
        {
            Sets =
            [
                new ArtifactNativeSetRuleDto("GoldenTroupe", 2),
                new ArtifactNativeSetRuleDto("GoldenTroupe", 2)
            ]
        };

        Assert.Throws<InvalidDataException>(() =>
            ArtifactNativePlanValidator.Validate(
                Plan([Lock("a")], [duplicate], sourceBuildCount: 1)));
    }

    [Fact]
    public void OneBuildCannotTargetTwoCharacters()
    {
        var otherCharacter = Quick("a", 1) with { CharacterKey = "Mona" };

        Assert.Throws<InvalidDataException>(() =>
            ArtifactNativePlanValidator.Validate(Plan(
                [Lock("a")],
                [Quick("a", 1), otherCharacter],
                sourceBuildCount: 1)));
    }

    [Fact]
    public void MissingQuickEquipMainStatSlotsExpandToEmptyTargets()
    {
        var expanded = ArtifactNativePlanValidator.QuickMainStats(Quick("a", 1));

        Assert.Empty(expanded["sands"]);
        Assert.Empty(expanded["goblet"]);
        Assert.Equal(["critRate_"], expanded["circlet"]);
    }

    private static ArtifactNativeSyncPlanDto Plan(
        IReadOnlyList<ArtifactNativeSetPlanDto> locks,
        IReadOnlyList<ArtifactNativeQuickEquipPlanDto> quick,
        int sourceBuildCount) => new(
        "READY", locks.Count > 0, true, 100, sourceBuildCount,
        locks, quick, [], "digest",
        "BUILD_SCOPED_LOCK_AND_QUICK_EQUIP_V1", "ready");

    private static ArtifactNativeSetPlanDto Lock(string buildId) => new(
        buildId, buildId, "GoldenTroupe", "circlet",
        ["critRate_"], ["critDMG_"]);

    private static ArtifactNativeQuickEquipPlanDto Quick(
        string buildId,
        int presetIndex) => new(
        buildId, buildId, "Furina", presetIndex,
        [new ArtifactNativeSetRuleDto("GoldenTroupe", 4)],
        new Dictionary<string, IReadOnlyList<string>>
        {
            ["circlet"] = ["critRate_"]
        },
        ["critDMG_"], []);
}
