using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.AutoPathing.Handler;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public partial class CombatNativeAdapterReplayTests
{
    // Existing 002枫丹湖中垂柳拳王艾威尔森传奇 node 3, not the user's combat strategy.
    private const string RecordedMavuikaTraversal = "玛薇卡 wait(0.2),e(hold),wait(0.05),keydown(w),wait(0.01),keypress(VK_SPACE),wait(0.15),keypress(VK_SPACE),wait(0.1),keypress(VK_SPACE),wait(0.01),keypress(VK_SPACE),wait(1.6),keypress(VK_SPACE),wait(0.15),keypress(VK_SPACE),wait(0.1),keypress(VK_SPACE),wait(0.01),keypress(VK_SPACE),wait(1.3),keypress(VK_SPACE),wait(0.15),keypress(VK_SPACE),wait(0.1),keypress(VK_SPACE),wait(0.01),keypress(VK_SPACE),wait(1.2),keypress(VK_SPACE),wait(0.15),keypress(VK_SPACE),wait(0.1),keypress(VK_SPACE),wait(0.01),keypress(VK_SPACE),wait(0.5);";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordedMavuikaRouteUsesItsNamedActorOrFailsPreflightWithoutInput(bool present)
    {
        var clock = new FakeTimeProvider();
        var script = CombatScriptParser.ParseContext(RecordedMavuikaTraversal, false);
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram(RecordedMavuikaTraversal))
        { SimulatedSkillCooldown = 10, // Synthetic confirmation signal, not real Mavuika cooldown verification.
            Actors = present ? [new("钟离", 1), new("芙宁娜", 2), new("玛薇卡", 3), new("琴", 4)] :
            [new("钟离", 1), new("芙宁娜", 2), new("那维莱特", 3), new("琴", 4)] };
        io.SetFrontActor(present ? "玛薇卡" : "钟离");
        var error = Record.Exception(() => CombatScriptHandler.ValidateRouteRequirements(
            [new Waypoint { Id = 3, Action = "combat_script", ActionParams = RecordedMavuikaTraversal }],
            "002枫丹湖中垂柳拳王艾威尔森 传奇", io.Actors.Select(actor => actor.Name)));
        if (!present)
        {
            Assert.Contains("第3点", Assert.IsType<BetterGenshinImpact.GameTask.AutoPathing.PathingRequiredActorMissingException>(error).Message);
            Assert.Contains("NO_APPLICABLE_ACTOR", error.Message);
            Assert.Contains("玛薇卡", error.Message);
            Assert.Empty(io.Inputs);
            Assert.Empty(io.Primitives);
            Assert.Empty(io.CapturedFrames);
            return;
        }
        Assert.Null(error);
        using var runner = NativeCombatFlowRunner.Create(script.CombatCommands, io, loop: false,
            mode: CombatScriptExecutionMode.LegacyPartyTemplate, purpose: CombatScriptExecutionPurpose.Pathing);
        var result = await runner.RunRoundAsync(default);
        Assert.Equal(CombatFlowResult.Succeeded, result);
        Assert.Equal("玛薇卡", Assert.Single(io.Inputs).Actor);
        Assert.Equal(16, io.Primitives.Count(command => command.Method == Method.KeyPress && command.Args![0] == "VK_SPACE"));
        Assert.False(io.HoldingInput);
    }
}
