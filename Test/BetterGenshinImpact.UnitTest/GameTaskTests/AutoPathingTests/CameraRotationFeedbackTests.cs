using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using Fischless.GameCapture;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

public class CameraRotationFeedbackTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    public async Task ScopedTerminalSettleDoesNotObserveOrCaptureBeforeItsSingleReadback(int controls)
    {
        var replay = new PathReplay { EmitReceipts = true };
        replay.BeforeCapture = _ => replay.Clock.Advance(TimeSpan.FromMilliseconds(1));
        CaptureFrameStamp seed;
        using (var initial = replay.Io.Capture()) seed = initial.FrameStamp;
        var scopeObservations = 0;
        var observationsAtLastInput = -1;
        var capturesAtLastInput = -1;
        var delaysAtLastInput = -1;
        replay.ReadCamera = () => replay.MouseInputs.Count == controls ? 119 : 129 + replay.MouseInputs.Count % 2;
        replay.BeforeMouseDispatch = () =>
        {
            if (replay.MouseInputs.Count != controls - 1) return;
            observationsAtLastInput = scopeObservations;
            capturesAtLastInput = replay.Frames;
            delaysAtLastInput = replay.Delays.Count;
        };
        var result = await BetterGenshinImpact.GameTask.Common.Ui.UiOperation.RunAsync(
            "scoped-camera-terminal", TimeSpan.FromSeconds(10), default, async operation =>
            {
                using var scope = new PathRecoveryScope(replay.Io, operation, default, seed, () =>
                {
                    scopeObservations++;
                    using var frame = replay.Io.Capture();
                    return Task.FromResult(new PathMoveObservation(frame.FrameStamp, new OpenCvSharp.Point2f(100, 100),
                        true, BetterGenshinImpact.GameTask.Common.BgiVision.MotionStatus.Normal, true));
                });
                return await new CameraRotateTask(default) { Io = replay.Io }
                    .WaitUntilRotatedTo(119, 5, controls, scope);
            }, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, replay.Clock);

        Assert.True(result);
        Assert.True(observationsAtLastInput > 0);
        Assert.Equal(observationsAtLastInput, scopeObservations);
        Assert.Equal(capturesAtLastInput + 1, replay.Frames);
        Assert.Equal(new[] { 60 }, replay.Delays.Skip(delaysAtLastInput));
        Assert.Equal(controls, replay.MouseInputs.Count);
        Assert.All(replay.Images, frame => Assert.True(frame.SrcMat.IsDisposed));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("foreign")]
    [InlineData("no-response")]
    [InlineData("camera-too-short")]
    public async Task ScopedTerminalFailureCannotLoopPastItsCameraDeadline(string kind)
    {
        var replay = new PathReplay { EmitReceipts = true };
        replay.BeforeCapture = _ => replay.Clock.Advance(TimeSpan.FromMilliseconds(1));
        CaptureFrameStamp seed;
        using (var initial = replay.Io.Capture()) seed = initial.FrameStamp;
        var lastScene = seed;
        var scopeObservations = 0;
        var capturesAtInput = 0;
        replay.ReadCamera = () => replay.MouseInputs.Count == 1 ? 119 : 129;
        replay.BeforeMouseDispatch = () => capturesAtInput = replay.Frames;
        replay.StampTransform = stamp =>
        {
            if (replay.MouseInputs.Count == 0) { lastScene = stamp; return stamp; }
            return kind switch
            {
                "duplicate" => lastScene,
                "foreign" => stamp with { SessionId = Guid.NewGuid() },
                _ => stamp
            };
        };
        replay.AvailabilityAt = _ => replay.MouseInputs.Count == 1 && kind == "no-response"
            ? BetterGenshinImpact.GameTask.Common.Ui.WorldFrameKind.Unknown
            : BetterGenshinImpact.GameTask.Common.Ui.WorldFrameKind.Playable;
        var cameraDeadline = replay.Clock.GetUtcNow().AddMilliseconds(kind == "camera-too-short" ? 40 : 80);
        var result = await BetterGenshinImpact.GameTask.Common.Ui.UiOperation.RunAsync(
            "scoped-camera-terminal-failure", TimeSpan.FromSeconds(10), default, async operation =>
            {
                using var scope = new PathRecoveryScope(replay.Io, operation, default, seed, () =>
                {
                    scopeObservations++;
                    using var frame = replay.Io.Capture();
                    return Task.FromResult(new PathMoveObservation(frame.FrameStamp, new OpenCvSharp.Point2f(100, 100),
                        true, BetterGenshinImpact.GameTask.Common.BgiVision.MotionStatus.Normal, true));
                });
                return await new CameraRotateTask(default) { Io = replay.Io, Deadline = () => cameraDeadline }
                    .WaitUntilRotatedTo(119, 5, 1, scope);
            }, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, replay.Clock);

        Assert.False(result);
        Assert.Equal(1, scopeObservations);
        Assert.Single(replay.MouseInputs);
        Assert.Equal(capturesAtInput + (kind == "camera-too-short" ? 0 : 1), replay.Frames);
        Assert.Equal(kind == "camera-too-short" ? Array.Empty<int>() : new[] { 60 }, replay.Delays);
        Assert.True(replay.Clock.GetUtcNow() < cameraDeadline);
        Assert.All(replay.Images, frame => Assert.True(frame.SrcMat.IsDisposed));
    }

    [Fact]
    public async Task LastControlInputGetsOneFreshReadOnlyConfirmation()
    {
        var replay = new PathReplay { EmitReceipts = true };
        var angleReads = 0;
        replay.ReadCamera = () =>
        {
            angleReads++;
            return replay.MouseInputs.Count == 50 ? 119 : 129 + replay.MouseInputs.Count % 2;
        };
        replay.BeforeCapture = _ =>
        {
            if (replay.MouseInputs.Count == 50)
                Assert.True(replay.Clock.GetUtcNow() - replay.MouseInputs[^1].At >= TimeSpan.FromMilliseconds(60));
        };
        var deadline = replay.Clock.GetUtcNow().AddSeconds(10);

        Assert.True(await new CameraRotateTask(default) { Io = replay.Io, Deadline = () => deadline }
            .WaitUntilRotatedTo(119, 5, 50));

        Assert.Equal(50, replay.MouseInputs.Count);
        Assert.Equal(51, angleReads);
        Assert.Equal(51, replay.Frames);
        Assert.True(replay.Clock.GetUtcNow() < deadline);
        Assert.All(replay.Images, frame => Assert.True(frame.SrcMat.IsDisposed));
    }

    [Theory]
    [InlineData("outside-tolerance")]
    [InlineData("duplicate")]
    [InlineData("foreign")]
    [InlineData("before-fence")]
    [InlineData("no-response")]
    [InlineData("nonfinite")]
    [InlineData("missing-source")]
    [InlineData("stale")]
    public async Task FinalReadbackCannotConfirmAnUnacceptedObservation(string kind)
    {
        var replay = new PathReplay { EmitReceipts = true };
        CaptureFrameStamp previous = default;
        replay.ReadCamera = () => replay.MouseInputs.Count == 50
            ? kind == "outside-tolerance" ? 128 : kind == "nonfinite" ? float.NaN : 119
            : 129 + replay.MouseInputs.Count % 2;
        replay.StampTransform = stamp =>
        {
            if (replay.MouseInputs.Count < 50) { previous = stamp; return stamp; }
            return kind switch
            {
                "duplicate" => previous,
                "foreign" => stamp with { SessionId = Guid.NewGuid() },
                "before-fence" => stamp with { CapturedTimestamp = previous.CapturedTimestamp - 1 },
                "missing-source" => default,
                "stale" => stamp with { CapturedTimestamp = stamp.CapturedTimestamp - 3 * stamp.TimestampFrequency },
                _ => stamp
            };
        };
        replay.AvailabilityAt = _ => kind == "no-response" && replay.MouseInputs.Count == 50
            ? BetterGenshinImpact.GameTask.Common.Ui.WorldFrameKind.Unknown
            : BetterGenshinImpact.GameTask.Common.Ui.WorldFrameKind.Playable;
        var deadline = replay.Clock.GetUtcNow().AddSeconds(10);

        Assert.False(await new CameraRotateTask(default) { Io = replay.Io, Deadline = () => deadline }
            .WaitUntilRotatedTo(119, 5, 50));

        Assert.Equal(50, replay.MouseInputs.Count);
        Assert.Equal(51, replay.Frames);
        Assert.True(replay.Clock.GetUtcNow() < deadline);
        Assert.All(replay.Images, frame => Assert.True(frame.SrcMat.IsDisposed));
    }

    [Theory]
    [InlineData("settle")]
    [InlineData("quality")]
    [InlineData("angle")]
    public async Task FinalReadbackCancellationAlwaysPropagates(string phase)
    {
        using var cancellation = new CancellationTokenSource();
        var replay = new PathReplay(cancellation.Token) { EmitReceipts = true };
        replay.ReadCamera = () =>
        {
            if (replay.MouseInputs.Count < 50) return 129 + replay.MouseInputs.Count % 2;
            if (phase == "angle") cancellation.Cancel();
            return 119;
        };
        replay.AfterDelay = _ => { if (replay.MouseInputs.Count == 50 && phase == "settle") cancellation.Cancel(); };
        replay.AvailabilityAt = _ =>
        {
            if (replay.MouseInputs.Count == 50 && phase == "quality")
            {
                cancellation.Cancel();
                return BetterGenshinImpact.GameTask.Common.Ui.WorldFrameKind.Unknown;
            }
            return BetterGenshinImpact.GameTask.Common.Ui.WorldFrameKind.Playable;
        };
        var deadline = replay.Clock.GetUtcNow().AddSeconds(10);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new CameraRotateTask(cancellation.Token) { Io = replay.Io, Deadline = () => deadline }
                .WaitUntilRotatedTo(119, 5, 50));

        Assert.Equal(50, replay.MouseInputs.Count);
        Assert.Equal(phase == "settle" ? 50 : 51, replay.Frames);
        Assert.All(replay.Images, frame => Assert.True(frame.SrcMat.IsDisposed));
    }

    [Fact]
    public async Task FinalReadbackDoesNotStartWhenTheOriginalDeadlineCannotFitItsSettle()
    {
        var replay = new PathReplay { EmitReceipts = true };
        replay.ReadCamera = () => 129 + replay.MouseInputs.Count % 2;
        var deadline = replay.Clock.GetUtcNow().AddMilliseconds(49 * 60 + 30);

        Assert.False(await new CameraRotateTask(default) { Io = replay.Io, Deadline = () => deadline }
            .WaitUntilRotatedTo(119, 5, 50));

        Assert.Equal(50, replay.MouseInputs.Count);
        Assert.Equal(50, replay.Frames);
        Assert.Equal(49, replay.Delays.Count(delay => delay == 60));
        Assert.True(replay.Clock.GetUtcNow() < deadline);
    }

    [Fact]
    public async Task AmbientParentRemainingDoesNotPermitAnotherSettleOrCapture()
    {
        var replay = new PathReplay { EmitReceipts = true, CameraAngle = 129 };

        var result = await BetterGenshinImpact.GameTask.Common.Ui.UiOperation.RunAsync(
            "camera-final-parent", TimeSpan.FromMilliseconds(30), default,
            _ => new CameraRotateTask(default) { Io = replay.Io }.WaitUntilRotatedTo(119, 5, 1),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, replay.Clock);

        Assert.False(result);
        Assert.Single(replay.MouseInputs);
        Assert.Equal(1, replay.Frames);
        Assert.Empty(replay.Delays);
        Assert.All(replay.Images, frame => Assert.True(frame.SrcMat.IsDisposed));
    }

    [Fact]
    public async Task FinalAngleRecognitionCannotConfirmAfterTheOriginalDeadline()
    {
        var replay = new PathReplay { EmitReceipts = true };
        replay.ReadCamera = () =>
        {
            if (replay.MouseInputs.Count < 50) return 129 + replay.MouseInputs.Count % 2;
            replay.Clock.Advance(TimeSpan.FromSeconds(20));
            return 119;
        };
        var deadline = replay.Clock.GetUtcNow().AddSeconds(10);

        Assert.False(await new CameraRotateTask(default) { Io = replay.Io, Deadline = () => deadline }
            .WaitUntilRotatedTo(119, 5, 50));

        Assert.Equal(50, replay.MouseInputs.Count);
        Assert.Equal(51, replay.Frames);
        Assert.All(replay.Images, frame => Assert.True(frame.SrcMat.IsDisposed));
    }

    [Fact]
    public async Task UnchangedEarlyStopDoesNotTakeAnAttemptBudgetReadback()
    {
        var replay = new PathReplay { EmitReceipts = true, LockCamera = true, CameraAngle = 129 };

        Assert.False(await new CameraRotateTask(default) { Io = replay.Io }.WaitUntilRotatedTo(119, 5, 50));

        Assert.Equal(11, replay.MouseInputs.Count);
        Assert.Equal(11, replay.Frames);
        Assert.Equal(10, replay.Delays.Count(delay => delay == 60));
    }

    [Fact]
    public async Task RecognitionCannotSendMouseAfterTheDefaultDeadline()
    {
        var replay = new PathReplay();
        replay.BeforeCapture = _ => replay.Clock.Advance(TimeSpan.FromMilliseconds(14900));
        replay.ReadCamera = () => { replay.Clock.Advance(TimeSpan.FromMilliseconds(200)); return 0; };
        Assert.False(await new CameraRotateTask(default) { Io = replay.Io }.WaitUntilRotatedTo(90, 2));
        Assert.Empty(replay.MouseInputs);
    }

    [Fact]
    public async Task TransportPreparationCannotDispatchAfterTheOriginalDeadline()
    {
        var replay = new PathReplay { EmitReceipts = true };
        replay.BeforeMouseDispatch = () => replay.Clock.Advance(TimeSpan.FromMilliseconds(80));
        var deadline = replay.Clock.GetUtcNow().AddMilliseconds(70);
        await Assert.ThrowsAsync<RetryException>(() =>
            new CameraRotateTask(default) { Io = replay.Io, Deadline = () => deadline }.WaitUntilRotatedTo(90, 2));
        Assert.Empty(replay.MouseInputs);
    }

    [Fact]
    public async Task AlreadyWithinToleranceDoesNotMoveMouse()
    {
        var replay = new PathReplay { CameraAngle = 89 };
        Assert.True(await new CameraRotateTask(default) { Io = replay.Io }.WaitUntilRotatedTo(90, 2));
        Assert.Empty(replay.MouseInputs);
    }

    [Fact]
    public async Task ActualRotationWaitsForFreshFeedbackAfterGameResponse()
    {
        var replay = new PathReplay();
        replay.BeforeCapture = _ =>
        {
            if (replay.MouseInputs.LastOrDefault() is var input && replay.MouseInputs.Count > 0)
                Assert.True(replay.Clock.GetUtcNow() - input.At >= TimeSpan.FromMilliseconds(60));
        };
        Assert.True(await new CameraRotateTask(default) { Io = replay.Io }.WaitUntilRotatedTo(90, 2));
        Assert.NotEmpty(replay.MouseInputs);
        Assert.All(replay.Images, frame => Assert.True(frame.SrcMat.IsDisposed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldOrDifferentSessionFeedbackCannotConfirmRotation(bool differentSession)
    {
        var replay = new PathReplay();
        CaptureFrameStamp initial = default;
        replay.StampTransform = stamp =>
        {
            if (!initial.IsKnown) initial = stamp;
            if (replay.MouseInputs.Count == 0) return stamp;
            return differentSession ? stamp with { SessionId = Guid.NewGuid() } : initial;
        };
        await Assert.ThrowsAsync<RetryException>(() => new CameraRotateTask(default) { Io = replay.Io }.WaitUntilRotatedTo(90, 2));
        Assert.Single(replay.MouseInputs);
    }

    [Fact]
    public async Task CancellationAfterMouseInputCannotConfirmTheOldAngle()
    {
        using var cancellation = new CancellationTokenSource();
        var replay = new PathReplay(cancellation.Token);
        replay.AfterDelay = _ => cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new CameraRotateTask(cancellation.Token) { Io = replay.Io }.WaitUntilRotatedTo(90, 2));
        Assert.Single(replay.MouseInputs);
        Assert.All(replay.Images, frame => Assert.True(frame.SrcMat.IsDisposed));
    }

    [Fact]
    public async Task OriginalParentDeadlineStillEndsFeedbackWait()
    {
        var replay = new PathReplay { LockCamera = true };
        var deadline = replay.Clock.GetUtcNow().AddMilliseconds(70);
        await Assert.ThrowsAsync<RetryException>(() =>
            new CameraRotateTask(default) { Io = replay.Io, Deadline = () => deadline }.WaitUntilRotatedTo(90, 2));
        Assert.InRange(replay.MouseInputs.Count, 1, 2);
        Assert.True(replay.Clock.GetUtcNow() - replay.Started < TimeSpan.FromMilliseconds(150));
    }
}
