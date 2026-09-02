using System;
using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatSafetyPolicyTests
{
    [Fact]
    public void NewConfigurationDefaultsToClosedLoopTargeting()
    {
        var config = new AutoFightConfig();

        Assert.True(config.EnableCombatTargeting);
        Assert.Equal(
            CombatTargetingMode.ClosedLoop,
            config.CombatTargetingMode);
    }

    [Theory]
    [InlineData(75, 450, 450)]
    [InlineData(600, 450, 600)]
    [InlineData(-1, 450, 450)]
    [InlineData(75, -1, 75)]
    public void PaimonEndCheck_WaitsForTheCompletePartyScreenDetectionWindow(
        int configuredDelayMs,
        int detectDelayTimeMs,
        int expectedDelayMs)
    {
        Assert.Equal(
            expectedDelayMs,
            AutoFightTask.GetPaimonEndCheckDelayMilliseconds(
                configuredDelayMs,
                detectDelayTimeMs));
    }

    [Theory]
    [InlineData(CombatTargetingMode.Legacy, true)]
    [InlineData(CombatTargetingMode.ObserveOnly, false)]
    [InlineData(CombatTargetingMode.ClosedLoop, false)]
    public void OnlyLegacyModeCanEnterTheUnboundedSeekPath(
        CombatTargetingMode mode,
        bool expected)
    {
        Assert.Equal(expected, BoundedSeekPolicy.ShouldUseLegacySeekPath(mode));
    }

    [Theory]
    [InlineData(50, 200)]
    [InlineData(250, 250)]
    [InlineData(1000, 350)]
    public void BoundedSeek_MovementPulseIsClamped(int requested, int expected)
    {
        Assert.Equal(
            TimeSpan.FromMilliseconds(expected),
            BoundedSeekPolicy.NormalizeMovementDuration(
                TimeSpan.FromMilliseconds(requested)));
    }

    [Fact]
    public void BoundedSeek_BudgetCannotExceedSixHundredMilliseconds()
    {
        Assert.Equal(
            TimeSpan.FromMilliseconds(600),
            BoundedSeekPolicy.NormalizeBudget(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void BoundedSeek_FixedTopHealthNeverRequestsMovement()
    {
        Assert.False(BoundedSeekPolicy.CanRequestMovement(
            AutoFightSeekAction.ApproachFixedTopHealthTarget,
            isFixedTopHealth: true));
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(100, 0, false)]
    [InlineData(0, 100, false)]
    public void BoundedSeek_CannotMoveInTheSameSliceAsACameraPulse(
        int horizontalOffset,
        int verticalOffset,
        bool expected)
    {
        Assert.Equal(
            expected,
            BoundedSeekPolicy.CanMoveAfterCameraPulse(
                horizontalOffset,
                verticalOffset));
    }

    [Theory]
    [InlineData(0, 4, true, true)]
    [InlineData(1, 4, true, false)]
    [InlineData(2, 4, false, false)]
    [InlineData(3, 4, false, true)]
    public void TxtSeek_IsAllowedOnlyBeforeOrAfterACompleteCommandRound(
        int commandIndex,
        int commandCount,
        bool beforeCommand,
        bool expected)
    {
        Assert.Equal(
            expected,
            BoundedSeekPolicy.CanSeekAtTxtRoundBoundary(
                commandIndex,
                commandCount,
                beforeCommand));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void JsonSeek_IsAllowedOnlyAfterTheActionCompletes(
        bool actionCompleted,
        bool expected)
    {
        Assert.Equal(
            expected,
            BoundedSeekPolicy.CanSeekAtJsonActionBoundary(actionCompleted));
    }

    [Theory]
    [InlineData(3, 3, 0, true)]
    [InlineData(3, 4, 0, false)]
    [InlineData(3, 3, 1, false)]
    public void PassiveObservation_IsPublishedOnlyWithoutAnInterveningExclusiveOperation(
        long captureEpoch,
        long currentEpoch,
        int activeExclusiveOperations,
        bool expected)
    {
        Assert.Equal(
            expected,
            AvatarRecognition.CanPublishPassiveObservation(
                captureEpoch,
                currentEpoch,
                activeExclusiveOperations));
    }

    [Theory]
    [InlineData(
        GuardianAttemptResult.ConfirmedNewCast,
        GuardianCoverageMode.BestEffort,
        false,
        0,
        GuardianBoundaryAction.ProceedProtected)]
    [InlineData(
        GuardianAttemptResult.UnavailableCooldown,
        GuardianCoverageMode.BestEffort,
        false,
        0,
        GuardianBoundaryAction.ProceedUnprotected)]
    [InlineData(
        GuardianAttemptResult.UnavailableCooldown,
        GuardianCoverageMode.RequireKnownCoverage,
        true,
        0,
        GuardianBoundaryAction.ProceedProtected)]
    [InlineData(
        GuardianAttemptResult.UnavailableCooldown,
        GuardianCoverageMode.RequireKnownCoverage,
        false,
        0,
        GuardianBoundaryAction.FailCombat)]
    [InlineData(
        GuardianAttemptResult.SwitchRejected,
        GuardianCoverageMode.BestEffort,
        false,
        0,
        GuardianBoundaryAction.Retry)]
    [InlineData(
        GuardianAttemptResult.SkillRejected,
        GuardianCoverageMode.BestEffort,
        false,
        2,
        GuardianBoundaryAction.ProceedUnprotected)]
    [InlineData(
        GuardianAttemptResult.SkillRejected,
        GuardianCoverageMode.RequireKnownCoverage,
        false,
        2,
        GuardianBoundaryAction.FailCombat)]
    [InlineData(
        GuardianAttemptResult.Cancelled,
        GuardianCoverageMode.BestEffort,
        false,
        0,
        GuardianBoundaryAction.Cancel)]
    public void GuardianDecisionTable_IsBoundedAndExplicit(
        GuardianAttemptResult result,
        GuardianCoverageMode mode,
        bool knownCoverageValid,
        int failedAttempts,
        GuardianBoundaryAction expected)
    {
        Assert.Equal(
            expected,
            GuardianSkillSwitchPolicy.DecideBoundary(
                result,
                mode,
                knownCoverageValid,
                failedAttempts));
    }

    [Fact]
    public void KnownCoverage_RequiresAConfirmedCastAndUnexpiredDuration()
    {
        var now = DateTime.UtcNow;

        Assert.True(GuardianSkillSwitchPolicy.IsKnownCoverageValid(
            now.AddSeconds(-5),
            durationSeconds: 10,
            now));
        Assert.False(GuardianSkillSwitchPolicy.IsKnownCoverageValid(
            now.AddSeconds(-5),
            durationSeconds: 4,
            now));
        Assert.False(GuardianSkillSwitchPolicy.IsKnownCoverageValid(
            default,
            durationSeconds: 10,
            now));
        Assert.False(GuardianSkillSwitchPolicy.IsKnownCoverageValid(
            now,
            durationSeconds: null,
            now));
    }

    [Fact]
    public void KnownCoverage_EntersTheRefreshWindowBeforeNaturalExpiration()
    {
        var now = DateTime.UtcNow;

        Assert.False(GuardianSkillSwitchPolicy.IsKnownCoverageValid(
            now.AddSeconds(-8.5),
            durationSeconds: 10,
            now,
            refreshReserve: TimeSpan.FromSeconds(2)));
    }

    [Theory]
    [InlineData(-7.5, 500)]
    [InlineData(-8.1, 0)]
    public void SeekBudget_IsClippedByTheGuardianRefreshDeadline(
        double castOffsetSeconds,
        int expectedMilliseconds)
    {
        var now = DateTime.UtcNow;

        Assert.Equal(
            TimeSpan.FromMilliseconds(expectedMilliseconds),
            GuardianSkillSwitchPolicy.GetSafeSeekBudget(
                now.AddSeconds(castOffsetSeconds),
                durationSeconds: 10,
                now,
                requestedBudget: TimeSpan.FromMilliseconds(600),
                refreshReserve: TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void ConfirmedGuardianCast_PersistsInSharedTracker()
    {
        const string guardianName = "测试盾位";
        var confirmedAt = DateTime.UtcNow;
        ESkillCdTracker.Clear();
        try
        {
            ESkillCdTracker.RecordConfirmedCast(
                guardianName,
                cooldownSeconds: 12,
                confirmedAt);

            Assert.Equal(
                confirmedAt,
                ESkillCdTracker.GetLastConfirmedCastAtUtc(guardianName));
            Assert.False(ESkillCdTracker.IsReady(guardianName));
        }
        finally
        {
            ESkillCdTracker.Clear();
        }

        Assert.Equal(default, ESkillCdTracker.GetLastConfirmedCastAtUtc(guardianName));
    }

    [Theory]
    [InlineData(GuardianCoverageMode.BestEffort, false, null, true)]
    [InlineData(GuardianCoverageMode.RequireKnownCoverage, false, 20d, false)]
    [InlineData(GuardianCoverageMode.RequireKnownCoverage, true, null, false)]
    [InlineData(GuardianCoverageMode.RequireKnownCoverage, true, 0d, false)]
    [InlineData(GuardianCoverageMode.RequireKnownCoverage, true, 20d, true)]
    public void StrictGuardianCoverage_RequiresGuardianAndKnownDuration(
        GuardianCoverageMode coverageMode,
        bool guardianConfigured,
        double? shieldDurationSeconds,
        bool expected)
    {
        Assert.Equal(
            expected,
            GuardianSkillSwitchPolicy.IsCoverageConfigurationValid(
                coverageMode,
                guardianConfigured,
                shieldDurationSeconds));
    }
}
