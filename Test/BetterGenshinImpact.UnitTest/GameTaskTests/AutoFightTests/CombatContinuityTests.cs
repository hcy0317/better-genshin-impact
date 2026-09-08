using System;
using System.IO;
using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.Common.Job;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatContinuityTests
{
    [Theory]
    [InlineData("水", "钟心那万", true)]
    [InlineData("水", "水", true)]
    [InlineData("水", "火", false)]
    [InlineData("草", "钟心纳久", true)]
    [InlineData("雷", "钟纳久万", true)]
    [InlineData("钟心那万", "水", true)]
    [InlineData("自定义.*", "自定义队伍", true)]
    public void ElementNamesAcceptTheirExactLegacyPreset(string requested, string actual, bool expected)
        => Assert.Equal(expected, PartyNameAliases.IsMatch(actual, requested));

    [Theory]
    [InlineData(0, 1920, 0)]
    [InlineData(960, 1920, 0)]
    [InlineData(1500, 1920, 120)]
    [InlineData(400, 1920, -120)]
    public void PassiveCameraPulseIsBounded(int targetX, int width, int expected)
        => Assert.Equal(expected, CombatContinuityPolicy.CameraPulse(targetX, width));

    [Fact]
    public void FinishCheckDoesNotPerformAuxiliaryMovementOrWaitForSeek()
    {
        var source = ReadSource("GameTask/AutoFight/AutoFightTask.cs");
        var start = source.IndexOf("public static async Task<bool> CheckFightFinish", StringComparison.Ordinal);
        var end = source.IndexOf("private static readonly AsyncLocal<DateTime> LastPassiveCameraFrame", start, StringComparison.Ordinal);
        var method = source[start..end];
        Assert.DoesNotContain("await RunConfiguredSeekAsync", method);
        Assert.DoesNotContain("await AutoFightSeek.DetectAndApproachEnemyAsync", method);
        Assert.Contains("probe.Observe(observed)", method);
    }

    [Fact]
    public void OrdinarySkillObservationRunsAfterReturningToTheStrategy()
    {
        var source = ReadSource("GameTask/AutoFight/Model/Avatar.cs");
        var start = source.IndexOf("if (!observeCooldown)", StringComparison.Ordinal);
        var end = source.IndexOf("Sleep(200, Ct)", start, StringComparison.Ordinal);
        Assert.Contains("QueueSkillCooldownObservation();", source[start..end]);
        Assert.Contains("return;", source[start..end]);
        Assert.Contains("observed == index ? ReadSkillCurrentCd(region) : 0", source);
    }

    [Fact]
    public void ConfirmedBackgroundArrowRemainsUsableWithoutFreshCapture()
    {
        var now = DateTime.UtcNow;
        var visual = new EnemySeekVisual(1400, 400, 12, 12, 100);
        var decision = new EnemySeekDecision(AutoFightSeekAction.Approach,
            EnemyIndicatorDirection.Right, visual, 1);
        var observation = new PassiveTargetObservation(false, false, now, visual, 1920, 1080, decision);
        Assert.True(AutoFightSeek.TryCreatePassiveDecision(observation, now, out var actual, out _, out _));
        Assert.Equal(decision, actual);
        Assert.False(AutoFightSeek.TryCreatePassiveDecision(observation, now.AddSeconds(1), out _, out _, out _));
    }

    private static string ReadSource(string path)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "BetterGenshinImpact.sln")))
            directory = directory.Parent;
        return File.ReadAllText(Path.Combine(directory!.FullName, "BetterGenshinImpact", path));
    }
}
