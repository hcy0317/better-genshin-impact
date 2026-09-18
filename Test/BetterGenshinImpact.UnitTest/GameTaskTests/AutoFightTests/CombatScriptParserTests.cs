using BetterGenshinImpact.GameTask.AutoFight.Script;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatScriptParserTests
{
    [Fact]
    public void ParsedSourceIdentityRemainsBoundToTheLoadedTextWhenDiskChanges()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"bgi-source-{Guid.NewGuid():N}.txt");
        try
        {
            const string original = "琴 attack(0.1)";
            System.IO.File.WriteAllText(path, original);
            var loaded = CombatScriptParser.Parse(path);
            System.IO.File.WriteAllText(path, "琴 attack(0.2)");
            var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(original)));
            Assert.All(loaded.CombatCommands, command =>
            {
                Assert.Equal(System.IO.Path.GetFullPath(path), command.SourceFile);
                Assert.Equal(expected, command.SourceTextSha256);
                Assert.Equal(expected, new CombatCommand(command).SourceTextSha256);
            });
        }
        finally { System.IO.File.Delete(path); }
    }

    /// <summary>
    /// 无角色前缀的指令中含有空格参数（如 walk(s, 0.2)），不应将空格误识别为角色分隔符
    /// </summary>
    [Fact]
    public void ParseLine_NoPrefixWithSpacedArg_PreservesWholeLineAsCommands()
    {
        // 无 validate 模式，执行 ParseContext
        var script = CombatScriptParser.ParseContext("walk(s, 0.2)", validate: false);
        Assert.Single(script.CombatCommands);
        Assert.Contains("walk", script.CombatCommands[0].Method.Alias);
        Assert.NotNull(script.CombatCommands[0].Args);
        Assert.Equal(2, script.CombatCommands[0].Args.Count);
        Assert.Equal("s", script.CombatCommands[0].Args[0]);
        Assert.Equal("0.2", script.CombatCommands[0].Args[1]);
    }

    /// <summary>
    /// 有角色前缀的指令正常解析
    /// </summary>
    [Fact]
    public void ParseLine_WithCharPrefix_SplitsCorrectly()
    {
        var script = CombatScriptParser.ParseContext("娜维娅 e", validate: false);
        Assert.Single(script.CombatCommands);
        Assert.Contains("e", script.CombatCommands[0].Method.Alias);
    }

    /// <summary>
    /// 有角色前缀且指令含有空格参数（如 娜维娅 walk(s, 0.2)），应正确拆分
    /// </summary>
    [Fact]
    public void ParseLine_CharPrefixWithSpacedArg_SplitsCorrectly()
    {
        var script = CombatScriptParser.ParseContext("娜维娅 walk(s, 0.2)", validate: false);
        Assert.Single(script.CombatCommands);
        Assert.Contains("walk", script.CombatCommands[0].Method.Alias);
        Assert.NotNull(script.CombatCommands[0].Args);
        Assert.Equal(2, script.CombatCommands[0].Args.Count);
        Assert.Equal("s", script.CombatCommands[0].Args[0]);
        Assert.Equal("0.2", script.CombatCommands[0].Args[1]);
    }

    /// <summary>
    /// 无角色前缀时传入 defaultAvatarName，应使用该名称作为角色名并正常解析指令
    /// </summary>
    [Fact]
    public void ParseLine_NoPrefixWithDefaultAvatarName_UsesProvidedName()
    {
        var script = CombatScriptParser.ParseContext("walk(s, 0.2)", validate: true, defaultAvatarName: "娜维娅");
        Assert.Single(script.CombatCommands);
        Assert.Equal("娜维娅", script.CombatCommands[0].Name);
        Assert.Contains("walk", script.CombatCommands[0].Method.Alias);
        Assert.NotNull(script.CombatCommands[0].Args);
        Assert.Equal(2, script.CombatCommands[0].Args.Count);
        Assert.Equal("s", script.CombatCommands[0].Args[0]);
        Assert.Equal("0.2", script.CombatCommands[0].Args[1]);
    }
}
