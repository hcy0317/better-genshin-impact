using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.Common.Map.Maps.Base;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

[CollectionDefinition(nameof(NavigationResumeDataCollection), DisableParallelization = true)]
public sealed class NavigationResumeDataCollection { }

[Collection(nameof(NavigationResumeDataCollection))]
public partial class NavigationResumeTests
{
    private sealed class ModelDataFixture : IDisposable
    {
        private readonly string _root = BetterGenshinImpact.Core.Config.Global.StartUpPath;
        private readonly string _assets = BetterGenshinImpact.Core.Config.Global.Absolute("Assets");
        private readonly BetterGenshinImpact.Core.Config.AllConfig? _config = BetterGenshinImpact.Service.ConfigService.Config;
        private readonly IDisposable _data = BetterGenshinImpact.Service.ConfigService.UseInMemoryConfig(new());
        public void Dispose()
        {
            try { _data.Dispose(); }
            finally
            {
                Assert.Equal(_root, BetterGenshinImpact.Core.Config.Global.StartUpPath);
                Assert.Equal(_assets, BetterGenshinImpact.Core.Config.Global.Absolute("Assets"));
                Assert.Same(_config, BetterGenshinImpact.Service.ConfigService.Config);
            }
        }
    }

    [Fact]
    public void InMemoryConfigScopesPreserveResourceRootAndRestoreExactReference()
    {
        var root = BetterGenshinImpact.Core.Config.Global.StartUpPath;
        var assets = BetterGenshinImpact.Core.Config.Global.Absolute("Assets");
        var original = BetterGenshinImpact.Service.ConfigService.Config;
        var outerData = new BetterGenshinImpact.Core.Config.AllConfig();
        var innerData = new BetterGenshinImpact.Core.Config.AllConfig();
        var outer = BetterGenshinImpact.Service.ConfigService.UseInMemoryConfig(outerData);
        try
        {
            Assert.Same(outerData, BetterGenshinImpact.Service.ConfigService.Config);
            using (BetterGenshinImpact.Service.ConfigService.UseInMemoryConfig(innerData))
                Assert.Same(innerData, BetterGenshinImpact.Service.ConfigService.Config);
            Assert.Same(outerData, BetterGenshinImpact.Service.ConfigService.Config);
            Assert.Throws<OperationCanceledException>((Action)(() =>
            {
                using var nested = BetterGenshinImpact.Service.ConfigService.UseInMemoryConfig(innerData);
                throw new OperationCanceledException("fixture exception");
            }));
            Assert.Same(outerData, BetterGenshinImpact.Service.ConfigService.Config);
            Assert.Equal(root, BetterGenshinImpact.Core.Config.Global.StartUpPath);
            Assert.Equal(assets, BetterGenshinImpact.Core.Config.Global.Absolute("Assets"));
        }
        finally { outer.Dispose(); }
        outer.Dispose();
        Assert.Same(original, BetterGenshinImpact.Service.ConfigService.Config);
        Assert.Equal(root, BetterGenshinImpact.Core.Config.Global.StartUpPath);
        Assert.Equal(assets, BetterGenshinImpact.Core.Config.Global.Absolute("Assets"));
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("parent-tight")]
    [InlineData("action-expired")]
    [InlineData("action-failed")]
    [InlineData("cancelled")]
    [InlineData("no-direct-arrival")]
    [InlineData("transformed-arrival")]
    [InlineData("authored-flight")]
    public async Task PublicPathingHandsLateReachedNavigationToTheActionsOriginalOwner(string scenario)
    {
        using var modelData = new ModelDataFixture();
        using var cancellation = new CancellationTokenSource();
        var replay = new PathReplay(cancellation.Token);
        replay.PositionAt = _ => new Point2f(100, 100);
        replay.MotionAt = _ => scenario == "authored-flight" ? BetterGenshinImpact.GameTask.Common.BgiVision.MotionStatus.Fly : BetterGenshinImpact.GameTask.Common.BgiVision.MotionStatus.Normal;
        var actionCalls = 0;
        replay.AfterDelay = milliseconds =>
        {
            if (actionCalls != 0 || milliseconds != 1000) return;
            if (scenario == "no-direct-arrival") replay.Direct = false;
            if (scenario == "transformed-arrival") replay.Transformed = true;
        };
        DateTimeOffset? navigationStarted = null;
        double navigationSeconds = 0;
        var actionOwnerBudget = TimeSpan.Zero;
        var scene = new PathExecutor.PathingSceneIo
        {
            Prepare = (_, _) => Task.FromResult(true),
            Convert = (point, map) => new WaypointForTrack(point, map, value => value),
            Teleport = (_, _) => Task.CompletedTask,
            LowHp = (_, point) =>
            {
                if (point.Id == 2)
                {
                    navigationStarted = replay.Clock.GetUtcNow();
                    replay.Clock.Advance(TimeSpan.FromSeconds(scenario == "authored-flight" ? 239.5 : 238.5));
                }
                return false;
            },
            RecoverAtStatue = _ => throw new InvalidOperationException("No healing requested"),
            ReadHealing = frame => new(frame.FrameStamp, true, false),
            MacroFactory = () => null,
            RunAction = async (_, token) =>
            {
                actionCalls++;
                navigationSeconds = (replay.Clock.GetUtcNow() - navigationStarted!.Value).TotalSeconds;
                return await BetterGenshinImpact.GameTask.Common.Ui.UiOperation.RunAsync(
                    "recorded-path-fragment", TimeSpan.FromSeconds(9.25), token, async action =>
                    {
                        actionOwnerBudget = action.Remaining;
                        await replay.Io.Delay(scenario == "action-expired" ? 10000 : 1000, action.Token);
                        if (scenario == "cancelled") cancellation.Cancel();
                        action.Check();
                        if (scenario == "action-failed") throw new InvalidOperationException("Original action failed");
                        return CombatScriptExecutor.FromFlowResult(CombatFlowResult.Succeeded);
                    }, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, replay.Clock);
            }
        };
        var subject = new PathExecutor(cancellation.Token, replay.Io, sceneIo: scene)
            { PartyConfig = new() { MainAvatarIndex = "1" } };
        var route = new PathingTask { FileName = "late-original-action", FullPath = "late-original-action.json", Positions =
        [
            new() { Id = 1, X = 100, Y = 100, Type = "teleport", MoveMode = "walk" },
            new() { Id = 2, X = 100, Y = 100, Type = "target", MoveMode = scenario == "authored-flight" ? "fly" : "walk", Action = "combat_script", ActionParams = "钟离 wait(1)" }
        ] };
        using var parent = BetterGenshinImpact.GameTask.Common.Ui.UiOperation.Begin(
            "original-path-parent", scenario == "parent-tight" ? TimeSpan.FromSeconds(240) : TimeSpan.FromMinutes(10), clock: replay.Clock);

        var error = await Record.ExceptionAsync(() => subject.Pathing(route));

        Assert.True(actionCalls == 1, $"actionCalls={actionCalls} navigationSeconds={navigationSeconds} error={error}");
        Assert.InRange(navigationSeconds, 239.49, 239.75);
        Assert.Same(parent, BetterGenshinImpact.GameTask.Common.Ui.UiOperation.Current);
        if (scenario != "completed")
        {
            Assert.NotNull(error);
            Assert.False(subject.SuccessEnd);
            Assert.False(subject.ActionSettlements.ContainsKey(1));
            if (scenario is "parent-tight" or "no-direct-arrival" or "transformed-arrival" or "authored-flight")
                Assert.InRange(actionOwnerBudget.TotalSeconds, 0, 1.51);
            return;
        }
        Assert.Null(error);
        Assert.Equal(TimeSpan.FromSeconds(9.25), actionOwnerBudget);
        Assert.True(subject.SuccessEnd);
        Assert.Equal(CombatExecutionKind.Completed, subject.ActionSettlements[1].Outcome.Kind);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("skipped")]
    [InlineData("foreign-healing")]
    [InlineData("digest-changed")]
    [InlineData("owner-expired")]
    [InlineData("parent-expired")]
    [InlineData("unreachable")]
    [InlineData("tickets-exhausted")]
    [InlineData("unsupported-jump")]
    [InlineData("cache-near-direct-far")]
    [InlineData("direct-stale-after-await")]
    [InlineData("direct-foreign-frame")]
    [InlineData("legacy-safe")]
    [InlineData("legacy-late-action")]
    [InlineData("legacy-late-parent-tight")]
    [InlineData("legacy-late-no-direct")]
    [InlineData("legacy-owner-expired")]
    public async Task PublicPathingResumesOnlyNavigationAfterConsumedMacroAndConfirmedHealing(string scenario)
    {
        using var modelData = new ModelDataFixture();
        var replay = new PathReplay();
        PathExecutor? subject = null;
        var healed = false;
        var recoveries = 0;
        WaypointActionSettlement? prefixSettlement = null;
        var macroCalls = 0;
        var teleports = 0;
        DateTimeOffset? originalCurrentDeadline = null;
        DateTimeOffset? returnedCurrentDeadline = null;
        var legacy = scenario.StartsWith("legacy-", StringComparison.Ordinal);
        var lateLegacyAction = scenario.StartsWith("legacy-late-", StringComparison.Ordinal);
        var completedLegacyActions = 0;
        var lateLegacyArrivalSettled = false;
        var legacyActionBudget = TimeSpan.Zero;
        var legacyActionNavigationRemaining = TimeSpan.Zero;
        var directCounter = scenario is "cache-near-direct-far" or "direct-stale-after-await" or "direct-foreign-frame";
        var position = new Point2f(100,100);
        var movementTarget = position;
        replay.PositionAt = _ => position;
        replay.AfterDelay = milliseconds =>
        {
            if (lateLegacyAction && healed && milliseconds == 1000 && macroCalls == 0 && originalCurrentDeadline is { } original &&
                Math.Abs(position.X - 120) + Math.Abs(position.Y - 100) < 2)
            {
                lateLegacyArrivalSettled = true;
                var late = original - replay.Clock.GetUtcNow() - TimeSpan.FromMilliseconds(500);
                if (late > TimeSpan.Zero) replay.Clock.Advance(late);
            }
            var forward = replay.Inputs.FirstOrDefault(input => input.Action.ToString() == "MoveForward");
            if (forward.Action.ToString() != "MoveForward" || !replay.Io.IsDown(forward.Action) || healed && scenario == "unreachable") return;
            var delta = movementTarget - position;
            var distance = Math.Sqrt(delta.X*delta.X + delta.Y*delta.Y);
            if (distance > 0) position += delta * (float)Math.Min(1, milliseconds*.03/distance);
        };
        var io = new PathMoveToIo
        {
            Clock=replay.Io.Clock, LoggerFactory=replay.Io.LoggerFactory, Capture=replay.Io.Capture, EndJudgment=replay.Io.EndJudgment,
            CombatHud=replay.Io.CombatHud, Motion=replay.Io.Motion, Transformed=replay.Io.Transformed,
            Availability=replay.Io.Availability, CameraOrientation=replay.Io.CameraOrientation,
            RotateUntil=replay.Io.RotateUntil, RotateStep=replay.Io.RotateStep, CheckInput=replay.Io.CheckInput,
            SwitchAvatar=replay.Io.SwitchAvatar, SwitchGuardianAvatar=replay.Io.SwitchGuardianAvatar,
            MouseMove=replay.Io.MouseMove, Dpi=replay.Io.Dpi,
            Send=replay.Io.Send, IsDown=replay.Io.IsDown,
            Delay=replay.Io.Delay, HurryOn=replay.Io.HurryOn,
            Locate=async (frame, point) =>
            {
                movementTarget=new((float)point.X,(float)point.Y);
                return (await replay.Io.Locate(frame,point)) with { Point=healed && teleports > 1 && directCounter ? movementTarget : position, IsDirect=!(healed && teleports > 1 && directCounter), Source=healed && teleports > 1 && directCounter ? PathPositionSource.Cache : PathPositionSource.Direct };
            },
            LocateDirect=async (frame, point) =>
            {
                if (healed && teleports > 1 && scenario == "direct-stale-after-await") replay.Clock.Advance(TimeSpan.FromSeconds(3));
                if (healed && teleports > 1 && scenario == "direct-foreign-frame") frame.FrameStamp = frame.FrameStamp with { SessionId=Guid.NewGuid() };
                return (await replay.Io.Locate(frame,point)) with { Point=healed && teleports > 1 && directCounter && scenario != "cache-near-direct-far" ? new((float)point.X,(float)point.Y) : position, IsDirect=!(scenario == "legacy-late-no-direct" && lateLegacyArrivalSettled && point.Id == 3), Source=PathPositionSource.Direct };
            }
        };
        var scene = new PathExecutor.PathingSceneIo
        {
            Prepare = (_,_) => Task.FromResult(true),
            Convert = (point, map) => new WaypointForTrack(point, map, value => value),
            Teleport = (point,_) => { teleports++; position=new((float)point.X,(float)point.Y); if (scenario == "legacy-owner-expired" && teleports > 1) replay.Clock.Advance(TimeSpan.FromSeconds(25)); return Task.CompletedTask; },
            LowHp = (_,point) =>
            {
                if (point.Id == 3 && legacy && healed)
                {
                    returnedCurrentDeadline = replay.Clock.GetUtcNow() + BetterGenshinImpact.GameTask.Common.Ui.UiOperation.Current!.Remaining;
                    if (lateLegacyAction)
                    {
                        var prepare = returnedCurrentDeadline.Value - replay.Clock.GetUtcNow() - TimeSpan.FromSeconds(25);
                        if (prepare > TimeSpan.Zero) replay.Clock.Advance(prepare);
                    }
                }
                if (point.Id != 3 || (scenario == "tickets-exhausted" ? recoveries >= 3 : healed)) return false;
                if (legacy)
                {
                    originalCurrentDeadline = replay.Clock.GetUtcNow() + BetterGenshinImpact.GameTask.Common.Ui.UiOperation.Current!.Remaining;
                    replay.Clock.Advance(TimeSpan.FromSeconds(200));
                }
                return true;
            },
            RecoverAtStatue = _ =>
            {
                recoveries++;
                prefixSettlement = subject!.ActionSettlements.GetValueOrDefault(1);
                healed = true;
                if (scenario == "digest-changed") subject.CurWaypoints.Item2[1].X += 1;
                replay.Clock.Advance(TimeSpan.FromSeconds(scenario == "owner-expired" ? 241 : scenario == "parent-expired" ? 11 : scenario == "legacy-owner-expired" ? 20 : 1));
                return Task.CompletedTask;
            },
            ReadHealing = frame => new(scenario == "foreign-healing" ? frame.FrameStamp with { SessionId = Guid.NewGuid() } : frame.FrameStamp, true, false),
            RunAction = async (point, token) =>
            {
                macroCalls++;
                if (lateLegacyAction && point.Id == 3)
                {
                    legacyActionNavigationRemaining = originalCurrentDeadline!.Value - replay.Clock.GetUtcNow();
                    return await BetterGenshinImpact.GameTask.Common.Ui.UiOperation.RunAsync(
                        "legacy-original-fragment", TimeSpan.FromSeconds(9.25), token, async action =>
                        {
                            legacyActionBudget = action.Remaining;
                            await replay.Io.Delay(1000, action.Token);
                            action.Check();
                            completedLegacyActions++;
                            return CombatScriptExecutor.FromFlowResult(CombatFlowResult.Succeeded);
                        }, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, replay.Clock);
                }
                return CombatScriptExecutor.FromFlowResult(scenario == "skipped" && point.Id == 2 ? CombatFlowResult.Skipped : CombatFlowResult.Succeeded);
            },
            MacroFactory = () => null
        };
        subject = new PathExecutor(default, io, sceneIo: scene)
            { PartyConfig = new() { MainAvatarIndex = "1" } };
        var route = new PathingTask { FileName="recorded-prefix-route", FullPath="recorded-prefix-route.json", Positions =
        [
            new() { Id=1, X=100,Y=100, Type="teleport",MoveMode="walk" },
            new() { Id=2, X=110,Y=100, Type="path",MoveMode=scenario == "unsupported-jump" ? "jump" : "walk",Action=legacy ? "" : "combat_script",ActionParams=legacy ? "" : "钟离 d(0.2),e" },
            new() { Id=3, X=120,Y=100, Type=lateLegacyAction ? "target" : "path",MoveMode="walk",Action=lateLegacyAction ? "combat_script" : "",ActionParams=lateLegacyAction ? "钟离 wait(1)" : "" },
            new() { Id=4, X=130,Y=100, Type="path",MoveMode="walk",Action="combat_script",ActionParams="attack(0.1)" }
        ] };
        using var parent = scenario is "parent-expired" or "legacy-late-parent-tight"
            ? BetterGenshinImpact.GameTask.Common.Ui.UiOperation.Begin("navigation-parent",
                TimeSpan.FromSeconds(scenario == "legacy-late-parent-tight" ? 240 : 10), clock: replay.Clock) : null;
        if (scenario is not ("completed" or "skipped" or "legacy-safe" or "legacy-late-action"))
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(() => subject.Pathing(route));
            Assert.DoesNotContain("Application host", error.ToString());
            Assert.DoesNotContain("NullReferenceException", error.ToString());
            Assert.False(subject.SuccessEnd);
            Assert.Equal(lateLegacyAction ? 1 : legacy ? 0 : 1, macroCalls);
            if (lateLegacyAction)
            {
                Assert.Equal(0, completedLegacyActions);
                Assert.False(subject.ActionSettlements.ContainsKey(2));
                Assert.Equal(originalCurrentDeadline, returnedCurrentDeadline);
                Assert.InRange(legacyActionNavigationRemaining.TotalSeconds, .49, .75);
                Assert.InRange(legacyActionBudget.TotalSeconds, 0, .75);
            }
            Assert.Equal(scenario == "tickets-exhausted" ? 3 : 1, recoveries);
            if (scenario == "tickets-exhausted") Assert.Equal(3, teleports); // no third projection ticket
            return;
        }
        var traversalError = await Record.ExceptionAsync(() => subject.Pathing(route));
        if (lateLegacyAction)
        {
            Assert.Equal(1, completedLegacyActions);
            Assert.Equal(TimeSpan.FromSeconds(9.25), legacyActionBudget);
            Assert.InRange(legacyActionNavigationRemaining.TotalSeconds, .49, .75);
            Assert.True(traversalError == null, $"legacy completed={completedLegacyActions} currentSettlement={subject.ActionSettlements.ContainsKey(2)} error={traversalError}");
            Assert.Equal(CombatExecutionKind.Completed, subject.ActionSettlements[2].Outcome.Kind);
        }
        Assert.Null(traversalError);
        Assert.True(subject.SuccessEnd);
        if (legacy) Assert.Equal(originalCurrentDeadline, returnedCurrentDeadline);
        if (!legacy)
        {
            Assert.Equal(prefixSettlement, subject.ActionSettlements[1]);
            Assert.Equal(scenario == "skipped" ? CombatExecutionKind.Skipped : CombatExecutionKind.Completed, subject.ActionSettlements[1].Outcome.Kind);
        }
        Assert.True(healed);
        Assert.Equal(lateLegacyAction ? 2 : legacy ? 1 : 2, macroCalls); // original prefix and original suffix, no prefix replay
        Assert.Equal(2, teleports); // original route entry only, initial and original-entry projection
        Assert.Equal(legacy ? "" : "钟离 d(0.2),e", route.Positions[1].ActionParams);
        Assert.Equal(legacy ? "" : "combat_script", route.Positions[1].Action);
    }
}

