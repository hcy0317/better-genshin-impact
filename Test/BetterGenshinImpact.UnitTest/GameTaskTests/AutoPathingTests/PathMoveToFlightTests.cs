using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.AutoPathing.Handler;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using Microsoft.Extensions.Time.Testing;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

public class PathMoveToFlightTests
{
    [Fact]
    public async Task SameXyMustObserveFlightBeforeCompleting()
    {
        var replay = new PathReplay();
        replay.MotionAt = frame => frame < 4 ? MotionStatus.Normal : MotionStatus.Fly;
        await replay.Executor.MoveTo(replay.Point("fly"));
        Assert.Contains(replay.Inputs, x => x.Action == GIActions.Jump);
        Assert.True(replay.Frames >= 4);
        Assert.Contains(200, replay.Delays);
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Fact]
    public async Task NeverFlyingTimesOutAtOriginalBudgetAndReleases()
    {
        var replay = new PathReplay();
        await Assert.ThrowsAsync<RetryException>(() => replay.Executor.MoveTo(replay.Point("fly")));
        Assert.True(replay.Clock.GetUtcNow() - replay.Started <= TimeSpan.FromSeconds(240.2));
        Assert.All(replay.Inputs.Where(x => x.Type != KeyType.KeyUp), input => Assert.True(input.At - replay.Started < TimeSpan.FromSeconds(240)));
        Assert.Equal(KeyType.KeyUp, replay.Inputs[^1].Type);
    }

    [Theory]
    [InlineData("climb")]
    [InlineData("hud")]
    [InlineData("fallback")]
    [InlineData("duplicate")]
    public async Task UnusableNearObservationNeverJumpsOrCompletes(string kind)
    {
        var replay = new PathReplay();
        replay.MotionAt = _ => kind == "climb" ? MotionStatus.Climb : MotionStatus.Normal;
        replay.Hud = kind != "hud";
        replay.Direct = kind != "fallback";
        replay.Duplicate = kind == "duplicate";
        await Assert.ThrowsAsync<RetryException>(() => replay.Executor.MoveTo(replay.Point("fly")));
        // Duplicate permits the first unique Normal observation only; subsequent duplicates cannot press again.
        Assert.True(replay.Inputs.Count(x => x.Action == GIActions.Jump) <= (kind == "duplicate" ? 1 : 0));
    }

    [Fact]
    public async Task FirstFrameFlyingNeedsNoJumpAndFlightDoesNotLeakIntoNextNode()
    {
        var replay = new PathReplay { MotionAt = _ => MotionStatus.Fly };
        await replay.Executor.MoveTo(replay.Point("fly"));
        Assert.DoesNotContain(replay.Inputs, x => x.Action == GIActions.Jump);
        replay.MotionAt = _ => MotionStatus.Normal;
        await Assert.ThrowsAsync<RetryException>(() => replay.Executor.MoveTo(replay.Point("fly")));
    }

