using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.AutoFight.SkillData;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class ElementPartyFlowTests
{
    public static IEnumerable<object[]> SupportBurstStates() => new[] { "风", "草", "岩", "矿物", "采集" }
        .SelectMany(party => new bool?[] { true, false, null }.Select(ready => new object[] { party, ready! }));

    [Theory]
    [MemberData(nameof(SupportBurstStates))]
    public async Task IndependentSupportBurstsDoNotReplaceOrRepeatTheDamageRoute(string party, bool? ready)
    {
        var clock = new FakeTimeProvider();
        var game = new ScriptGame(clock);
        var actors = party switch
        {
            "风" => new[] { "琴", "芙宁娜", "枫原万叶" },
            "草" => ["纳西妲", "芙宁娜"],
            "岩" => ["班尼特", "香菱", "娜维娅"],
            _ => ["琴"]
        };
        foreach (var actor in actors) game.BurstReadiness[actor] = ready;
        using var execution = new CombatFlowExecution(LoadProgram(party), game, clock);
        await RunPass(execution);
        var damageActor = party switch { "风" => "枫原万叶", "草" => "久岐忍", "采集" => "纳西妲", _ => "娜维娅" };
        Assert.Single(game.Inputs.Where(command => command.Name == damageActor && command.Method == Method.Skill));
        Assert.Equal(ready == true ? actors.Length : 0, game.Inputs.Count(command => command.Method == Method.Burst));
        Assert.Contains(game.Inputs, command => command.Method == Method.Attack);
        Assert.All(game.Inputs.Where(command => command.Method == Method.Burst).GroupBy(command => command.Name), group => Assert.Single(group));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, null)]
    [InlineData(null, true)]
    public async Task FireCannotEnterItsTwoBurstRouteWhenOnlyOneActorIsReady(bool? bennett, bool? xiangling)
    {
        var clock = new FakeTimeProvider();
        var game = new ScriptGame(clock);
        game.BurstReadiness["班尼特"] = bennett;
        game.BurstReadiness["香菱"] = xiangling;
        using var execution = new CombatFlowExecution(LoadProgram("火"), game, clock);
        await RunPass(execution);
        Assert.DoesNotContain(game.Inputs, command => command.Method == Method.Burst);
        Assert.Null(execution.Context.Find("本次双火完成"));
        Assert.Contains(game.Inputs, command => command.Name == "班尼特" && command.Method == Method.Attack && command.Args![0] == "0.6");
    }

    [Theory]
    [InlineData(true, CombatFlowResult.Succeeded, 2, 0, 1)]
    [InlineData(false, CombatFlowResult.Succeeded, 1, 1, 0)]
    [InlineData(null, CombatFlowResult.Succeeded, 1, 1, 0)]
    [InlineData(true, CombatFlowResult.Pending, 1, 1, 1)]
    [InlineData(true, CombatFlowResult.Failed, 1, 1, 1)]
    [InlineData(true, CombatFlowResult.Unknown, 1, 1, 1)]
    public async Task WaterChoosesOneOutputRouteFromItsOwnBurstState(bool? ready, CombatFlowResult burst, int sweeps, int skills, int bursts)
    {
        var clock = new FakeTimeProvider();
        var game = new ScriptGame(clock) { BurstResult = burst };
        game.BurstReadiness["那维莱特"] = ready;
        using var execution = new CombatFlowExecution(LoadProgram("水"), game, clock);
        await RunPass(execution);
        Assert.Equal(sweeps, game.Inputs.Count(command => command.Name == "那维莱特" && command.Method == Method.KeyDown));
        var neuvillette = game.Inputs.Where(command => command.Name == "那维莱特").ToArray();
        Assert.Equal(skills, neuvillette.Count(command => command.Method == Method.Skill));
        Assert.Equal(bursts, neuvillette.Count(command => command.Method == Method.Burst));
        Assert.Equal(ready == true ? Method.Burst : Method.Skill, neuvillette[0].Method);
        Assert.DoesNotContain(game.Inputs, command => command.Name == "琴" && command.Method == Method.Skill);
    }

    [Theory]
    [InlineData("雷", true, CombatFlowResult.Succeeded)]
    [InlineData("雷", true, CombatFlowResult.Pending)]
    [InlineData("雷", false, CombatFlowResult.Succeeded)]
    [InlineData("雷", null, CombatFlowResult.Succeeded)]
    [InlineData("冰", true, CombatFlowResult.Succeeded)]
    [InlineData("冰", true, CombatFlowResult.Pending)]
    [InlineData("冰", false, CombatFlowResult.Succeeded)]
    [InlineData("冰", null, CombatFlowResult.Succeeded)]
    [InlineData("火", true, CombatFlowResult.Succeeded)]
    [InlineData("火", true, CombatFlowResult.Pending)]
    [InlineData("火", false, CombatFlowResult.Succeeded)]
    [InlineData("火", null, CombatFlowResult.Succeeded)]
    public async Task CriticalBurstRoutesAndTheirFallbacksAreMutuallyExclusive(string party, bool? ready, CombatFlowResult burst)
    {
        var clock = new FakeTimeProvider();
        var game = new ScriptGame(clock) { BurstResult = burst };
        var (actors, completed, fallbackActor, fallbackSeconds) = party switch
        {
            "雷" => (new[] { "雷电将军" }, "本次拔刀完成", "纳西妲", "0.6"),
            "冰" => (new[] { "神里绫华" }, "本次霜灭完成", "神里绫华", "0.5"),
            _ => (new[] { "班尼特", "香菱" }, "本次双火完成", "班尼特", "0.6")
        };
        foreach (var actor in actors) game.BurstReadiness[actor] = ready;
        using var execution = new CombatFlowExecution(LoadProgram(party), game, clock);
        await RunPass(execution);
        var succeeded = ready == true && burst == CombatFlowResult.Succeeded;
        Assert.Equal(succeeded, execution.Context.Find(completed) != null);
        Assert.Equal(!succeeded, game.Inputs.Any(command => command.Name == fallbackActor &&
            command.Method == Method.Attack && command.Args![0] == fallbackSeconds));
        Assert.Equal(ready == true ? succeeded ? actors.Length : 1 : 0,
            game.Inputs.Count(command => actors.Contains(command.Name) && command.Method == Method.Burst));
    }

    [Theory]
    [InlineData(0.1, "1.4")]
    [InlineData(2.1, "0.5")]
    [InlineData(3.0, "0.4")]
    public async Task NaviaSelectsOnlyTheShortAttackThatFitsThisShotsRemainingInfusion(double delay, string attack)
    {
        var clock = new FakeTimeProvider();
        var game = new ScriptGame(clock) { NaviaSkillSeconds = delay };
        using var execution = new CombatFlowExecution(LoadProgram("岩", knownPassives: true), game, clock);
        await RunPass(execution);
        var shot = game.Inputs.FindIndex(command => command.Name == "娜维娅" && command.Method == Method.Skill);
        Assert.True(shot >= 0);
        Assert.Equal(attack, game.Inputs.Skip(shot + 1).First(command => command.Method == Method.Attack).Args![0]);
        Assert.Single(game.Inputs.Where(command => command.Name == "娜维娅" && command.Method == Method.Skill));
    }

    [Theory]
    [InlineData("水")]
    [InlineData("火")]
    [InlineData("风")]
    [InlineData("雷")]
    [InlineData("草")]
    [InlineData("冰")]
    [InlineData("岩")]
    [InlineData("矿物")]
    [InlineData("采集")]
    public async Task EachPresetRequiresANewSuccessfulOpeningAndLatchesItOnlyWithinOneBattle(string party)
    {
        var program = LoadProgram(party);
        var clock = new FakeTimeProvider();
        var game = new ScriptGame(clock) { FailFirstShield = true };
        using var execution = new CombatFlowExecution(program, game, clock);
        Assert.Equal(CombatFlowResult.Failed, await RunPass(execution));
        Assert.Null(execution.Context.Find("开场完成"));
        Assert.DoesNotContain(game.Inputs, command => command.Method == Method.Attack || command.Method == Method.Charge);
        await RunPass(execution);
        var opening = execution.Context.Find("开场完成");
        Assert.NotNull(opening);
        await RunPass(execution);
        Assert.Equal(opening, execution.Context.Find("开场完成"));
        using var nextBattle = new CombatFlowExecution(program, new ScriptGame(clock), clock);
        Assert.Null(nextBattle.Context.Find("开场完成"));
        await RunPass(nextBattle);
        Assert.NotNull(nextBattle.Context.Find("开场完成"));
    }

    private static async Task<CombatFlowResult> RunPass(CombatFlowExecution execution)
    {
        for (var step = 0; step < 400; step++)
        {
            var result = await execution.StepAsync();
            if (result.RoundCompleted) return result.Result;
        }
        throw new Xunit.Sdk.XunitException("策略未在有界步骤内到达根边界");
    }

    private static CombatFlowProgram LoadProgram(string party, bool database = true, bool knownPassives = false)
    {
        var script = CombatScriptParser.Parse(RepoPath("BetterGenshinImpact", "User", "AutoFight", "00-" + party + ".txt"));
        if (!database) return CombatFlowProgram.Compile(script);
        var seed = CombatSkillCatalog.ParseLocalJson<SkillCatalogSeed>(File.ReadAllText(RepoPath("BetterGenshinImpact", "GameTask", "AutoFight", "Assets", "SkillData", "builtin-skills.json")));
        var rules = CombatSkillCatalog.ParseLocalJson<SkillMechanicsRulePack>(File.ReadAllText(RepoPath("BetterGenshinImpact", "GameTask", "AutoFight", "Assets", "SkillData", "builtin-mechanics.json")));
        var profiles = knownPassives ? new Dictionary<string, CharacterSkillProfile>
        {
            ["navia"] = new() { CharacterKey = "navia", UnlockedPassives = ["navia.passive1"], Reason = "已知测试适用条件" },
            ["sangonomiyakokomi"] = new() { CharacterKey = "sangonomiyakokomi", UnlockedPassives = ["sangonomiyakokomi.passive1"], Reason = "已知测试适用条件" }
        } : null;
        return CombatFlowProgram.Compile(script, new SkillCatalogSnapshot(rules.Apply(seed.Skills.ToDictionary(skill => skill.Id)), profiles));
    }

    private static string RepoPath(params string[] segments)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "BetterGenshinImpact.sln"))) root = root.Parent;
        return Path.Combine([root!.FullName, .. segments]);
    }

    private sealed class ScriptGame(FakeTimeProvider clock) : ICombatFlowGame
    {
        public bool FailFirstShield { get; set; }
        public bool BurstReady { get; set; }
        public Dictionary<string, bool?> BurstReadiness { get; } = new(StringComparer.Ordinal);
        private readonly HashSet<string> _spentBursts = new(StringComparer.Ordinal);
        private bool? ReadBurst(string actor) => _spentBursts.Contains(actor) ? false : BurstReadiness.GetValueOrDefault(actor, BurstReady);
        public CombatFlowResult BurstResult { get; set; } = CombatFlowResult.Succeeded;
        public double NaviaSkillSeconds { get; set; } = 0.1;
        public List<CombatCommand> Inputs { get; } = [];
        public ValueTask<CombatFlowResult> ExecuteAsync(CombatFlowAction action, CancellationToken ct)
        {
            if (action.Command.Method == Method.Burst && ReadBurst(action.Command.Name) != true) return ValueTask.FromResult(CombatFlowResult.Skipped);
            action.ReportActiveActor(action.Command.Name);
            if (!action.TryBeginInput()) return ValueTask.FromResult(CombatFlowResult.Skipped);
            Inputs.Add(action.Command);
            var seconds = action.Command.Method == Method.Wait || action.Command.Method == Method.Attack || action.Command.Method == Method.Charge
                ? double.Parse(action.Command.Args![0], System.Globalization.CultureInfo.InvariantCulture)
                : action.Command.Name == "娜维娅" && action.Command.Method == Method.Skill ? NaviaSkillSeconds : 0.1;
            clock.Advance(TimeSpan.FromSeconds(seconds));
            if (action.Command.Name == "钟离" && action.Command.Method == Method.Skill && FailFirstShield)
            {
                FailFirstShield = false;
                return ValueTask.FromResult(CombatFlowResult.Failed);
            }
            if (action.Command.Method == Method.Burst && BurstResult == CombatFlowResult.Succeeded) _spentBursts.Add(action.Command.Name);
            return ValueTask.FromResult(action.Command.Method == Method.Burst ? BurstResult : CombatFlowResult.Succeeded);
        }
        public object? Observe(string function, IReadOnlyList<object?> args, string actor) => function switch
        {
            "q-ready" => ReadBurst(args.FirstOrDefault()?.ToString() ?? actor), "e-ready" => true,
            "q-energy-low" => _spentBursts.Contains(args.FirstOrDefault()?.ToString() ?? actor) ? true : null,
            "q-cd" => _spentBursts.Contains(args.FirstOrDefault()?.ToString() ?? actor) ? true : null, "low-hp" => false,
            _ => null
        };
        public ValueTask YieldAsync(CancellationToken ct) { clock.Advance(TimeSpan.FromMilliseconds(50)); return ValueTask.CompletedTask; }
    }
}