public partial class NavigationResumeTests
{
    [Theory]
    [InlineData("completed")]
    [InlineData("context-changed")]
    [InlineData("target-slot-changed")]
    [InlineData("resolved-ir-changed")]
    [InlineData("missing-target-proof")]
    [InlineData("wrong-target")]
    [InlineData("foreign-target-proof")]
    [InlineData("stale-target-proof")]
    [InlineData("duplicate-target-proof")]
    [InlineData("digest-changed")]
    [InlineData("owner-expired")]
    [InlineData("parent-expired")]
    [InlineData("third-ticket")]
    [InlineData("unsafe-q")]
    [InlineData("unsafe-ui")]
    [InlineData("long-wait")]
    [InlineData("multi-actor")]
    public Task PublicPathingReplaysTheWholeRecordedPrefixAfterConfirmedHealing(string scenario) =>
        ReplayWholeRecordedPrefixAsync(scenario, revive: false);

    [Theory]
    [InlineData("completed")]
    [InlineData("missing-proof")]
    [InlineData("stale-proof")]
    [InlineData("foreign-root")]
    [InlineData("wrong-target")]
    [InlineData("unknown-hp")]
    [InlineData("owner-expired")]
    [InlineData("third-ticket")]
    public Task PublicPathingAssessesConfirmedReviveThroughTheOriginalSemanticBoundary(string scenario) =>
        ReplayWholeRecordedPrefixAsync(scenario, revive: true);

