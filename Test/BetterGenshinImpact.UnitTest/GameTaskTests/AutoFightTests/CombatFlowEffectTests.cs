using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.AutoFight.SkillData;
using Microsoft.Extensions.Time.Testing;
using Newtonsoft.Json;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatFlowEffectTests
{
    [Theory]
    [InlineData(CombatFlowResult.Pending)]
    [InlineData(CombatFlowResult.Unknown)]
    [InlineData(CombatFlowResult.Failed)]
    [InlineData(CombatFlowResult.Skipped)]
    public async Task AnUnconfirmedBurstNeverRefreshesTheExistingEffect(CombatFlowResult result)
    {
        var program = CombatFlowProgram.Compile("珊瑚宫心海 e(record=水母)\n珊瑚宫心海 q(keep=水母,refresh=水母)", Catalog());
        var clock = new FakeTimeProvider();
        var game = new EffectGame(clock) { BurstResult = result };
        using var execution = new CombatFlowExecution(program, game, clock);
        await execution.StepAsync();
        var original = execution.Context.Find("水母")!;
        clock.Advance(TimeSpan.FromSeconds(3));
        game.ActionDuration = TimeSpan.FromSeconds(1);
        await execution.StepAsync();
        Assert.Equal(original, execution.Context.Find("水母"));
        Assert.Equal(8, execution.Context.Remaining("水母"));
        Assert.Equal(2, game.Actions.Count);
    }

    [Fact]
    public void LeavingOneConfirmedRangeDoesNotRevokeAnUnrelatedRangeOrEventHistory()
    {
        var clock = new FakeTimeProvider();
        using var context = new CombatFlowContext(clock);
        context.TryRecord("水母窗口", 0, 12, new("珊瑚宫心海", "e", "summon", Scope: "range", RangeId: "field-A"));
        context.TryReference("治疗前提", "水母窗口");
        context.TryRecord("另一个领域", 0, 20, new("班尼特", "q", "field", Scope: "range", RangeId: "field-B"));
        Assert.True(context.ObserveScope(new(context.BattleId, 1, 0, RangeId: "field-A", InRange: false)));
        Assert.Equal(0, context.Remaining("水母窗口"));
        Assert.Equal(0, context.Remaining("治疗前提"));
        Assert.Equal(20, context.Remaining("另一个领域"));
        Assert.NotNull(context.Find("水母窗口"));
    }

    [Fact]
    public void ConfirmedTargetChangeRevokesOldAliasesButRejectsStaleOrForeignObservations()
    {
        var clock = new FakeTimeProvider();
        using var context = new CombatFlowContext(clock);
        Assert.True(context.ObserveScope(new(context.BattleId, 1, 0, TargetId: "enemy-A")));
        context.TryRecord("草标记", 0, 12, new("纳西妲", "e", "mark", Scope: "target", TargetId: "enemy-A"));
        context.TryReference("输出前提", "草标记");
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(context.ObserveScope(new(Guid.NewGuid(), 2, 1, TargetId: "enemy-B")));
        Assert.Equal(11, context.Remaining("草标记"));
        Assert.True(context.ObserveScope(new(context.BattleId, 2, 1, TargetId: "enemy-B")));
        Assert.Equal(0, context.Remaining("草标记"));
        Assert.Equal(0, context.Remaining("输出前提"));
        Assert.NotNull(context.Find("草标记"));
        Assert.False(context.ObserveScope(new(context.BattleId, 1, 0, TargetId: "enemy-A")));
        Assert.Equal(0, context.Remaining("草标记"));
    }

    [Fact]
    public async Task RefreshCannotAuthorizeAProducerFromAnotherSourceRevision()
    {
        var catalog = Catalog();
        catalog.Skills["test.e"].Revision = "test-v2";
        var clock = new FakeTimeProvider();
        var game = new EffectGame(clock);
        using var execution = new CombatFlowExecution(CombatFlowProgram.Compile("""
            珊瑚宫心海 e(record=水母)
            珊瑚宫心海 q(keep=水母,refresh=水母)
            """, catalog), game, clock);
        await execution.RunRoundAsync();
        Assert.Single(game.Actions);
    }

    [Fact]
    public void SwitchingRevokesOnlyEffectsWhoseMechanicsEndOnSwitch()
    {
        using var context = new CombatFlowContext(new FakeTimeProvider());
        context.TryRecord("站场姿态", 0, 12, new("珊瑚宫心海", "q", "test.stance", EndsOnSwitch: true));
        context.TryRecord("后台效果", 0, 12, new("珊瑚宫心海", "e", "test.summon"));
        context.ObserveActiveActor("珊瑚宫心海");
        Assert.Equal(12, context.Remaining("站场姿态"));
        context.ObserveActiveActor("琴");
        Assert.Equal(0, context.Remaining("站场姿态"));
        Assert.Equal(12, context.Remaining("后台效果"));
    }

    [Fact]
    public void ConfirmedDisappearanceRevokesAllReferencesWithoutErasingTheEvents()
    {
        var clock = new FakeTimeProvider();
        using var context = new CombatFlowContext(clock);
        context.TryRecord("主标记", 0, 12, new("珊瑚宫心海", "e", "test.effect"));
        Assert.True(context.TryReference("别名", "主标记"));
        var original = context.Find("主标记")!;
        Assert.Equal(original.EffectInstanceId, context.Find("别名")!.EffectInstanceId);
        Assert.True(context.InvalidateEffect(original.EffectInstanceId!.Value, original.EffectVersion, "confirmed disappearance"));
        Assert.Equal(0, context.Remaining("主标记"));
        Assert.Equal(0, context.Remaining("别名"));
        Assert.Equal(0, context.Find("主标记")!.OccurredAt);
        Assert.False(context.TryRefresh("主标记", original.Generation, 1, 12, original.EffectVersion));
    }

    [Fact]
    public async Task RefreshRechecksTheWindowAfterSwitchingBeforeSendingInput()
    {
        var program = CombatFlowProgram.Compile("""
            珊瑚宫心海 e(record=水母)
            珊瑚宫心海 q(keep=水母,refresh=水母)
            """, Catalog());
        var clock = new FakeTimeProvider();
        var game = new EffectGame(clock);
        using var execution = new CombatFlowExecution(program, game, clock);
        await execution.StepAsync();
        clock.Advance(TimeSpan.FromSeconds(8));
        game.PrepareDelay = TimeSpan.FromSeconds(3);
        await execution.RunRoundAsync();
        Assert.Single(game.Actions);
        Assert.Equal(0, execution.Context.Find("水母")!.OccurredAt);
    }

    [Fact]
    public async Task RefreshWithInsufficientTimeForTheEffectNeverSendsInput()
    {
        var program = CombatFlowProgram.Compile("""
            珊瑚宫心海 e(record=水母)
            珊瑚宫心海 q(keep=水母,refresh=水母)
            """, Catalog());
        var clock = new FakeTimeProvider();
        var game = new EffectGame(clock);
        using var execution = new CombatFlowExecution(program, game, clock);
        await execution.StepAsync();
        var original = execution.Context.Find("水母");
        clock.Advance(TimeSpan.FromSeconds(11.999));
        await execution.RunRoundAsync();
        Assert.Single(game.Actions);
        Assert.Equal(original, execution.Context.Find("水母"));
    }

    [Theory]
    [InlineData("珊瑚宫心海 e(effect=不存在,record=水母)")]
    [InlineData("珊瑚宫心海 e(record=水母)\nrecord(水母,duration=12)")]
    public void IncompatibleEffectDeclarationsAreRejectedBeforeGameInput(string text)
    {
        Assert.Throws<FormatException>(() => CombatFlowProgram.Compile(text, Catalog()));
    }

    [Fact]
    public void ReplacingAnEffectUnderAnotherNameRevokesOldCoverageButKeepsItsHistory()
    {
        var clock = new FakeTimeProvider();
        using var context = new CombatFlowContext(clock);
        var source = new CombatRecordSource("珊瑚宫心海", "e", "test.water-summon", "test-v1", "test.e", "summon");
        context.TryRecord("旧标记", 0, 12, source);
        clock.Advance(TimeSpan.FromSeconds(5));
        context.TryRecord("新标记", 5, 12, source);
        Assert.Equal(0, context.Remaining("旧标记"));
        Assert.Equal(0, context.Find("旧标记")!.OccurredAt);
        Assert.Equal(12, context.Remaining("新标记"));
    }

    [Fact]
    public async Task ConfirmedRelationRefreshesTheNamedEffectFromTheInputTime()
    {
        var program = CombatFlowProgram.Compile("""
            珊瑚宫心海 e(record=水母)
            珊瑚宫心海 q(keep=水母,refresh=水母)
            """, Catalog());
        var clock = new FakeTimeProvider();
        var game = new EffectGame(clock);
        using var execution = new CombatFlowExecution(program, game, clock);
        await execution.StepAsync();
        var original = execution.Context.Find("水母")!;
        clock.Advance(TimeSpan.FromSeconds(6));
        game.ActionDuration = TimeSpan.FromSeconds(1);
        await execution.StepAsync();
        var refreshed = execution.Context.Find("水母")!;
        Assert.Equal(2, game.Actions.Count);
        Assert.Equal("test.water-summon", refreshed.Source!.Effect);
        Assert.True(refreshed.Generation > original.Generation);
        Assert.Equal(6, refreshed.OccurredAt);
        Assert.Equal(11, execution.Context.Remaining("水母"));
    }

    private static SkillCatalogSnapshot Catalog()
    {
        // Synthetic mechanics at the data boundary, not a claim about a live game.
        var skills = JsonConvert.DeserializeObject<SkillFact[]>("""
            [
              {"Id":"test.e","CharacterKey":"test","Character":"珊瑚宫心海","Slot":"e","Revision":"test-v1",
               "Metrics":{"duration":{"Values":[12]},"cd":{"Values":[20]}},
               "Forms":{"press":{"CooldownMetric":"cd","Effects":[{"Id":"test.water-summon","Capability":"summon","DurationMetric":"duration"}]}}},
              {"Id":"test.q","CharacterKey":"test","Character":"珊瑚宫心海","Slot":"q","Revision":"test-v1",
               "Forms":{"press":{"Refreshes":[{"EffectId":"test.water-summon","ProducerSkillId":"test.e","Verified":true,"Revision":"test-v1","ProducerRevision":"test-v1","SourceUrl":"https://example.com/mechanics"}]}}}
            ]
            """)!;
        return new(skills.ToDictionary(skill => skill.Id));
    }

    private sealed class EffectGame(FakeTimeProvider clock) : ICombatFlowGame
    {
        public CombatFlowResult BurstResult { get; set; } = CombatFlowResult.Succeeded;
        public TimeSpan PrepareDelay { get; set; }
        public TimeSpan ActionDuration { get; set; }
        public List<CombatCommand> Actions { get; } = [];
        public ValueTask<CombatFlowResult> ExecuteAsync(CombatFlowAction action, CancellationToken ct)
        {
            clock.Advance(PrepareDelay);
            action.ReportActiveActor(action.Command.Name);
            if (!action.TryBeginInput()) return ValueTask.FromResult(CombatFlowResult.Skipped);
            Actions.Add(action.Command);
            clock.Advance(ActionDuration);
            return ValueTask.FromResult(action.Command.Method == Method.Burst ? BurstResult : CombatFlowResult.Succeeded);
        }
        public object? Observe(string function, IReadOnlyList<object?> args, string actor) => null;
        public ValueTask YieldAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }
}
