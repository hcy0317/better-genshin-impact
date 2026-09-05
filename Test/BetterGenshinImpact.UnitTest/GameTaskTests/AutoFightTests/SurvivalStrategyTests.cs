using System;
using System.IO;
using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class SurvivalStrategyTests
{
    [Theory]
    [InlineData(0.5, true)]
    [InlineData(2, false)]
    [InlineData(12, false)]
    [InlineData(19, false)]
    public void ExplicitRefreshOnlySkipsAFreshlyConfirmedCast(double age, bool skipped)
    {
        Assert.Equal(skipped, GuardianSkillSwitchPolicy.ShouldSkipCoveredGuardianSkill(
            true, true, true, DateTime.UnixEpoch, 20,
            DateTime.UnixEpoch.AddSeconds(age), refreshRequested: true));
    }

    [Theory]
    [InlineData("energy 1 cd 0", 0.95, BurstReadyState.Ready)]
    [InlineData("energy 1 cd 0", 0.5, BurstReadyState.Unknown)]
    [InlineData("energy 0 cd 0", 0.95, BurstReadyState.Unknown)]
    [InlineData("energy 1 cd 1", 0.95, BurstReadyState.Cooldown)]
    public void BurstRequiresPositiveReadyEvidence(string label, double confidence, BurstReadyState expected)
        => Assert.Equal(expected, Avatar.ClassifyBurstReadiness(label, confidence));

    [Theory]
    [InlineData("e(refresh)")]
    [InlineData("e(hold,refresh)")]
    [InlineData("e(hold,wait,fast,refresh)")]
    public void RefreshCannotBeUsedWithoutItsCriticalCastContract(string text)
        => Assert.Throws<ArgumentException>(() => new CombatCommand("钟离", text));

    [Fact]
    public void ExplicitRefreshParsesAsAConfirmedHeldSkill()
    {
        var command = new CombatCommand("钟离", "e(hold,wait,refresh)");
        Assert.Equal(Method.Skill, command.Method);
        Assert.Contains("refresh", command.Args!);
    }

    [Fact]
    public void CriticalRefreshFailureCannotBeSwallowedBySimpleStrategyExecution()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "BetterGenshinImpact.sln"))) root = root.Parent;
        var source = File.ReadAllText(Path.Combine(root!.FullName,
            "BetterGenshinImpact/GameTask/AutoFight/Script/CombatScriptExecutor.cs"));
        var start = source.IndexOf("catch (GuardianCoverageException)", StringComparison.Ordinal);
        var end = source.IndexOf("catch (RetryException", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        Assert.Contains("ReleaseAllKey", source[start..end]);
        Assert.Contains("throw;", source[start..end]);
    }

    [Fact]
    public void InvalidBurstConfidenceIsNotPositiveReadinessEvidence()
        => Assert.Equal(BurstReadyState.Unknown, Avatar.ClassifyBurstReadiness("energy 1 cd 0", double.NaN));
}