    [Fact]
    public Task MissingCurrentUiOriginCannotBorrowAFreshReviveProofForSemanticReplay() =>
        ReplayWholeRecordedPrefixAsync("missing-ui-origin", revive: true);

    private async Task ReplayWholeRecordedPrefixAsync(string scenario, bool revive)
    {
        BetterGenshinImpact.Helpers.ApplicationHostBootstrapGuard.ProhibitForCurrentProcess();
        using var modelData = new ModelDataFixture();
        var replay = new PathReplay { EmitReceipts = true };
        PathExecutor? subject = null;
        var nativeInputsAtRecovery = -1;
        var hpRestoreSeconds = BetterGenshinImpact.Service.ConfigService.Config!.TpConfig.HpRestoreDuration;
        var position = new Point2f(524.73f, 525.45f);
        var target = position;
        var healed = false;
        var recoveryCount = 0;
        var reviveSignals = new List<CombatRecoveryCompletedException>();
        ReviveRecoveryProof? publishedReviveProof = null;
        bool? publishedReviveFreshnessAtVerification = null;
        var currentOriginReads = 0;
        DateTimeOffset? verifiedReviveDeadline = null;
        var teleports = new List<(Fischless.GameCapture.CaptureFrameStamp Before, Fischless.GameCapture.CaptureFrameStamp After, long Completed)>();
        var actions = new List<(bool Returned, int Id, string Action, string Parameters, string Ir)>();
        var initialVisits = new List<int>();
        var suffixVisits = 0;
        var airborne = false;
        long? jumpReleased = null;
        long? landingRequested = null;
        var landings = new List<(Fischless.GameCapture.CaptureFrameStamp InputSource, long Completed, Fischless.GameCapture.CaptureFrameStamp After)>();
        Fischless.GameCapture.CaptureFrameStamp landingSource = default;
        var stopCalls = 0;
        DateTimeOffset? originalDeadline = null, returnedDeadline = null;
        replay.PositionAt = _ => position;
        replay.BeforeLocate = point => target = new((float)point.X, (float)point.Y);
        replay.OnInput = (action, kind) =>
        {
            if (action == BetterGenshinImpact.Core.Simulator.Extensions.GIActions.Jump && kind == BetterGenshinImpact.Core.Simulator.Extensions.KeyType.KeyUp)
                jumpReleased = replay.Clock.GetTimestamp();
            if (action == BetterGenshinImpact.Core.Simulator.Extensions.GIActions.NormalAttack)
            {
                landingSource = replay.LastSource;
                replay.Clock.Advance(TimeSpan.FromMilliseconds(1));
                landingRequested = replay.Clock.GetTimestamp();
            }
        };
        replay.MotionAt = _ =>
        {
            if (landingRequested is { } stopped && replay.Clock.GetElapsedTime(stopped) >= TimeSpan.FromMilliseconds(60))
            {
                airborne = false;
                jumpReleased = null;
                landingRequested = null;
                landings.Add((landingSource, stopped, replay.LastSource));
            }
            else if (jumpReleased is { } opened && replay.Clock.GetElapsedTime(opened) >= TimeSpan.FromMilliseconds(60)) airborne = true;
            return airborne ? BetterGenshinImpact.GameTask.Common.BgiVision.MotionStatus.Fly : BetterGenshinImpact.GameTask.Common.BgiVision.MotionStatus.Normal;
        };
        replay.AfterDelay = milliseconds =>
        {
            if (!replay.Io.IsDown(BetterGenshinImpact.Core.Simulator.Extensions.GIActions.MoveForward)) return;
            var delta = target - position;
            var distance = Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
            if (distance > 0) position += delta * (float)Math.Min(1, milliseconds * .03 / distance);
        };
        using var native = new FullPrefixNativeIo(replay);
        var raw = new FullPrefixMacroIo(replay, native.InputCoordinator);
        using var macro = new PathingMacroSession(raw);
        var macroOutcomes = new List<(int Id, CombatExecutionResult Result)>();
        var miningSelections = new List<(bool Returned, string Roster, string Strategy, string Ir)>();
        var stops = new BetterGenshinImpact.GameTask.AutoPathing.Handler.StopFlyingHandler();
        async Task<CombatExecutionResult> ExecuteNative(IReadOnlyList<CombatCommand> commands, CancellationToken token)
        {
            using var runner = NativeCombatFlowRunner.Create(commands, native, loop: false,
                purpose: CombatScriptExecutionPurpose.Pathing);
            using var recoveryOwner = CombatRecoveryOwner.Enter(token, runner.RetireForRecovery);
            var nativeStarted = replay.Clock.GetTimestamp();
            var actual = await runner.RunRoundAsync(token);
            if (actual == CombatFlowResult.Failed)
                native.NativeFailure = $"result={actual}; ir={FullPrefixIr(commands)}; source={replay.LastSource}; heldE={native.HeldE}; inputs={native.Inputs.Count};" +
                    $" now={replay.Clock.GetUtcNow():O};elapsed={replay.Clock.GetElapsedTime(nativeStarted).TotalSeconds:R};tokenCancelled={token.IsCancellationRequested};" +
                    $" parentRemaining={BetterGenshinImpact.GameTask.Common.Ui.UiOperation.Current?.Remaining};macroTail={macro.HasTail};" +
                    Environment.NewLine + string.Join(Environment.NewLine, native.Diagnostics.TakeLast(80));
            return CombatScriptExecutor.FromFlowResult(actual);
        }
        var boundMining = new BetterGenshinImpact.GameTask.AutoPathing.Handler.MiningHandler(native);
        async Task<(CombatExecutionResult Outcome, NavigationResumePlan.MiningBinding? Binding)> RunBoundMining(
            WaypointForTrack point, NavigationResumePlan.MiningBinding? expected, CancellationToken token)
        {
            Assert.True(point.Id.HasValue);
            var executed = await boundMining.RunWithBindingAsync(token, point, expected);
            var binding = Assert.IsType<NavigationResumePlan.MiningBinding>(executed.Binding);
            Assert.Equal(CombatExecutionKind.Completed, executed.Outcome.Kind);
            miningSelections.Add((healed, binding.ContextDescriptor, binding.Strategy, binding.IrDigest));
            actions.Add((healed, point.Id.Value, point.Action, point.ActionParams ?? "", binding.IrDigest));
            macroOutcomes.Add((point.Id.Value, executed.Outcome));
            return executed;
        }
        Task RecoverAtStatue(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            recoveryCount++;
            healed = true;
            nativeInputsAtRecovery = native.Inputs.Count;
            if (scenario == "context-changed") native.Actors = [new("钟离", 1), new("娜维娅", 2), new("凯亚", 3), new("香菱", 4)];
            if (scenario == "target-slot-changed") native.Actors = [new("娜维娅", 1), new("钟离", 2), new("班尼特", 3), new("香菱", 4)];
            if (scenario == "resolved-ir-changed") native.Actors = [new("娜维娅", 1), new("班尼特", 2), new("凯亚", 3), new("香菱", 4)];
            if (scenario == "missing-target-proof") native.TargetIdentityName = null;
            if (!revive && scenario == "wrong-target") native.TargetIdentityName = "娜维娅";
            if (scenario == "foreign-target-proof") native.IdentityFault = "foreign";
            if (scenario == "stale-target-proof") native.IdentityFault = "stale";
            if (scenario == "duplicate-target-proof") native.IdentityFault = "duplicate";
            if (scenario == "digest-changed") subject!.CurWaypoints.Item2[1].X += 1;
            position = new(9000, 9000);
            // Preserve the real configured external statue wait; this does not set skill readiness.
            replay.Clock.Advance(TimeSpan.FromSeconds(scenario == "owner-expired" ? 241 : scenario == "parent-expired" ? 1201 : hpRestoreSeconds));
            return Task.CompletedTask;
        }
        var scene = new PathExecutor.PathingSceneIo
        {
            ReadCurrentUiOperation = revive && scenario == "missing-ui-origin" ? () =>
            {
                currentOriginReads++;
                var actual = BetterGenshinImpact.GameTask.Common.Ui.UiOperation.Current;
                Assert.NotNull(actual);
                actual.Check();
                Assert.False(actual.Token.IsCancellationRequested);
                TaskExecutionScope.ThrowIfFailed();
                Assert.NotNull(publishedReviveProof);
                Assert.True(publishedReviveProof.Frame.SourceBound);
                Assert.True(publishedReviveProof.Frame.HasUsableEvidence);
                Assert.True(publishedReviveProof.Frame.Confirms(publishedReviveProof.Target));
                Assert.False(publishedReviveProof.LowHp);
                Assert.Equal(publishedReviveProof.UiRootId, actual.RootId);
                Assert.Equal(originalDeadline, verifiedReviveDeadline);
                Assert.True(replay.Clock.GetUtcNow() < originalDeadline!.Value);
                return null; // Only this guard read is absent; ambient/scope/token/owner remain unchanged.
            } : null,
            Prepare = (_, _) => Task.FromResult(true),
            Convert = (point, map) => new WaypointForTrack(point, map, value => value),
            Teleport = (point, token) =>
            {
                token.ThrowIfCancellationRequested();
                var before = replay.LastSource;
                replay.Clock.Advance(TimeSpan.FromMilliseconds(1));
                var completed = replay.Clock.GetTimestamp();
                position = new((float)point.X, (float)point.Y);
                airborne = false; jumpReleased = null; landingRequested = null;
                replay.Clock.Advance(TimeSpan.FromMilliseconds(60));
                using var after = replay.Io.Capture();
                teleports.Add((before, after.FrameStamp, completed));
                return Task.CompletedTask;
            },
            LowHp = (_, point) =>
            {
                Assert.True(point.Id.HasValue);
                var pointId = point.Id.Value;
                if (!healed && (initialVisits.Count == 0 || initialVisits[^1] != pointId)) initialVisits.Add(pointId);
                if (healed && pointId == 27) suffixVisits++;
                if (pointId != 26) return false;
                var deadline = replay.Clock.GetUtcNow() + BetterGenshinImpact.GameTask.Common.Ui.UiOperation.Current!.Remaining;
                if (revive && (!healed || scenario == "third-ticket" && recoveryCount < 3))
                {
                    if (healed) returnedDeadline = deadline; else originalDeadline = deadline;
                    var token = BetterGenshinImpact.GameTask.Common.Ui.UiOperation.Current!.Token;
                    long recoveryCompleted = 0;
                    async Task RecoverForSignal()
                    {
                        await RecoverAtStatue(token);
                        recoveryCompleted = replay.Clock.GetTimestamp();
                    }
                    Task<ReviveRecoveryProof?> VerifyForSignal()
                    {
                        replay.Clock.Advance(TimeSpan.FromMilliseconds(61));
                        using var observed = replay.Io.Capture();
                        var bound = new ReviveRecoveryFrame(0, true,
                            BetterGenshinImpact.GameTask.Common.BgiVision.ReviveUiState.None, 1,
                            System.Collections.Immutable.ImmutableDictionary<int, string>.Empty.Add(1, "钟离"))
                            .WithSource(observed.FrameStamp, replay.Clock);
                        var current = BetterGenshinImpact.GameTask.Common.Ui.UiOperation.Current!;
                        publishedReviveProof = scenario == "missing-proof" ? null : new(bound,
                            scenario == "wrong-target" ? new ReviveTarget("娜维娅", 2) : new ReviveTarget("钟离", 1),
                            scenario == "unknown-hp" ? null : false,
                            scenario == "foreign-root" ? Guid.NewGuid().ToString("N") : current.RootId,
                            current.Id, recoveryCompleted);
                        if (scenario == "stale-proof") replay.Clock.Advance(
                            BetterGenshinImpact.GameTask.Common.Ui.UiSnapshot.RecoveryMaximumAge + TimeSpan.FromMilliseconds(1));
                        publishedReviveFreshnessAtVerification = publishedReviveProof?.Frame.HasUsableEvidence;
                        if (publishedReviveProof != null)
                        {
                            if (scenario == "stale-proof") Assert.False(publishedReviveFreshnessAtVerification);
                            else Assert.True(publishedReviveFreshnessAtVerification);
                        }
                        verifiedReviveDeadline = replay.Clock.GetUtcNow() + current.Remaining;
                        return Task.FromResult(publishedReviveProof);
                    }
                    try
                    {
                        CombatRecoveryCompletedException.RecoverVerifiedAsync(RecoverForSignal, VerifyForSignal, token)
                            .GetAwaiter().GetResult();
                    }
                    catch (CombatRecoveryCompletedException signal)
                    {
                        reviveSignals.Add(signal);
                        throw;
                    }
                    throw new InvalidOperationException("Verified recovery must propagate its completion signal.");
                }
                if (healed) { returnedDeadline = deadline; return scenario == "third-ticket" && recoveryCount < 3; }
                originalDeadline = deadline;
                var changed = scenario switch
                {
                    "unsafe-q" => "钟离 q",
                    "unsafe-ui" => "keypress(F)",
                    "long-wait" => "wait(30)",
                    "multi-actor" => "钟离 e,娜维娅 e",
                    _ => null
                };
                if (changed != null) subject!.CurWaypoints.Item2[1].ActionParams = changed;
                return true;
            },
            RecoverAtStatue = RecoverAtStatue,
            ReadHealing = frame => new(frame.FrameStamp, healed, false),
            BeforeAction = async (point, token) =>
            {
                Assert.Equal("stop_flying", point.Action);
                Assert.Equal("fly", point.MoveMode);
                Assert.Equal(BetterGenshinImpact.GameTask.Common.BgiVision.MotionStatus.Fly, replay.Io.Motion(replay.Images[^1]));
                await stops.RunAsync(token, point, replay.Io);
                stopCalls++;
                Assert.Equal(BetterGenshinImpact.GameTask.Common.BgiVision.MotionStatus.Normal, replay.Io.Motion(replay.Images[^1]));
            },
            RunAction = async (point, token) =>
            {
                Assert.True(point.Id.HasValue);
                var pointId = point.Id.Value;
                if (point.Action is "stop_flying" or "log_output")
                {
                    actions.Add((healed, pointId, point.Action, point.ActionParams ?? "", ""));
                    // Their original work is the before handler / diagnostic log. This is
                    // an explicit absence of an after-fragment, never a fabricated Completed.
                    return new(CombatExecutionKind.Skipped, "NO_AFTER_FRAGMENT_FOR_ORIGINAL_ACTION");
                }
                if (point.Action == "mining") return (await RunBoundMining(point, null, token)).Outcome;
                Assert.Equal("combat_script", point.Action);
                var script = point.CombatScript!;
                Assert.NotNull(script);
                actions.Add((healed, pointId, point.Action, point.ActionParams ?? "", FullPrefixIr(script.CombatCommands)));
                var resultFromOriginal = await macro.ExecuteAsync(
                    LegacyPathingMacroPlan.Create(script, native.Actors.Select(actor => actor.Name)), ExecuteNative, token);
                macroOutcomes.Add((pointId, resultFromOriginal));
                return resultFromOriginal;
            },
            RunMining = RunBoundMining,
            ReadMiningBinding = boundMining.ReadCurrentBindingAsync,
            MacroFactory = () => macro
        };
        var route = FullRecordedHealingPrefix();
        var original = route.Positions.Select(point => (point.Id, point.X, point.Y, point.Type, point.MoveMode, point.Action, point.ActionParams)).ToArray();
        subject = new PathExecutor(default, replay.Io, sceneIo: scene)
            { PartyConfig = new() { MainAvatarIndex = "1" } };
        using var parent = BetterGenshinImpact.GameTask.Common.Ui.UiOperation.Begin("full-recorded-prefix-parent",
            TimeSpan.FromMinutes(20), logger: Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, clock: replay.Clock);
        var error = await Record.ExceptionAsync(() => subject.Pathing(route));

        // These preconditions deliberately run before the desired recovery assertion.
        // Guard/bootstrap, parser, input or native-runner failures are not business RED.
        Assert.DoesNotContain("Application host", error?.ToString() ?? "");
        Assert.DoesNotContain("NullReferenceException", error?.ToString() ?? "");
        Assert.True(healed, $"Original prefix never reached its low-HP checkpoint: {error}{Environment.NewLine}nativeFailure={native.NativeFailure}");
        Assert.Equal(Enumerable.Range(1, 26), initialVisits);
        Assert.True(recoveryCount == (scenario == "third-ticket" ? 3 : 1),
            $"actualHeals={recoveryCount}; error={error}; plan={subject.NavigationResumeRejection};nativeFailure={native.NativeFailure}");
        Assert.Equal(new[] { 2, 3, 6, 7, 14, 21, 24, 25 }, actions.Where(action => !action.Returned).Select(action => action.Id));
        Assert.NotEmpty(native.Inputs);
        Assert.Contains(350, raw.Delays);
        Assert.Empty(raw.Inputs);
        Assert.All(macroOutcomes, item => Assert.True(item.Result.CanContinue,
            $"id={item.Id}; result={item.Result}; error={error};nativeFailure={native.NativeFailure}"));
        Assert.Contains(macroOutcomes, item => item.Id == 25 && item.Result.Kind == CombatExecutionKind.Completed);
        Assert.NotEmpty(landings);
        Assert.All(landings, landing =>
        {
            Assert.True(landing.After.IsAfter(landing.InputSource));
            Assert.True(landing.After.CapturedTimestamp - landing.Completed >= landing.After.TimestampFrequency * .06);
        });
        Assert.False(native.HeldE);
        Assert.Empty(raw.Held);
        if (revive && scenario != "owner-expired")
        {
            Assert.NotEmpty(reviveSignals);
            Assert.Equal(originalDeadline, verifiedReviveDeadline);
            if (scenario == "missing-proof") Assert.Null(reviveSignals[^1].Proof);
            else
            {
                Assert.NotNull(publishedReviveProof);
                Assert.True(publishedReviveProof.Frame.SourceBound);
                // Replay may legitimately outlive the old observation's age window; assert its admission-time value.
                if (scenario == "stale-proof") Assert.False(publishedReviveFreshnessAtVerification);
                else Assert.True(publishedReviveFreshnessAtVerification);
                if (scenario == "foreign-root") Assert.NotEqual(parent.RootId, publishedReviveProof.UiRootId);
                if (scenario == "wrong-target") Assert.False(publishedReviveProof.Frame.Confirms(publishedReviveProof.Target));
                if (scenario == "unknown-hp") Assert.Null(publishedReviveProof.LowHp);
                // Until this producer precondition passes, refusal controls are drafts, not signed negatives.
                Assert.Same(publishedReviveProof, reviveSignals[^1].Proof);
            }
        }
        if (revive && scenario == "missing-ui-origin")
        {
            Assert.Equal(1, currentOriginReads);
            Assert.NotNull(error); // P2 business RED: the old null-current guard authorizes full replay.
            Assert.Contains("revive-origin-ui-owner-mismatch", subject.NavigationResumeRejection ?? "");
            Assert.False(subject.SuccessEnd);
            Assert.Equal(1, recoveryCount);
            Assert.Equal(1, teleports.Count);
            Assert.Equal(nativeInputsAtRecovery, native.Inputs.Count);
            Assert.Equal(1, stopCalls);
            Assert.Equal(0, suffixVisits);
            return;
        }
        if (scenario == "third-ticket")
        {
            Assert.NotNull(error);
            Assert.False(subject.SuccessEnd);
            Assert.Equal(3, teleports.Count); // Initial entry plus only the two original shared recovery tickets.
            Assert.Equal(3, stopCalls);
            Assert.Equal(nativeInputsAtRecovery, native.Inputs.Count);
            Assert.Equal(0, suffixVisits);
            return;
        }
        if (scenario != "completed")
        {
            Assert.NotNull(error);
            Assert.False(subject.SuccessEnd);
            Assert.Equal(1, teleports.Count);
            Assert.Equal(nativeInputsAtRecovery, native.Inputs.Count);
            Assert.Equal(1, stopCalls);
            Assert.Equal(0, suffixVisits);
            return;
        }
        Assert.True(error == null, $"{error}{Environment.NewLine}plan={subject.NavigationResumeRejection}{Environment.NewLine}nativeFailure={native.NativeFailure}"); // Same null requirement, with actual admission diagnostics.
        Assert.True(subject.SuccessEnd);
        Assert.Equal(2, teleports.Count);
        Assert.All(teleports, teleport =>
        {
            Assert.True(teleport.After.IsKnown);
            Assert.True(teleport.After.CapturedTimestamp > teleport.Completed);
            if (teleport.Before.IsKnown) Assert.True(teleport.After.IsAfter(teleport.Before));
        });
        Assert.Equal(2, stopCalls);
        Assert.Equal(1, suffixVisits);
        Assert.Equal(originalDeadline, returnedDeadline);
        Assert.Equal(actions.Where(action => !action.Returned).Select(action => (action.Id, action.Action, action.Parameters, action.Ir)),
            actions.Where(action => action.Returned).Select(action => (action.Id, action.Action, action.Parameters, action.Ir)));
        Assert.Equal(2, miningSelections.Count);
        Assert.False(miningSelections[0].Returned);
        Assert.True(miningSelections[1].Returned);
        Assert.Equal(miningSelections[0].Roster, miningSelections[1].Roster);
        Assert.Equal(miningSelections[0].Strategy, miningSelections[1].Strategy);
        Assert.Equal(miningSelections[0].Ir, miningSelections[1].Ir);
        Assert.Equal(original, route.Positions.Select(point => (point.Id, point.X, point.Y, point.Type, point.MoveMode, point.Action, point.ActionParams)));
    }

