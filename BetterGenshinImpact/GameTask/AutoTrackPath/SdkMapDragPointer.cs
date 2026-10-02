using System;
using System.Drawing;
using BetterGenshinImpact.Core.Input;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Runtime;
using BetterGenshinImpact.Helpers;
using Fischless.WindowsInput;

namespace BetterGenshinImpact.GameTask.AutoTrackPath;

/// <summary>Web地图使用SDK虚拟指针；确认的是SDK调用，不冒充操作系统光标或地图移动成功。</summary>
internal sealed class SdkMapDragPointer(GameRuntime runtime, Rectangle bounds) : IMapDragPointer
{
    private readonly IInputChannel _channel = runtime.Input.Foreground;
    private Point _position;

    public void Check()
    {
        TaskExecutionScope.ThrowIfFailed();
        UiOperation.Current?.Check();
        var current = runtime.Window.Viewport.ScreenRect;
        if (!ReferenceEquals(TaskContext.Instance().Runtime, runtime) || !runtime.Window.IsAlive || runtime.Window.IsMinimized ||
            new Rectangle(current.X, current.Y, current.Width, current.Height) != bounds)
            throw new InvalidOperationException("SDK地图拖动期间运行环境或视口发生变化");
    }

    public Point Position => _position;

    public void MoveTo(Point point)
    {
        if (!bounds.Contains(point)) throw new ArgumentOutOfRangeException(nameof(point));
        using var capture = new InputDispatchCapture(Check);
        _channel.Mouse.MoveMouseTo(point.X * 65535d / PrimaryScreen.WorkingArea.Width,
            point.Y * 65535d / PrimaryScreen.WorkingArea.Height);
        if (!capture.HasCompleteReceipt) throw new InvalidOperationException("SDK虚拟指针定位未确认");
        _position = point;
    }

    public void Down()
    {
        using var capture = new InputDispatchCapture(Check);
        _channel.Mouse.LeftButtonDown();
        if (!capture.HasCompleteReceipt) throw new InvalidOperationException("SDK地图按下未确认");
    }

    public void Up()
    {
        // 期限/场景变化后仍允许向原通道发出配对释放，不重新激活或切换输入后端。
        using var capture = new InputDispatchCapture();
        _channel.Mouse.LeftButtonUp();
        if (!capture.HasCompleteReceipt) throw new InvalidOperationException("SDK地图松开未确认");
    }
}
