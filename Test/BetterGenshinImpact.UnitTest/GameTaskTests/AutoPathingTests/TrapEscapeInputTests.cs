using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.Common.BgiVision;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

public class TrapEscapeInputTests
{
    [Theory]
    [InlineData(MotionStatus.Climb)]
    [InlineData(MotionStatus.Fly)]
    public async Task CoarseEscapeDoesNotJumpWhileAttachedOrAlreadyFlying(MotionStatus motion)
    {
        var replay = new PathReplay { EmitReceipts = true };
        replay.MotionAt = _ => motion;
        var escape = new TrapEscaper(default, replay.Io, () => replay.Started.AddSeconds(25));
        await escape.RotateAndMove();
        Assert.DoesNotContain(replay.Inputs, input => input.Action == GIActions.Jump && input.Type is KeyType.KeyDown or KeyType.KeyPress);
        Assert.Contains(replay.Inputs, input => input.Type == KeyType.KeyDown &&
            input.Action is GIActions.MoveBackward or GIActions.MoveLeft or GIActions.MoveRight);
        Assert.All(replay.Images, frame => Assert.True(frame.SrcMat.IsDisposed));
    }

    [Fact]
    public async Task CoarseEscapeHoldsDetachAndJumpAndChecksPoseAfterRelease()
    {
        var replay = new PathReplay { EmitReceipts = true };
        var detached = false;
        var forwardHeld = true;
        var releaseSettledBeforeDetach = false;
        DateTimeOffset? forwardReleasedAt = null;
        DateTimeOffset? down = null;
        replay.MotionAt = _ => detached ? MotionStatus.Normal : MotionStatus.Climb;
        replay.OnInput = (action, type) =>
        {
            if (action == GIActions.MoveForward)
            {
                if (type == KeyType.KeyUp && forwardHeld)
                {
                    forwardReleasedAt = replay.Clock.GetUtcNow();
                    forwardHeld = false;
                }
                else if (type == KeyType.KeyDown) forwardHeld = true;
            }
            if (action != GIActions.Drop) return;
            if (type == KeyType.KeyDown)
            {
                down = replay.Clock.GetUtcNow();
                releaseSettledBeforeDetach = forwardReleasedAt is { } released &&
                    replay.Clock.GetUtcNow() - released >= TimeSpan.FromMilliseconds(60);
            }
            if (type == KeyType.KeyUp && down is { } started && releaseSettledBeforeDetach &&
                replay.Clock.GetUtcNow() - started >= TimeSpan.FromMilliseconds(60)) detached = true;
        };
        var escape = new TrapEscaper(default, replay.Io, () => replay.Started.AddSeconds(25));
        await escape.RotateAndMove();
        Assert.True(detached, "The game must observe released forward input before the held detach pulse.");
        Assert.DoesNotContain(replay.Inputs, input => (input.Action == GIActions.Drop || input.Action == GIActions.Jump) && input.Type == KeyType.KeyPress);
        Assert.DoesNotContain(replay.Inputs, input => input.Action == GIActions.NormalAttack);
        foreach (var action in new[] { GIActions.Drop, GIActions.Jump })
        {
            var first = replay.Inputs.First(input => input.Action == action && input.Type == KeyType.KeyDown);
            var released = replay.Inputs.First(input => input.Action == action && input.Type == KeyType.KeyUp && input.At >= first.At);
            Assert.True(released.At - first.At >= TimeSpan.FromMilliseconds(60));
        }
        Assert.All(replay.Images, frame => Assert.True(frame.SrcMat.IsDisposed));
    }

    [Fact]
    public async Task CancellationWhileForwardReleaseSettlesDoesNotStartDetach()
    {
        using var cancellation = new CancellationTokenSource();
        var replay = new PathReplay(cancellation.Token) { EmitReceipts = true };
        replay.AfterDelay = _ => cancellation.Cancel();
        var escape = new TrapEscaper(cancellation.Token, replay.Io, () => replay.Started.AddSeconds(25));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => escape.RotateAndMove());
        Assert.Contains(replay.Inputs, input => input.Action == GIActions.MoveForward && input.Type == KeyType.KeyUp);
        Assert.DoesNotContain(replay.Inputs, input => input.Action == GIActions.Drop || input.Action == GIActions.Jump);
        Assert.All(replay.Images, frame => Assert.True(frame.SrcMat.IsDisposed));
    }

    [Fact]
    public async Task OriginalParentDeadlineCanEndBeforeDetachIsSent()
    {
        var replay = new PathReplay { EmitReceipts = true };
        var escape = new TrapEscaper(default, replay.Io, () => replay.Started.AddMilliseconds(30));
        await Assert.ThrowsAsync<BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception.RetryException>(() => escape.RotateAndMove());
        Assert.DoesNotContain(replay.Inputs, input => input.Action == GIActions.Drop || input.Action == GIActions.Jump);
        Assert.All(replay.Images, frame => Assert.True(frame.SrcMat.IsDisposed));
    }

    [Fact]
    public async Task CancellationDuringDetachReleasesTheKeyAndDoesNotStartMovement()
    {
        using var cancellation = new CancellationTokenSource();
        var replay = new PathReplay(cancellation.Token) { EmitReceipts = true };
        var detaching = false;
        replay.OnInput = (action, type) => { if (action == GIActions.Drop && type == KeyType.KeyDown) detaching = true; };
        replay.AfterDelay = _ => { if (detaching) cancellation.Cancel(); };
        var escape = new TrapEscaper(cancellation.Token, replay.Io, () => replay.Started.AddSeconds(25));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => escape.RotateAndMove());
        Assert.Contains(replay.Inputs, input => input.Action == GIActions.Drop && input.Type == KeyType.KeyUp);
        Assert.DoesNotContain(replay.Inputs, input => input.Action == GIActions.MoveBackward && input.Type == KeyType.KeyDown);
    }
}
