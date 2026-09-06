using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class ElementPartyResourceTests
{
    public static IEnumerable<object[]> Parties()
    {
        yield return ["火", new[] { "钟离", "班尼特", "香菱", "芙宁娜" }];
        yield return ["水", new[] { "钟离", "芙宁娜", "那维莱特", "琴" }];
        yield return ["风", new[] { "钟离", "芙宁娜", "琴", "枫原万叶" }];
        yield return ["雷", new[] { "钟离", "纳西妲", "菲谢尔", "雷电将军" }];
        yield return ["草", new[] { "钟离", "芙宁娜", "纳西妲", "久岐忍" }];
        yield return ["冰", new[] { "钟离", "珊瑚宫心海", "神里绫华", "芙宁娜" }];
        yield return ["岩", new[] { "钟离", "班尼特", "娜维娅", "香菱" }];
        yield return ["矿物", new[] { "钟离", "娜维娅", "枫原万叶", "琴" }];
        yield return ["采集", new[] { "钟离", "纳西妲", "枫原万叶", "琴" }];
    }

    [Theory]
    [MemberData(nameof(Parties))]
    public void PresetAndParsedStrategyHaveExactlyTheSameFourCharacters(string name, string[] expected)
    {
        var group = JObject.Parse(File.ReadAllText(UserPath("ScriptGroup", "手动-配置自动化队伍.json")));
        var presets = group["projects"]!.Where(p => (string?)p["folderName"] == "AutoSwitchRoles"
            && (string?)p["jsScriptSettingsObject"]?["switchPartyName"] == name).ToArray();
        var preset = Assert.Single(presets)["jsScriptSettingsObject"]!;
        Assert.Equal(expected, Enumerable.Range(1, 4).Select(i => (string)preset["position" + i]!));
        var strategy = CombatScriptParser.Parse(UserPath("AutoFight", "00-" + name + ".txt"));
        Assert.Equal(expected.OrderBy(n => n), strategy.AvatarNames.OrderBy(n => n));
        var entry = strategy.CombatCommands.First(command => command.Method != Method.Strategy && command.Method != Method.Timing);
        Assert.Equal(Method.Call, entry.Method);
        Assert.Equal("battle", entry.Options["once"]);
        Assert.True(entry.HasFlag("required"));
        Assert.Contains(strategy.CombatCommands, c => c.Name == "钟离" && c.Method == Method.Skill && c.Args!.Contains("hold"));
        Assert.DoesNotContain(strategy.CombatCommands, c => c.Method == Method.KeyPress &&
            c.Args!.Any(arg => arg.Equals("q", StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(strategy.CombatCommands, c => c.Method == Method.Ready);
        Assert.All(strategy.CombatCommands.Where(c => c.Method == Method.Skill && c.Name == "钟离"),
            c => { Assert.True(c.HasFlag("hold") && c.HasFlag("wait") && c.HasFlag("required")); Assert.Equal("护盾", c.Options["record"]); });
        Assert.All(strategy.CombatCommands.Where(c => c.Method == Method.Skill && c.Name != "钟离"),
            c => Assert.Contains("fast", c.Args!));
        // 统一编译器检查分支、原子片段与恢复预算；不再对 define 区的平铺文本累加旧固定 12 秒轴。
        var program = CombatFlowProgram.Compile(strategy);
        Assert.True(program.Loop);
        Assert.Contains(strategy.CombatCommands, command => command.Options.GetValueOrDefault("watch") == "护盾" &&
            command.Options.GetValueOrDefault("maintain") == "护盾" && command.Options.GetValueOrDefault("watch-mode") == "call");
        if (name is "矿物" or "采集")
            Assert.Equal(2, expected.Count(n => n is "琴" or "枫原万叶"));
    }

    [Fact]
    public void SameFourCharactersCannotSelectDifferentRotations()
    {
        var groups = Parties().GroupBy(row => string.Join(',', ((string[])row[1]).OrderBy(n => n)));
        foreach (var group in groups.Where(g => g.Count() > 1))
        {
            string[]? expected = null;
            foreach (var row in group)
            {
                var body = File.ReadAllLines(UserPath("AutoFight", "00-" + row[0] + ".txt"))
                    .Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith("//")).ToArray();
                if (expected != null) Assert.Equal(expected, body);
                expected = body;
            }
        }
    }

    private static string UserPath(params string[] segments)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "BetterGenshinImpact.sln"))) root = root.Parent;
        return Path.Combine([root!.FullName, "BetterGenshinImpact", "User", .. segments]);
    }
}
