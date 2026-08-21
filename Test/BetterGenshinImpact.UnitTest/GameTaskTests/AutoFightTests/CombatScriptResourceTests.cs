using System.Runtime.CompilerServices;
using BetterGenshinImpact.GameTask.AutoPathing.Handler;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.Service;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatScriptResourceTests
{
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

    [Theory]
    [InlineData(0, 0, (int)AutoFightSeekAction.Scan)]
    [InlineData(600, 3, (int)AutoFightSeekAction.Approach)]
    [InlineData(600, 6, (int)AutoFightSeekAction.Approach)]
    [InlineData(600, 7, (int)AutoFightSeekAction.KeepFighting)]
    [InlineData(600, 24, (int)AutoFightSeekAction.KeepFighting)]
    [InlineData(758, 7, (int)AutoFightSeekAction.Scan)]
    [InlineData(722, 8, (int)AutoFightSeekAction.Scan)]
    [InlineData(600, 25, (int)AutoFightSeekAction.Scan)]
    public void GetSeekAction_ShouldOnlyScanWhenNoUsableEnemyIsVisible(
        int x,
        int height,
        int expected)
    {
        Assert.Equal((AutoFightSeekAction)expected, AutoFightSeek.GetSeekAction(x, height));
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
