using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.AutoPathing;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

public class PathReconnectTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TrapNativeOperationsInheritOnlyOneHundredMillisecondsOfParentBudget(bool rotate)
    {
        var replay = new PathReplay { AvailabilityAt = _ => WorldFrameKind.Reconnecting };
        var trap = new TrapEscaper(default, replay.Io, () => replay.Started.AddMilliseconds(100));
        await Assert.ThrowsAsync<RetryException>(() => rotate ? trap.RotateAndMove() : trap.MoveTo(replay.Point("walk")));
        Assert.True(replay.Clock.GetUtcNow() <= replay.Started.AddMilliseconds(101));
        Assert.All(replay.Inputs, input => Assert.Equal(KeyType.KeyUp, input.Type));
        Assert.Empty(replay.MouseInputs);
    }

    [Fact]
    public async Task ActualMoveToTrapCannotResetTheLastOneHundredMilliseconds()
    {
        var blocked = false;
        var replay = new PathReplay { MinimumWait = 1100, PositionAt = _ => new Point2f(90, 100),
            AvailabilityAt = _ => blocked ? WorldFrameKind.Reconnecting : WorldFrameKind.Playable };
        replay.BeforeCapture = frame => { if (frame == 12) replay.Clock.Advance(replay.Started.AddSeconds(239.9) - replay.Clock.GetUtcNow()); };
        replay.OnInput = (action, _) => { if (action == GIActions.Drop) { blocked = true; replay.MinimumWait = 0; } };
        await Assert.ThrowsAsync<RetryException>(() => replay.Executor.MoveTo(replay.Point("walk")));
        Assert.True(blocked);
        Assert.True(replay.Clock.GetUtcNow() <= replay.Started.AddSeconds(240.001));
        Assert.All(replay.Inputs.Where(input => input.At >= replay.Started.AddSeconds(239.9)),
            input => Assert.True(input.Type == KeyType.KeyUp || input.Action == GIActions.Drop));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GuardianSkillWaitsWhenReconnectStartsDuringDelayOrSwitch(bool duringSwitch)
    {
        var blocked = false;
        var replay = new PathReplay { AvailabilityAt = _ => blocked ? WorldFrameKind.Reconnecting : WorldFrameKind.Playable };
        replay.Executor.PartyConfig.GuardianAvatarIndex = "2";
        if (duringSwitch) replay.OnGuardianSwitch = () => blocked = true;
        else replay.AfterDelay = _ => blocked = true;
        await Assert.ThrowsAsync<RetryException>(() => replay.Executor.UseElementalSkill());
        Assert.Equal(duringSwitch ? 1 : 0, replay.GuardianCalls);
        Assert.Equal(0, replay.SkillCalls);
        Assert.All(replay.Inputs, input => Assert.Equal(KeyType.KeyUp, input.Type));
    }

    [Fact]
    public async Task PreciseApproachRechecksAfterFocusBeforeForwardPulse()
    {
        var blocked = false;
        var replay = new PathReplay { PositionAt = _ => new Point2f(104, 100),
            AvailabilityAt = _ => blocked ? WorldFrameKind.Reconnecting : WorldFrameKind.Playable };
        replay.OnCheckInput = () => { replay.Clock.Advance(TimeSpan.FromSeconds(3)); blocked = true; };
        await Assert.ThrowsAsync<RetryException>(() => replay.Executor.MoveCloseTo(replay.Point("walk")));
        Assert.DoesNotContain(replay.Inputs, input => input.Type == KeyType.KeyDown);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreciseAndTrapDoNotLocateBlockedFrames(bool trap)
    {
        var replay = new PathReplay { AvailabilityAt = _ => WorldFrameKind.Loading };
        await Assert.ThrowsAsync<RetryException>(() => trap
            ? new TrapEscaper(default, replay.Io).MoveTo(replay.Point("walk"))
            : replay.Executor.MoveCloseTo(replay.Point("walk")));
        Assert.Equal(0, replay.LocateCalls);
        Assert.Empty(replay.MouseInputs);
        Assert.DoesNotContain(replay.Inputs, input => input.Type == KeyType.KeyDown);
        Assert.All(replay.Images, frame => Assert.True(frame.SrcMat.IsDisposed));
    }

    [Fact]
    public async Task PersistentReconnectDoesNotStartForwardOrCameraInput()
    {
        var replay = new PathReplay { AvailabilityAt = _ => WorldFrameKind.Reconnecting };
        await Assert.ThrowsAsync<RetryException>(() => replay.Executor.MoveTo(replay.Point("walk")));
        Assert.DoesNotContain(replay.Inputs, input => input.Action == GIActions.MoveForward && input.Type == KeyType.KeyDown);
        Assert.Empty(replay.MouseInputs);
        Assert.Equal(0, replay.SwitchCalls);
        Assert.Equal(0, replay.LocateCalls);
        Assert.All(replay.Images, frame => Assert.True(frame.SrcMat.IsDisposed));
    }

    [Fact]
    public async Task ReconnectAndLoadingWaitForFreshPlayableFramesBeforeMoving()
    {
        var replay = new PathReplay
        {
            AvailabilityAt = frame => frame < 4 ? WorldFrameKind.Reconnecting
                : frame < 7 ? WorldFrameKind.Loading : WorldFrameKind.Playable
        };
        await replay.Executor.MoveTo(replay.Point("walk"));
        Assert.All(replay.Inputs.Where(input => input.Type == KeyType.KeyDown),
            input => Assert.True(input.At - replay.Started >= TimeSpan.FromSeconds(1)));
        Assert.All(replay.Images, frame => Assert.True(frame.SrcMat.IsDisposed));
    }

    [Fact]
    public async Task FailedInitialRotationNeverStartsForward()
    {
        var replay = new PathReplay { RotationSucceeds = false };
        await Assert.ThrowsAsync<RetryException>(() => replay.Executor.MoveTo(replay.Point("walk")));
        Assert.DoesNotContain(replay.Inputs, input => input.Type == KeyType.KeyDown);
    }

    [Fact]
    public async Task ReconnectMidMovementReleasesBeforeWaitingAndResumesOnlyWhenReady()
    {
        var replay = new PathReplay
        {
            PositionAt = frame => frame < 10 ? new Point2f(90, 100) : new Point2f(100, 100),
            AvailabilityAt = frame => frame is >= 4 and < 7 ? WorldFrameKind.Reconnecting
                : frame is >= 7 and < 9 ? WorldFrameKind.Loading : WorldFrameKind.Playable
        };
        await replay.Executor.MoveTo(replay.Point("walk"));
        Assert.Contains(replay.Inputs, input => input.Action == GIActions.MoveForward && input.Type == KeyType.KeyDown);
        Assert.Contains(replay.Inputs, input => input.Action == GIActions.MoveForward && input.Type == KeyType.KeyUp &&
            input.At - replay.Started < TimeSpan.FromSeconds(0.3));
        Assert.DoesNotContain(replay.Inputs, input => input.Type == KeyType.KeyDown &&
            input.At - replay.Started > TimeSpan.FromSeconds(0.15) && input.At - replay.Started < TimeSpan.FromSeconds(0.9));
    }

    [Fact]
    public async Task CameraWaitDoesNotSpendMouseAttemptsOnBlockedFrames()
    {
        var replay = new PathReplay { AvailabilityAt = frame => frame < 4 ? WorldFrameKind.Reconnecting
            : frame < 7 ? WorldFrameKind.Loading : WorldFrameKind.Playable };
        var rotate = new CameraRotateTask(default) { Io = replay.Io };
        Assert.True(await rotate.WaitUntilRotatedTo(90, 5));
        Assert.NotEmpty(replay.MouseInputs);
        Assert.All(replay.MouseInputs, input => Assert.True(input.At - replay.Started >= TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task InvalidDirectionAndLockedCameraFailWithoutRestartingMovement()
    {
        var replay = new PathReplay { ReadCamera = () => float.NaN };
        var rotate = new CameraRotateTask(default) { Io = replay.Io };
        Assert.False(await rotate.WaitUntilRotatedTo(90, 5));
        Assert.Empty(replay.MouseInputs);
        replay.ReadCamera = null;
        replay.LockCamera = true;
        Assert.False(await rotate.WaitUntilRotatedTo(90, 5));
        Assert.InRange(replay.MouseInputs.Count, 1, 11);
        Assert.DoesNotContain(replay.Inputs, input => input.Type == KeyType.KeyDown);
    }

    [Fact]
    public async Task CancellationDuringBlockedWaitPropagatesAndOnlyReleases()
    {
        using var ct = new CancellationTokenSource();
        var replay = new PathReplay(ct.Token) { AvailabilityAt = _ => WorldFrameKind.Loading };
        replay.AfterDelay = _ => ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replay.Executor.MoveTo(replay.Point("walk")));
        Assert.All(replay.Inputs, input => Assert.Equal(KeyType.KeyUp, input.Type));
        Assert.All(replay.Images, frame => Assert.True(frame.SrcMat.IsDisposed));
    }
}
