using System.Runtime.CompilerServices;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoFight.Script;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class GatheredLootCommandsTests
{
    [Fact]
    public void FailedCommandStopsTheSequenceAndReleasesHeldInputs()
    {
        var picker = (Avatar)RuntimeHelpers.GetUninitializedObject(typeof(Avatar));
        picker.CombatScenes = (CombatScenes)RuntimeHelpers.GetUninitializedObject(typeof(CombatScenes));
        var commands = CombatScriptParser.ParseContext("琴 keydown(e),wait(0.1),keyup(e)").CombatCommands;
        var calls = 0; var observations = 0; var releases = 0;
        Assert.False(GatheredLootCommands.Run(picker, commands, () => observations++, () => releases++, default,
            (_, _, _) => ++calls < 2));
        Assert.Equal(2, calls);
        Assert.Equal(1, observations);
        Assert.Equal(1, releases);
    }

    [Fact]
    public void ExceptionsAndCancellationPropagateAfterReleasingInputs()
    {
        var picker = (Avatar)RuntimeHelpers.GetUninitializedObject(typeof(Avatar));
        picker.CombatScenes = (CombatScenes)RuntimeHelpers.GetUninitializedObject(typeof(CombatScenes));
        var commands = CombatScriptParser.ParseContext("琴 keydown(e)").CombatCommands;
        var releases = 0;
        var error = new InvalidOperationException("input failed");
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() => GatheredLootCommands.Run(
            picker, commands, () => { }, () => releases++, default, (_, _, _) => throw error)));
        using var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => GatheredLootCommands.Run(picker, commands,
            () => { }, () => releases++, cts.Token, (_, _, _) => throw new Exception("must not execute")));
        Assert.Equal(2, releases);
    }
    [Fact]
    public void CommandsUsePickerOwningSceneAndAlwaysReleaseInputs()
    {
        var oldScene = (CombatScenes)RuntimeHelpers.GetUninitializedObject(typeof(CombatScenes));
        var newScene = (CombatScenes)RuntimeHelpers.GetUninitializedObject(typeof(CombatScenes));
        var picker = (Avatar)RuntimeHelpers.GetUninitializedObject(typeof(Avatar));
        picker.CombatScenes = newScene;
        var commands = CombatScriptParser.ParseContext("琴 keydown(e),keyup(e)").CombatCommands;
        var calls = 0;
        var releases = 0;
        Assert.True(GatheredLootCommands.Run(picker, commands, () => { }, () => releases++, default,
            (command, scene, previous) =>
            {
                Assert.Same(newScene, scene);
                Assert.NotSame(oldScene, scene);
                if (calls++ > 0) Assert.Same(commands[0], previous);
                return true;
            }));
        Assert.Equal(2, calls);
        Assert.Equal(1, releases);
    }
}
