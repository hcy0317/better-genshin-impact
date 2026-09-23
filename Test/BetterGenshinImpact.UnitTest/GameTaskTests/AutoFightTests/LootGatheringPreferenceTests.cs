using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.AutoPathing.Handler;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class LootGatheringPreferenceTests
{
    [Fact]
    public void MissingKazuhaKeepsQinAndNonGatheringActionsAreNeverFiltered()
    {
        var original = CombatScriptParser.ParseContext(Alternatives, false);
        var qinOnly = LegacyPathingMacroPlan.Create(original, ["琴"]).Segments.SelectMany(segment => segment.Commands);
        Assert.Contains(qinOnly, command => command.Name == "琴");
        var healing = CombatScriptParser.ParseContext("万叶 e;琴 e,q", false);
        var selected = LootGatheringPreference.Select(healing.CombatCommands, new HashSet<string> { "枫原万叶", "琴" });
        Assert.Equal(healing.CombatCommands, selected);
        var enhanced = CombatScriptParser.ParseContext("万叶 e(required);琴 e", false);
        Assert.Equal(enhanced.CombatCommands, LootGatheringPreference.Select(enhanced.CombatCommands,
            new HashSet<string> { "枫原万叶", "琴" }));
    }

    [Fact]
    public void DedicatedDoubleListPrefersKazuhaButExplicitSingleActorRemainsExplicit()
    {
        Assert.Equal(new[] { "万叶-长E" }, PickUpCollectHandler.SelectAlternativeNames(["琴-短E", "万叶-长E"], ["琴", "枫原万叶"]));
        Assert.Equal(new[] { "琴-短E" }, PickUpCollectHandler.SelectAlternativeNames(["万叶", "琴-短E"], ["琴"]));
        Assert.Equal(new[] { "琴-短E" }, PickUpCollectHandler.SelectAlternativeNames(["琴-短E"], ["琴", "枫原万叶"]));
    }

    internal const string Alternatives = "keypress(f);万叶 attack(.08),keydown(e),wait(.7),keyup(e),attack(.2);" +
        "琴 attack(.08),keydown(e),wait(.4),moveby(1000,0),wait(.2),moveby(1000,0),wait(.2),moveby(1000,-3500),wait(1.8),keyup(e),wait(.3),click(middle)";

    [Fact]
    public void BothCollectorsUseKazuhaOnlyWithoutDroppingAnonymousInteraction()
    {
        var script = CombatScriptParser.ParseContext(Alternatives, false);
        var plan = LegacyPathingMacroPlan.Create(script, ["枫原万叶", "琴"]);
        var commands = plan.Segments.SelectMany(segment => segment.Commands).ToArray();
        Assert.DoesNotContain(commands, command => command.Name == "琴");
        Assert.Contains(commands, command => command.Name == "枫原万叶");
        Assert.Equal(Method.KeyPress, commands[0].Method);
        Assert.Contains(script.CombatCommands, command => command.Name == "琴");
    }
}
