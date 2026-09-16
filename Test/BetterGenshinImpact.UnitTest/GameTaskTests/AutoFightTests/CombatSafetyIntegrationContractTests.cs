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
            "public static Task ContinuousTargetingLoopAsync",
            "private static bool PublishPassiveObservation");

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

        foreach (var source in new[] { txt, json })
        {
            Assert.Contains("NativeCombatFlowRunner.Create", source);
            Assert.Contains("NativeCombatBattleHostIo.Create", source);
            Assert.Contains("battleHost.AdvanceAsync(flow", source);
        }
        // TXT/JSON均把旧护盾配置交给同一编译层；不再要求已经退役的两套内联循环。
        var runner = ReadSource("BetterGenshinImpact", "GameTask", "AutoFight", "Script", "Flow", "NativeCombatFlowRunner.cs");
        var adapter = ReadSource("BetterGenshinImpact", "GameTask", "AutoFight", "Script", "Flow", "LegacyCombatFlowAdapter.cs");
        var jsonCore = ReadSource("BetterGenshinImpact", "GameTask", "AutoFight", "Script", "Flow", "JsonCombatFlowExecution.cs");
        Assert.Contains("LegacyGuardianOptions.From", runner);
        Assert.Contains("LegacyCombatFlowAdapter.ApplyGuardian", jsonCore);
        Assert.Contains("maintain={record}", adapter);
        Assert.Contains("GuardianDurationLimit = guardian.Duration", adapter);
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
    public void CombatThread_OnlyConsumesPassiveSeekObservations()
    {
        var source = ReadSource(
            "BetterGenshinImpact",
            "GameTask",
            "AutoFight",
            "AutoFightTask.cs");

        Assert.Contains("RunPassiveSeek", source);
        Assert.Contains("AvatarRecognition.LatestPassiveObservation", source);
        Assert.DoesNotContain("await RunConfiguredSeekAsync", source);
        Assert.DoesNotContain("await AutoFightSeek.RunBoundedSeekSliceAsync", source);
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
            "private static readonly AsyncLocal<DateTime> LastPassiveCameraFrame");

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
            "internal void SimulateSwitchAction");
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

        Assert.Contains("var castAt = inputAtUtc ?? now", confirm);
        Assert.Contains("LastConfirmedSkillCastAtUtc = castAt", confirm);
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
