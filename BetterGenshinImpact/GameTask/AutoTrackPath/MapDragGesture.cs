using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.GameTask.AutoTrackPath;

internal interface IMapDragPointer
{
    void Check();
    Point Position { get; }
    void MoveTo(Point point);
    void Down();
    void Up();
}

/// <summary>地图拖动专用：只发送有边界的绝对位置，不使用受鼠标加速度影响的相对输入。</summary>
internal static class MapDragGesture
{
    internal static Point Normalize(Point point, Rectangle desktop)
    {
        if (desktop.Width < 2 || desktop.Height < 2 || !desktop.Contains(point))
            throw new ArgumentOutOfRangeException(nameof(point), "地图坐标不在当前虚拟桌面内");
        return new Point(
            (int)Math.Round(((double)point.X - desktop.Left) * 65535 / (desktop.Width - 1)),
            (int)Math.Round(((double)point.Y - desktop.Top) * 65535 / (desktop.Height - 1)));
    }

    internal static async Task<Point> RunAsync(IMapDragPointer pointer, Rectangle bounds, Point start, Point end,
        int steps, int stepDelay, Func<int, CancellationToken, Task> delay, CancellationToken ct)
    {
        if (!bounds.Contains(start) || !bounds.Contains(end) || steps is < 1 or > 60)
            throw new ArgumentOutOfRangeException(nameof(end), "地图拖动起止点必须在捕获区域内");
        void Check()
        {
            ct.ThrowIfCancellationRequested();
            pointer.Check();
        }
        void Verify(Point expected)
        {
            var actual = pointer.Position;
            if (!bounds.Contains(actual) || Math.Abs((long)actual.X - expected.X) > 3 || Math.Abs((long)actual.Y - expected.Y) > 3)
                throw new InvalidOperationException($"地图鼠标落点异常：期望={expected}，实际={actual}，停止拖动");
        }
        async Task Move(Point point)
        {
            Check();
            pointer.MoveTo(point);
            // SendInput后光标读回可能晚一帧；只观察等待，不重复注入位移。
            for (var attempt = 0; attempt < 3; attempt++)
            {
                Check();
                var actual = pointer.Position;
                if (bounds.Contains(actual) && Math.Abs((long)actual.X - point.X) <= 3 && Math.Abs((long)actual.Y - point.Y) <= 3)
                    return;
                await delay(16, ct);
            }
            Check();
            Verify(point);
        }

        await Move(start);
        Check();
        Verify(start);
        var initial = pointer.Position;
        // Down也置于finally保护内：部分发送后抛错仍需要松键。
        try
        {
            pointer.Down();
            await delay(50, ct);
            var previous = start;
            for (var i = 1; i <= steps; i++)
            {
                await delay(Math.Max(16, stepDelay), ct);
                Check();
                Verify(previous);
                var next = new Point(
                    (int)Math.Round(start.X + ((double)end.X - start.X) * i / steps),
                    (int)Math.Round(start.Y + ((double)end.Y - start.Y) * i / steps));
                await Move(next);
                previous = next;
            }
            await delay(50, ct);
            Check();
            Verify(end);
            var final = pointer.Position;
            return new Point(final.X - initial.X, final.Y - initial.Y);
        }
        finally
        {
            // 不调用会抢焦点/等待任务恢复的包装器，先释放再交给上层恢复。
            pointer.Up();
        }
    }
}
