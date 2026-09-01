using System.Runtime.CompilerServices;
using BetterGenshinImpact.GameTask.AutoDomain;
using BetterGenshinImpact.GameTask.AutoDomain.Model;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoDomainTests;

public class ResinUseRecordTests
{
    [Fact]
    public void BuildFromDomainParamHonorsConfiguredResinPriority()
    {
        var taskParam = CreateTaskParam();
        taskParam.ResinPriorityList = ["须臾树脂", "浓缩树脂", "原粹树脂"];
        taskParam.TransientResinUseCount = 1;
        taskParam.CondensedResinUseCount = 1;
        taskParam.OriginalResinUseCount = 1;

        var records = ResinUseRecord.BuildFromDomainParam(taskParam);

        Assert.Equal(
            ["须臾树脂", "浓缩树脂", "原粹树脂"],
            records.Select(record => record.Name));
    }

    [Fact]
    public void BuildFromDomainParamKeepsOriginalResinVariantsAtOriginalPriority()
    {
        var taskParam = CreateTaskParam();
        taskParam.ResinPriorityList = ["须臾树脂", "原粹树脂"];
        taskParam.TransientResinUseCount = 1;
        taskParam.OriginalResin40UseCount = 1;
        taskParam.OriginalResin20UseCount = 1;
        taskParam.OriginalResinUseCount = 1;

        var records = ResinUseRecord.BuildFromDomainParam(taskParam);

        Assert.Equal(
            ["须臾树脂", "原粹树脂40", "原粹树脂20", "原粹树脂"],
            records.Select(record => record.Name));
    }

    private static AutoDomainParam CreateTaskParam()
    {
        var taskParam = (AutoDomainParam)RuntimeHelpers.GetUninitializedObject(typeof(AutoDomainParam));
        taskParam.SpecifyResinUse = true;
        taskParam.ResinPriorityList = [];
        return taskParam;
    }
}
