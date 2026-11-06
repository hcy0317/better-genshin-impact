using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.Helpers;
using Fischless.GameCapture;
using Fischless.WindowsInput;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

public class SidestepFeedbackTests(ITestOutputHelper output)
{
    [Fact]
    public async Task DelayFeedbackStopsLeftBeforeTailAndDoesNotSendReverseRight()
    {
        using var replay = new Replay("lost-delay");
        var result = await replay.Run();
        output.WriteLine($"scope={typeof(PathRecoveryScope).Assembly.GetName().Name} mvid={typeof(PathRecoveryScope).Assembly.ManifestModule.ModuleVersionId} inputs={string.Join(",", replay.Inputs)} observations={replay.Observations}");
        Assert.DoesNotContain(replay.Inputs, item => item == "MoveRight:KeyDown");
        Assert.True(result);
        Assert.Equal(1, replay.Inputs.Count(item => item == "MoveLeft:KeyUp"));
        Assert.NotEmpty(replay.ReleaseFrames);
        Assert.InRange(replay.Observations, 1, 23); // 旧7C75同一whole反例实际读取23帧。
        Assert.All(replay.ReleaseFrames, frame => Assert.True(frame.CapturedTimestamp > replay.LeftUpAt));
        Assert.InRange(replay.Clock.GetElapsedTime(replay.LeftDownAt, replay.LeftUpAt).TotalMilliseconds, 60, 350);
        Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
        Assert.Null(System.Windows.Application.Current);
        Assert.Equal(0, replay.NativeCalls);
    }

    [Fact]
    public async Task HeldDisplacementCannotBecomeSuccessIfReleaseFeedbackFallsBack()
    {
        using var replay = new Replay("falls-after-release");
        var result = await replay.Run();
        Assert.False(result);
        Assert.NotEmpty(replay.ReleaseFrames);
        Assert.Equal(0, replay.NativeCalls);
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("before-fence")]
    [InlineData("stale")]
    [InlineData("duplicate")]
    [InlineData("invalid")]
    [InlineData("unknown-source")]
    [InlineData("climb")]
    [InlineData("swim")]
    public async Task IneligibleReleaseFeedbackCannotAuthorizeRecoveryOrReverseInput(string mode)
    {
        using var replay = new Replay(mode);
        bool? recovered = null;
        var error = await Record.ExceptionAsync(async () => recovered = await replay.Run());
        Assert.True(error != null || recovered == false, "Ineligible post-release feedback was accepted as recovery.");
        Assert.DoesNotContain(replay.Inputs, item => item == "MoveRight:KeyDown");
        Assert.Contains("MoveLeft:KeyUp", replay.Inputs);
        Assert.Equal(0, replay.NativeCalls);
    }

    [Theory]
    [InlineData("unknown-down")]
    [InlineData("keyup-failed")]
    [InlineData("cancel")]
    [InlineData("deadline")]
    public async Task NativeFailureOrParentStopCleansUpWithoutAnotherSide(string mode)
    {
        using var replay = new Replay(mode);
        var error = await Record.ExceptionAsync(() => replay.Run());
        Assert.NotNull(error);
        Assert.Contains("MoveLeft:KeyUp", replay.Inputs);
        Assert.DoesNotContain(replay.Inputs, item => item == "MoveRight:KeyDown");
        Assert.Equal(0, replay.NativeCalls);
    }

    [Fact]
    public async Task NoMovementUsesOnlyTheOriginalTwoSidesAndDoesNotRenewAWindow()
    {
        using var replay = new Replay("stationary");
        Assert.False(await replay.Run());
        Assert.Equal(new[] { "MoveLeft:KeyDown", "MoveLeft:KeyUp", "MoveRight:KeyDown", "MoveRight:KeyUp" }, replay.Inputs);
        Assert.InRange(replay.TotalHeldMs, 0, 710);
        Assert.Equal(0, replay.NativeCalls);
    }

    private sealed class Replay(string mode) : IDisposable
    {
        internal FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        internal List<string> Inputs { get; } = [];
        internal List<CaptureFrameStamp> ReleaseFrames { get; } = [];
        private readonly CancellationTokenSource _cancel = new();
        private readonly Guid _session = Guid.NewGuid();
        private long _sequence;
        private GIActions? _held;
        private int _heldReads;
        private bool _leftReleased;
        private CaptureFrameStamp? _firstRelease;
        private long _heldStarted;
        internal long LeftDownAt { get; private set; }
        internal long LeftUpAt { get; private set; }
        internal double TotalHeldMs { get; private set; }
        internal int NativeCalls { get; private set; }
        internal int Observations { get; private set; }

