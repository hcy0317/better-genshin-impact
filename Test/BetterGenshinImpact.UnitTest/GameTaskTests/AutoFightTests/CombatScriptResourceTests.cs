using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using BetterGenshinImpact.Core.Script.Dependence;
using BetterGenshinImpact.GameTask.AutoPathing.Handler;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.Service;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatScriptResourceTests
{
    private static readonly Regex WaitRegex = new(@"wait\(([0-9]+(?:\.[0-9]+)?)\)", RegexOptions.Compiled);

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(120, true)]
    public void IsTimeTimeoutEnabled_ShouldDisableNonPositiveTimeouts(int timeoutSeconds, bool expected)
    {
        Assert.Equal(expected, AutoFightParam.IsTimeTimeoutEnabled(timeoutSeconds));
    }

    [Theory]
    [InlineData(false, 240, 200, 0, false)]
    [InlineData(false, 240, 200, 6, true)]
    [InlineData(true, 199, 200, 0, false)]
    [InlineData(true, 201, 200, 0, true)]
    public void ShouldStopForCombatTimeout_ShouldKeepSeekLimitWhenTimeTimeoutIsDisabled(bool fightTimeoutEnabled, int elapsedSeconds, int timeoutSeconds, int rotationCount, bool expected)
    {
        Assert.Equal(expected, AutoFightParam.ShouldStopForCombatTimeout(
            fightTimeoutEnabled,
            TimeSpan.FromSeconds(elapsedSeconds),
            TimeSpan.FromSeconds(timeoutSeconds),
            rotationCount));
    }

    [Theory]
    [InlineData(false, 240, 200, 0, false)]
    [InlineData(false, 240, 200, 6, true)]
    [InlineData(true, 201, 200, 0, true)]
    public void ShouldSkipPostFightPickupAfterForcedStop_ShouldSkipForTimeoutOrSeekLimit(bool fightTimeoutEnabled, int elapsedSeconds, int timeoutSeconds, int rotationCount, bool expected)
    {
        Assert.Equal(expected, AutoFightParam.ShouldSkipPostFightPickupAfterForcedStop(
            fightTimeoutEnabled,
            TimeSpan.FromSeconds(elapsedSeconds),
            TimeSpan.FromSeconds(timeoutSeconds),
            rotationCount));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void ShouldRunKazuhaGatheredDropsScan_ShouldOnlySupplementDisabledFullScan(
        bool kazuhaPickupEnabled,
        bool fullScanEnabled,
        bool expected)
    {
        Assert.Equal(expected, AutoFightParam.ShouldRunKazuhaGatheredDropsScan(
            kazuhaPickupEnabled,
            fullScanEnabled));
    }

    [Theory]
    [InlineData(false, true, 5, 5, true)]
    [InlineData(false, true, 4, 5, false)]
    [InlineData(true, true, 6, 5, false)]
    [InlineData(false, false, 6, 5, false)]
    [InlineData(false, true, 6, 0, false)]
    public void ShouldRunPeriodicFinishCheck_ShouldRunOnlyWhenTimeTimeoutIsDisabledAndDetectEnabled(bool fightTimeoutEnabled, bool fightFinishDetectEnabled, int elapsedSeconds, int intervalSeconds, bool expected)
    {
        Assert.Equal(expected, AutoFightParam.ShouldRunPeriodicFinishCheck(
            fightTimeoutEnabled,
            fightFinishDetectEnabled,
            TimeSpan.FromSeconds(elapsedSeconds),
            TimeSpan.FromSeconds(intervalSeconds)));
    }

    [Theory]
    [InlineData(false, 5, 5)]
    [InlineData(true, 5, 10)]
    [InlineData(true, 0, 0)]
    [InlineData(true, 12, 12)]
    public void NormalizeFinishCheckInterval_ShouldClampOnlyShortRotateIntervals(bool rotateFindEnemyEnabled, int intervalSeconds, int expectedSeconds)
    {
        Assert.Equal(
            TimeSpan.FromSeconds(expectedSeconds),
            AutoFightParam.NormalizeFinishCheckInterval(TimeSpan.FromSeconds(intervalSeconds), rotateFindEnemyEnabled));
    }

    [Fact]
    public void SeekCameraOffset_ShouldProduceAWaveDuringHorizontalSweep()
    {
        var offsets = Enumerable.Range(0, 18)
            .Select(retryCount => AutoFightSeek.GetSeekCameraOffset(1500, 900, rotationCount: 0, retryCount))
            .ToList();

        Assert.All(offsets, offset => Assert.True(offset.x > 0));
        Assert.Contains(offsets, offset => offset.y > 0);
        Assert.Contains(offsets, offset => offset.y < 0);
    }

    [Fact]
    public void SeekCameraOffset_HorizontalStepShouldNotDependOnVerticalWavePhase()
    {
        var firstOffset = AutoFightSeek.GetSeekCameraOffset(1500, 900, rotationCount: 0, retryCount: 0);
        var secondOffset = AutoFightSeek.GetSeekCameraOffset(1500, 900, rotationCount: 0, retryCount: 1);

        Assert.Equal(firstOffset.x, secondOffset.x);
        Assert.NotEqual(firstOffset.y, secondOffset.y);
    }

    [Fact]
    public void SeekCameraOffset_ThreeRotationsShouldCoverParallelWaveTracks()
    {
        var targets = Enumerable.Range(0, 3)
            .Select(rotationCount => AutoFightSeek.GetSeekCameraVerticalTargetOffset(900, rotationCount, retryCount: 0))
            .ToList();

        Assert.Equal(new[] { -720, 0, 720 }, targets);
    }

    [Fact]
    public void SeekCameraOffset_ParallelTracksShouldCoverTheFullVerticalStripAtEachPhase()
    {
        var targets = Enumerable.Range(0, 3)
            .Select(rotationCount => AutoFightSeek.GetSeekCameraVerticalTargetOffset(900, rotationCount, retryCount: 1))
            .ToList();

        Assert.Equal(new[] { -1080, -360, 360 }, targets);
        Assert.Equal(720, targets[1] - targets[0]);
        Assert.Equal(720, targets[2] - targets[1]);
    }

    [Fact]
    public void SeekCameraOffset_TargetOffsetShouldUseLargerVerticalClampForFarBands()
    {
        var upperTarget = AutoFightSeek.GetSeekCameraVerticalTargetOffset(3000, rotationCount: 2, retryCount: 3);
        var lowerTarget = AutoFightSeek.GetSeekCameraVerticalTargetOffset(3000, rotationCount: 0, retryCount: 1);

        Assert.Equal(3200, upperTarget);
        Assert.Equal(-3200, lowerTarget);
        Assert.True(Math.Abs(upperTarget) <= 3200, "upper seek target should stay within the current vertical clamp");
        Assert.True(Math.Abs(lowerTarget) <= 3200, "lower seek target should stay within the current vertical clamp");
    }

    [Fact]
    public void SeekCameraOffset_SecondTrackSetShouldUseOppositeWavePhase()
    {
        var firstSet = AutoFightSeek.GetSeekCameraVerticalTargetOffset(900, rotationCount: 0, retryCount: 1);
        var secondSet = AutoFightSeek.GetSeekCameraVerticalTargetOffset(900, rotationCount: 3, retryCount: 1);

        Assert.Equal(-1080, firstSet);
        Assert.Equal(-360, secondSet);
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(1, 0, false)]
    [InlineData(3, 0, true)]
    [InlineData(6, 0, true)]
    [InlineData(3, 1, false)]
    public void ShouldResetCameraBeforeSeek_ShouldRecoverAbnormalViewEveryThirdFailedRotation(int rotationCount, int retryCount, bool expected)
    {
        Assert.Equal(expected, AutoFightSeek.ShouldResetCameraBeforeSeek(rotationCount, retryCount));
    }

    [Fact]
    public void Recenter_ShouldRunBeforeSeekScan()
    {
        Assert.True(AutoFightSeek.ShouldRecenterCameraBeforeSeek());
    }

    [Fact]
    public void SelectSeekDecision_ThinHealthBarMustApproachEvenWhenRedFillIsWide()
    {
        var decision = AutoFightSeek.SelectSeekDecision(
            [new EnemySeekVisual(590, 300, 320, 5, 1600)],
            imageWidth: 1500,
            imageHeight: 900);

        Assert.Equal(AutoFightSeekAction.ApproachVisibleEnemy, decision.Action);
    }

    [Theory]
    [InlineData(30, 12, false)]
    [InlineData(320, 12, false)]
    [InlineData(30, 5, true)]
    [InlineData(320, 5, true)]
    public void ShouldApproachVisibleEnemy_MustUseHeightInsteadOfRemainingRedWidth(
        int width,
        int height,
        bool expected)
    {
        var healthBar = new EnemySeekVisual(800, 400, width, height, width * height);

        Assert.Equal(expected, AutoFightSeek.ShouldApproachVisibleEnemy(
            healthBar,
            imageWidth: 1920,
            imageHeight: 1080));
    }

    [Theory]
    [InlineData(12, 430, 24, 20, (int)EnemyIndicatorDirection.Left)]
    [InlineData(1460, 430, 24, 20, (int)EnemyIndicatorDirection.Right)]
    [InlineData(744, 24, 24, 20, (int)EnemyIndicatorDirection.Forward)]
    [InlineData(744, 860, 24, 20, (int)EnemyIndicatorDirection.Behind)]
    public void SelectSeekDecision_RedDirectionIndicatorMustDriveApproachDirection(
        int x,
        int y,
        int width,
        int height,
        int expectedDirection)
    {
        var decision = AutoFightSeek.SelectSeekDecision(
            [
                new EnemySeekVisual(x, y, width, height, 260)
            ],
            imageWidth: 1500,
            imageHeight: 900);

        Assert.Equal(AutoFightSeekAction.Approach, decision.Action);
        Assert.Equal((EnemyIndicatorDirection)expectedDirection, decision.Direction);
    }

    [Fact]
    public void SelectSeekDecision_VisibleHealthBarMustTakePriorityOverOtherEnemyArrows()
    {
        var decision = AutoFightSeek.SelectSeekDecision(
            [
                new EnemySeekVisual(590, 300, 320, 5, 1600),
                new EnemySeekVisual(12, 430, 24, 20, 260, -90)
            ],
            imageWidth: 1500,
            imageHeight: 900);

        Assert.Equal(AutoFightSeekAction.ApproachVisibleEnemy, decision.Action);
    }

    [Fact]
    public void SelectSeekDecision_MultipleArrowsMustChooseTheSmallestTurnAndBoundOneStep()
    {
        var decision = AutoFightSeek.SelectSeekDecision(
            [
                new EnemySeekVisual(12, 430, 24, 20, 260, -145),
                new EnemySeekVisual(744, 24, 24, 20, 260, 24),
                new EnemySeekVisual(1460, 430, 24, 20, 260, 92)
            ],
            imageWidth: 1500,
            imageHeight: 900);

        Assert.Equal(AutoFightSeekAction.Approach, decision.Action);
        Assert.Equal(24, decision.Visual!.Value.IndicatorBearingDegrees);
        Assert.Equal(3, decision.SignalCount);
        Assert.InRange(Math.Abs(AutoFightSeek.GetIndicatorCameraOffset(
            decision.Direction,
            decision.Visual.Value,
            1500,
            900)), 1, 640);
    }

    [Fact]
    public void SelectSeekDecision_LockedRouteMustIgnoreEveryOtherArrow()
    {
        var decision = AutoFightSeek.SelectSeekDecision(
            [
                new EnemySeekVisual(12, 430, 24, 20, 260, -145),
                new EnemySeekVisual(744, 24, 24, 20, 260, 24),
                new EnemySeekVisual(1460, 430, 24, 20, 260, 92)
            ],
            imageWidth: 1500,
            imageHeight: 900,
            indicatorRouteLocked: true);

        Assert.Equal(AutoFightSeekAction.ContinueLockedRoute, decision.Action);
        Assert.Equal(EnemyIndicatorDirection.None, decision.Direction);
        Assert.Null(decision.Visual);
    }

    [Fact]
    public void SelectSeekDecision_LockedRouteMustStillYieldToAVisibleHealthBar()
    {
        var decision = AutoFightSeek.SelectSeekDecision(
            [
                new EnemySeekVisual(720, 300, 60, 5, 300),
                new EnemySeekVisual(12, 430, 24, 20, 260, -145),
                new EnemySeekVisual(1460, 430, 24, 20, 260, 92)
            ],
            imageWidth: 1500,
            imageHeight: 900,
            indicatorRouteLocked: true);

        Assert.Equal(AutoFightSeekAction.ApproachVisibleEnemy, decision.Action);
        Assert.NotNull(decision.Visual);
        Assert.Equal(60, decision.Visual!.Value.Width);
    }

    [Fact]
    public void IndicatorCameraSteps_MustCoverTheWholeBearingWithoutAnUnboundedMouseJump()
    {
        var visual = new EnemySeekVisual(12, 430, 24, 20, 260, -145);

        var steps = AutoFightSeek.GetIndicatorCameraSteps(visual, 1500, 900);

        Assert.Equal((int)Math.Round(-145d / 180d * 1920), steps.Sum());
        Assert.All(steps, step => Assert.InRange(Math.Abs(step), 1, 640));
    }

    [Fact]
    public void SelectSeekDecision_CentralRedNoiseMustNotTriggerBlindForwardMovement()
    {
        var decision = AutoFightSeek.SelectSeekDecision(
            [new EnemySeekVisual(800, 420, 2, 2, 4)],
            imageWidth: 1500,
            imageHeight: 900);

        Assert.Equal(AutoFightSeekAction.Scan, decision.Action);
        Assert.Equal(EnemyIndicatorDirection.None, decision.Direction);
    }

    [Fact]
    public void SelectSeekDecision_FixedTopEliteHealthBarMustAdvanceOnceThenKeepFighting()
    {
        var fixedTopHealthBar = new EnemySeekVisual(969, 125, 251, 11, 2600);

        var approaching = AutoFightSeek.SelectSeekDecision(
            [fixedTopHealthBar],
            imageWidth: 1920,
            imageHeight: 1080,
            indicatorRouteLocked: true,
            fixedTopHealthTracked: true,
            fixedTopHealthAdvanceCompleted: false);
        var outputPointReady = AutoFightSeek.SelectSeekDecision(
            [fixedTopHealthBar],
            imageWidth: 1920,
            imageHeight: 1080,
            indicatorRouteLocked: true,
            fixedTopHealthTracked: true,
            fixedTopHealthAdvanceCompleted: true);
        var exhausted = AutoFightSeek.SelectSeekDecision(
            [fixedTopHealthBar],
            imageWidth: 1920,
            imageHeight: 1080,
            indicatorRouteLocked: true,
            fixedTopHealthTracked: true,
            fixedTopHealthAdvanceCompleted: true,
            fixedTopHealthExhausted: true);

        Assert.Equal(AutoFightSeekAction.ApproachFixedTopHealthTarget, approaching.Action);
        Assert.Equal(EnemyIndicatorDirection.None, approaching.Direction);
        Assert.Equal(AutoFightSeekAction.KeepFighting, outputPointReady.Action);
        Assert.Equal(AutoFightSeekAction.Scan, exhausted.Action);
    }

    [Fact]
    public void SelectSeekDecision_FixedTopEliteHealthBarMustBeatHudLikeShortBarsAndArrows()
    {
        var decision = AutoFightSeek.SelectSeekDecision(
            [
                new EnemySeekVisual(969, 125, 251, 11, 2600),
                new EnemySeekVisual(928, 386, 25, 8, 170),
                new EnemySeekVisual(1750, 591, 24, 20, 260, -2)
            ],
            imageWidth: 1920,
            imageHeight: 1080,
            indicatorRouteLocked: true,
            fixedTopHealthTracked: true,
            fixedTopHealthAdvanceCompleted: false);

        Assert.Equal(AutoFightSeekAction.ApproachFixedTopHealthTarget, decision.Action);
        Assert.Equal(251, decision.Visual!.Value.Width);
    }

    [Fact]
    public void FixedTopEliteHealthBar_PhaseLockMustResetAfterThreeMissingFrames()
    {
        AutoFightSeek.ResetSeekState();
        try
        {
            var healthBar = new EnemySeekVisual(969, 125, 251, 11, 2600);
            var now = new DateTime(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);
            Assert.Equal((true, false, false), AutoFightSeek.ObserveFixedTopHealthPresence(healthBar, now));
            AutoFightSeek.MarkFixedTopHealthAdvanceCompleted(now);
            Assert.Equal((true, true, false), AutoFightSeek.ObserveFixedTopHealthPresence(null, now));
            Assert.Equal((true, true, false), AutoFightSeek.ObserveFixedTopHealthPresence(null, now));
            Assert.Equal((false, false, false), AutoFightSeek.ObserveFixedTopHealthPresence(null, now));
        }
        finally
        {
            AutoFightSeek.ResetSeekState();
        }
    }

    [Fact]
    public void FixedTopEliteHealthBar_NoProgressMustAllowOneSecondNudgesButRemainBounded()
    {
        AutoFightSeek.ResetSeekState();
        try
        {
            var healthBar = new EnemySeekVisual(969, 125, 251, 11, 2600);
            var started = new DateTime(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);
            Assert.Equal((true, false, false), AutoFightSeek.ObserveFixedTopHealthPresence(healthBar, started));
            for (var i = 0; i < 6; i++)
            {
                var nudgeCompleted = started.AddSeconds(i * 11);
                AutoFightSeek.MarkFixedTopHealthAdvanceCompleted(nudgeCompleted);
                var state = AutoFightSeek.ObserveFixedTopHealthPresence(
                    healthBar,
                    nudgeCompleted.AddSeconds(11));
                if (i < 5)
                {
                    Assert.Equal((true, false, false), state);
                }
                else
                {
                    Assert.Equal((true, true, true), state);
                }
            }
            Assert.Equal(6, AutoFightSeek.GetFixedTopHealthAdvanceCount());
        }
        finally
        {
            AutoFightSeek.ResetSeekState();
        }
    }

    [Fact]
    public void ClassifySeekVisual_MustPreserveHealthBarsButRejectSmallHudNoise()
    {
        using var mask = Mat.Zeros(1, 1, MatType.CV_8UC1).ToMat();
        var fixedTopHealthBar = new EnemySeekVisual(969, 125, 251, 11, 2600);

        Assert.Equal(
            fixedTopHealthBar,
            AutoFightSeek.ClassifySeekVisual(mask, fixedTopHealthBar, 1920, 1080));
        Assert.Null(AutoFightSeek.ClassifySeekVisual(
            mask,
            new EnemySeekVisual(1820, 795, 9, 14, 80),
            1920,
            1080));
    }

    [Theory]
    [InlineData(971, 448, 18, 2)]
    [InlineData(731, 523, 30, 2)]
    [InlineData(1084, 943, 18, 2)]
    public void ClassifySeekVisual_RuntimeTwoPixelRedFragmentsMustBeRejected(
        int x,
        int y,
        int width,
        int height)
    {
        using var mask = Mat.Zeros(1, 1, MatType.CV_8UC1).ToMat();

        Assert.Null(AutoFightSeek.ClassifySeekVisual(
            mask,
            new EnemySeekVisual(x, y, width, height, width * height),
            1920,
            1080));
    }

    [Fact]
    public void SelectSeekDecision_LargerRuntimeHealthBarMustBeatTinyCentralFragment()
    {
        var tinyCentralFragment = new EnemySeekVisual(951, 500, 18, 6, 108);
        var visibleEnemyHealthBar = new EnemySeekVisual(729, 300, 145, 11, 1595);

        var decision = AutoFightSeek.SelectSeekDecision(
            [tinyCentralFragment, visibleEnemyHealthBar],
            imageWidth: 1920,
            imageHeight: 1080);

        Assert.Equal(visibleEnemyHealthBar, decision.Visual);
        Assert.Equal(AutoFightSeekAction.KeepFighting, decision.Action);
    }

    [Fact]
    public void VisibleHealthApproachPolicy_MustBoundContinuousMovementUntilThreeMissingFrames()
    {
        var policy = new VisibleHealthApproachPolicy(
            maxApproachSteps: 4,
            missingFramesBeforeReset: 3);
        var healthBar = new EnemySeekVisual(729, 300, 145, 11, 1595);
        var approach = new EnemySeekDecision(
            AutoFightSeekAction.ApproachVisibleEnemy,
            EnemyIndicatorDirection.Left,
            healthBar,
            1);

        for (var step = 0; step < 4; step++)
        {
            Assert.Equal(
                AutoFightSeekAction.ApproachVisibleEnemy,
                policy.Evaluate(approach, 1920, 1080).Action);
            policy.RecordApproachStep(0, 0);
        }

        Assert.Equal(AutoFightSeekAction.Scan, policy.Evaluate(approach, 1920, 1080).Action);

        var missing = new EnemySeekDecision(AutoFightSeekAction.Scan, EnemyIndicatorDirection.None);
        Assert.Equal(AutoFightSeekAction.Scan, policy.Evaluate(missing, 1920, 1080).Action);
        Assert.Equal(AutoFightSeekAction.Scan, policy.Evaluate(missing, 1920, 1080).Action);
        Assert.Equal(AutoFightSeekAction.Scan, policy.Evaluate(missing, 1920, 1080).Action);
        Assert.Equal(
            AutoFightSeekAction.ApproachVisibleEnemy,
            policy.Evaluate(approach, 1920, 1080).Action);
    }

    [Fact]
    public void VisibleHealthApproachPolicy_NewTargetResetsAnExhaustedBudgetWithoutMissingFrames()
    {
        var policy = new VisibleHealthApproachPolicy(
            maxApproachSteps: 4,
            missingFramesBeforeReset: 3);
        var firstTarget = new EnemySeekDecision(
            AutoFightSeekAction.ApproachVisibleEnemy,
            EnemyIndicatorDirection.Left,
            new EnemySeekVisual(700, 300, 145, 11, 1595),
            1);
        var nextTarget = new EnemySeekDecision(
            AutoFightSeekAction.ApproachVisibleEnemy,
            EnemyIndicatorDirection.Right,
            new EnemySeekVisual(1500, 300, 145, 11, 1595),
            1);

        for (var step = 0; step < 4; step++)
        {
            Assert.Equal(
                AutoFightSeekAction.ApproachVisibleEnemy,
                policy.Evaluate(firstTarget, 1920, 1080).Action);
            var visual = firstTarget.Visual!.Value;
            policy.RecordApproachStep(
                AutoFightSeek.GetVisibleEnemyCameraOffset(visual, 1920),
                AutoFightSeek.GetVisibleEnemyCameraVerticalOffset(visual, 1080));
        }

        Assert.Equal(
            AutoFightSeekAction.ApproachVisibleEnemy,
            policy.Evaluate(nextTarget, 1920, 1080).Action);
    }

    [Fact]
    public void VisibleHealthApproachPolicy_UnknownCameraResetCannotRefillAnExhaustedBudget()
    {
        var policy = new VisibleHealthApproachPolicy(
            maxApproachSteps: 4,
            missingFramesBeforeReset: 3);
        var target = new EnemySeekDecision(
            AutoFightSeekAction.ApproachVisibleEnemy,
            EnemyIndicatorDirection.Right,
            new EnemySeekVisual(1450, 300, 145, 11, 1595),
            1);

        for (var step = 0; step < 4; step++)
        {
            Assert.Equal(
                AutoFightSeekAction.ApproachVisibleEnemy,
                policy.Evaluate(target, 1920, 1080).Action);
            policy.RecordApproachStep(0, 0);
        }

        policy.PreserveBudgetAcrossUnknownCameraMovement();
        var afterCameraReset = target with
        {
            Visual = new EnemySeekVisual(720, 460, 145, 11, 1595)
        };
        Assert.Equal(
            AutoFightSeekAction.Scan,
            policy.Evaluate(afterCameraReset, 1920, 1080).Action);
    }

    [Fact]
    public void VisibleHealthTargetConsistency_RuntimeJumpsMustNotSwitchTargets()
    {
        var nearCenter = new EnemySeekVisual(1088, 502, 18, 6, 108);
        var jumpsFartherRight = new EnemySeekVisual(1434, 385, 39, 11, 429);
        var farRight = new EnemySeekVisual(1434, 385, 39, 11, 429);
        var crossesTheScreen = new EnemySeekVisual(731, 523, 30, 6, 180);
        var turnsTowardCenter = new EnemySeekVisual(1100, 430, 50, 11, 550);

        Assert.False(AutoFightSeek.IsVisibleHealthTargetConsistent(
            nearCenter, jumpsFartherRight, 1920, 1080));
        Assert.False(AutoFightSeek.IsVisibleHealthTargetConsistent(
            farRight, crossesTheScreen, 1920, 1080));
        Assert.True(AutoFightSeek.IsVisibleHealthTargetConsistent(
            farRight,
            crossesTheScreen,
            1920,
            1080,
            cameraHorizontalOffset: AutoFightSeek.GetVisibleEnemyCameraOffset(farRight, 1920),
            cameraVerticalOffset: AutoFightSeek.GetVisibleEnemyCameraVerticalOffset(farRight, 1080)));
        Assert.True(AutoFightSeek.IsVisibleHealthTargetConsistent(
            farRight, turnsTowardCenter, 1920, 1080));
    }

    [Fact]
    public void SelectSeekDecision_WideTopHealthBarMustUseTheOneSecondOutputPointPolicy()
    {
        var decision = AutoFightSeek.SelectSeekDecision(
            [new EnemySeekVisual(757, 82, 406, 9, 3654)],
            imageWidth: 1920,
            imageHeight: 1080);

        Assert.Equal(AutoFightSeekAction.ApproachFixedTopHealthTarget, decision.Action);
        Assert.Equal(EnemyIndicatorDirection.None, decision.Direction);
    }

    [Fact]
    public void SelectSeekDecision_720pThreePixelHealthBarRemainsDetectable()
    {
        var decision = AutoFightSeek.SelectSeekDecision(
            [new EnemySeekVisual(490, 250, 120, 3, 360)],
            imageWidth: 1280,
            imageHeight: 720);

        Assert.Equal(AutoFightSeekAction.ApproachVisibleEnemy, decision.Action);
    }

    [Fact]
    public void FightFinishCheck_MustUseTheUnifiedArrowAndHealthBarApproachPipeline()
    {
        var source = File.ReadAllText(SourcePath(
            "BetterGenshinImpact", "GameTask", "AutoFight", "AutoFightTask.cs"));
        var section = SourceSection(
            source,
            "public static async Task<bool> CheckFightFinish",
            "private static Dictionary<string, double> ParseStringToDictionary");

        Assert.Contains("AutoFightSeek.DetectAndApproachEnemyAsync", section, StringComparison.Ordinal);
        Assert.DoesNotContain("AvatarRecognition.FindBloodBars", section, StringComparison.Ordinal);
        Assert.DoesNotContain("MoveForwardTask.MoveForwardAsync", section, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(744, 10, 0)]
    [InlineData(1480, 440, 90)]
    [InlineData(744, 880, 180)]
    [InlineData(10, 440, -90)]
    public void IndicatorBearing_MustMapTheFullScreenPositionToAContinuousAngle(
        int x,
        int y,
        double expectedDegrees)
    {
        var bearing = AutoFightSeek.GetIndicatorBearingDegrees(
            new EnemySeekVisual(x, y, 12, 12, 80),
            imageWidth: 1500,
            imageHeight: 900);

        Assert.InRange(bearing, expectedDegrees - 2, expectedDegrees + 2);
    }

    [Fact]
    public void DirectionIndicatorFeature_MustRemainStableAfterArbitraryRotation()
    {
        var path = SourcePath(
            "BetterGenshinImpact", "GameTask", "AutoFight", "Assets", "1920x1080",
            "enemy_direction_indicator_variant_03.png");
        using var template = Cv2.ImRead(path, ImreadModes.Unchanged);
        using var alpha = new Mat();
        Cv2.ExtractChannel(template, alpha, 3);
        var templateContour = AutoFightSeek.GetLargestExternalContour(alpha);
        Assert.NotNull(templateContour);

        using var canvas = Mat.Zeros(64, 64, MatType.CV_8UC1).ToMat();
        var targetRect = new Rect(
            (canvas.Width - alpha.Width) / 2,
            (canvas.Height - alpha.Height) / 2,
            alpha.Width,
            alpha.Height);
        using (var target = new Mat(canvas, targetRect))
        {
            alpha.CopyTo(target);
        }

        using var transform = Cv2.GetRotationMatrix2D(new Point2f(32, 32), 47, 1);
        using var rotated = new Mat();
        Cv2.WarpAffine(canvas, rotated, transform, canvas.Size(), InterpolationFlags.Nearest);

        Assert.True(AutoFightSeek.MatchesDirectionIndicatorFeature(
            rotated,
            [templateContour!],
            threshold: 0.27));

        var rotatedContour = AutoFightSeek.GetLargestExternalContour(rotated);
        Assert.NotNull(rotatedContour);
        var bounds = Cv2.BoundingRect(rotatedContour!);
        using var rotatedCrop = new Mat(rotated, bounds);
        Assert.True(AutoFightSeek.IsDirectionIndicatorGeometry(
            new EnemySeekVisual(
                1820,
                795,
                bounds.Width,
                bounds.Height,
                Cv2.CountNonZero(rotatedCrop)),
            imageWidth: 1920,
            imageHeight: 1080));
    }

    [Theory]
    [InlineData(9, 14, 80)]
    [InlineData(15, 15, 180)]
    [InlineData(17, 17, 210)]
    [InlineData(18, 21, 250)]
    public void DirectionIndicatorGeometry_MustRejectSmallHudGhostsFromLiveCombat(
        int width,
        int height,
        int area)
    {
        Assert.False(AutoFightSeek.IsDirectionIndicatorGeometry(
            new EnemySeekVisual(1820, 795, width, height, area),
            imageWidth: 1920,
            imageHeight: 1080));
    }

    [Fact]
    public void DirectionIndicatorFeature_MustRequireTheInternalLightHollow()
    {
        using var template = LoadDirectionIndicatorAlpha("enemy_direction_indicator_variant_03.png");
        var templateContour = AutoFightSeek.GetLargestExternalContour(template);
        Assert.NotNull(templateContour);
        Assert.InRange(AutoFightSeek.GetDirectionIndicatorHollowRatio(template)!.Value, 0.002, 0.25);

        using var solidGhost = template.Clone();
        using var working = template.Clone();
        Cv2.FindContours(
            working,
            out Point[][] contours,
            out HierarchyIndex[] hierarchy,
            RetrievalModes.Tree,
            ContourApproximationModes.ApproxSimple);
        for (var i = 0; i < contours.Length; i++)
        {
            if (hierarchy[i].Parent >= 0)
            {
                Cv2.DrawContours(solidGhost, contours, i, Scalar.White, -1);
            }
        }

        Assert.Null(AutoFightSeek.GetDirectionIndicatorHollowRatio(solidGhost));
        Assert.False(AutoFightSeek.MatchesDirectionIndicatorFeature(
            solidGhost,
            [templateContour!],
            threshold: 0.27));
    }

    [Fact]
    public void DirectionIndicatorConfirmation_MustRejectAJumpingGhostAcrossTwoFrames()
    {
        var first = new EnemySeekVisual(120, 420, 30, 24, 360, -60);
        var stable = new EnemySeekVisual(128, 425, 31, 24, 370, -56);
        var jumpingGhost = new EnemySeekVisual(310, 180, 30, 24, 360, 35);

        Assert.True(AutoFightSeek.AreDirectionIndicatorsStable(first, stable, 1920, 1080));
        Assert.False(AutoFightSeek.AreDirectionIndicatorsStable(first, jumpingGhost, 1920, 1080));
    }

    [Fact]
    public void DirectionIndicatorFeature_ConvexHudNotificationDotMustBeRejected()
    {
        using var template = LoadDirectionIndicatorAlpha("enemy_direction_indicator_variant_03.png");
        var templateContour = AutoFightSeek.GetLargestExternalContour(template);
        Assert.NotNull(templateContour);

        using var notificationDot = Mat.Zeros(32, 32, MatType.CV_8UC1).ToMat();
        Cv2.Circle(notificationDot, new Point(16, 16), 10, Scalar.White, -1);

        Assert.False(AutoFightSeek.MatchesDirectionIndicatorFeature(
            notificationDot,
            [templateContour!],
            threshold: 0.30));
    }

    [Fact]
    public void DirectionIndicatorFeature_ConvexTriangleMustBeRejectedWithoutTheArrowNotch()
    {
        using var template = LoadDirectionIndicatorAlpha("enemy_direction_indicator_variant_03.png");
        var templateContour = AutoFightSeek.GetLargestExternalContour(template);
        Assert.NotNull(templateContour);

        using var convexTriangle = Mat.Zeros(32, 32, MatType.CV_8UC1).ToMat();
        Cv2.FillConvexPoly(
            convexTriangle,
            [new Point(16, 3), new Point(29, 27), new Point(3, 27)],
            Scalar.White);

        Assert.False(AutoFightSeek.MatchesDirectionIndicatorFeature(
            convexTriangle,
            [templateContour!],
            threshold: 0.30));
    }

    [Fact]
    public void DirectionIndicatorConcavity_UserArrowMustPassButConvexHudShapeMustFail()
    {
        using var template = LoadDirectionIndicatorAlpha("enemy_direction_indicator_variant_03.png");
        var arrowContour = AutoFightSeek.GetLargestExternalContour(template);
        Assert.NotNull(arrowContour);

        using var notificationDot = Mat.Zeros(32, 32, MatType.CV_8UC1).ToMat();
        Cv2.Circle(notificationDot, new Point(16, 16), 10, Scalar.White, -1);
        var dotContour = AutoFightSeek.GetLargestExternalContour(notificationDot);
        Assert.NotNull(dotContour);

        Assert.True(AutoFightSeek.HasDirectionIndicatorConcavity(arrowContour!));
        Assert.False(AutoFightSeek.HasDirectionIndicatorConcavity(dotContour!));
    }

    [Fact]
    public void DirectionIndicatorOrientation_MustUseTheArrowTipInsteadOfItsScreenPosition()
    {
        using var left = LoadDirectionIndicatorAlpha("enemy_direction_indicator_left.png");
        using var right = LoadDirectionIndicatorAlpha("enemy_direction_indicator_variant_06.png");

        var leftBearing = AutoFightSeek.GetDirectionIndicatorOrientationDegrees(left);
        var rightBearing = AutoFightSeek.GetDirectionIndicatorOrientationDegrees(right);

        Assert.NotNull(leftBearing);
        Assert.NotNull(rightBearing);
        Assert.InRange(leftBearing!.Value, -135, -45);
        Assert.InRange(rightBearing!.Value, 45, 135);
    }

    [Theory]
    [InlineData(720, 300, 60, 5, (int)AutoFightSeekAction.ApproachVisibleEnemy)]
    [InlineData(80, 300, 180, 5, (int)AutoFightSeekAction.ApproachVisibleEnemy)]
    [InlineData(720, 40, 180, 5, (int)AutoFightSeekAction.ApproachFixedTopHealthTarget)]
    public void SelectSeekDecision_DistantOrOffCenterHealthBarMustDriveApproach(
        int x,
        int y,
        int width,
        int height,
        int expectedAction)
    {
        var decision = AutoFightSeek.SelectSeekDecision(
            [new EnemySeekVisual(x, y, width, height, width * height)],
            imageWidth: 1500,
            imageHeight: 900);

        Assert.Equal((AutoFightSeekAction)expectedAction, decision.Action);
    }

    [Fact]
    public void VisibleEnemyCameraOffsets_MustTurnTowardTheHealthBar()
    {
        var healthBar = new EnemySeekVisual(100, 40, 60, 5, 240);

        Assert.True(AutoFightSeek.GetVisibleEnemyCameraOffset(healthBar, 1920) < 0);
        Assert.True(AutoFightSeek.GetVisibleEnemyCameraVerticalOffset(healthBar, 1080) < 0);
    }

    [Fact]
    public void SeekDetectionRegion_ShouldUseTheWholeCaptureInsteadOfA1500By900TopLeftCrop()
    {
        var region = AutoFightSeek.GetSeekDetectionRegion(imageWidth: 1920, imageHeight: 1080);

        Assert.Equal(0, region.X);
        Assert.Equal(0, region.Y);
        Assert.Equal(1920, region.Width);
        Assert.Equal(1080, region.Height);
    }

    [Theory]
    [InlineData(1700, 260, 120, 6, true)]
    [InlineData(800, 1000, 320, 8, true)]
    [InlineData(900, 320, 180, 6, false)]
    public void PlayerHudHealthBars_AreExcludedWithoutCroppingEdgeIndicators(
        int x,
        int y,
        int width,
        int height,
        bool expected)
    {
        var visual = new EnemySeekVisual(x, y, width, height, width * height);

        Assert.Equal(
            expected,
            AutoFightSeek.IsPlayerHudHealthBar(visual, 1920, 1080));
    }

    [Theory]
    [InlineData((int)AutoFightSeekAction.Approach, 0, false)]
    [InlineData((int)AutoFightSeekAction.ApproachVisibleEnemy, 5, false)]
    [InlineData((int)AutoFightSeekAction.ContinueLockedRoute, 5, true)]
    [InlineData((int)AutoFightSeekAction.ContinueLockedRoute, 6, false)]
    [InlineData((int)AutoFightSeekAction.KeepFighting, 0, false)]
    [InlineData((int)AutoFightSeekAction.Scan, 0, false)]
    public void ShouldContinueLockedRouteSegment_MustRunForSixSecondsAtATime(
        int action,
        int completedSteps,
        bool expected)
    {
        var decision = new EnemySeekDecision((AutoFightSeekAction)action, EnemyIndicatorDirection.Forward);

        Assert.Equal(expected, AutoFightSeek.ShouldContinueLockedRouteSegment(decision, completedSteps));
    }

    [Fact]
    public void SelectSeekDecision_Recent1920CaptureArrowMustUseFullFrameCenter()
    {
        var decision = AutoFightSeek.SelectSeekDecision(
            [new EnemySeekVisual(722, 83, 24, 20, 260)],
            imageWidth: 1920,
            imageHeight: 1080);

        Assert.Equal(AutoFightSeekAction.Approach, decision.Action);
        Assert.Equal(EnemyIndicatorDirection.Left, decision.Direction);
    }

    [Fact]
    public void SelectSeekDecision_UserArrowBoundsMustBeRecognizedAsAnIndicator()
    {
        var decision = AutoFightSeek.SelectSeekDecision(
            [new EnemySeekVisual(7, 430, 34, 31, 518)],
            imageWidth: 1920,
            imageHeight: 1080);

        Assert.Equal(AutoFightSeekAction.Approach, decision.Action);
        Assert.Equal(EnemyIndicatorDirection.Left, decision.Direction);
    }

    [Fact]
    public void CreateSeekColorMask_MustIncludeTheObservedPinkRedArrowColor()
    {
        using var source = new Mat(1, 1, MatType.CV_8UC3, new Scalar(94, 74, 247));
        using var mask = AutoFightSeek.CreateSeekColorMask(source, new Scalar(255, 90, 90), null);

        Assert.Equal(255, mask.At<byte>(0, 0));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(5, true)]
    [InlineData(6, false)]
    public void LockedRouteApproach_MustRemainBoundedToSixSecondsBeforeReselection(
        int completedSteps,
        bool expected)
    {
        var decision = new EnemySeekDecision(AutoFightSeekAction.ContinueLockedRoute, EnemyIndicatorDirection.None);

        Assert.Equal(expected, AutoFightSeek.ShouldContinueLockedRouteSegment(decision, completedSteps));
    }

    [Fact]
    public void LockedRouteVerticalSweep_MustReturnToTheOriginalPitchAfterSixSeconds()
    {
        var total = Enumerable.Range(0, 6).Sum(AutoFightSeek.GetLockedRouteVerticalStep);

        Assert.Equal(0, total);
    }

    [Theory]
    [InlineData(10, -240)]
    [InlineData(450, 0)]
    [InlineData(850, 240)]
    public void IndicatorVerticalOffset_MustFollowTopAndBottomArrowPositions(int y, int expected)
    {
        var visual = new EnemySeekVisual(20, y, 20, 20, 200);

        Assert.Equal(expected, AutoFightSeek.GetIndicatorCameraVerticalOffset(visual, 900));
    }

    [Theory]
    [InlineData(0, null, 1)]
    [InlineData(2, null, 3)]
    [InlineData(2, false, 0)]
    [InlineData(2, true, 0)]
    public void GetNextRotationCount_ShouldAdvanceOnlyAfterMissingEnemy(
        int currentRotationCount,
        bool? seekResult,
        int expected)
    {
        Assert.Equal(expected, AutoFightSeek.GetNextRotationCount(currentRotationCount, seekResult));
    }

    [Theory]
    [InlineData("璃月路线.json", @"C:\path\锄地专区\精英400@汐\璃月路线.json", true)]
    [InlineData("400精英.json", @"C:\path\锄地专区\其他\400精英.json", true)]
    [InlineData("蕈兽.json", @"C:\path\敌人与魔物\蕈兽\蕈兽.json", false)]
    [InlineData("小怪2000@mno.json", @"C:\path\锄地专区\小怪2000@mno\路线.json", false)]
    public void Elite400PathingSource_ShouldDisableTimeTimeoutOnlyForMatchingPathingTasks(string fileName, string fullPath, bool expected)
    {
        Assert.Equal(expected, AutoFightHandler.IsElite400PathingSource(fileName, fullPath));
    }

    [Fact]
    public void ApplyElite400NoTimeoutSafety_ShouldKeepNonTimeExitProtectionEnabled()
    {
        var taskParams = (AutoFightParam)RuntimeHelpers.GetUninitializedObject(typeof(AutoFightParam));
        taskParams.FinishDetectConfig = new AutoFightParam.FightFinishDetectConfig();
        taskParams.Timeout = 200;
        taskParams.FightFinishDetectEnabled = false;
        taskParams.FinishDetectConfig.RotateFindEnemyEnabled = false;

        AutoFightHandler.ApplyElite400NoTimeoutSafety(taskParams);

        Assert.Equal(0, taskParams.Timeout);
        Assert.True(taskParams.FightFinishDetectEnabled);
        Assert.True(taskParams.FinishDetectConfig.RotateFindEnemyEnabled);
    }

    [Fact]
    public void ForcedStopPickupGuard_ShouldPrecedeAllPostFightPickupPaths()
    {
        AssertForcedStopGuardPrecedesPickupPaths(
            SourcePath("BetterGenshinImpact", "GameTask", "AutoFight", "AutoFightTask.cs"),
            hasPostFightPickupMethod: false);
        AssertForcedStopGuardPrecedesPickupPaths(
            SourcePath("BetterGenshinImpact", "GameTask", "AutoFight", "AutoFightJsonTask.cs"),
            hasPostFightPickupMethod: true);
    }

    [Fact]
    public void BuildFromJson_ShouldPreservePathingSourceForJsRunFile()
    {
        const string json = """
        {
          "info": {
            "name": "route",
            "map_match_method": "TemplateMatch"
          },
          "positions": []
        }
        """;
        var sourcePath = Path.Combine("C:", "repo", "pathing", "锄地专区", "精英400@汐", "璃月路线.json");
        _ = new ConfigService().Get();

        var task = PathingTask.BuildFromJson(json, sourcePath);

        Assert.Equal("璃月路线.json", task.FileName);
        Assert.Equal(sourcePath, task.FullPath);
        Assert.True(AutoFightHandler.IsElite400PathingSource(task.FileName, task.FullPath));
    }

    [Fact]
    public void FourHundredEliteScript_ShouldCheckpointEveryCompletedRouteWithoutDuplicatingRecords()
    {
        var path = SourcePath(
            "BetterGenshinImpact",
            "User",
            "JsScript",
            "FullyAutoAndSemiAutoTools",
            "main.js");
        var source = File.ReadAllText(path);
        var runList = SourceSection(source, "async function runList(", "async function runMap(");
        var successCheckpoint = SourceSection(runList, "Record.paths.add(path)", "路径列表执行完成");
        var failureCheckpoint = SourceSection(runList, "catch (error)", "Record.paths.add(path)");
        var saveRecordPaths = SourceSection(source, "async function saveRecordPaths()", "async function saveRecord()");
        var saveRecord = SourceSection(source, "async function saveRecord()", "function getTimeDifference(");

        Assert.Contains("RecordPath.paths.add(value)", successCheckpoint, StringComparison.Ordinal);
        Assert.Contains("await saveRecordPaths();", successCheckpoint, StringComparison.Ordinal);
        Assert.Contains("await saveRecord();", successCheckpoint, StringComparison.Ordinal);

        Assert.Contains("Record.errorPaths.add(path)", failureCheckpoint, StringComparison.Ordinal);
        Assert.Contains("await saveRecord();", failureCheckpoint, StringComparison.Ordinal);
        Assert.Contains("continue;", failureCheckpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("throw new Error", failureCheckpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("Record.paths.add(path)", failureCheckpoint, StringComparison.Ordinal);

        Assert.Contains("RecordPathList = RecordPathList.filter(item => item.uid !== Record.uid);", saveRecordPaths, StringComparison.Ordinal);
        Assert.DoesNotContain("temp.paths = [...recordToSave.paths, ...temp.paths]", saveRecordPaths, StringComparison.Ordinal);
        Assert.Contains("const otherRecords = RecordList.filter", saveRecord, StringComparison.Ordinal);
        Assert.Contains("RecordList = [...otherRecords, recordToSave];", saveRecord, StringComparison.Ordinal);
        Assert.DoesNotContain("RecordList.push(recordToSave)", saveRecord, StringComparison.Ordinal);

        Assert.Contains("let recordStateInitialized = false", source, StringComparison.Ordinal);
        Assert.Contains("recordStateInitialized = true", source, StringComparison.Ordinal);
        Assert.Contains("if (recordStateInitialized)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void FourHundredEliteScript_ShouldRethrowCancellationBeforeRecordingRouteFailure()
    {
        var path = SourcePath(
            "BetterGenshinImpact",
            "User",
            "JsScript",
            "FullyAutoAndSemiAutoTools",
            "main.js");
        var source = File.ReadAllText(path);
        var runList = SourceSection(source, "async function runList(", "async function runMap(");
        var failureCheckpoint = SourceSection(runList, "catch (error)", "Record.paths.add(path)");

        var cancellationCheckIndex = failureCheckpoint.IndexOf("if (pathingScript.isCancellationRequested)", StringComparison.Ordinal);
        var rethrowIndex = failureCheckpoint.IndexOf("throw error;", StringComparison.Ordinal);
        var failureRecordIndex = failureCheckpoint.IndexOf("Record.errorPaths.add(path)", StringComparison.Ordinal);

        Assert.NotNull(typeof(AutoPathingScript).GetProperty("IsCancellationRequested"));
        Assert.True(cancellationCheckIndex >= 0, "The route loop must check the host cancellation state.");
        Assert.True(rethrowIndex > cancellationCheckIndex, "Cancellation must rethrow the original error.");
        Assert.True(failureRecordIndex > rethrowIndex, "Cancellation must exit before recording a route failure.");
    }

    [Fact]
    public void ZhongXinNaWanStrategy_ShouldParseAndRefreshKokomiBeforeShield()
    {
        var path = SourcePath("BetterGenshinImpact", "User", "AutoFight", "00-钟心那万.txt");
        var text = File.ReadAllText(path);
        var lines = ReadScriptLines(path);

        var script = CombatScriptParser.Parse(path);

        Assert.Contains("珊瑚宫心海", script.AvatarNames);
        Assert.Contains("那维莱特", script.AvatarNames);
        Assert.DoesNotContain("click(middle)", text, StringComparison.OrdinalIgnoreCase);

        var kokomiSkill = lines.FindIndex(line => line.StartsWith("珊瑚宫心海 e", StringComparison.Ordinal));
        var firstNeuvilletteBeam = lines.FindIndex(line => line.StartsWith("那维莱特", StringComparison.Ordinal) && line.Contains(" e, ") && line.Contains("keydown(VK_LBUTTON)"));
        var kokomiBurst = lines.FindIndex(line => line.StartsWith("珊瑚宫心海 keypress(q)", StringComparison.Ordinal));
        var shieldAfterBurst = lines.FindIndex(kokomiBurst + 1, line => line.StartsWith("钟离 ", StringComparison.Ordinal));

        Assert.True(kokomiSkill >= 0, "missing Kokomi E line");
        Assert.True(firstNeuvilletteBeam >= 0, "missing first Neuvillette E beam line");
        Assert.True(kokomiBurst >= 0, "missing Kokomi Q refresh line");
        Assert.True(shieldAfterBurst >= 0, "missing Zhongli shield after Kokomi Q line");
        Assert.True(kokomiSkill < firstNeuvilletteBeam && firstNeuvilletteBeam < kokomiBurst && kokomiBurst < shieldAfterBurst,
            "expected Kokomi E -> Neuvillette E beam -> Kokomi Q -> Zhongli shield order");

        var neuvilletteBeamLines = lines
            .Where(line => line.StartsWith("那维莱特", StringComparison.Ordinal) && line.Contains("keydown(VK_LBUTTON)"))
            .ToList();

        Assert.Equal(3, neuvilletteBeamLines.Count);
        foreach (var line in neuvilletteBeamLines)
        {
            var keydownMatches = Regex.Matches(line, @"keydown\(VK_LBUTTON\)").Cast<Match>().ToList();
            var keyupMatches = Regex.Matches(line, @"keyup\(VK_LBUTTON\)").Cast<Match>().ToList();
            var firstMoveByIndex = line.IndexOf("moveby(", StringComparison.Ordinal);

            var keydownMatch = Assert.Single(keydownMatches);
            var keyupMatch = Assert.Single(keyupMatches);
            Assert.True(firstMoveByIndex > keydownMatch.Index, "expected Neuvillette to hold attack before camera sweep");
            Assert.True(
                keydownMatch.Index < firstMoveByIndex &&
                firstMoveByIndex < keyupMatch.Index,
                "expected Neuvillette to keep holding attack through the sweep");

            var preSweepSegment = line[keydownMatch.Index..firstMoveByIndex];
            var preSweepWaitSeconds = WaitRegex.Matches(preSweepSegment).Cast<Match>()
                .Select(match => double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
                .Sum();
            Assert.True(preSweepWaitSeconds >= 1.2, $"Neuvillette pre-sweep hold is too short: {preSweepWaitSeconds:F2}s");

            var beamSegment = line[keydownMatch.Index..keyupMatch.Index];
            var waitSeconds = WaitRegex.Matches(beamSegment).Cast<Match>()
                .Select(match => double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
                .Sum();

            Assert.True(waitSeconds >= 3.4, $"Neuvillette beam hold is too short: {waitSeconds:F2}s");
            Assert.Contains("moveby(1800, -1400)", beamSegment, StringComparison.Ordinal);
            Assert.Contains("moveby(1800, 1300)", beamSegment, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void FourHundredEliteScriptGroups_ShouldKeepGroupTimeoutScopedToNormalValue()
    {
        AssertScriptGroupTimeout("自动化一条龙.json", expectedAutoFightTimeout: 200, containsElite400: true);
        AssertScriptGroupTimeout("每天4点10-自动化总控.json", expectedAutoFightTimeout: 200, containsElite400: true);
        AssertScriptGroupTimeout("每周周常-AutoMonday.json", expectedAutoFightTimeout: 200, containsElite400: false);
        AssertScriptGroupTimeout("手动-配置自动化队伍.json", expectedAutoFightTimeout: 200, containsElite400: false);
        AssertScriptGroupTimeout("下午16点10-角色养成一条龙.json", expectedAutoFightTimeout: 200, containsElite400: false);
    }

    private static void AssertScriptGroupTimeout(string fileName, int expectedAutoFightTimeout, bool containsElite400)
    {
        var path = SourcePath("BetterGenshinImpact", "User", "ScriptGroup", fileName);
        var text = File.ReadAllText(path);
        using var document = JsonDocument.Parse(text);

        var pathingConfig = document.RootElement.GetProperty("config").GetProperty("pathingConfig");
        var autoFightTimeout = pathingConfig.GetProperty("autoFightConfig").GetProperty("timeout").GetInt32();
        var shellTimeout = document.RootElement.GetProperty("config").GetProperty("shellConfig").GetProperty("timeout").GetInt32();

        Assert.Equal(expectedAutoFightTimeout, autoFightTimeout);
        Assert.Equal(60, shellTimeout);
        Assert.Equal(containsElite400, text.Contains("精英400@汐", StringComparison.Ordinal));
    }

    private static List<string> ReadScriptLines(string path)
    {
        return File.ReadLines(path)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("//", StringComparison.Ordinal) && !line.StartsWith("#", StringComparison.Ordinal))
            .ToList();
    }

    private static Mat LoadDirectionIndicatorAlpha(string fileName)
    {
        var path = SourcePath(
            "BetterGenshinImpact", "GameTask", "AutoFight", "Assets", "1920x1080", fileName);
        using var image = Cv2.ImRead(path, ImreadModes.Unchanged);
        var alpha = new Mat();
        Cv2.ExtractChannel(image, alpha, 3);
        return alpha;
    }

    private static string SourceSection(string source, string startToken, string endToken)
    {
        var startIndex = source.IndexOf(startToken, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"missing source token: {startToken}");

        var endIndex = source.IndexOf(endToken, startIndex + startToken.Length, StringComparison.Ordinal);
        Assert.True(endIndex >= 0, $"missing source token after {startToken}: {endToken}");

        return source[startIndex..endIndex];
    }

    private static string SourcePath(params string[] parts)
    {
        return Path.Combine([FindRepoRoot(), .. parts]);
    }

    private static void AssertForcedStopGuardPrecedesPickupPaths(string path, bool hasPostFightPickupMethod)
    {
        var source = File.ReadAllText(path);
        var afterFightTaskIndex = source.IndexOf("await fightTask;", StringComparison.Ordinal);
        Assert.True(afterFightTaskIndex >= 0, $"{path} missing fightTask completion marker");

        var guardIndex = source.IndexOf("if (skipPostFightPickupFlag)", afterFightTaskIndex, StringComparison.Ordinal);
        Assert.True(guardIndex >= 0, $"{path} missing forced-stop pickup guard after fightTask");

        AssertTokenAfterGuard(source, "ExpBasedPickupEnabled", guardIndex, path);
        AssertTokenAfterGuard(source, "ScanPickTask", guardIndex, path);
        AssertTokenAfterGuard(source, "_taskParam.KazuhaPickupEnabled", guardIndex, path);

        if (!hasPostFightPickupMethod)
        {
            return;
        }

        var postFightPickupIndex = source.IndexOf("private async Task PostFightPickup", StringComparison.Ordinal);
        Assert.True(postFightPickupIndex >= 0, $"{path} missing PostFightPickup method");

        var postFightGuardIndex = source.IndexOf("if (skipPostFightPickupFlag)", postFightPickupIndex, StringComparison.Ordinal);
        var postFightKazuhaIndex = source.IndexOf("_taskParam.KazuhaPickupEnabled", postFightPickupIndex, StringComparison.Ordinal);
        Assert.True(postFightGuardIndex >= 0 && postFightGuardIndex < postFightKazuhaIndex,
            $"{path} PostFightPickup should check forced-stop before Kazuha/Jean pickup");
    }

    private static void AssertTokenAfterGuard(string source, string token, int guardIndex, string path)
    {
        var tokenIndex = source.IndexOf(token, guardIndex, StringComparison.Ordinal);
        Assert.True(tokenIndex >= 0, $"{path} missing pickup token {token}");
        Assert.True(guardIndex < tokenIndex, $"{path} forced-stop guard must precede {token}");
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BetterGenshinImpact.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate BetterGenshinImpact.sln from the test output directory.");
    }
}
