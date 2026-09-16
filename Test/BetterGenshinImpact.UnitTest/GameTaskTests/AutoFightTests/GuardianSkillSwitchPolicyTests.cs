using System;
using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Model;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class GuardianSkillSwitchPolicyTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(5, 2)]
    public void NormalizeAttemptCount_ShouldBoundRepeatedTenKeySwitchCycles(int requested, int expected)
    {
        Assert.Equal(expected, GuardianSkillSwitchPolicy.NormalizeAttemptCount(requested));
    }

    [Theory]
    [InlineData(true, true, true, 10, 5, true)]
    [InlineData(true, true, true, 5, 5, false)]
    [InlineData(true, true, true, 0, 0, false)]
    [InlineData(false, true, true, 10, 5, false)]
    [InlineData(true, false, true, 10, 5, false)]
    [InlineData(true, true, false, 10, 5, false)]
    public void DuplicateGuardianSkill_IsSkippedOnlyWhileTheConfirmedShieldIsActive(
        bool guardianSkillHandled,
        bool commandTargetsGuardian,
        bool isSkillCommand,
        int elapsedSeconds,
        int durationSeconds,
        bool expected)
    {
        Assert.Equal(
            expected,
            GuardianSkillSwitchPolicy.ShouldSkipCoveredGuardianSkill(
                guardianSkillHandled,
                commandTargetsGuardian,
                isSkillCommand,
                DateTime.UnixEpoch.AddSeconds(elapsedSeconds),
                durationSeconds,
                DateTime.UnixEpoch.AddSeconds(10)));
    }

    [Fact]
    public void PriorityGuardianSkill_ShouldNotBeRecheckedAfterItWasHandledInTheSameLoop()
    {
        Assert.False(GuardianSkillSwitchPolicy.ShouldEnsureGuardianSkill(
            guardianSkillHandled: true,
            shouldSwitch: true));
    }

    [Fact]
    public void PriorityGuardianSkill_CanRetryAfterAnUnsuccessfulAttempt()
    {
        Assert.True(GuardianSkillSwitchPolicy.ShouldEnsureGuardianSkill(
            guardianSkillHandled: false,
            shouldSwitch: true));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public void GuardianSkillFailure_ShouldRetryTheCurrentStrategyBlock(
        bool guardianSkillRequired,
        bool guardianSkillHandled,
        bool expected)
    {
        Assert.Equal(expected, GuardianSkillSwitchPolicy.ShouldRetryBlock(
            guardianSkillRequired,
            guardianSkillHandled));
    }

    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(true, true, true, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, false, false)]
    public void GuardianSkillSuccess_RequiresReadyToCooldownOnTheConfirmedAvatar(
        bool baselineCooldownVisible,
        bool cooldownVisibleAfterInput,
        bool guardianStillActive,
        bool expected)
    {
        Assert.Equal(expected, GuardianSkillSwitchPolicy.IsSkillCastConfirmed(
            baselineCooldownVisible,
            cooldownVisibleAfterInput,
            guardianStillActive));
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    public void GuardianCooldown_CanOnlyBeReusedAfterAConfirmedSkillUse(
        bool skillReady,
        bool hasConfirmedSkillCooldown,
        bool expected)
    {
        Assert.Equal(expected, GuardianSkillSwitchPolicy.CanReuseConfirmedCooldown(
            skillReady,
            hasConfirmedSkillCooldown));
    }

    [Fact]
    public void AvatarSwitchConfirmation_RequiresTwoConsecutiveTargetFrames()
    {
        var consecutive = 0;

        consecutive = AvatarSwitchConfirmationPolicy.Observe(
            consecutive, observedIndex: 1, expectedIndex: 1);
        Assert.False(AvatarSwitchConfirmationPolicy.IsConfirmed(consecutive));

        consecutive = AvatarSwitchConfirmationPolicy.Observe(
            consecutive, observedIndex: 2, expectedIndex: 1);
        Assert.Equal(0, consecutive);

        consecutive = AvatarSwitchConfirmationPolicy.Observe(
            consecutive, observedIndex: 1, expectedIndex: 1);
        consecutive = AvatarSwitchConfirmationPolicy.Observe(
            consecutive, observedIndex: 1, expectedIndex: 1);
        Assert.True(AvatarSwitchConfirmationPolicy.IsConfirmed(consecutive));
    }

    [Fact]
    public void CombatExecution_MustStopTheCurrentBlockWhenSwitchOrShieldConfirmationFails()
    {
        var root = FindRepoRoot();
        var avatarSource = File.ReadAllText(Path.Combine(
            root,
            "BetterGenshinImpact",
            "GameTask",
            "AutoFight",
            "Model",
            "Avatar.cs"));
        var commandSource = File.ReadAllText(Path.Combine(
            root,
            "BetterGenshinImpact",
            "GameTask",
            "AutoFight",
            "Script",
            "CombatCommand.cs"));
        var taskSource = File.ReadAllText(Path.Combine(
            root,
            "BetterGenshinImpact",
            "GameTask",
            "AutoFight",
            "AutoFightTask.cs"));
        var jsonTaskSource = File.ReadAllText(Path.Combine(
            root,
            "BetterGenshinImpact",
            "GameTask",
            "AutoFight",
            "AutoFightJsonTask.cs"));
        var seekSource = File.ReadAllText(Path.Combine(
            root,
            "BetterGenshinImpact",
            "GameTask",
            "AutoFight",
            "AutoFightSeek.cs"));
        var domainSource = File.ReadAllText(Path.Combine(
            root,
            "BetterGenshinImpact",
            "GameTask",
            "AutoDomain",
            "AutoDomainTask.cs"));
        var stygianSource = File.ReadAllText(Path.Combine(
            root,
            "BetterGenshinImpact",
            "GameTask",
            "AutoStygianOnslaught",
            "AutoStygianOnslaughtTask.cs"));

        Assert.Contains("AvatarSelectionProtocol.Select", avatarSource,
            StringComparison.Ordinal);
        var selectionSource = File.ReadAllText(Path.Combine(root, "BetterGenshinImpact", "GameTask",
            "AutoFight", "Model", "AvatarSelectionProtocol.cs"));
        Assert.Contains("AvatarSwitchConfirmationPolicy.Observe", selectionSource, StringComparison.Ordinal);
        Assert.Contains("public bool Execute(CombatScenes", commandSource,
            StringComparison.Ordinal);
        Assert.Contains("ExecuteWithResult(combatScenes, lastCommand).CanContinue", commandSource,
            StringComparison.Ordinal);
        Assert.Contains("if (!avatar.TrySwitch(10)) return new(CombatExecutionKind.Failed", commandSource,
            StringComparison.Ordinal);
        Assert.Contains("NativeCombatFlowRunner.Create", taskSource, StringComparison.Ordinal);
        Assert.Contains("NativeCombatBattleHostIo.ApplyResult", taskSource, StringComparison.Ordinal);
        Assert.Contains("TryUseGuardianSkillOnceAsync", seekSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain("guardianAvatar.UseSkill", seekSource,
            StringComparison.Ordinal);
        Assert.Contains("NativeCombatFlowRunner.Create", jsonTaskSource, StringComparison.Ordinal);
        Assert.Contains("NativeCombatBattleHostIo.ApplyResult", jsonTaskSource, StringComparison.Ordinal);
        foreach (var externalScene in new[] { domainSource, stygianSource })
        {
            Assert.Contains("NativeCombatBattleHostIo.CreateForExternalScene", externalScene, StringComparison.Ordinal);
            Assert.Contains("TaskExecutionScope.StopUnconfirmedCombat", externalScene, StringComparison.Ordinal);
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName,
                    "BetterGenshinImpact.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate BetterGenshinImpact.sln.");
    }
}
