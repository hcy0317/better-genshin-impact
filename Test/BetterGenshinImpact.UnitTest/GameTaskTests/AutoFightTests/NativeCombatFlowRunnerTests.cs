using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.AutoFight.SkillData;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class NativeCombatFlowRunnerTests
{
    [Theory]
    [InlineData("missing-file")]
    [InlineData("corrupt-file")]
    [InlineData("missing-character")]
    public async Task NativeEntryUsesInlineTimingWhenTheCatalogCannotSupplyTheCharacter(string catalogState)
    {
        var directory = Path.Combine(Path.GetTempPath(), "bgi-native-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "skills.db");
        if (catalogState == "corrupt-file") File.WriteAllText(path, "This is not a SQLite database.");
        if (catalogState == "missing-character")
            new SkillCatalogStore(path).Import([new()
            {
                Id = "other.e", CharacterKey = "other", Character = "班尼特", Slot = "e",
                Revision = "offline-test", SourceUrl = "https://example.com/offline-test",
                Metrics = new() { ["duration"] = new() { Values = [20] } }
            }], "offline-test", DateTimeOffset.UtcNow);

        var strategy = new JsonCombatStrategy
        {
            Info = new() { Declarations = ["timing(随策略携带,cd=5,duration=8)"] },
            Actions = [new() { Character = "琴", Action = "e(timing=随策略携带,record=窗口),attack(0.1,keep=窗口)" }]
        };
        var game = new FailedSkillGame { Result = CombatFlowResult.Succeeded };
        using var runner = NativeCombatFlowRunner.Create(strategy, game,
            () => new SkillCatalogStore(path).ReadSnapshot(), new FakeTimeProvider());
        Assert.NotNull(runner);
        var completed = false;
        for (var step = 0; step < 10 && !completed; step++) completed = (await runner.StepAsync(default)).RoundCompleted;
        Assert.True(completed);
        Assert.Equal(8, runner.Context.Find("窗口")!.Duration);
        Assert.Equal(new[] { Method.Skill, Method.Attack }, game.Actions);
    }

    [Fact]
    public void InputReleaseFailureStillClosesTheGameAdapterAndBattle()
    {
        var game = new FailedSkillGame { ThrowOnRelease = true };
        var runner = NativeCombatFlowRunner.Create(new JsonCombatStrategy
        { Actions = [new() { Action = "record(标记)" }] }, game)!;
        Assert.Throws<InvalidOperationException>(() => runner.Dispose());
        Assert.False(runner.Context.IsOpen);
        Assert.True(game.Closed);
    }

    [Fact]
    public async Task CheckRequestsTheExistingHostDetectorWithoutSendingCharacterInput()
    {
        var strategy = new JsonCombatStrategy
        {
            Actions = [new() { Action = "record(检查前),check" }]
        };
        var game = new FailedSkillGame();
        using var runner = NativeCombatFlowRunner.Create(strategy, game, clock: new FakeTimeProvider());
        await runner!.StepAsync(default);
        Assert.Empty(game.Actions);
        Assert.True(runner.TakeFinishCheckRequest());
        Assert.False(runner.TakeFinishCheckRequest());
    }

    [Fact]
    public async Task EnhancedJsonFailureReturnsToTheHostAndClosingTheRunnerClosesItsBattle()
    {
        var strategy = new JsonCombatStrategy
        {
            Info = new() { Declarations = ["segment(start,name=开场,define)\n琴 e(required)\nsegment(end,record=开场完成)"] },
            Actions = [new() { Character = "琴", Action = "call(开场,once=battle,required),attack(0.1)" }]
        };
        var game = new FailedSkillGame();
        using var runner = NativeCombatFlowRunner.Create(strategy, game, clock: new FakeTimeProvider());
        Assert.NotNull(runner);
        await Assert.ThrowsAsync<CombatFlowRecoveryException>(async () =>
        {
            for (var i = 0; i < 10; i++) await runner.StepAsync(default);
        });
        Assert.Null(runner.Context.Find("开场完成"));
        Assert.Equal(new[] { Method.Skill }, game.Actions);
        runner.Dispose();
        Assert.False(runner.Context.IsOpen);
        Assert.True(game.Closed);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await runner.StepAsync(default));
    }

    private sealed class FailedSkillGame : ICombatFlowGame, IDisposable
    {
        public List<Method> Actions { get; } = [];
        public bool Closed { get; private set; }
        public bool ThrowOnRelease { get; init; }
        public CombatFlowResult Result { get; init; } = CombatFlowResult.Failed;
        public void ReleaseHeldInput() { if (ThrowOnRelease) throw new InvalidOperationException("模拟释放失败"); }
        public ValueTask<CombatFlowResult> ExecuteAsync(CombatFlowAction action, CancellationToken ct)
        {
            if (!action.TryBeginInput()) return ValueTask.FromResult(CombatFlowResult.Skipped);
            Actions.Add(action.Command.Method);
            return ValueTask.FromResult(Result);
        }
        public object? Observe(string function, IReadOnlyList<object?> args, string actor) => null;
        public ValueTask YieldAsync(CancellationToken ct) => ValueTask.CompletedTask;
        public void Dispose() => Closed = true;
    }
}