    [Theory]
    [InlineData("walk")]
    [InlineData("run")]
    public async Task OrdinarySameXyStillCompletesImmediately(string mode)
    {
        var replay = new PathReplay();
        await replay.Executor.MoveTo(replay.Point(mode));
        Assert.Empty(replay.Delays);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopFlyingUsesNewObservationNotPreviousFlight(bool flyingNow)
    {
        var replay = new PathReplay { MotionAt = _ => MotionStatus.Fly };
        await replay.Executor.MoveTo(replay.Point("fly"));
        var first = replay.Frames + 1;
        replay.MotionAt = frame => flyingNow && frame == first ? MotionStatus.Fly : MotionStatus.Normal;
        await new StopFlyingHandler().RunAsync(default, null, replay.Io);
        Assert.Equal(flyingNow ? 1 : 0, replay.Inputs.Count(x => x.Action == GIActions.NormalAttack));
    }

    [Fact]
    public async Task FocusDelayCannotReuseAnEarlierNormalFrame()
    {
        var replay = new PathReplay();
        replay.OnCheckInput = () => { replay.Clock.Advance(TimeSpan.FromSeconds(3)); replay.MotionAt = _ => MotionStatus.Climb; };
        await Assert.ThrowsAsync<RetryException>(() => replay.Executor.MoveTo(replay.Point("fly")));
        Assert.DoesNotContain(replay.Inputs, x => x.Action == GIActions.Jump);
    }

    [Fact]
    public async Task CaptureReturningAfterDeadlineCannotReportArrival()
    {
        var replay = new PathReplay { MotionAt = _ => MotionStatus.Fly };
        replay.BeforeCapture = frame => { if (frame == 2) replay.Clock.Advance(TimeSpan.FromSeconds(241)); };
        await Assert.ThrowsAsync<RetryException>(() => replay.Executor.MoveTo(replay.Point("fly")));
        Assert.All(replay.Inputs.Where(x => x.At - replay.Started >= TimeSpan.FromSeconds(240)), x => Assert.Equal(KeyType.KeyUp, x.Type));
    }

    [Theory]
    [InlineData("capture")]
    [InlineData("locate")]
    [InlineData("cancel")]
    public async Task MoveToReleasesWWhenCaptureLocateOrCancellationFails(string reason)
    {
        using var cancellation = new CancellationTokenSource();
        var replay = new PathReplay(cancellation.Token);
        if (reason == "capture") replay.BeforeCapture = frame => { if (frame == 2) throw new IOException("capture"); };
        if (reason == "locate") replay.PositionAt = frame => frame == 2 ? throw new IOException("locate") : new Point2f(100, 100);
        if (reason == "cancel") replay.BeforeCapture = frame => { if (frame == 2) cancellation.Cancel(); };
        Assert.NotNull(await Record.ExceptionAsync(() => replay.Executor.MoveTo(replay.Point("fly"))));
        Assert.Equal((GIActions.MoveForward, KeyType.KeyUp), (replay.Inputs[^1].Action, replay.Inputs[^1].Type));
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Fact]
    public async Task NearFlightAdvancesWithNonBlockingTwentyFpsCapture()
    {
        var replay = new PathReplay { ProducerIntervalMilliseconds = 50 };
        replay.MotionAt = _ => replay.Inputs.Any(x => x.Action == GIActions.Jump) ? MotionStatus.Fly : MotionStatus.Normal;
        await replay.Executor.MoveTo(replay.Point("fly"));
        Assert.Single(replay.Inputs.Where(x => x.Action == GIActions.Jump));
    }

    [Theory]
    [InlineData("fly", "motion", 240)]
    [InlineData("fly", "hud", 240)]
    [InlineData("climb", "motion", 60)]
    [InlineData("climb", "hud", 60)]
    public async Task SlowRecognitionCannotCompleteOrInputAfterOriginalDeadline(string mode, string reader, int seconds)
    {
        var replay = new PathReplay();
        replay.HudAt = _ => { if (reader == "hud") replay.Clock.Advance(TimeSpan.FromSeconds(seconds)); return true; };
        replay.MotionAt = _ => { if (reader == "motion") replay.Clock.Advance(TimeSpan.FromSeconds(seconds)); return MotionStatus.Fly; };
        await Assert.ThrowsAsync<RetryException>(() => replay.Executor.MoveTo(replay.Point(mode)));
        Assert.All(replay.Inputs.Where(x => x.At >= replay.Started.AddSeconds(seconds)), x => Assert.Equal(KeyType.KeyUp, x.Type));
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Theory]
    [InlineData("motion")]
    [InlineData("hud")]
    public async Task SlowRecognitionCannotAuthorizeJumpFromAnExpiredSource(string reader)
    {
        var replay = new PathReplay();
        replay.HudAt = _ => { if (reader == "hud") replay.Clock.Advance(TimeSpan.FromSeconds(2.1)); return true; };
        replay.MotionAt = _ => { if (reader == "motion") replay.Clock.Advance(TimeSpan.FromSeconds(2.1)); return MotionStatus.Normal; };
        await Assert.ThrowsAsync<RetryException>(() => replay.Executor.MoveTo(replay.Point("fly")));
        Assert.DoesNotContain(replay.Inputs, x => x.Action == GIActions.Jump);
    }
}

internal sealed class PathReplay
{
    internal readonly FakeTimeProvider Clock = new();
    internal readonly List<(GIActions Action, KeyType Type, DateTimeOffset At)> Inputs = new();
    internal readonly List<int> Delays = new();
    internal readonly List<ImageRegion> Images = new();
    internal int Frames;
    internal Func<int, MotionStatus> MotionAt = _ => MotionStatus.Normal;
    internal readonly PathExecutor Executor;
    internal readonly PathMoveToIo Io;
    internal DateTimeOffset Started => new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
    internal bool Hud = true, Direct = true, Duplicate;
    internal Func<int, bool>? HudAt;
    internal Func<int, Point2f> PositionAt = _ => new Point2f(100, 100);
    internal int MinimumWait;
    internal int ProducerIntervalMilliseconds;
    internal float CameraAngle;
    internal readonly List<(int X, DateTimeOffset At)> MouseInputs = new();
    internal readonly List<(GIActions Action, KeyType Type, DateTimeOffset At)> RecoveryInputs = new();
    internal Func<float>? ReadCamera;
    internal bool LockCamera;
    internal Action<int>? BeforeCapture, AfterDelay;
    internal Action? OnCheckInput;
    internal Action<GIActions, KeyType>? OnInput;
    internal Func<CaptureFrameStamp, CaptureFrameStamp>? StampTransform;
    private CaptureFrameStamp _stamp;
    private readonly HashSet<GIActions> _held = new();
    private readonly CaptureFrameSource _source;
    internal PathReplay(CancellationToken cancellation = default)
    {
        _source = new(Clock);
        Io = new PathMoveToIo
        {
            LoggerFactory = () => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            Clock = Clock,
            CheckInput = () => OnCheckInput?.Invoke(),
            CameraOrientation = _ => ReadCamera?.Invoke() ?? CameraAngle, Dpi = () => 1,
            MouseMove = (x, _) => { MouseInputs.Add((x, Clock.GetUtcNow())); if (!LockCamera) CameraAngle += x / (Math.Abs(x) > 360 ? 4f : Math.Abs(x) > 90 ? 3f : Math.Abs(x) > 10 ? 2f : 1f); },
            Capture = () => { Frames++; BeforeCapture?.Invoke(Frames); if (!_stamp.IsKnown || !Duplicate && (ProducerIntervalMilliseconds == 0 || Clock.GetElapsedTime(_stamp.CapturedTimestamp).TotalMilliseconds >= ProducerIntervalMilliseconds)) _stamp = _source.Next(); var image = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3), 0, 0) { FrameStamp = StampTransform?.Invoke(_stamp) ?? _stamp }; Images.Add(image); return image; },
            Locate = (_, _) => Task.FromResult(new PathPosition(PositionAt(Frames), 0, Direct)),
            Motion = _ => MotionAt(Frames), CombatHud = _ => HudAt?.Invoke(Frames) ?? Hud,
            SwitchAvatar = _ => Task.CompletedTask, EndJudgment = _ => { },
            RotateUntil = (_, _) => Task.FromResult(true), RotateStep = (_, _) => 0,
            HurryOn = (_, _, _, _, _) => Task.FromResult(false),
            Send = (action, type) => { Inputs.Add((action, type, Clock.GetUtcNow())); if (UiOperation.Current?.Name == "path-climb-recovery") RecoveryInputs.Add((action, type, Clock.GetUtcNow())); if (type == KeyType.KeyDown) _held.Add(action); else if (type == KeyType.KeyUp) _held.Remove(action); OnInput?.Invoke(action, type); },
            IsDown = action => _held.Contains(action),
            Delay = (ms, ct) => { ct.ThrowIfCancellationRequested(); Delays.Add(ms); Clock.Advance(TimeSpan.FromMilliseconds(Math.Max(ms, MinimumWait))); AfterDelay?.Invoke(ms); return Task.CompletedTask; }
        };
        Executor = new PathExecutor(cancellation, Io) { PartyConfig = new PathingPartyConfig { MainAvatarIndex = "1" } };
    }
    internal WaypointForTrack Point(string mode) => new(new Waypoint { X = 0, Y = 0, MoveMode = mode,
        Type = mode == "fly" ? "target" : "path", Action = mode == "fly" ? "stop_flying" : null },
        new BetterGenshinImpact.GameTask.Common.Map.Maps.Base.RouteMapContext("Teyvat", "SIFT", null), point => point) { X = 100, Y = 100 };
}
