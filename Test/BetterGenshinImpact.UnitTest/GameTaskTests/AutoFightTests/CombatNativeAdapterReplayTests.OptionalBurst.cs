using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public partial class CombatNativeAdapterReplayTests
{
    [Fact]
    public async Task ExplicitOptionalBurstWithFreshKnownLowEnergySkipsWithoutSubmittingInput()
    {
        var clock = new FakeTimeProvider();
        var program = LoadProgram("钟离 q(optional),attack(.1)");
        using var io = new PhysicalReplay(clock, false, 20, program)
        { Actors = [new("钟离", 1)], CompleteSkillReceipts = true };
        io.SetFrontActor("钟离");
        using var runner = NativeCombatFlowRunner.Create(program, io);
        Assert.Equal(CombatFlowResult.Succeeded, await RunBoundedReplayRound(runner, io));
        Assert.Empty(io.Inputs);
        Assert.Single(io.Primitives.Where(command => command.Method == Method.Attack));
    }

    [Fact]
    public async Task ExplicitOptionalBurstCanExpireWithoutClaimingASkillEffectOrBlockingIndependentAttack()
    {
        var clock = new FakeTimeProvider();
        var program = LoadProgram("钟离 q(optional,id=可选Q),attack(.1)");
        using var io = new PhysicalReplay(clock, true, 20, program)
        { Actors = [new("钟离", 1)], DropAllSkills = true, CompleteSkillReceipts = true,
            Logger = new HostTimingLogger(() => { }) };
        io.SetFrontActor("钟离");
        io.ScheduleEnergy(0, "钟离", true);
        using var runner = NativeCombatFlowRunner.Create(program, io);
        var skipped = false;
        var result = CombatFlowResult.Unknown;
        for (var index = 0; index < 600; index++)
        {
            var step = await runner.StepAsync(default);
            skipped |= step.Result.ToString() == "SkippedOptional";
            if (step.RoundCompleted) { result = step.Result; break; }
            await io.DelayAsync(50, default);
        }
        Assert.True(result == CombatFlowResult.Succeeded, string.Join("\n", runner.RuntimeStatistics.RecentEvents
            .TakeLast(8).Select(item => $"{item.At:F3} {item.ReportedResult} {item.Reason} {string.Join(";", item.Observations)}")));
        Assert.Equal(2, io.Inputs.Count);
        Assert.Single(io.Primitives.Where(command => command.Method == Method.Attack));
        Assert.InRange(runner.Context.Now - io.Inputs[0].At, 8, 9.5);
        Assert.True(skipped);
    }

    [Theory]
    [InlineData("钟离 q(optional,required)")]
    [InlineData("钟离 e(optional)")]
    [InlineData("班尼特 q(optional)")]
    [InlineData("钟离 q(optional,record=效果)")]
    [InlineData("segment(原子,atomic) { 钟离 q(optional) }")]
    [InlineData("call(保护,required)\nsegment(保护,define) { 钟离 q(optional) }")]
    [InlineData("钟离 q(optional,id=效果)\nsegment(依赖,requires=succeeded(效果)) { 钟离 attack(.1) }")]
    public void OptionalBurstCannotWeakenProtectionOrUnknownCapabilities(string text)
        => Assert.Throws<FormatException>(() => LoadProgram(text));

    [Fact]
    public async Task AcceptedHoldSkillStillWaitsForItsContinuationWindowBeforeTheBurst()
    {
        var clock = new FakeTimeProvider();
        var program = LoadProgram("钟离 e(hold,required),q(required)");
        using var io = new PhysicalReplay(clock, false, 20, program)
        { Actors = [new("钟离", 1)], CompleteSkillReceipts = true };
        io.SetFrontActor("钟离");
        io.ScheduleEnergy(0, "钟离", true);
        using var runner = NativeCombatFlowRunner.Create(program, io);
        Assert.Equal(CombatFlowResult.Succeeded, await RunBoundedReplayRound(runner, io));
        var skill = Assert.Single(io.Inputs.Where(input => input.Skill == Method.Skill));
        var burst = Assert.Single(io.Inputs.Where(input => input.Skill == Method.Burst));
        Assert.True(burst.At - skill.At >= 2, $"E accepted too early for Q: {burst.At - skill.At:F3}s");
    }

    [Fact]
    public async Task OptionalBurstWithIncompleteReceiptMustFailWithoutRunningTheIndependentAttack()
    {
        var clock = new FakeTimeProvider();
        var program = LoadProgram("钟离 q(optional),attack(.1)");
        using var io = new PhysicalReplay(clock, false, 20, program)
        { Actors = [new("钟离", 1)], DropAllSkills = true, CompleteSkillReceipts = false };
        io.SetFrontActor("钟离");
        io.ScheduleEnergy(0, "钟离", true);
        using var runner = NativeCombatFlowRunner.Create(program, io);
        Assert.Equal(CombatFlowResult.Failed, await RunBoundedReplayRound(runner, io));
        Assert.Single(io.Inputs);
        Assert.DoesNotContain(io.Primitives, command => command.Method == Method.Attack);
    }

    [Fact]
    public async Task AddingOneOptionalActionCannotMakeAnUnmarkedRequiredActionOptional()
    {
        var clock = new FakeTimeProvider();
        var program = LoadProgram("钟离 q(optional)\n班尼特 q\n钟离 attack(.1)");
        using var io = new PhysicalReplay(clock, false, 20, program)
        { Actors = [new("钟离", 1), new("班尼特", 2)], DropAllSkills = true, CompleteSkillReceipts = true };
        io.SetFrontActor("钟离");
        io.ScheduleEnergy(0, "钟离", true);
        io.ScheduleEnergy(0, "班尼特", true);
        using var runner = NativeCombatFlowRunner.Create(program, io);
        Assert.Equal(CombatFlowResult.Failed, await RunBoundedReplayRound(runner, io));
        Assert.DoesNotContain(io.Primitives, command => command.Method == Method.Attack);
    }
}
