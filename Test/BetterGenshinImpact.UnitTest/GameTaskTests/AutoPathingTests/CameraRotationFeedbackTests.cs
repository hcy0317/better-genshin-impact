using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using Fischless.GameCapture;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

public class CameraRotationFeedbackTests
{
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
