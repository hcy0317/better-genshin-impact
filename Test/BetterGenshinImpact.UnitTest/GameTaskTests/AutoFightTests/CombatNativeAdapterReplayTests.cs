using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.AutoFight.SkillData;
using Fischless.GameCapture;
using Microsoft.Extensions.Time.Testing;
using Xunit.Abstractions;
using BetterGenshinImpact.GameTask.Model.Area;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

// B层：真实解析/调度/在途协议与选角策略，帧到达和游戏物理效果是可控外部边界。
public class CombatNativeAdapterReplayTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(300)]
    [InlineData(1800)]
    public async Task BlockingTailObservationReleasesInputAndCannotCompleteTheMacro(double blockedCost)
    {
        var program = LoadProgram("call(宏,required)\nsegment(宏,define,atomic,record=完成) { 那维莱特 keydown(VK_LBUTTON), wait(0.08), moveby(10,0), keyup(VK_LBUTTON) }");
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, program) { HeldCaptureCost = blockedCost };
        using var runner = NativeCombatFlowRunner.Create(program, io);
        CombatFlowStep step = default;
        for (var i = 0; i < 20 && !step.RoundCompleted; i++) step = await runner.StepAsync(default);
        Assert.Null(runner.Context.Find("完成"));
        Assert.Equal(CombatFlowResult.Failed, step.Result);
        Assert.Single(io.Primitives);
        Assert.False(io.HoldingInput);
        Assert.Contains(runner.RuntimeStatistics.RecentEvents, entry => entry.Reason?.Contains("150ms") == true);
        runner.Dispose();
        Assert.All(io.CapturedFrames, frame => Assert.True(frame.SrcMat.IsDisposed));
    }

    [Fact]
    public async Task CancellationInsideTheTailObservationReleasesHeldInputBeforeReturning()
    {
        var program = LoadProgram("segment(宏,atomic) { 那维莱特 keydown(VK_LBUTTON), wait(0.08), keyup(VK_LBUTTON) }");
        var clock = new FakeTimeProvider();
        using var cancellation = new CancellationTokenSource();
        using var io = new PhysicalReplay(clock, false, 50, program);
        io.AfterCapture = () => { if (io.Primitives.Count != 0) cancellation.Cancel(); };
        using var runner = NativeCombatFlowRunner.Create(program, io);
        await runner.StepAsync(cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await runner.StepAsync(cancellation.Token));
        Assert.False(io.HoldingInput);
        Assert.Single(io.Primitives);
    }

    [Theory]
    [InlineData(false, 50)]
    [InlineData(false, 150)]
    [InlineData(true, 50)]
    [InlineData(true, 150)]
    public async Task WaterBranchWithoutMaintenanceMeetsItsOriginalWindow(bool burstReady, double decisionCost)
    {
        var script = LoadWaterScript();
        // 沿用发布策略原定义体；此独立场景只让所选主轴在新护盾后立即开始，明确排除必要维护压力。
        var program = LoadProgram("timing(离线护盾,cd=12,duration=20)\ncall(开场,once=battle,required)\n" +
            "branch(if=q-ready(那维莱特),then=Q双喷,else=E单喷,unknown=E单喷)\n" + script[script.IndexOf("segment(开场", StringComparison.Ordinal)..]);
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, burstReady, decisionCost, program);
        using var runner = NativeCombatFlowRunner.Create(program, io);
        var ledger = new OutputLedger();
        CombatFlowStep step = default;
        for (var i = 0; i < 500 && !step.RoundCompleted; i++)
        {
            step = await runner.StepAsync(default);
            ledger.Observe(runner.RuntimeStatistics, runner.Context.Find("喷射宏完成")?.Generation,
                runner.Context.Find("喷射宏完成")?.OccurredAt, io.HeldReleases);
        }
        var branch = Assert.Single(ledger.Sources).Value;
        var macros = ledger.Macros.Where(macro => macro.Branch == branch.Branch.Id).ToArray();
        Assert.Equal(burstReady ? 2 : 1, macros.Length);
        Assert.Empty(ledger.Maintenance);
        Assert.Equal((burstReady ? 2 : 1) * 26, io.Primitives.Count);
        var total = macros[^1].CompletedAt - branch.Branch.EnteredAt;
        output.WriteLine($"BRANCH_NO_MAINTENANCE burst={burstReady} decision={decisionCost} total={total:F3}");
        Assert.InRange(total, 0, burstReady ? 16 : 10);
        if (!burstReady) Assert.InRange(macros[0].StartedAt - branch.Branch.EnteredAt, 0, 5);
    }

    [Theory]
    [InlineData(50, 4.0)]
    [InlineData(150, 6.0)]
    public async Task RawAtomicSweepOverlapsObservationWithExistingWaits(double cost, double maximum)
    {
        var script = "segment(喷射宏,atomic,timeout=16,record=宏完成) {\n那维莱特 keydown(VK_LBUTTON), wait(1.6), " +
            string.Join(", ", Enumerable.Repeat("moveby(1800,0), wait(0.08)", 24)) + ", keyup(VK_LBUTTON)\n}";
        var program = LoadProgram(script);
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, cost, program);
        using var runner = NativeCombatFlowRunner.Create(program, io);
        for (var i = 0; i < 150 && runner.Context.Find("宏完成") == null; i++) await runner.StepAsync(default);
        Assert.NotNull(runner.Context.Find("宏完成"));
        Assert.Equal(26, io.Primitives.Count);
        Assert.InRange(runner.Context.Now, 3.52, maximum);
        Assert.Equal(Method.KeyDown, io.Primitives[0].Method);
        Assert.Equal(Method.KeyUp, io.Primitives[^1].Method);
        Assert.All(io.Primitives.Skip(1).SkipLast(1), command => Assert.Equal(Method.MoveBy, command.Method));
    }

    [Fact]
    public async Task SharedNativeAdapterCarriesThePreparedCooldownFrameIntoAdmission()
    {
        var program = LoadProgram("琴 e(required,if=e-ready(琴))");
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, program);
        using var runner = NativeCombatFlowRunner.Create(program, io);
        for (var i = 0; i < 20 && io.Inputs.Count == 0; i++) await runner.StepAsync(default);
        Assert.Single(io.Inputs);
        Assert.Single(io.FirstInputCooldownSources.Distinct());
    }

    [Fact]
    public async Task SharedNativeAdapterConfirmsOneSkillWithoutRepeatingItsPhysicalInput()
    {
        var program = LoadProgram("琴 e(required)");
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, program);
        using var runner = NativeCombatFlowRunner.Create(program, io);
        CombatFlowStep step = default;
        for (var i = 0; i < 40 && !step.RoundCompleted; i++) step = await runner.StepAsync(default);
        Assert.True(step.RoundCompleted);
        Assert.Equal(CombatFlowResult.Succeeded, step.Result);
        Assert.Single(io.Inputs);
        Assert.Equal("琴", io.Inputs[0].Actor);
        Assert.InRange(runner.Context.Now, 1, 4);
    }

    [Fact]
    public async Task SharedNativeAdapterRejectsInputWhenCancellationArrivesDuringCapture()
    {
        var program = LoadProgram("琴 e(required)");
        var clock = new FakeTimeProvider();
        using var cancellation = new CancellationTokenSource();
        using var io = new PhysicalReplay(clock, false, 50, program) { AfterCapture = cancellation.Cancel };
        using var runner = NativeCombatFlowRunner.Create(program, io);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await runner.StepAsync(cancellation.Token));
        Assert.Empty(io.Inputs);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(150)]
    public async Task SharedNativeAdapterObservesDeclaredEnergyAndHealingEvents(double cost)
    {
        var program = LoadProgram("琴 e(required,feed=琴)\n琴 q(required,if=low-hp(琴) && q-ready(琴))");
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, cost, program) { ResourceEvents = true };
        io.ScheduleHealth(0, "琴", low: true);
        using var runner = NativeCombatFlowRunner.Create(program, io);
        CombatFlowStep step = default;
        for (var i = 0; i < 60 && !step.RoundCompleted; i++) step = await runner.StepAsync(default);
        Assert.Equal(CombatFlowResult.Succeeded, step.Result);
        Assert.Equal(new[] { Method.Skill, Method.Burst }, io.Inputs.Select(input => input.Skill));
        Assert.All(io.Inputs, input => Assert.InRange(input.At, 0, 4));
        using var after = io.Capture();
        Assert.False(io.ReadLowHp(after!));
    }

    [Theory]
    [InlineData("frozen")]
    [InlineData("future")]
    [InlineData("restart")]
    [InlineData("foreign-actor")]
    public async Task AtomicEvidenceCannotCrossSourceOrActorBoundaries(string fault)
    {
        var program = LoadProgram("call(宏,required)\nsegment(宏,define,atomic,record=完成) { 那维莱特 keydown(VK_LBUTTON), wait(1.6), moveby(10,0), keyup(VK_LBUTTON) }");
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, program) { HeldFrameFault = fault };
        using var runner = NativeCombatFlowRunner.Create(program, io);
        CombatFlowStep step = default;
        for (var i = 0; i < 20 && !step.RoundCompleted; i++) step = await runner.StepAsync(default);
        Assert.Equal(CombatFlowResult.Failed, step.Result);
        Assert.Null(runner.Context.Find("完成"));
        Assert.Single(io.Primitives);
        Assert.False(io.HoldingInput);
        Assert.Empty(io.Selections);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(150)]
    public async Task KnownCooldownUsesTheDeclaredFallbackWithinTwoSeconds(double cost)
    {
        var program = LoadProgram("call(主轴,required)\nsegment(主轴,define,onfail=保底) { 那维莱特 e(fast,required) }\nsegment(保底,define) { 那维莱特 attack(0.5) }");
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, cost, program);
        io.PrimeKnownSkillCooldown("那维莱特", 8);
        using var runner = NativeCombatFlowRunner.Create(program, io);
        for (var i = 0; i < 15 && double.IsPositiveInfinity(io.FirstAttack); i++) await runner.StepAsync(default);
        Assert.InRange(io.FirstAttack, 0, 2);
        Assert.Empty(io.Inputs);
    }

    [Theory]
    [InlineData(false, 150)]
    [InlineData(true, 150)]
    [InlineData(false, 50)]
    [InlineData(true, 50)]
    public async Task WaterRotationKeepsUsefulOutputWithBoundedNativeDecisionCosts(bool burstReady, double decisionCost)
    {
        var script = LoadWaterScript();
        var program = LoadProgram(script);
        var clock = new FakeTimeProvider();
        using var game = new PhysicalReplay(clock, burstReady, decisionCost, program);
        using var execution = NativeCombatFlowRunner.Create(program, game);
        var ledger = new OutputLedger();
        for (var i = 0; i < 1600 && execution.Context.Now < 60; i++)
        {
            var step = await execution.StepAsync(default);
            ledger.Observe(execution.RuntimeStatistics, execution.Context.Find("喷射宏完成")?.Generation,
                execution.Context.Find("喷射宏完成")?.OccurredAt, game.HeldReleases);
            if (i < 5 || step.RoundCompleted || step.Result != CombatFlowResult.Succeeded)
                output.WriteLine($"step={i} t={execution.Context.Now:F3} result={step.Result} round={step.RoundCompleted} atomic={execution.IsAtomic} shield={execution.Context.Remaining("护盾")} last={execution.RuntimeStatistics.RecentEvents.LastOrDefault()}");
        }
        output.WriteLine($"burst={burstReady} cost={decisionCost} time={execution.Context.Now:F3}, inputs={string.Join(",", game.Inputs)}, macros={string.Join(",", ledger.Macros)}, outputs={string.Join(",", ledger.Outputs)}");
        Assert.NotEmpty(ledger.Macros);
        Assert.All(Enumerable.Range(0, 4), interval => Assert.Contains(ledger.Outputs,
            at => at >= interval * 15 && at < (interval + 1) * 15));
        Assert.True(game.FirstShieldConfirmed <= 3.5, $"opening={game.FirstShieldConfirmed:F3}");
        var branch = ledger.Sources.Values.First(source => source.Branch.Name == (burstReady ? "Q双喷" : "E单喷"));
        var macros = ledger.Macros.Where(macro => macro.Branch == branch.Branch.Id).ToArray();
        Assert.True(macros.Length >= (burstReady ? 2 : 1), "不能把后续E fallback的宏算作同一次Q双喷");
        var maintenance = ledger.Maintenance.Values.Where(item => item.Branch == branch.Branch.Id).ToArray();
        Assert.InRange(maintenance.Length, 0, 1);
        var maintenanceSeconds = maintenance.Sum(item => macros.First(macro => macro.StartedAt >= item.Call.EnteredAt).StartedAt - item.Call.EnteredAt);
        Assert.InRange(maintenanceSeconds, 0, 6);
        var total = macros[burstReady ? 1 : 0].CompletedAt - branch.Branch.EnteredAt;
        output.WriteLine($"BRANCH_RESULT burst={burstReady} decision={decisionCost} total={total:F3} maintenance={maintenanceSeconds:F3} count={maintenance.Length}");
        if (!burstReady) Assert.True(macros[0].StartedAt - branch.Branch.EnteredAt <= 5 + (maintenance.Length == 0 ? 0 : 6));
        Assert.True(total <= (burstReady ? 16 : 10) + (maintenance.Length == 0 ? 0 : 6), "原分支未在1.5规定的总窗口内完成；维护仍计入原始总钟");
    }

    private static string LoadWaterScript()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "BetterGenshinImpact.sln"))) root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root.FullName, "BetterGenshinImpact", "User", "AutoFight", "00-水.txt"));
    }

    private static CombatFlowProgram LoadProgram(string script)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "BetterGenshinImpact.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var data = Path.Combine(root.FullName, "BetterGenshinImpact", "GameTask", "AutoFight", "Assets", "SkillData");
        var seed = CombatSkillCatalog.ParseLocalJson<SkillCatalogSeed>(File.ReadAllText(Path.Combine(data, "builtin-skills.json")));
        var rules = CombatSkillCatalog.ParseLocalJson<SkillMechanicsRulePack>(File.ReadAllText(Path.Combine(data, "builtin-mechanics.json")));
        return CombatFlowProgram.Compile(script, new SkillCatalogSnapshot(rules.Apply(seed.Skills.ToDictionary(skill => skill.Id))));
    }

    private sealed class OutputLedger
    {
        internal sealed record Source(Guid Attempt, CombatCallContext Branch);
        internal sealed record Macro(Guid Id, Guid Branch, double StartedAt, double CompletedAt);
        private long _sequence;
        private long? _completionGeneration;
        private readonly Dictionary<Guid, (Guid Branch, double At)> _starts = new();
        private Macro? _released;
        internal Dictionary<Guid, Source> Sources { get; } = new();
        internal Dictionary<Guid, (Guid Branch, CombatCallContext Call)> Maintenance { get; } = new();
        internal List<Macro> Macros { get; } = [];
        internal List<double> Outputs { get; } = [];
        internal void Observe(CombatFlowStatistics statistics, long? generation, double? completedAt, HashSet<double> heldReleases)
        {
            foreach (var entry in statistics.RecentEvents.Where(entry => entry.Sequence > _sequence))
            {
                _sequence = entry.Sequence;
                var branch = entry.CallPath.LastOrDefault(call => call.Name is "E单喷" or "Q双喷");
                if (branch != null && entry.CallPath.LastOrDefault(call => call.Name == "补盾") is { } maintenance)
                    Maintenance.TryAdd(maintenance.Id, (branch.Id, maintenance));
                if (branch != null && entry.Actor == "那维莱特" && entry.Action is "skill" or "burst" &&
                    entry.InputStarted && entry.AttemptId is { } attempt)
                    Sources.TryAdd(branch.Id, new(attempt, branch));
                if (entry.ReportedResult != nameof(CombatFlowResult.Succeeded)) continue;
                if (entry.Action == "attack" || entry.Actor == "那维莱特" && entry.Action is "skill" or "burst")
                    Outputs.Add(entry.At);
                var macro = entry.CallPath.LastOrDefault(call => call.Name == "喷射宏");
                if (macro == null || branch == null) continue;
                if (entry.Action == "keydown" && entry.InputAt is { } down) _starts[macro.Id] = (branch.Id, down);
                if (entry.Action == "keyup" && entry.InputAt is { } up && heldReleases.Contains(up) && _starts.TryGetValue(macro.Id, out var started))
                    _released = new(macro.Id, started.Branch, started.At, entry.At);
            }
            if (generation != null && generation != _completionGeneration && completedAt != null && _released != null)
            {
                if (_released.CompletedAt <= completedAt && !Macros.Any(macro => macro.Id == _released.Id))
                {
                    Macros.Add(_released with { CompletedAt = completedAt.Value });
                    Outputs.Add(completedAt.Value);
                }
            }
            _completionGeneration = generation;
        }
    }

    private sealed class PhysicalReplay : INativeCombatIo, IDisposable
    {
        private readonly FakeTimeProvider _clock;
        private readonly CaptureFrameSource _producer;
        private readonly double _cost;
        private readonly bool _burstReady;
        private readonly CombatFlowProgram _program;
        private readonly long _started;
        private long _nextFrameTimestamp;
        private CaptureFrameStamp _latest;
        private string _actor = "那维莱特";
        private string? _selected;
        private double _selectionCompletes;
        private bool _held;
        private readonly Dictionary<(string, Method), (double Visible, double Ready)> _cooldowns = new();
        private readonly Dictionary<string, double> _knownECdUntil = new();
        private readonly Dictionary<CaptureFrameStamp, Snapshot> _snapshots = new();
        private readonly List<CaptureFrameStamp> _cooldownReadSources = [];
        private readonly List<(double At, string Actor, bool Full)> _energyEvents = [];
        private readonly List<(double At, string Actor, bool Low)> _healthEvents = [];
        public List<(string Actor, Method Skill, double At)> Inputs { get; } = [];
        public IReadOnlyList<CaptureFrameStamp> FirstInputCooldownSources { get; private set; } = [];
        public HashSet<double> HeldReleases { get; } = [];
        public List<CombatCommand> Primitives { get; } = [];
        public double FirstShieldConfirmed { get; private set; } = double.PositiveInfinity;
        public Action? AfterCapture { get; set; }
        public double? HeldCaptureCost { get; init; }
        public string? HeldFrameFault { get; init; }
        private CaptureFrameStamp _lastDelivered;
        public List<int> Selections { get; } = [];
        public double FirstAttack { get; private set; } = double.PositiveInfinity;
        public void PrimeKnownSkillCooldown(string actor, double seconds) => _knownECdUntil[actor] = Now + seconds;
        public bool HoldingInput => _held;
        public List<ImageRegion> CapturedFrames { get; } = [];
        public bool ResourceEvents { get; init; }
        public void ScheduleHealth(double at, string actor, bool low) => _healthEvents.Add((at, actor, low));
        private double Now => _clock.GetElapsedTime(_started).TotalSeconds;
        private sealed record Snapshot(string? Actor, double At);

        public TimeProvider Clock => _clock;
        public Microsoft.Extensions.Logging.ILogger Logger => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        public CombatInputCoordinator InputCoordinator { get; } = new();
        public IReadOnlyList<NativeCombatActor> Actors { get; } = new[]
        { new NativeCombatActor("钟离", 1), new("芙宁娜", 2), new("那维莱特", 3), new("琴", 4) };
        public double LastFinishCheckAge => 0;

        public PhysicalReplay(FakeTimeProvider clock, bool burstReady, double cost, CombatFlowProgram program)
        {
            _clock = clock; _producer = new(clock); _cost = cost; _burstReady = burstReady; _program = program;
            _started = clock.GetTimestamp(); _latest = _producer.Next();
            _nextFrameTimestamp = _started + clock.TimestampFrequency / 20;
        }

        private void Advance(double milliseconds)
        {
            var target = _clock.GetTimestamp() + (long)Math.Round(milliseconds * _clock.TimestampFrequency / 1000);
            while (_clock.GetTimestamp() < target)
            {
                var next = Math.Min(target, _nextFrameTimestamp);
                _clock.Advance(_clock.GetElapsedTime(_clock.GetTimestamp(), next));
                if (_selected != null && Now >= _selectionCompletes) { _actor = _selected; _selected = null; }
                if (_clock.GetTimestamp() == _nextFrameTimestamp)
                {
                    _latest = _producer.Next();
                    _nextFrameTimestamp += _clock.TimestampFrequency / 20;
                }
            }
        }

        public ImageRegion? Capture()
        {
            var wait = _clock.GetElapsedTime(_clock.GetTimestamp(), _nextFrameTimestamp).TotalMilliseconds;
            Advance(wait);
            var source = _latest;
            if (_held && HeldFrameFault == "frozen") source = _lastDelivered;
            if (_held && HeldFrameFault == "future") source = source with { CapturedTimestamp = _clock.GetTimestamp() + _clock.TimestampFrequency };
            if (_held && HeldFrameFault == "restart") { _producer.Restart(); source = _producer.Next(); }
            var at = _clock.GetElapsedTime(_started, source.CapturedTimestamp).TotalSeconds;
            var animation = _cooldowns.TryGetValue((_actor, Method.Burst), out var burst) && at < burst.Visible;
            _snapshots[source] = new(animation ? null : _held && HeldFrameFault == "foreign-actor" ? "琴" : _actor, at);
            _lastDelivered = source;
            Advance(Math.Max(0, (_held ? HeldCaptureCost ?? _cost : _cost) - wait));
            AfterCapture?.Invoke();
            var frame = new ImageRegion(new Mat(1, 1, MatType.CV_8UC3, Scalar.Black), 0, 0) { FrameStamp = source };
            CapturedFrames.Add(frame);
            return frame;
        }

        private Snapshot Frame(ImageRegion frame)
        {
            ObjectDisposedException.ThrowIf(frame.SrcMat.IsDisposed, frame);
            return _snapshots[frame.FrameStamp];
        }
        public bool IsCombatHud(ImageRegion frame) => Frame(frame).Actor != null;
        public bool IsMainUi(ImageRegion frame) => IsCombatHud(frame);
        public int ReadActive(ImageRegion frame, AvatarActiveCheckContext context) =>
            Actors.FirstOrDefault(actor => actor.Name == Frame(frame).Actor)?.Index ?? -1;
        public bool? IsActorActive(NativeCombatActor actor, ImageRegion frame) =>
            Frame(frame).Actor is { } active ? active == actor.Name : null;
        private bool Cooling(NativeCombatActor actor, Method skill, double at) =>
            _cooldowns.TryGetValue((actor.Name, skill), out var cd) && at >= cd.Visible && at < cd.Ready;
        private bool EnergyFull(NativeCombatActor actor, double at)
        {
            var events = _energyEvents.Where(item => item.Actor == actor.Name && item.At <= at).OrderBy(item => item.At).ToArray();
            return events.Length != 0 ? events[^1].Full : _burstReady && actor.Name == "那维莱特";
        }
        public double ReadSkillCooldown(NativeCombatActor actor, ImageRegion frame)
        {
            _cooldownReadSources.Add(frame.FrameStamp);
            var at = Frame(frame).At;
            return Cooling(actor, Method.Skill, at) ? _cooldowns[(actor.Name, Method.Skill)].Ready - at : 0;
        }
        public bool IsSkillReady(NativeCombatActor actor, ImageRegion frame, double cooldown)
        {
            if (cooldown > 0) _knownECdUntil[actor.Name] = Now + cooldown;
            return IsActorActive(actor, frame) == true && cooldown <= 0;
        }
        public BurstObservation ReadBurst(ImageRegion frame, bool active)
        {
            var sample = Frame(frame);
            var actor = Actors.FirstOrDefault(item => item.Name == sample.Actor);
            return active && actor != null ? new(EnergyFull(actor, sample.At), Cooling(actor, Method.Burst, sample.At)) : default;
        }
        public bool ReadLowHp(ImageRegion frame)
        {
            var sample = Frame(frame);
            var events = _healthEvents.Where(item => item.Actor == sample.Actor && item.At <= sample.At).OrderBy(item => item.At).ToArray();
            return events.Length != 0 && events[^1].Low;
        }
        public HashSet<int> ReadSideBurstReady(ImageRegion frame)
        {
            var sample = Frame(frame);
            return Actors.Where(actor => EnergyFull(actor, sample.At) && !Cooling(actor, Method.Burst, sample.At))
                .Select(actor => actor.Index).ToHashSet();
        }
        public bool TryGetKnownSkillCooldown(string actor, out double cooldown)
        {
            cooldown = Math.Max(0, _knownECdUntil.GetValueOrDefault(actor) - Now);
            return _knownECdUntil.ContainsKey(actor);
        }
        public void ConfirmSkill(NativeCombatActor actor, double cooldown, DateTime inputAtUtc)
        {
            _knownECdUntil[actor.Name] = Now + cooldown;
            if (actor.Name == "钟离") FirstShieldConfirmed = Math.Min(FirstShieldConfirmed, Now);
        }
        public Task WaitSkillCooldown(NativeCombatActor actor, CancellationToken ct)
        {
            if (TryGetKnownSkillCooldown(actor.Name, out var cooldown) && cooldown > 0) WaitForSelection((int)Math.Ceiling(cooldown * 1000), ct);
            return Task.CompletedTask;
        }
        public void SelectActor(int index, CancellationToken ct)
        {
            Selections.Add(index);
            ct.ThrowIfCancellationRequested();
            var actor = Actors.Single(item => item.Index == index).Name;
            if (_selected == null && _actor != actor) { _selected = actor; _selectionCompletes = Now + 1; }
        }
        public bool OnSelectionMismatch(NativeCombatActor actor, int attempt, int attempts, int observed, CancellationToken ct) => false;
        public void WaitForSelection(int milliseconds, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (CombatActionScope.Current is { } scope) scope.WaitAsync(milliseconds, DelayAsync).GetAwaiter().GetResult();
            else Advance(milliseconds);
            ct.ThrowIfCancellationRequested();
        }
        public void ResolveSelectionRecovery(NativeCombatActor actor, AvatarSelectionProtocol.Result<ImageRegion> selection, CancellationToken ct) =>
            throw new InvalidOperationException("健康回放意外进入非战斗恢复");
        public void CheckDefeated(ImageRegion frame, CancellationToken ct) { ct.ThrowIfCancellationRequested(); }
        private void Send(NativeCombatActor actor, Method skill, bool hold)
        {
            if (Inputs.Count == 0) FirstInputCooldownSources = _cooldownReadSources.ToArray();
            Inputs.Add((actor.Name, skill, Now));
            var cooldown = _program.GetTiming(actor.Name, skill == Method.Skill ? "e" : "q", hold)?.Cooldown
                ?? throw new InvalidDataException("生产目录缺少CD：" + actor.Name);
            _cooldowns[(actor.Name, skill)] = (Now + (skill == Method.Burst ? 2.4 : .4), Now + cooldown);
            if (skill == Method.Burst) _energyEvents.Add((Now, actor.Name, false));
            // 仅这个受控场景声明一次E后0.8s发生就绪事件，不声称真实粒子数或能量转化率。
            if (ResourceEvents && actor.Name == "琴")
            {
                if (skill == Method.Skill) _energyEvents.Add((Now + .8, actor.Name, true));
                else ScheduleHealth(Now + 2.4, actor.Name, low: false);
            }
            WaitForSelection(hold ? 1000 : 50, default);
        }
        public void SendSkill(NativeCombatActor actor, bool hold) => Send(actor, Method.Skill, hold);
        public void SendBurst(NativeCombatActor actor) => Send(actor, Method.Burst, false);
        public void ExecutePrimitive(NativeCombatActor actor, CombatCommand command)
        {
            Primitives.Add(command);
            if (command.Method == Method.KeyDown) _held = true;
            if (command.Method == Method.KeyUp)
            {
                if (_held && actor.Name == "那维莱特") HeldReleases.Add(Now);
                _held = false;
            }
            if (command.Method == Method.Attack)
            {
                FirstAttack = Math.Min(FirstAttack, Now);
                WaitForSelection((int)(double.Parse(command.Args![0], System.Globalization.CultureInfo.InvariantCulture) * 1000), default);
            }
        }
        public Task PrepareVisionAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public IDisposable BeginExclusive(bool allowPassiveObservation) => new Lease();
        private sealed class Lease : IDisposable { public void Dispose() { } }
        public void ReleaseInput() => _held = false;
        public Task DelayAsync(int milliseconds, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Advance(milliseconds); ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
        public void Dispose() => _snapshots.Clear();
    }
}
