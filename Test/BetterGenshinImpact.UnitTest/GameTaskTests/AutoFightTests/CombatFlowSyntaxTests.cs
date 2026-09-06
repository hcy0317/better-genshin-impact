using BetterGenshinImpact.GameTask.AutoFight.Script;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatFlowSyntaxTests
{
    [Theory]
    [InlineData("record-active('')")]
    [InlineData("succeeded('')")]
    [InlineData("call-index('')")]
    public void EmptyConditionSelectorsAreLocatedSyntaxErrors(string condition)
    {
        var error = Assert.Throws<FormatException>(() => BetterGenshinImpact.GameTask.AutoFight.Script.Flow.CombatFlowProgram.Compile($"\n琴 attack(0.1,if={condition})"));
        Assert.Contains("第2行", error.Message);
    }

    [Fact]
    public void TwoActionsCannotShareOneResultIdWithinTheSameInvocation()
    {
        var error = Assert.Throws<FormatException>(() => BetterGenshinImpact.GameTask.AutoFight.Script.Flow.CombatFlowProgram.Compile("""
            琴 e(id=关键动作)
            琴 q(id=关键动作)
            琴 attack(0.1,if=succeeded(关键动作))
            """));
        Assert.Contains("重复", error.Message);
        Assert.Contains("第2行", error.Message);
    }

    [Fact]
    public void ACallAndTailJumpCycleCannotHideRecursiveStackGrowth()
    {
        var error = Assert.Throws<FormatException>(() => BetterGenshinImpact.GameTask.AutoFight.Script.Flow.CombatFlowProgram.Compile("""
            call(甲)
            segment(甲,define) { call(乙) }
            segment(乙,define) { jump(甲) }
            """));
        Assert.Contains("递归", error.Message);
        // 纯尾转移不增加调用栈，仍由既有跳转预算约束，不误禁合法的有界控制流。
        BetterGenshinImpact.GameTask.AutoFight.Script.Flow.CombatFlowProgram.Compile("""
            jump(甲)
            segment(甲,define) { jump(乙) }
            segment(乙,define) { jump(甲) }
            """);
    }

    [Fact]
    public void FileLexicalErrorsRetainTheFilePathAndOriginalLine()
    {
        var path = Path.Combine(Path.GetTempPath(), "bgi-combat-syntax-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(path, "\n琴 attack(");
            var error = Assert.Throws<FormatException>(() => CombatScriptParser.Parse(path));
            Assert.Contains(path, error.Message);
            Assert.Contains("第2行", error.Message);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("琴 attack(0.1,if=!succeeded(拼错结果))")]
    [InlineData("琴 attack(0.1,if=call-index(拼错片段)=0)")]
    public void MisspelledResultOrCallSelectorsCannotAuthorizeActionsThroughNegation(string source)
    {
        var error = Assert.Throws<FormatException>(() => BetterGenshinImpact.GameTask.AutoFight.Script.Flow.CombatFlowProgram.Compile(source));
        Assert.Contains("未声明", error.Message);
        Assert.Contains("第1行", error.Message);
    }

    [Fact]
    public void QuotedNamesWithEqualsAndEquivalentUnicodeCompileConsistently()
    {
        var script = CombatScriptParser.ParseContext("""
            timing('Café=窗口',duration=8)
            record('Café=事件',timing='Café=窗口')
            call('Café=片段')
            segment(start,name='Café=片段',define,requires=record-active('Café=事件'))
            琴 attack(0.1,keep='Café=事件')
            segment(end)
            """);
        BetterGenshinImpact.GameTask.AutoFight.Script.Flow.CombatFlowProgram.Compile(script);
        Assert.Equal("Café=窗口", script.CombatCommands[0].Args!.Single());
        Assert.Equal("Café=片段", script.CombatCommands[2].Args!.Single());
    }

    [Theory]
    [InlineData("琴 attack(0.1,keep=未声明)", 3)]
    [InlineData("call(缺失)", 3)]
    [InlineData("timing(甲,duration=8)\ntiming(甲,duration=9)", 4)]
    [InlineData("segment(start,name=输出,requires=record-active(缺失))\n琴 wait(0.1)\nsegment(end)", 3)]
    [InlineData("琴 attack(0.1,if=unknownFunction())", 3)]
    [InlineData("segment(start,name=缺尾)\n琴 wait(0.1)", 3)]
    public void SemanticDiagnosticsCarryTheResponsibleDeclarationLocation(string text, int line)
    {
        var error = Assert.Throws<FormatException>(() => BetterGenshinImpact.GameTask.AutoFight.Script.Flow.CombatFlowProgram.Compile("\n// 位置保持\n" + text));
        Assert.Contains($"第{line}行", error.Message);
    }

    [Theory]
    [InlineData("钟离 e(record=甲,maintain=甲,keep=乙,refresh=乙)\nrecord(乙,duration=8)", 3)]
    [InlineData("钟离 e(fast,keep=乙,refresh=乙)\nrecord(乙,duration=8)", 3)]
    [InlineData("return(多余)", 3)]
    [InlineData("call(甲,乙)\nsegment(start,name=甲,define)\nsegment(end)", 3)]
    [InlineData("segment(start,name=甲,define)\n琴 wait(0.1)\nsegment(end,timeout=2)", 5)]
    public void ConflictingOrIgnoredParametersFailAtTheActualSourceCommand(string text, int line)
    {
        var error = Assert.Throws<FormatException>(() => BetterGenshinImpact.GameTask.AutoFight.Script.Flow.CombatFlowProgram.Compile("\n// 位置保持\n" + text));
        Assert.Contains($"第{line}行", error.Message);
        Assert.Contains("列", error.Message);
    }

    [Theory]
    [InlineData("琴 walk(s,0.2,record=标记)")]
    [InlineData("琴 keypress(q,record=标记)")]
    [InlineData("琴 moveby(3,4,record=标记)")]
    public void ValidPrimitiveArgumentsSurviveCompilationSnapshotCopy(string text)
    {
        BetterGenshinImpact.GameTask.AutoFight.Script.Flow.CombatFlowProgram.Compile(text);
    }

    [Theory]
    [InlineData("琴 attack(NaN,record=标记)")]
    [InlineData("琴 attack(1,2,record=标记)")]
    [InlineData("琴 wait(-1,record=标记)")]
    [InlineData("琴 wait(Infinity,record=标记)")]
    [InlineData("琴 moveby(nope,1,record=标记)")]
    public void InvalidPrimitiveArgumentsAreRejectedByTheFlowCompiler(string text)
    {
        Assert.Throws<FormatException>(() => BetterGenshinImpact.GameTask.AutoFight.Script.Flow.CombatFlowProgram.Compile(text));
    }

    [Fact]
    public void AtomicTimeoutAccountsForBranchBodies()
    {
        Assert.Throws<FormatException>(() => BetterGenshinImpact.GameTask.AutoFight.Script.Flow.CombatFlowProgram.Compile("""
            segment(start,atomic)
            branch(if=true,then=长段)
            segment(end)
            segment(start,name=长段,define)
            琴 attack(20)
            segment(end)
            """));
    }

    [Theory]
    [InlineData("round(1),call(开场,once=battle,required)")]
    [InlineData("call(开场,once=battle,required)", true)]
    public void OpeningCannotBeBypassedByRoundFiltersOrBeTheOnlyRepeatingWatchTarget(string opening, bool watch = false)
    {
        var text = opening + "\n琴 attack(0.1)\nsegment(start,name=开场,define)\n钟离 e(required,record=覆盖" +
            (watch ? ",watch=覆盖" : "") + ")\nsegment(end)";
        Assert.Throws<FormatException>(() => BetterGenshinImpact.GameTask.AutoFight.Script.Flow.CombatFlowProgram.Compile(text));
    }

    [Fact]
    public void DiagnosticsKeepTheOriginalLineAndCommandColumnIncludingBlankLines()
    {
        var exception = Assert.Throws<FormatException>(() => BetterGenshinImpact.GameTask.AutoFight.Script.Flow.CombatFlowProgram.Compile(
            "\n// 注释\n    琴 attack(0.1,typo=foo)"));
        Assert.Contains("第3行", exception.Message);
        Assert.Contains("第7列", exception.Message);
        Assert.Contains("typo", exception.Message);
    }

    [Fact]
    public void QuotedRecordNamesDoNotConfuseSpacesHyphensOrSubtraction()
    {
        var expression = ConditionEvaluator.Compile("record-exists('后台 窗口-甲') && t-5 > 1");
        Assert.True(expression.EvaluateBoolean((function, args) => function switch
        {
            "record-exists" => args.Single()?.ToString() == "后台 窗口-甲",
            "t" => 7d,
            _ => null
        }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MutuallyDependentRecordsNeedAReachableInitializer(bool initialized)
    {
        var text = (initialized ? "record(甲)\n" : "") + """
            琴 attack(0.1,record=甲,if=record-exists(乙))
            班尼特 e(record=乙,if=record-exists(甲))
            """;
        if (initialized) BetterGenshinImpact.GameTask.AutoFight.Script.Flow.CombatFlowProgram.Compile(text);
        else Assert.Throws<FormatException>(() => BetterGenshinImpact.GameTask.AutoFight.Script.Flow.CombatFlowProgram.Compile(text));
    }

    [Fact]
    public void UndeclaredRecordInAFragmentRequirementIsDiagnosedBeforeExecution()
    {
        var exception = Assert.Throws<FormatException>(() => BetterGenshinImpact.GameTask.AutoFight.Script.Flow.CombatFlowProgram.Compile("""
            call(输出)
            segment(start,name=输出,define,requires=record-active(未声明))
            琴 attack(0.1)
            segment(end)
            """));
        Assert.Contains("未声明", exception.Message);
    }

    [Fact]
    public void CommonRequiredFlagDoesNotOccupyMovementOrKeyArguments()
    {
        var script = CombatScriptParser.ParseContext("琴 walk(s,0.2,required), keypress(q,required)");
        Assert.Equal(new[] { "s", "0.2" }, script.CombatCommands[0].Args);
        Assert.Equal(new[] { "q" }, script.CombatCommands[1].Args);
        Assert.All(script.CombatCommands, command => Assert.True(command.HasFlag("required")));
        Assert.True(script.HasFlowCommands);
    }

    [Fact]
    public void FragmentJumpCannotAccidentallyExecuteTheLegacyPhysicalJump()
    {
        var script = CombatScriptParser.ParseContext("琴 jump\njump(维护)");
        Assert.Equal(Method.Jump, script.CombatCommands[0].Method);
        Assert.Equal(Method.JumpTo, script.CombatCommands[1].Method);
    }

    [Fact]
    public void LogicalOrInsideCommandDoesNotBecomeARoundSeparator()
    {
        var script = CombatScriptParser.ParseContext("branch(if=e-ready(班尼特) || q-ready(香菱),then=供能)");
        Assert.Equal("e-ready(班尼特) || q-ready(香菱)", Assert.Single(script.CombatCommands).Options["if"]);
    }

    [Theory]
    [InlineData("钟离 e(recrod=护盾)")]
    [InlineData("钟离 e(unknownflag)")]
    [InlineData("segment(start,atomic,timeout=0)\n钟离 e\nsegment(end)")]
    [InlineData("call(甲,record=标记)\nsegment(start,name=甲,define)\n钟离 e\nsegment(end)")]
    [InlineData("call(甲)\nsegment(start,name=甲,define)\ncall(甲)\nsegment(end)")]
    [InlineData("琴 attack(1,keep=未声明)")]
    public void InvalidFlowIsRejectedBeforeAnyGameInput(string text)
    {
        Assert.Throws<FormatException>(() => BetterGenshinImpact.GameTask.AutoFight.Script.Flow.CombatFlowProgram.Compile(text));
    }

    [Fact]
    public void HeadDefinitionsAndCommonParametersPreserveOriginalActionArguments()
    {
        var script = CombatScriptParser.ParseContext("""
            timing(盾位,cd=12,duration=20)
            钟离 e（hold,record=护盾,timing=盾位）
            琴 wait(0.1,keep=护盾)
            """);
        Assert.True(script.AvatarNames.SetEquals(["钟离", "琴"]));
        Assert.Equal("hold", Assert.Single(script.CombatCommands[1].Args!));
        Assert.Equal("护盾", script.CombatCommands[1].Options["record"]);
        Assert.Equal("0.1", Assert.Single(script.CombatCommands[2].Args!));
    }
}
