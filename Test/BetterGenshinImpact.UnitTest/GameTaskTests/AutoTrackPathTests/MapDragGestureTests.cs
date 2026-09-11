using System.Drawing;
using BetterGenshinImpact.GameTask.AutoTrackPath;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoTrackPathTests;

public class MapDragGestureTests
{
    private static readonly Rectangle Bounds = new(-1920, 0, 1920, 1080);
    private static readonly Point Start = new(-800, 600);
    private static readonly Point End = new(-1037, 416);

    [Theory]
    [InlineData(-1920, 0, 0, 0)]
    [InlineData(1919, 1079, 65535, 65535)]
    public void AbsoluteCoordinatesUseFullVirtualDesktop(int x, int y, int expectedX, int expectedY)
    {
        Assert.Equal(new Point(expectedX, expectedY), MapDragGesture.Normalize(new Point(x, y), new Rectangle(-1920, 0, 3840, 1080)));
    }

    [Fact]
    public async Task NormalDragUsesBoundedAbsolutePointsAndMinimumHoldTimes()
    {
        var pointer = new Pointer();
        var waits = new List<int>();
        var delta = await MapDragGesture.RunAsync(pointer, Bounds, Start, End, 8, 1,
            (ms, _) => { waits.Add(ms); return Task.CompletedTask; }, default);
        Assert.Equal(new Point(-237, -184), delta);
        Assert.All(pointer.Moves, point => Assert.True(Bounds.Contains(point)));
        Assert.Equal(Start, pointer.Moves[0]);
        Assert.Equal(End, pointer.Moves[^1]);
        Assert.Equal(1, pointer.Downs);
        Assert.Equal(1, pointer.Ups);
        Assert.True(waits[0] >= 50);
        Assert.All(waits.Skip(1), ms => Assert.True(ms >= 16));
    }

    [Fact]
    public async Task IncorrectInitialCursorPositionNeverPressesButton()
    {
        var pointer = new Pointer { Offset = new Point(20, 0) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(pointer));
        Assert.Equal(0, pointer.Downs);
        Assert.Equal(0, pointer.Ups);
    }

    [Fact]
    public async Task OutsideTargetNeverMovesOrPresses()
    {
        var pointer = new Pointer();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => MapDragGesture.RunAsync(pointer, Bounds, Start,
            new Point(0, 500), 8, 16, (_, _) => Task.CompletedTask, default));
        Assert.Empty(pointer.Moves);
        Assert.Equal(0, pointer.Downs);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("focus")]
    [InlineData("drift")]
    [InlineData("move")]
    [InlineData("down")]
    public async Task InterruptedDragReleasesExactlyOnce(string failure)
    {
        using var cts = new CancellationTokenSource();
        var pointer = new Pointer { FailDown = failure == "down" };
        await Assert.ThrowsAnyAsync<Exception>(() => MapDragGesture.RunAsync(pointer, Bounds, Start, End, 8, 16,
            (_, _) => {
                if (failure == "cancel") cts.Cancel();
                if (failure == "focus") pointer.Ready = false;
                if (failure == "drift") pointer.Offset = new Point(400, 0);
                if (failure == "move") pointer.FailMove = true;
                return Task.CompletedTask;
            }, cts.Token));
        Assert.Equal(1, pointer.Downs);
        Assert.Equal(1, pointer.Ups);
    }

    [Fact]
    public async Task DelayedCursorReadbackWaitsWithoutRepeatedMovement()
    {
        var pointer = new Pointer { Offset = new Point(20, 0) };
        await MapDragGesture.RunAsync(pointer, Bounds, Start, End, 8, 16,
            (_, _) => { pointer.Offset = Point.Empty; return Task.CompletedTask; }, default);
        Assert.Equal(9, pointer.Moves.Count);
        Assert.Equal(1, pointer.Downs);
        Assert.Equal(1, pointer.Ups);
    }

    [Fact]
    public async Task AlreadyCancelledNeverMovesOrPresses()
    {
        var pointer = new Pointer();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MapDragGesture.RunAsync(pointer, Bounds, Start, End,
            8, 16, (_, _) => Task.CompletedTask, new CancellationToken(true)));
        Assert.Empty(pointer.Moves);
        Assert.Equal(0, pointer.Downs);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(1920, 0)]
    [InlineData(0, 1080)]
    public void NormalizationRejectsOutOfDesktopPoints(int x, int y)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MapDragGesture.Normalize(new Point(x, y), new Rectangle(0, 0, 1920, 1080)));
    }

    private static Task<Point> Run(Pointer pointer) => MapDragGesture.RunAsync(pointer, Bounds, Start, End, 8, 16,
        (_, _) => Task.CompletedTask, default);

    private sealed class Pointer : IMapDragPointer
    {
        public readonly List<Point> Moves = [];
        public int Downs, Ups;
        public bool Ready = true, FailMove, FailDown;
        public Point Offset;
        private Point _position;
        public void Check() { if (!Ready) throw new InvalidOperationException("focus or layout changed"); }
        public Point Position => new(_position.X + Offset.X, _position.Y + Offset.Y);
        public void MoveTo(Point point) { if (FailMove) throw new InvalidOperationException("dispatch failed"); Moves.Add(point); _position = point; }
        public void Down() { Downs++; if (FailDown) throw new InvalidOperationException("partial down dispatch"); }
        public void Up() => Ups++;
    }
}
