using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoPathing.Handler;
using System.Globalization;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

public class PickUpCollectActionTests
{
    [Fact]
    public void JeanBattlePickup_ShouldFinishItsSweepWithinTheBoundedHold()
    {
        var action = PickUpCollectHandler.PickUpActions.Single(value => value.StartsWith("琴-长E "));
        var commands = CombatScriptParser.ParseContext("琴 " + action.Split(' ', 2)[1]).CombatCommands;
        var keyDown = commands.FindIndex(command => command.Method == Method.KeyDown);
        var keyUp = commands.FindIndex(command => command.Method == Method.KeyUp);
        Assert.True(keyDown >= 0 && keyUp > keyDown);
        var heldCommands = commands.Skip(keyDown + 1).Take(keyUp - keyDown - 1).ToArray();
        var holdSeconds = heldCommands.Where(command => command.Method == Method.Wait)
            .Sum(command => double.Parse(command.Args![0], CultureInfo.InvariantCulture));
        var horizontalSweep = heldCommands.Where(command => command.Method == Method.MoveBy)
            .Sum(command => Math.Abs(int.Parse(command.Args![0], CultureInfo.InvariantCulture)));

        Assert.InRange(holdSeconds, 0.5, 3.0);
        Assert.InRange(horizontalSweep, 1, 4500);
        Assert.DoesNotContain(commands.Skip(keyUp + 1), command => command.Method == Method.MoveBy);
    }

    [Fact]
    public void JeanArtifactPickup_ShouldKeepItsExistingShortAction()
    {
        Assert.Contains("琴-短E wait(0.1),keydown(E),wait(0.4),moveby(1000,0),wait(0.2),moveby(1000,0),wait(0.2),moveby(1000,0),wait(0.2),moveby(1000,-3500),wait(1.8),keyup(E),wait(0.3),click(middle)",
            PickUpCollectHandler.PickUpActions);
    }
}