        private CaptureFrameStamp Frame() => new(_session, ++_sequence, Clock.GetTimestamp(), Clock.TimestampFrequency, Clock.GetUtcNow());
        internal Task<bool> Run() => UiOperation.RunAsync("sidestep-whole", TimeSpan.FromSeconds(10), _cancel.Token, async operation =>
        {
            var start = Frame();
            var io = new PathMoveToIo
            {
                Clock = Clock, CheckInput = () => _cancel.Token.ThrowIfCancellationRequested(),
                Delay = (ms, ct) => { ct.ThrowIfCancellationRequested(); Clock.Advance(TimeSpan.FromMilliseconds(ms)); return Task.CompletedTask; },
                Send = (action, type) => InputDispatchCapture.DispatchTransport(admit =>
                {
                    admit();
                    Inputs.Add($"{action}:{type}");
                    if (type == KeyType.KeyDown)
                    {
                        _held = action; _heldReads = 0; _heldStarted = Clock.GetTimestamp();
                        if (action == GIActions.MoveLeft) LeftDownAt = _heldStarted;
                        if (mode == "unknown-down") throw new InvalidOperationException("fake transport unknown");
                    }
                    if (type == KeyType.KeyUp)
                    {
                        TotalHeldMs += Clock.GetElapsedTime(_heldStarted).TotalMilliseconds;
                        if (action == GIActions.MoveLeft) { LeftUpAt = Clock.GetTimestamp(); _leftReleased = true; }
                        _held = null;
                        if (mode == "keyup-failed") throw new InvalidOperationException("fake release failed");
                    }
                })
            };
            Task<PathMoveObservation> Observe()
            {
                Observations++;
                Clock.Advance(TimeSpan.FromMilliseconds(2));
                var position = new Point2f(0, 0);
                if (_held == GIActions.MoveLeft)
                {
                    _heldReads++;
                    if (mode == "cancel") _cancel.Cancel();
                    if (mode == "deadline") Clock.Advance(TimeSpan.FromSeconds(11));
                    if (mode != "stationary") position = new Point2f(_heldReads == 1 ? 2.1f : 2.3f, 0);
                    if (mode == "lost-delay" && _heldReads >= 3)
                    { position = new Point2f(.5f, 0); Clock.Advance(TimeSpan.FromMilliseconds(350)); }
                }
                else if (_leftReleased && mode != "stationary")
                    position = new Point2f(mode == "falls-after-release" || mode == "lost-delay" && _heldReads >= 3 ? .5f : 2.3f, 0);
                var stamp = Frame();
                var motion = MotionStatus.Normal;
                var valid = true;
                if (_leftReleased && _held == null)
                {
                    ReleaseFrames.Add(stamp);
                    if (mode == "foreign") stamp = stamp with { SessionId = Guid.NewGuid() };
                    if (mode == "duplicate") { _firstRelease ??= stamp; stamp = _firstRelease.Value; }
                    if (mode == "invalid") valid = false;
                    if (mode == "unknown-source") stamp = default;
                    if (mode == "before-fence") stamp = stamp with { CapturedTimestamp = LeftUpAt };
                    if (mode == "stale") { Clock.Advance(TimeSpan.FromSeconds(3)); valid = false; }
                    if (mode == "climb") motion = MotionStatus.Climb;
                    if (mode == "swim") motion = MotionStatus.Swim;
                }
                return Task.FromResult(new PathMoveObservation(stamp, position, valid, motion, true));
            }
            using var capture = new InputDispatchCapture();
            using var scope = new PathRecoveryScope(io, operation, operation.Token, start, Observe,
                (_, _, _, _) => { });
            try { return await PathPreciseRecovery.SidestepAsync(scope, new(start, new Point2f(0, 0), true, MotionStatus.Normal, true)); }
            finally { NativeCalls = capture.NativeCalls; }
        }, NullLogger.Instance, Clock);
        public void Dispose() => _cancel.Dispose();
    }
}
