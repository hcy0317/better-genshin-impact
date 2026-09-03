using System;
using System.IO;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatSafetyIntegrationContractTests
{
    [Fact]
    public void ContinuousTargetingLoop_IsAPassiveObserver()
    {
        var source = ReadSource(
            "BetterGenshinImpact",
            "GameTask",
            "AutoFight",
            "Model",
            "AvatarRecognition.cs");
        var loop = Slice(
            source,
            "public static async Task ContinuousTargetingLoopAsync",
            "private static void PublishPassiveObservation");

        Assert.DoesNotContain("MoveMouseBy", loop, StringComparison.Ordinal);
        Assert.DoesNotContain("ReleaseAllKey", loop, StringComparison.Ordinal);
        Assert.DoesNotContain("MiddleButtonClick", loop, StringComparison.Ordinal);
    }

    [Fact]
    public void TxtAndJsonCombat_UseTheSameGuardianBoundary()
    {
        var txt = ReadSource(
            "BetterGenshinImpact",
            "GameTask",
            "AutoFight",
            "AutoFightTask.cs");
        var json = ReadSource(
            "BetterGenshinImpact",
            "GameTask",
            "AutoFight",
            "AutoFightJsonTask.cs");

        Assert.Contains("AutoFightSkill.EnsureGuardianBoundaryAsync", txt);
        Assert.Contains("AutoFightSkill.EnsureGuardianBoundaryAsync", json);
        Assert.Contains("GuardianSkillSwitchPolicy.ShouldSkipCoveredGuardianSkill", txt);
        Assert.Contains("GuardianSkillSwitchPolicy.ShouldSkipCoveredGuardianSkill", json);
        Assert.Contains("FindNextGuardianSkillCommandIndex", txt);
        Assert.Contains("护盾已到期，快进到策略中下一次 E 时机", txt);
    }

    [Fact]
    public void TxtAndJsonCombat_LogTheEffectiveSafetyConfiguration()
    {
        var txt = ReadSource(
            "BetterGenshinImpact",
            "GameTask",
            "AutoFight",
            "AutoFightTask.cs");
        var json = ReadSource(
            "BetterGenshinImpact",
            "GameTask",
            "AutoFight",
            "AutoFightJsonTask.cs");

        Assert.Contains("ValidateAndLogCombatSafetyConfiguration(Logger, _taskParam)", txt);
        Assert.Contains("ValidateAndLogCombatSafetyConfiguration(Logger, _taskParam)", json);
    }

    [Fact]
    public void BoundedSeek_IsSelectedOutsideLegacyMode()
    {
        var source = ReadSource(
            "BetterGenshinImpact",
            "GameTask",
            "AutoFight",
            "AutoFightTask.cs");

        Assert.Contains("CombatTargetingMode.Legacy", source);
        Assert.Contains("RunBoundedSeekSliceAsync", source);
        Assert.Contains("BoundedSeekPolicy.MaximumBudget", source);
    }

    [Fact]
    public void ExhaustedSeekBudget_DoesNotSkipFightFinishDetection()
    {
        var source = ReadSource(
            "BetterGenshinImpact",
            "GameTask",
            "AutoFight",
            "AutoFightTask.cs");
        var finishCheck = Slice(
            source,
            "public static async Task<bool> CheckFightFinish",
            "private static async Task<bool?> RunConfiguredSeekAsync");

        Assert.DoesNotContain(
            "finishDetectConfig.GetSeekBudget() <= TimeSpan.Zero",
            finishCheck,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GuardianCoverage_UsesOnlyTheConfirmedCastTimestamp()
    {
        var avatar = ReadSource(
            "BetterGenshinImpact",
            "GameTask",
            "AutoFight",
            "Model",
            "Avatar.cs");
        var confirm = Slice(
            avatar,
            "internal void ConfirmSkillUsed",
            "private void SimulateSwitchAction");
        var ordinarySkill = Slice(
            avatar,
            "public double AfterUseSkill",
            "/// <summary>\r\n    /// 元素战技是否正在CD中");
        var seek = ReadSource(
            "BetterGenshinImpact",
            "GameTask",
            "AutoFight",
            "AutoFightSeek.cs");
        var guardianBoundary = Slice(
            seek,
            "public static async Task<GuardianBoundaryAction> EnsureGuardianBoundaryAsync",
            "private static void LogGuardianBoundaryDecision");

        Assert.Contains("LastConfirmedSkillCastAtUtc = now", confirm);
        Assert.DoesNotContain("LastConfirmedSkillCastAtUtc", ordinarySkill);
        Assert.Contains("guardianAvatar.LastConfirmedSkillCastAtUtc", guardianBoundary);
        Assert.DoesNotContain("guardianAvatar.LastSkillTime", guardianBoundary);
    }

    [Fact]
    public void OrdinarySkill_ConfirmsOnlyAReadyToCooldownTransition()
    {
        var avatar = ReadSource(
            "BetterGenshinImpact",
            "GameTask",
            "AutoFight",
            "Model",
            "Avatar.cs");
        var useSkill = Slice(
            avatar,
            "public void UseSkill",
            "/// <summary>\r\n    /// 使用完元素战技的回调");

        Assert.Contains("var skillReadyBeforeCast = IsSkillReady()", useSkill,
            StringComparison.Ordinal);
        Assert.Contains("if (skillReadyBeforeCast && cd > 0)", useSkill,
            StringComparison.Ordinal);
        Assert.Contains("ConfirmSkillUsed(cd)", useSkill,
            StringComparison.Ordinal);
    }

    private static string Slice(string source, string start, string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        var endIndex = source.IndexOf(end, startIndex, StringComparison.Ordinal);
        Assert.True(startIndex >= 0 && endIndex > startIndex);
        return source[startIndex..endIndex];
    }

    private static string ReadSource(params string[] relativeSegments)
    {
        var root = FindRepoRoot();
        return File.ReadAllText(Path.Combine([root, .. relativeSegments]));
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "BetterGenshinImpact.sln")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("BetterGenshinImpact.sln was not found");
    }
}