    private static string FullPrefixIr(IEnumerable<CombatCommand> commands) => string.Join(";",
        commands.Select(command => command.Name + ":" + command.Method.Alias[0] + ":" + string.Join(",", command.Args ?? [])));

    private static PathingTask FullRecordedHealingPrefix() => new()
    {
        FileName = "10-无名遗迹x10-recorded-prefix.json", FullPath = "recorded/10-无名遗迹x10-recorded-prefix.json",
        Info = new() { MapName = "TheChasm" },
        Positions =
        [
            new() { Id=1, X=524.73, Y=525.45, Type="teleport", MoveMode="walk", Action="", ActionParams="" },
            new() { Id=2, X=515.49, Y=529.14, Type="path", MoveMode="run", Action="combat_script", ActionParams="钟离 d(0.2),e" },
            new() { Id=3, X=509.83, Y=531.22, Type="path", MoveMode="run", Action="combat_script", ActionParams="wait(0.35)" },
            new() { Id=4, X=508.83, Y=535.22, Type="path", MoveMode="fly", Action="", ActionParams="" },
            new() { Id=5, X=508.71, Y=555.03, Type="path", MoveMode="fly", Action="", ActionParams="" },
            new() { Id=6, X=508.71, Y=555.03, Type="path", MoveMode="fly", Action="stop_flying", ActionParams="" },
            new() { Id=7, X=511.08, Y=560.67, Type="target", MoveMode="walk", Action="combat_script", ActionParams="钟离 s(0.2),e(hold)" },
            new() { Id=8, X=518.31, Y=561.78, Type="target", MoveMode="walk", Action="", ActionParams="" },
            new() { Id=9, X=510.52, Y=569.53, Type="target", MoveMode="walk", Action="", ActionParams="" },
            new() { Id=10, X=510.28, Y=564.79, Type="target", MoveMode="walk", Action="", ActionParams="" },
            new() { Id=11, X=505.39, Y=562.01, Type="target", MoveMode="walk", Action="", ActionParams="" },
            new() { Id=12, X=506.67, Y=560.53, Type="target", MoveMode="walk", Action="", ActionParams="" },
            new() { Id=13, X=509.91, Y=562.11, Type="target", MoveMode="walk", Action="", ActionParams="" },
            new() { Id=14, X=509.32, Y=552.66, Type="target", MoveMode="walk", Action="combat_script", ActionParams="钟离 d(0.2),e(hold)" },
            new() { Id=15, X=510.48, Y=549.51, Type="target", MoveMode="run", Action="", ActionParams="" },
            new() { Id=16, X=524.08, Y=516.23, Type="path", MoveMode="walk", Action="", ActionParams="" },
            new() { Id=17, X=532.91, Y=499.42, Type="path", MoveMode="run", Action="", ActionParams="" },
            new() { Id=18, X=523.63, Y=450.94, Type="path", MoveMode="run", Action="", ActionParams="" },
            new() { Id=19, X=538.63, Y=409.68, Type="path", MoveMode="run", Action="", ActionParams="" },
            new() { Id=20, X=539.83, Y=381.65, Type="path", MoveMode="run", Action="", ActionParams="" },
            new() { Id=21, X=546.32, Y=377.46, Type="path", MoveMode="run", Action="log_output", ActionParams="========= ============可选战斗，默认关闭========= ========= ========= ============可选战斗，默认关闭========= ========= ========= ============可选战斗，默认关闭========= ========= ========= ============可选战斗，默认关闭========= ========= ========= ============可选战斗，默认关闭========= =========" },
            new() { Id=22, X=553.26, Y=367.1, Type="path", MoveMode="run", Action="", ActionParams="" },
            new() { Id=23, X=561.26, Y=363.1, Type="path", MoveMode="run", Action="", ActionParams="" },
            new() { Id=24, X=567.46, Y=359.38, Type="path", MoveMode="run", Action="combat_script", ActionParams="钟离 d(0.2),e" },
            new() { Id=25, X=579.94, Y=348, Type="path", MoveMode="run", Action="mining", ActionParams="" },
            new() { Id=26, X=580, Y=347.95, Type="path", MoveMode="run", Action="", ActionParams="" },
            new() { Id=27, X=584.9, Y=347.56, Type="target", MoveMode="run", Action="", ActionParams="" },
        ]
    };

