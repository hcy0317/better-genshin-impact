using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public partial class CombatNativeAdapterReplayTests
{
    [Fact]
    public async Task ExternalHostKeepsOneNativeBattleAcrossTwoRequiredShieldAndOptionalBurstRounds()
    {
        var clock = new FakeTimeProvider();
        const string text = "钟离 e(hold),q(optional),attack(1.2)";
        var parsed = CombatScriptParser.ParseContext(text);
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram(text))
        { Actors = [new("钟离", 1)], CompleteSkillReceipts = true };
        io.SetFrontActor("钟离");
        using var flow = NativeCombatFlowRunner.Create(CombatScriptExecutor.ExternalSessionCommands(parsed), io, loop: true);
        var battle = flow.Context.BattleId;
        using var cancelled = new CancellationTokenSource();
        var observations = 0;
        var scene = new SceneReplayIo(clock, battle)
        {
            BeforeObservation = () =>
            {
                Assert.Equal(battle, flow.Context.BattleId);
                if (io.Primitives.Count(command => command.Method == Method.Attack) >= 2) cancelled.Cancel();
                Assert.True(++observations < 2000, "两轮战斗未在有界观察内完成");
            }
        };
        using var host = new CombatBattleHost(scene, new() { ExternalCompletionAuthority = true,
            FinishDetectionEnabled = false, SeekEnabled = false, TimeoutSeconds = 30 });
        var bindings = new List<Guid>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CombatScriptExecutor.RunExternalSessionCoreAsync(flow, host, bindings.Add, cancelled.Token));
        Assert.Equal(new[] { battle }, bindings);
        Assert.Equal(2, io.Primitives.Count(command => command.Method == Method.Attack));
        Assert.DoesNotContain(io.Inputs, input => input.Skill == Method.Burst);
        Assert.True(io.FirstShieldConfirmed >= 0);
    }

    [Fact]
    public async Task ExternalDefaultRequiredBurstCannotBeSilentlySkippedLikeALegacyFragment()
    {
        var clock = new FakeTimeProvider();
        const string text = "钟离 q,attack(.1)";
        var commands = CombatScriptExecutor.ExternalSessionCommands(CombatScriptParser.ParseContext(text));
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram(text)) { Actors = [new("钟离", 1)] };
        io.SetFrontActor("钟离");
        using var flow = NativeCombatFlowRunner.Create(commands, io, loop: false);
        Assert.Equal(CombatFlowResult.Deferred, await RunBoundedReplayRound(flow, io));
        Assert.DoesNotContain(io.Primitives, command => command.Method == Method.Attack);
    }
}
