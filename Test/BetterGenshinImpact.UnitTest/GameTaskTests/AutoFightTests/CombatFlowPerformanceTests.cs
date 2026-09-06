using System.Diagnostics;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.AutoFight.SkillData;
using Microsoft.Extensions.Time.Testing;
using Newtonsoft.Json;
using Xunit.Abstractions;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

[CollectionDefinition("CombatFlowPerformance", DisableParallelization = true)]
public class CombatFlowPerformanceCollection;

/// <summary>真实编译/调度器＋可控游戏 I/O；不截图、不发游戏输入，不声称原生 OCR 或旧版宿主已测。</summary>
[Collection("CombatFlowPerformance")]
public class CombatFlowPerformanceTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Workloads() => new[] { 20, 30, 60 }
        .SelectMany(seconds => new[] { "normal", "contention", "slow-observation", "pending", "all-skipped" }
            .Select(scenario => new object[] { seconds, scenario }));

    [Theory]
    [MemberData(nameof(Workloads))]
    public async Task RepresentativeBattleLengthsHaveBoundedProgressAndNoPrematureOutput(int seconds, string scenario)
    {
        var compileStart = Stopwatch.GetTimestamp();
        var program = Compile(scenario);
        var compileMilliseconds = Stopwatch.GetElapsedTime(compileStart).TotalMilliseconds;
        await Run(program, scenario, 1, collect: false); // 各路径预热；结果不混入分位数。
        List<double> stepTimes = [], schedulerTimes = [], observationTimes = [];
        var cpuStart = Process.GetCurrentProcess().TotalProcessorTime;
        var allocatedStart = GC.GetAllocatedBytesForCurrentThread();
        var wallStart = Stopwatch.GetTimestamp();
        var passes = 0;
        var inputs = 0;
        var observations = 0;
        var simulatedFrames = 0;
        var simulatedSwitches = 0;
        var yields = 0;
        var maxNoProgressSeconds = 0d;
        var maintenanceDecisions = 0;
        for (var repetition = 0; repetition < 10; repetition++)
        {
            var sample = await Run(program, scenario, seconds, collect: true);
            stepTimes.AddRange(sample.Steps);
            schedulerTimes.AddRange(sample.Scheduler);
            observationTimes.AddRange(sample.Game.ObservationWallMilliseconds);
            passes += sample.Passes;
            inputs += sample.Game.Inputs;
            observations += sample.Game.Observations;
            simulatedFrames += sample.Game.Frames;
            simulatedSwitches += sample.Game.Switches;
            yields += sample.Game.Yields;
            maintenanceDecisions += sample.Decisions;
            maxNoProgressSeconds = Math.Max(maxNoProgressSeconds, sample.Game.MaxNoProgress);
            Assert.Equal(0, sample.Game.PrematureOutput);
            if (scenario == "pending") Assert.InRange(sample.Game.BurstInputs, 0, 1);
            if (scenario == "all-skipped") { Assert.Equal(0, sample.Game.Inputs); Assert.True(sample.Game.Yields > 0); }
            if (scenario == "contention") Assert.True(sample.Game.Outputs > 0);
        }
        var wallMilliseconds = Stopwatch.GetElapsedTime(wallStart).TotalMilliseconds;
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedStart;
        var cpuMilliseconds = (Process.GetCurrentProcess().TotalProcessorTime - cpuStart).TotalMilliseconds;
        output.WriteLine("COMBAT_PERFORMANCE " + JsonConvert.SerializeObject(new
        {
            SchemaVersion = 1, Scenario = scenario, SimulatedBattleSeconds = seconds, Repetitions = 10,
            Adapter = "deterministic simulated game I/O; not native/game validation", CompileMilliseconds = compileMilliseconds,
            StepWallMilliseconds = Percentiles(stepTimes), SchedulerExclusiveMilliseconds = Percentiles(schedulerTimes),
            ObservationAdapterWallMilliseconds = Percentiles(observationTimes),
            SimulatedObservationLatencySeconds = scenario == "slow-observation" ? 0.2 : 0,
            FrameworkMillisecondsPerPass = schedulerTimes.Sum() / Math.Max(1, passes),
            Passes = passes, Steps = stepTimes.Count, Inputs = inputs, Observations = observations,
            SimulatedFrames = simulatedFrames, SimulatedSwitches = simulatedSwitches, Yields = yields,
            MaxSimulatedNoOutputSeconds = maxNoProgressSeconds, MaintenanceDecisionChanges = maintenanceDecisions,
            PrematureOutput = 0, MeasuredWallMilliseconds = wallMilliseconds, CpuMilliseconds = cpuMilliseconds,
            AllocatedBytesIncludingTestAdapter = allocatedBytes,
            NativeScreenshots = (int?)null, NativeOcr = (int?)null, NativeEventLatency = (double?)null,
            NativeLogVolume = (int?)null, PreChangeNativeBaseline = "not available; no before/after native speedup claim"
        }));
    }

    [Theory]
    [InlineData(20)]
    [InlineData(30)]
    [InlineData(60)]
    public async Task LongMacroCancellationChecksEveryWaitSliceAndReleasesItsHeldKey(int seconds)
    {
        var program = CombatFlowProgram.Compile($"segment(start,atomic,timeout={seconds + 5})\n琴 keydown(VK_LBUTTON),wait({seconds},required),keyup(VK_LBUTTON)\nsegment(end,record=宏完成)");
        var clock = new FakeTimeProvider();
        using var cancellation = new CancellationTokenSource();
        var game = new MeasuredGame(clock, "macro") { CancelAt = .5, Cancellation = cancellation };
        using var execution = new CombatFlowExecution(program, game, clock);
        game.Context = execution.Context;
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await execution.RunRoundAsync(cancellation.Token));
        execution.Dispose();
        Assert.False(game.Held);
        Assert.Null(execution.Context.Find("宏完成"));
        Assert.InRange(clock.GetUtcNow().Subtract(game.StartedAt).TotalSeconds, .5, .6);
        Assert.InRange(game.MaximumWaitSliceMilliseconds, 1, 50);
        output.WriteLine("COMBAT_CANCELLATION " + JsonConvert.SerializeObject(new
        { RequestedSeconds = seconds, ObservedSimulatedSeconds = clock.GetUtcNow().Subtract(game.StartedAt).TotalSeconds,
            game.MaximumWaitSliceMilliseconds, HeldAfterExit = game.Held, Adapter = "fake clock; no native input" }));
    }

    private static object Percentiles(List<double> values)
    {
        values.Sort();
        double At(double p) => values.Count == 0 ? 0 : values[(int)Math.Ceiling(p * values.Count) - 1];
        return new { Count = values.Count, P50 = At(.5), P95 = At(.95), P99 = At(.99), Max = values.LastOrDefault() };
    }

    private static CombatFlowProgram Compile(string scenario)
    {
        if (scenario == "all-skipped") return CombatFlowProgram.Compile("strategy(loop=battle)\n琴 attack(0.1,if=q-ready(琴))");
        if (scenario == "pending") return CombatFlowProgram.Compile("""
            strategy(loop=battle)
            call(关键)
            琴 attack(0.2)
            segment(start,name=关键,define)
            香菱 q(required)
            香菱 attack(1)
            segment(end)
            """);
        var lifetime = scenario == "contention" ? 5 : 20;
        var facts = new Dictionary<string, SkillFact>
        {
            ["shield.e"] = Fact("shield.e", "钟离", "shield", lifetime),
            ["buff.e"] = Fact("buff.e", "班尼特", "buff", lifetime)
        };
        return CombatFlowProgram.Compile("""
            strategy(loop=battle)
            钟离 e(record=护盾,maintain=护盾,watch=护盾,before=1,required)
            班尼特 e(record=增益,maintain=增益,watch=增益,before=1)
            segment(start,atomic,requires=record-active(护盾))
            琴 attack(1,keep=护盾,required,if=!low-hp(琴))
            琴 attack(1,keep=增益,required)
            segment(end)
            琴 attack(0.1,keep=护盾)
            """, new SkillCatalogSnapshot(facts));
    }

    private static SkillFact Fact(string id, string actor, string capability, double lifetime) => new()
    {
        Id = id, Character = actor, Slot = "e", Metrics = new() { ["duration"] = new() { Values = [lifetime] } },
        Forms = new() { ["press"] = new() { Effects = [new() { Id = capability, Capability = capability, DurationMetric = "duration" }] } }
    };

    private sealed record Sample(MeasuredGame Game, List<double> Steps, List<double> Scheduler, int Passes, int Decisions);

    private static async Task<Sample> Run(CombatFlowProgram program, string scenario, int seconds, bool collect)
    {
        var clock = new FakeTimeProvider();
        var game = new MeasuredGame(clock, scenario);
        using var execution = new CombatFlowExecution(program, game, clock);
        game.Context = execution.Context;
        List<double> steps = [], scheduler = [];
        var passes = 0;
        var decisions = 0;
        string? lastDecision = null;
        var count = 0;
        while (execution.Context.Now < seconds)
        {
            Assert.True(++count < seconds * 100 + 100, "调度未在输入、必要等待或空闲让出后推进时间");
            var adapterBefore = game.WallTicks;
            var start = Stopwatch.GetTimestamp();
            var result = await execution.StepAsync();
            var elapsed = Stopwatch.GetTimestamp() - start;
            if (collect)
            {
                steps.Add(elapsed * 1000d / Stopwatch.Frequency);
                scheduler.Add(Math.Max(0, elapsed - (game.WallTicks - adapterBefore)) * 1000d / Stopwatch.Frequency);
            }
            if (result.RoundCompleted) passes++;
            if (execution.LastMaintenanceDecision != lastDecision) { decisions++; lastDecision = execution.LastMaintenanceDecision; }
        }
        game.MaxNoProgress = Math.Max(game.MaxNoProgress, execution.Context.Now - game.LastOutputAt);
        return new(game, steps, scheduler, passes, decisions);
    }

    private sealed class MeasuredGame(FakeTimeProvider clock, string scenario) : ICombatFlowGame
    {
        private readonly Dictionary<string, object?> _frame = new(StringComparer.Ordinal);
        private CombatSkillAttempts? _attempts;
        private string? _actor;
        public DateTimeOffset StartedAt { get; } = clock.GetUtcNow();
        public CombatFlowContext Context { get; set; } = null!;
        public long WallTicks;
        public List<double> ObservationWallMilliseconds { get; } = [];
        public int Observations, Frames, Inputs, Switches, Yields, Outputs, BurstInputs, PrematureOutput;
        public double MaxNoProgress, LastOutputAt;
        public bool Held;
        public double? CancelAt;
        public CancellationTokenSource? Cancellation;
        public int MaximumWaitSliceMilliseconds;
        public void BeginStep() => _frame.Clear();
        public void ReleaseHeldInput() => Held = false;

        public object? Observe(string function, IReadOnlyList<object?> args, string actor)
        {
            var key = function + ":" + (args.FirstOrDefault()?.ToString() ?? actor);
            if (_frame.TryGetValue(key, out var cached)) return cached;
            var start = Stopwatch.GetTimestamp();
            if (_frame.Count == 0) Frames++;
            Observations++;
            if (scenario == "slow-observation") clock.Advance(TimeSpan.FromSeconds(.2));
            object? result = function switch
            {
                "q-ready" => scenario == "pending" && BurstInputs == 0 ? true : scenario == "pending" ? null : false,
                "low-hp" => false, "e-ready" => true, _ => null
            };
            _frame[key] = result;
            var elapsed = Stopwatch.GetTimestamp() - start;
            WallTicks += elapsed;
            ObservationWallMilliseconds.Add(elapsed * 1000d / Stopwatch.Frequency);
            return result;
        }

        public async ValueTask<CombatFlowResult> ExecuteAsync(CombatFlowAction action, CancellationToken ct)
        {
            var start = Stopwatch.GetTimestamp();
            try
            {
                var command = action.Command;
                _attempts ??= new(action.BattleId);
                if (command.Method == Method.Burst && _attempts.IsOccupied(command.Name, command.Method)) return CombatFlowResult.Pending;
                action.ReportActiveActor(command.Name);
                if (!action.TryBeginInput()) return CombatFlowResult.Skipped;
                Inputs++;
                if (_actor != command.Name) { Switches++; _actor = command.Name; }
                if (command.Method == Method.Burst)
                {
                    _attempts.TryBegin(command.Name, command.Method, "benchmark", action.Now, action.Now + action.RemainingBudget);
                    BurstInputs++;
                    clock.Advance(TimeSpan.FromSeconds(.2));
                    return CombatFlowResult.Pending;
                }
                if (command.Method == Method.KeyDown) Held = true;
                if (command.Method == Method.KeyUp) Held = false;
                var seconds = command.Method == Method.Skill ? scenario == "contention" ? 1 : .25
                    : command.Method == Method.Wait || command.Method == Method.Attack
                        ? double.Parse(command.Args![0], System.Globalization.CultureInfo.InvariantCulture) : .05;
                if (command.Method == Method.Attack)
                {
                    if (command.Options.TryGetValue("keep", out var keep) && Context.Remaining(keep) is not > 0) PrematureOutput++;
                    Outputs++;
                    MaxNoProgress = Math.Max(MaxNoProgress, Context.Now - LastOutputAt);
                    LastOutputAt = Context.Now;
                }
                using var scope = new CombatActionScope(action, ct);
                try
                {
                    await scope.WaitAsync((int)(seconds * 1000), (milliseconds, _) =>
                    {
                        MaximumWaitSliceMilliseconds = Math.Max(MaximumWaitSliceMilliseconds, milliseconds);
                        clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
                        if (CancelAt is { } cancel && Context.Now >= cancel) Cancellation!.Cancel();
                        return Task.CompletedTask;
                    });
                    return CombatFlowResult.Succeeded;
                }
                catch (CombatActionInterruptedException) { Held = false; return CombatFlowResult.Skipped; }
                catch (OperationCanceledException) { Held = false; throw; }
            }
            finally { WallTicks += Stopwatch.GetTimestamp() - start; }
        }

        public ValueTask YieldAsync(CancellationToken ct)
        {
            var start = Stopwatch.GetTimestamp();
            Yields++;
            clock.Advance(TimeSpan.FromMilliseconds(50));
            WallTicks += Stopwatch.GetTimestamp() - start;
            return ValueTask.CompletedTask;
        }
    }
}
