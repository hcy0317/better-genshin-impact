using System;
using System.Drawing;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.GameTask.Common.Ui;
using Fischless.WindowsInput;
using Vanara.PInvoke;

namespace BetterGenshinImpact.GameTask.AutoTrackPath;

internal sealed class MapDragPointer(nint window, Rectangle capture, Rectangle desktop) : IMapDragPointer
{
    // 临界拖动序列禁止通用输入包装器自动激活另一个窗口。
    private readonly InputSimulator _input = new();

    public void Check()
    {
        TaskExecutionScope.ThrowIfFailed();
        UiOperation.Current?.Check();
        var current = SystemControl.GetCaptureRect(window);
        if (TaskContext.Instance().GameHandle != window || !User32.IsWindow(window) || User32.IsIconic(window)
            || (nint)User32.GetForegroundWindow() != window
            || new Rectangle(current.X, current.Y, current.Width, current.Height) != capture
            || System.Windows.Forms.SystemInformation.VirtualScreen != desktop)
            throw new InvalidOperationException("地图拖动期间游戏焦点、窗口或桌面布局发生变化，停止输入");
    }

    public Point Position
    {
        get
        {
            if (!User32.GetCursorPos(out var point))
                throw new InvalidOperationException("无法读取地图拖动鼠标位置");
            return new Point(point.X, point.Y);
        }
    }

    public void MoveTo(Point point)
    {
        if (!capture.Contains(point)) throw new ArgumentOutOfRangeException(nameof(point));
        var normalized = MapDragGesture.Normalize(point, desktop);
        _input.Mouse.MoveMouseToPositionOnVirtualDesktop(normalized.X, normalized.Y);
    }

    public void Down() => _input.Mouse.LeftButtonDown();
    public void Up() => Simulation.DispatchWithPostMessageFallback(
        () => _input.Mouse.LeftButtonUp(), () => new PostMessageSimulator(window).LeftButtonUp());
}
