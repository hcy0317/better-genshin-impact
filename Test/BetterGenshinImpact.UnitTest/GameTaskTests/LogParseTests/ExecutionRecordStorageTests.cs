using System.Dynamic;
using BetterGenshinImpact.Core.Script.Group;
using BetterGenshinImpact.GameTask.LogParse;
using Newtonsoft.Json;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.LogParseTests;

public class ExecutionRecordStorageTests
{
    [Fact]
    public void ChangedJavascriptSettings_ShouldInvalidateSuccessfulDailyRecord()
    {
        var project = CreateProject("采集选中的材料");
        var record = JsonConvert.DeserializeObject<ExecutionRecord>("""
            {
              "guid": "0cba32ff-5090-4556-8c1b-47ad6c97febf",
              "group_name": "每日采集",
              "project_name": "CD采集-临时",
              "folder_name": "CD-Aware-AutoGather",
              "type": "Javascript",
              "start_time": "2026-09-04T16:30:40",
              "end_time": "2026-09-04T16:30:54",
              "is_successful": true,
              "settings_fingerprint": "5BB1482CD1AD393EB240C191CE13A219FF55CE570414C99B12720808E309154B"
            }
            """)!;

        var shouldSkip = ExecutionRecordStorage.IsSkipTask(
            project,
            out _,
            [new DailyExecutionRecord { Name = "20260904", ExecutionRecords = [record] }]);

        Assert.False(shouldSkip);
    }

    [Fact]
    public void UnchangedJavascriptSettings_ShouldReuseSuccessfulDailyRecord()
    {
        var project = CreateProject("采集选中的材料");
        var record = CreateRecord("B93303BE84CE5BB1C442602768FE57E6F7F170DAA101F729EAC585261C1675F8");

        var shouldSkip = ExecutionRecordStorage.IsSkipTask(
            project,
            out _,
            [new DailyExecutionRecord { Name = "20260904", ExecutionRecords = [record] }]);

        Assert.True(shouldSkip);
    }

    [Fact]
    public void LegacyRecordWithoutSettingsFingerprint_ShouldKeepCompatibility()
    {
        var project = CreateProject("采集选中的材料");
        var record = CreateRecord(null);

        var shouldSkip = ExecutionRecordStorage.IsSkipTask(
            project,
            out _,
            [new DailyExecutionRecord { Name = "20260904", ExecutionRecords = [record] }]);

        Assert.True(shouldSkip);
    }

    [Fact]
    public void SettingsFingerprint_ShouldIgnoreObjectPropertyOrder()
    {
        var first = CreateProject("采集选中的材料");
        ((IDictionary<string, object?>)first.JsScriptSettingsObject!)["partyName"] = "钟纳久万";
        IDictionary<string, object?> reversedSettings = new ExpandoObject();
        reversedSettings["partyName"] = "钟纳久万";
        reversedSettings["runMode"] = "采集选中的材料";
        var second = CreateProject("采集选中的材料");
        second.JsScriptSettingsObject = (ExpandoObject)reversedSettings;

        Assert.Equal(
            ExecutionRecordStorage.ComputeSettingsFingerprint(first),
            ExecutionRecordStorage.ComputeSettingsFingerprint(second));
    }

    private static ExecutionRecord CreateRecord(string? settingsFingerprint)
    {
        return new ExecutionRecord
        {
            GroupName = "每日采集",
            ProjectName = "CD采集-临时",
            FolderName = "CD-Aware-AutoGather",
            Type = "Javascript",
            StartTime = DateTime.Now.AddMinutes(-1),
            EndTime = DateTime.Now,
            IsSuccessful = true,
            SettingsFingerprint = settingsFingerprint
        };
    }

    private static ScriptGroupProject CreateProject(string runMode)
    {
        IDictionary<string, object?> settings = new ExpandoObject();
        settings["runMode"] = runMode;
        var group = new ScriptGroup { Name = "每日采集" };
        group.Config.PathingConfig.TaskCompletionSkipRuleConfig.Enable = true;
        group.Config.PathingConfig.TaskCompletionSkipRuleConfig.BoundaryTime = -1;
        group.Config.PathingConfig.TaskCompletionSkipRuleConfig.LastRunGapSeconds = int.MaxValue;
        var project = new ScriptGroupProject("CD采集-临时", "CD-Aware-AutoGather", "Javascript")
        {
            JsScriptSettingsObject = (ExpandoObject)settings
        };
        group.AddProject(project);
        return project;
    }
}