    private sealed class FullPrefixMacroIo(PathReplay replay, CombatInputCoordinator coordinator) : IPathingMacroIo
    {
        public TimeProvider Clock => replay.Clock;
        public CombatInputCoordinator Coordinator => coordinator;
        internal readonly List<(PathingMacroInput Input, long Started, long Completed)> Inputs = new();
        internal readonly List<int> Delays = new();
        internal readonly HashSet<Vanara.PInvoke.User32.VK> Held = new();
        public PathingMacroObservation Observe(string phase = "boundary")
        {
            replay.Clock.Advance(TimeSpan.FromMilliseconds(20));
            using var frame = replay.Io.Capture();
            return new(PathingMacroScene.World, frame.FrameStamp);
        }
        public Vanara.PInvoke.User32.VK Map(Vanara.PInvoke.User32.VK key) => key;
        public BetterGenshinImpact.GameTask.AutoFight.CombatBattleHostInputResult Send(PathingMacroInput input, Action admit)
        {
            admit();
            var started = Clock.GetTimestamp();
            if (input.Kind == PathingMacroInputKind.KeyDown) Held.Add(input.Key);
            if (input.Kind == PathingMacroInputKind.KeyUp) Held.Remove(input.Key);
            replay.Clock.Advance(TimeSpan.FromMilliseconds(1));
            var completed = Clock.GetTimestamp();
            Inputs.Add((input, started, completed));
            return FullPrefixReceipt(started, completed);
        }
        public Task Delay(int milliseconds, CancellationToken token)
        {
            Delays.Add(milliseconds);
            return replay.Io.Delay(milliseconds, token);
        }
    }

