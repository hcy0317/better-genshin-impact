using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoBoss;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using Microsoft.Extensions.Time.Testing;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatSceneDistanceTests
{
    [Theory]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public void HealthFillNeedsABandRatherThanOnlyTheColoredEdge(int fillRows, bool expected)
    {
        using var pixels = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black);
        var visual = new EnemySeekVisual(800, 400, 60, 5, 300);
        Cv2.Rectangle(pixels, new Rect(800, 400, 60, 5), new Scalar(110, 155, 220), -1);
        Cv2.Rectangle(pixels, new Rect(800, 405 - fillRows, 60, fillRows), new Scalar(90, 90, 255), -1);
        using var mask = AutoFightSeek.CreateSeekColorMask(pixels, new Scalar(255, 90, 90), null);
        Assert.Equal(expected, AutoFightSeek.MatchesHealthBarFeature(mask, pixels, visual));
    }

    [Fact]
    public void ScatteredSalmonHighlightsCannotBorrowAnOrangeContoursHorizontalRun()
    {
        using var pixels = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black);
        var rect = new Rect(800, 400, 60, 6);
        Cv2.Rectangle(pixels, rect, new Scalar(110, 155, 220), -1);
        for (var x = rect.X; x < rect.Right; x += 10)
            Cv2.Rectangle(pixels, new Rect(x, rect.Y, 5, rect.Height), new Scalar(90, 90, 255), -1);
        using var mask = AutoFightSeek.CreateSeekColorMask(pixels, new Scalar(255, 90, 90), null);
        var visual = new EnemySeekVisual(rect.X, rect.Y, rect.Width, rect.Height, rect.Width * rect.Height);
        Assert.False(AutoFightSeek.MatchesHealthBarFeature(mask, pixels, visual));
        // A continuous real fill at the same size still satisfies the same horizontal-run rule.
        Cv2.Rectangle(pixels, rect, new Scalar(90, 90, 255), -1);
        using var healthMask = AutoFightSeek.CreateSeekColorMask(pixels, new Scalar(255, 90, 90), null);
        Assert.True(AutoFightSeek.MatchesHealthBarFeature(healthMask, pixels, visual));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("翠翎恐蕈")]
    [InlineData("半永恒统辖矩阵 ")]
    public void UnverifiedBossIdentityCannotBorrowTheMatrixHud(string boss)
    {
        Assert.Equal(CombatSceneCapabilities.Unknown, AutoBossTask.SceneForBoss(boss));
    }

    [Fact]
    public void MatrixTaskFactoryDeclaresOnlyObservedHudAndNoUnprovenLock()
    {
        var scene = AutoBossTask.SceneForBoss("半永恒统辖矩阵");
        Assert.NotNull(scene.FixedHealthSlot);
        Assert.False(scene.AllowTargetLock);
        Assert.False(scene.PreferFixedTarget);
        var decision = AutoFightSeek.SelectSeekDecision([new(721, 82, 478, 11, 5000)],
            1920, 1080, scene: scene);
        Assert.Equal(SeekCueKind.FixedTopHealth, decision.Cue);
        Assert.NotEqual(AutoFightSeekAction.ApproachVisibleEnemy, decision.Action);
    }

    [Theory]
    [InlineData(1080)]
    [InlineData(2160)]
    public void ThicknessHysteresisUsesActionDemandAndNeverRemainingRedWidth(int imageHeight)
    {
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        var battle = Guid.NewGuid();
        var melee = new CombatTargetDistance();
        var ranged = new CombatTargetDistance();
        CombatBattleObservation Frame(int thickness, int redWidth = 80)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            return new(source.Next(), battle, CombatObservationQuality.Available,
                new(AutoFightSeekAction.ApproachVisibleEnemy, EnemyIndicatorDirection.None,
                    new(920, 400, redWidth, thickness * imageHeight / 1080, redWidth * thickness)
                    { HealthBarTrackWidth = 80 }, 1, SeekCueKind.HealthBar), 1920, imageHeight);
        }
        var initial = Frame(4);
        Assert.True(melee.Observe(initial, CombatEngagementDemand.Melee, clock));
        Assert.False(ranged.Observe(initial, CombatEngagementDemand.Ranged, clock));
        Assert.True(melee.Observe(Frame(5), CombatEngagementDemand.Melee, clock));
        Assert.True(melee.Observe(Frame(6), CombatEngagementDemand.Melee, clock));
        Assert.True(melee.Observe(Frame(5), CombatEngagementDemand.Melee, clock));
        Assert.True(melee.Observe(Frame(6, 10), CombatEngagementDemand.Melee, clock));
        Assert.True(melee.Observe(Frame(6, 8), CombatEngagementDemand.Melee, clock));
        var close = Frame(6, 6);
        Assert.False(melee.Observe(close, CombatEngagementDemand.Melee, clock));
        Assert.Null(melee.Observe(close, CombatEngagementDemand.Melee, clock));
        Assert.False(melee.Observe(Frame(5), CombatEngagementDemand.Melee, clock));
        Assert.True(melee.Observe(Frame(4), CombatEngagementDemand.Melee, clock));
        var ambiguous = Frame(5);
        ambiguous = ambiguous with { Target = ambiguous.Target!.Value with { SignalCount = 2 } };
        Assert.Null(melee.Observe(ambiguous, CombatEngagementDemand.Melee, clock));
        Assert.True(melee.Observe(Frame(5), CombatEngagementDemand.Melee, clock));
    }

    [Theory]
    [InlineData(300)]
    [InlineData(30)]
    [InlineData(6)]
    public void DeclaredHudSlotRetainsIdentityAtLowHealthWithoutMovingTowardAdds(int redWidth)
    {
        var scene = new CombatSceneCapabilities(new(.34, .08, .66, .12), PreferFixedTarget: true);
        var bar = new EnemySeekVisual(700, 100, redWidth, 8, redWidth * 8);
        var add = new EnemySeekVisual(920, 400, 80, 4, 320);
        var selected = AutoFightSeek.SelectSeekDecision([bar, add], 1920, 1080, scene: scene);
        Assert.Equal(SeekCueKind.FixedTopHealth, selected.Cue);
        Assert.Equal(AutoFightSeekAction.KeepFighting, selected.Action);
        Assert.Equal(redWidth, selected.Visual!.Value.Width);
        var floating = AutoFightSeek.SelectSeekDecision([bar, add], 1920, 1080,
            scene: scene with { PreferFixedTarget = false });
        Assert.Equal(SeekCueKind.HealthBar, floating.Cue);
        Assert.Equal(add, floating.Visual);
        Assert.Equal(bar, floating.FixedTopHealth);
        using var pixels = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black);
        Cv2.Rectangle(pixels, new Rect(700, 100, redWidth, 8), new Scalar(90, 90, 255), -1);
        using var frame = new ImageRegion(pixels.Clone(), 0, 0) { FrameStamp = new CaptureFrameSource().Next() };
        var actual = AvatarRecognition.ReadPassiveTargetEvidence(frame, scene).Decision;
        Assert.Equal(SeekCueKind.FixedTopHealth, actual.Cue);
        Assert.Equal(redWidth, actual.Visual!.Value.Width);
        // The same pixels under an undeclared scene cannot reuse a declared interpretation from ReadOnce.
        Assert.NotEqual(SeekCueKind.FixedTopHealth, AvatarRecognition.ReadPassiveTargetEvidence(frame).Decision.Cue);
    }
}
