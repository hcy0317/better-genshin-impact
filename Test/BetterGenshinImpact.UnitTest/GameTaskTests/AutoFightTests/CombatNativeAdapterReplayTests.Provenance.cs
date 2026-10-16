using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public partial class CombatNativeAdapterReplayTests
{
    [Fact]
    public void ParsedMemoryJsonIsNotMisidentifiedAsAnUntrackedGeneratedStrategy()
    {
        const string json = "{\"info\":{\"name\":\"fixture\"},\"actions\":[{\"character\":\"那维莱特\",\"action\":\"attack(.1)\"}]}";
        var strategy = JsonCombatStrategyParser.Parse(json);
        var logger = new SourceIdentityLogger();
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram("那维莱特 attack(.1)")) { Logger = logger };
        using var flow = NativeCombatFlowRunner.Create(strategy, NativeCombatFlowRunner.CreateAdapter(io), clock: clock)!;
        var message = Assert.Single(logger.Messages.Where(line => line.Contains("FIGHT_STRATEGY ")));
        Assert.Contains("sourceKind=memory", message);
        Assert.Contains("source=in-memory", message);
        Assert.Contains("entry=JsonCombatStrategyParser.Parse", message);
        Assert.Contains(strategy.SourceTextSha256!, message);
        Assert.DoesNotContain(json, message);
    }

    [Fact]
    public void MemoryEntryLogBindsParsedContentCallerModeAndSessionWithoutLoggingScriptText()
    {
        const string text = "钟离 q(optional),attack(1.2)";
        var parsed = CombatScriptParser.ParseContext(text);
        parsed.BindSourceContext("memory:external-progress", "Dispatcher.CreateCombatSession", "source-session");
        var logger = new SourceIdentityLogger();
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram(text)) { Logger = logger, Actors = [new("钟离", 1)] };
        using var flow = NativeCombatFlowRunner.Create(CombatScriptExecutor.ExternalSessionCommands(parsed), io, loop: true);
        var message = Assert.Single(logger.Messages.Where(line => line.Contains("FIGHT_STRATEGY ")));
        Assert.Contains(parsed.SourceTextSha256!, message);
        Assert.Contains("entry=Dispatcher.CreateCombatSession", message);
        Assert.Contains("sourceKind=memory", message);
        Assert.Contains("source=memory:external-progress", message);
        Assert.Contains("session=source-session", message);
        Assert.Contains("mode=battle", message);
        Assert.Contains("purpose=Combat", message);
        Assert.Contains(flow.Context.BattleId.ToString(), message);
        Assert.DoesNotContain("unavailable", message);
        Assert.DoesNotContain(text, message);
    }
}