    private static BetterGenshinImpact.GameTask.AutoFight.CombatBattleHostInputResult FullPrefixReceipt(long started, long completed) =>
        new(BetterGenshinImpact.GameTask.AutoFight.CombatBattleHostInputStatus.Sent, completed)
        { StartedTimestamp = started, ObservableAfterTimestamp = completed, NativeRequested = 1, NativeSubmitted = 1, NativeUncertain = false };

    private sealed class FullPrefixNativeIo(PathReplay replay) : INativeCombatIo, IDisposable
    {
        public TimeProvider Clock => replay.Clock;
        private Microsoft.Extensions.Logging.ILogger? _logger;
        public Microsoft.Extensions.Logging.ILogger Logger => _logger ??= new FullPrefixDiagnosticLogger(Diagnostics);
        internal readonly List<string> Diagnostics = new();
        internal string? NativeFailure;
        public CombatInputCoordinator InputCoordinator { get; } = new();
        public IReadOnlyList<NativeCombatActor> Actors { get; set; } =
            [new("钟离", 1), new("娜维娅", 2), new("班尼特", 3), new("香菱", 4)];
        public double LastFinishCheckAge => 0;
        internal string RosterSignature => string.Join(";", Actors.Select(actor => actor.Index + ":" + actor.Name));
        internal readonly List<(string Input, long Started, long Completed)> Inputs = new();
        internal readonly List<(Fischless.GameCapture.CaptureFrameStamp Source, string Roster)> CapturedRosters = new();
        internal bool HeldE;
        internal string? TargetIdentityName = "钟离";
        internal string? IdentityFault;
        private Fischless.GameCapture.CaptureFrameStamp? _firstIdentity;
        public bool SupportsActorIdentity => true;
        public ReviveRecoveryFrame? ReadActorIdentity(NativeCombatActor actor, BetterGenshinImpact.GameTask.Model.Area.ImageRegion frame)
        {
            if (TargetIdentityName == null) return null;
            var source = frame.FrameStamp;
            _firstIdentity ??= source;
            if (IdentityFault == "foreign") source = source with { SessionId = Guid.NewGuid() };
            if (IdentityFault == "stale") source = source with { CapturedTimestamp = source.CapturedTimestamp - source.TimestampFrequency * 2 };
            if (IdentityFault == "duplicate") source = _firstIdentity.Value;
            return new ReviveRecoveryFrame(source.Sequence, true, BetterGenshinImpact.GameTask.Common.BgiVision.ReviveUiState.None, 1,
                new Dictionary<int, string> { [actor.Index] = TargetIdentityName }).WithSource(source, Clock);
        }
        private DateTimeOffset _readyAt;
        private DateTimeOffset? _knownReadyAt;
        public Task PrepareVisionAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public IDisposable BeginExclusive(bool allowPassiveObservation) => new FullPrefixNoopScope();
        public BetterGenshinImpact.GameTask.Model.Area.ImageRegion Capture()
        {
            replay.Clock.Advance(TimeSpan.FromMilliseconds(20));
            var frame = replay.Io.Capture();
            CapturedRosters.Add((frame.FrameStamp, RosterSignature));
            return frame;
        }
        public bool IsCombatHud(BetterGenshinImpact.GameTask.Model.Area.ImageRegion frame) => true;
        public bool IsMainUi(BetterGenshinImpact.GameTask.Model.Area.ImageRegion frame) => true;
        public BetterGenshinImpact.GameTask.Common.BgiVision.CombatControlObservation ReadControl(BetterGenshinImpact.GameTask.Model.Area.ImageRegion frame)
        {
            Diagnostics.Add($"control=Normal source={frame.FrameStamp}");
            return new(BetterGenshinImpact.GameTask.Common.BgiVision.MotionStatus.Normal, false);
        }
        public int ReadActive(BetterGenshinImpact.GameTask.Model.Area.ImageRegion frame, BetterGenshinImpact.GameTask.AutoFight.Model.AvatarActiveCheckContext context)
        {
            Diagnostics.Add($"active=1 source={frame.FrameStamp}");
            return 1;
        }
        public bool? IsActorActive(NativeCombatActor actor, BetterGenshinImpact.GameTask.Model.Area.ImageRegion frame) => actor.Index == 1;
        public double ReadSkillCooldown(NativeCombatActor actor, BetterGenshinImpact.GameTask.Model.Area.ImageRegion frame)
        {
            var remaining = Math.Max(0, (_readyAt - Clock.GetUtcNow()).TotalSeconds);
            Diagnostics.Add($"observed-cooldown actor={actor.Name} remaining={remaining:R} source={frame.FrameStamp}");
            return remaining;
        }
        public bool IsSkillReady(NativeCombatActor actor, BetterGenshinImpact.GameTask.Model.Area.ImageRegion frame, double cooldown) => cooldown == 0;
        public BetterGenshinImpact.GameTask.AutoFight.Model.BurstObservation ReadBurst(BetterGenshinImpact.GameTask.Model.Area.ImageRegion frame, bool active) => default;
        public bool? ReadLowHp(BetterGenshinImpact.GameTask.Model.Area.ImageRegion frame) => false;
        public HashSet<int> ReadSideBurstReady(BetterGenshinImpact.GameTask.Model.Area.ImageRegion frame) => [];
        public bool TryGetKnownSkillCooldown(string actor, out double cooldown)
        {
            cooldown = _knownReadyAt is { } ready ? Math.Max(0, (ready - Clock.GetUtcNow()).TotalSeconds) : 0;
            Diagnostics.Add($"known-cooldown actor={actor} remaining={cooldown:R} known={_knownReadyAt.HasValue} source={replay.LastSource}");
            return _knownReadyAt.HasValue;
        }
        public void ConfirmSkill(NativeCombatActor actor, double cooldown, DateTime inputAtUtc) =>
            _knownReadyAt = Clock.GetUtcNow() + TimeSpan.FromSeconds(cooldown);
        public Task WaitSkillCooldown(NativeCombatActor actor, CancellationToken token) =>
            DelayAsync(TryGetKnownSkillCooldown(actor.Name, out var remaining) ? (int)Math.Ceiling(remaining * 1000) : 0, token);
        public BetterGenshinImpact.GameTask.AutoFight.CombatBattleHostInputResult SelectActor(int index, BetterGenshinImpact.GameTask.AutoFight.CombatNativeInputRequest request, CancellationToken token) =>
            throw new InvalidOperationException("This recorded prefix only selects the already observed Zhongli slot.");
        public void WaitForSelection(int milliseconds, CancellationToken token) { token.ThrowIfCancellationRequested(); replay.Clock.Advance(TimeSpan.FromMilliseconds(milliseconds)); }
        public bool OnSelectionMismatch(NativeCombatActor actor, int attempt, int attempts, int observed, CancellationToken token) => false;
        public void ResolveSelectionRecovery(NativeCombatActor actor, AvatarSelectionProtocol.Result<BetterGenshinImpact.GameTask.Model.Area.ImageRegion> selection, CancellationToken token) =>
            throw new InvalidOperationException("The recorded main HUD cannot enter non-combat recovery.");
        public void CheckDefeated(BetterGenshinImpact.GameTask.Model.Area.ImageRegion frame, CancellationToken token) => token.ThrowIfCancellationRequested();
        public BetterGenshinImpact.GameTask.AutoFight.CombatBattleHostInputResult SubmitInput(NativeCombatActor actor, CombatCommand command,
            BetterGenshinImpact.GameTask.AutoFight.CombatNativeInputRequest request, Action beforeFirstNative, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); beforeFirstNative();
            var result = Receive(command.Method.ToString());
            if (command.Method == Method.Skill) _readyAt = Clock.GetUtcNow() + TimeSpan.FromSeconds(12);
            return result;
        }
        public Vanara.PInvoke.User32.VK? HeldEPhysicalKey() => Vanara.PInvoke.User32.VK.VK_E;
        public BetterGenshinImpact.GameTask.AutoFight.CombatBattleHostInputResult SubmitHeldE(Vanara.PInvoke.User32.VK key, bool down,
            BetterGenshinImpact.GameTask.AutoFight.CombatNativeInputRequest request, Action beforeFirstNative, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); beforeFirstNative();
            Assert.Equal(Vanara.PInvoke.User32.VK.VK_E, key);
            HeldE = down;
            var result = Receive(down ? "E-down" : "E-up");
            if (!down) _readyAt = Clock.GetUtcNow() + TimeSpan.FromSeconds(12);
            return result;
        }
        private BetterGenshinImpact.GameTask.AutoFight.CombatBattleHostInputResult Receive(string input)
        {
            var started = Clock.GetTimestamp();
            replay.Clock.Advance(TimeSpan.FromMilliseconds(1));
            var completed = Clock.GetTimestamp();
            Inputs.Add((input, started, completed));
            return FullPrefixReceipt(started, completed);
        }
        public void ReleaseHeldE(Vanara.PInvoke.User32.VK key) => HeldE = false;
        public void ReleaseInput() => HeldE = false;
        public Task DelayAsync(int milliseconds, CancellationToken token) => replay.Io.Delay(Math.Max(1, milliseconds), token);
        public void Dispose() => ReleaseInput();
    }

    private sealed class FullPrefixDiagnosticLogger(List<string> entries) : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => new FullPrefixNoopScope();
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? error, Func<TState, Exception?, string> formatter) =>
            entries.Add($"{level}: {formatter(state, error)}{(error == null ? "" : Environment.NewLine + error)}");
    }

    private sealed class FullPrefixNoopScope : IDisposable { public void Dispose() { } }
}
