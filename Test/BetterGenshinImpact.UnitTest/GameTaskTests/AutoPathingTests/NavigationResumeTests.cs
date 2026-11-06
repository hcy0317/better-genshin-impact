using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.Common.Map.Maps.Base;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

[CollectionDefinition(nameof(NavigationResumeDataCollection), DisableParallelization = true)]
public sealed class NavigationResumeDataCollection { }

[Collection(nameof(NavigationResumeDataCollection))]
public class NavigationResumeTests
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
        var directCounter = scenario is "cache-near-direct-far" or "direct-stale-after-await" or "direct-foreign-frame";
        var position = new Point2f(100,100);
        var movementTarget = position;
        replay.PositionAt = _ => position;
        replay.AfterDelay = milliseconds =>
        {
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
                return (await replay.Io.Locate(frame,point)) with { Point=healed && teleports > 1 && directCounter && scenario != "cache-near-direct-far" ? new((float)point.X,(float)point.Y) : position, IsDirect=true, Source=PathPositionSource.Direct };
            }
        };
        var scene = new PathExecutor.PathingSceneIo
        {
            Prepare = (_,_) => Task.FromResult(true),
            Convert = (point, map) => new WaypointForTrack(point, map, value => value),
            Teleport = (point,_) => { teleports++; position=new((float)point.X,(float)point.Y); if (scenario == "legacy-owner-expired" && teleports > 1) replay.Clock.Advance(TimeSpan.FromSeconds(25)); return Task.CompletedTask; },
            LowHp = (_,point) =>
            {
                if (point.Id == 3 && legacy && healed) returnedCurrentDeadline = replay.Clock.GetUtcNow() + BetterGenshinImpact.GameTask.Common.Ui.UiOperation.Current!.Remaining;
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
            RunAction = (point,_) =>
            {
                macroCalls++;
                return Task.FromResult(CombatScriptExecutor.FromFlowResult(scenario == "skipped" && point.Id == 2 ? CombatFlowResult.Skipped : CombatFlowResult.Succeeded));
            },
            MacroFactory = () => null
        };
        subject = new PathExecutor(default, io, sceneIo: scene)
            { PartyConfig = new() { MainAvatarIndex = "1" } };
        var route = new PathingTask { FileName="recorded-prefix-route", FullPath="recorded-prefix-route.json", Positions =
        [
            new() { Id=1, X=100,Y=100, Type="teleport",MoveMode="walk" },
            new() { Id=2, X=110,Y=100, Type="path",MoveMode=scenario == "unsupported-jump" ? "jump" : "walk",Action=legacy ? "" : "combat_script",ActionParams=legacy ? "" : "钟离 d(0.2),e" },
            new() { Id=3, X=120,Y=100, Type="path",MoveMode="walk" },
            new() { Id=4, X=130,Y=100, Type="path",MoveMode="walk",Action="combat_script",ActionParams="attack(0.1)" }
        ] };
        using var parent = scenario == "parent-expired" ? BetterGenshinImpact.GameTask.Common.Ui.UiOperation.Begin("navigation-parent", TimeSpan.FromSeconds(10), clock: replay.Clock) : null;
        if (scenario is not ("completed" or "skipped" or "legacy-safe"))
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(() => subject.Pathing(route));
            Assert.DoesNotContain("Application host", error.ToString());
            Assert.DoesNotContain("NullReferenceException", error.ToString());
            Assert.False(subject.SuccessEnd);
            Assert.Equal(legacy ? 0 : 1, macroCalls);
            Assert.Equal(scenario == "tickets-exhausted" ? 3 : 1, recoveries);
            if (scenario == "tickets-exhausted") Assert.Equal(3, teleports); // no third projection ticket
            return;
        }
        await subject.Pathing(route);
        Assert.True(subject.SuccessEnd);
        if (legacy) Assert.Equal(originalCurrentDeadline, returnedCurrentDeadline);
        if (!legacy)
        {
            Assert.Equal(prefixSettlement, subject.ActionSettlements[1]);
            Assert.Equal(scenario == "skipped" ? CombatExecutionKind.Skipped : CombatExecutionKind.Completed, subject.ActionSettlements[1].Outcome.Kind);
        }
        Assert.True(healed);
        Assert.Equal(legacy ? 1 : 2, macroCalls); // original prefix and original suffix, no prefix replay
        Assert.Equal(2, teleports); // original route entry only, initial and original-entry projection
        Assert.Equal(legacy ? "" : "钟离 d(0.2),e", route.Positions[1].ActionParams);
        Assert.Equal(legacy ? "" : "combat_script", route.Positions[1].Action);
    }
}
