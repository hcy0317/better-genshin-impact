using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class LegacyGuardianCompilationTests
{
    [Fact]
    public void LegacyHeldSkillWithGuardianCompilesAndKeepsItsSourceAndCoverageCondition()
    {
        var script = CombatScriptParser.ParseContext(new string('\n', 43) +
            "钟离  s(0.2), e(hold), wait(0.2), w(0.2)");
        var original = Assert.Single(script.CombatCommands.Where(command => command.Method == Method.Skill));
        var prepared = LegacyCombatFlowAdapter.Prepare(script.CombatCommands, ["钟离", "琴"], true,
            CombatScriptExecutionMode.LegacyPartyTemplate,
            new("钟离", true, GuardianCoverageMode.RequireKnownCoverage, 20));

        var program = CombatFlowProgram.Compile(prepared);
        var skill = Assert.Single(program.Root.Nodes.Where(node => node.Command.Method == Method.Skill &&
            node.Command.SourceTextSha256 == original.SourceTextSha256));
        Assert.Equal(44, skill.Command.SourceLine);
        Assert.Equal(13, skill.Command.SourceColumn);
        Assert.True(program.Loop);
        Assert.True(program.LegacyOutcomePolicy);
        Assert.Equal(4, script.CombatCommands.Count);
        Assert.Empty(original.Options);
        Assert.True(skill.Command.HasFlag("hold"));
        Assert.NotNull(skill.Condition);
        Assert.True(skill.Condition.EvaluateBoolean((function, args) => function == "record-active" ? false : null));
        Assert.False(skill.Condition.EvaluateBoolean((function, args) => function == "record-active" ? true : null));
        Assert.Null(skill.Condition.EvaluateBoolean((_, _) => null));
    }

    [Fact]
    public void PublicConditionsCannotAddressCompilerRecords()
    {
        var error = Assert.Throws<FormatException>(() => ConditionEvaluator.Compile("record-active(\"$legacy-guardian\")"));
        Assert.Contains("保留命名空间", error.Message);
    }

    [Fact]
    public void GeneratedCommandFlagDoesNotMakeUserConditionTrusted()
    {
        var command = new CombatCommand("琴", "q(if=record-active(\"$legacy-guardian\"))") { IsCompilerGenerated = true };
        var error = Assert.Throws<FormatException>(() => CombatFlowProgram.Compile(new CombatScript(new HashSet<string> { command.Name }, new List<CombatCommand> { command })));
        Assert.Contains("保留命名空间", error.Message);
    }

    [Fact]
    public void TrustedConditionStillRequiresItsRecordDeclaration()
    {
        var command = new CombatCommand("钟离", "e(if=record-active(\"$missing\"))")
            { IsCompilerGenerated = true, HasCompilerGeneratedCondition = true };
        var error = Assert.Throws<FormatException>(() => CombatFlowProgram.Compile(new CombatScript(new HashSet<string> { command.Name }, new List<CombatCommand> { command })));
        Assert.Contains("未声明", error.Message);
    }
}
