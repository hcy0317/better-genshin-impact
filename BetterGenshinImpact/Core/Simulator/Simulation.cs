using Fischless.WindowsInput;
using System;
using System.Threading;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.Common;
using Microsoft.Extensions.Logging;
using Vanara.PInvoke;

namespace BetterGenshinImpact.Core.Simulator;

public class Simulation
{
    private const long RemoteSessionActivationIntervalMilliseconds = 500;
    private const int RemoteSessionActivationSettleMilliseconds = 100;
    private static long _lastRemoteSessionActivationTick;

    public static InputSimulator SendInput { get; } = new(PrepareGameWindowForInput);

    public static MouseEventSimulator MouseEvent { get; } = new();

    public static PostMessageSimulator PostMessage(IntPtr hWnd)
    {
        return new PostMessageSimulator(hWnd);
    }

    public static void ReleaseAllKey()
    {
        foreach (User32.VK key in Enum.GetValues(typeof(User32.VK)))
        {
            // 检查键是否被按下
            if (IsKeyDown(key)) // 强制转换 VK 枚举为 int
            {
                TaskControl.Logger.LogDebug($"解除{key}的按下状态.");
                SendInput.Keyboard.KeyUp(key);
            }
        }
		SendInput.Mouse.LeftButtonUp();
        SendInput.Mouse.RightButtonUp();
        SendInput.Mouse.MiddleButtonUp();
    }

    public static bool IsKeyDown(User32.VK key)
    {
        // 获取按键状态
        var state = User32.GetAsyncKeyState((int)key);

        // 检查高位是否为 1（表示按键被按下）
        return (state & 0x8000) != 0;
    }

    private static void PrepareGameWindowForInput()
    {
        if (!System.Windows.Forms.SystemInformation.TerminalServerSession ||
            !TaskContext.Instance().IsInitialized)
        {
            return;
        }

        var now = Environment.TickCount64;
        var lastActivation = Volatile.Read(ref _lastRemoteSessionActivationTick);
        if (!RemoteSessionInputPolicy.ShouldActivateBeforeInput(
                isTerminalServerSession: true,
                isTaskContextInitialized: true,
                isGameWindowActive: SystemControl.IsGenshinImpactActive(),
                now,
                lastActivation,
                RemoteSessionActivationIntervalMilliseconds))
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastRemoteSessionActivationTick, now, lastActivation) != lastActivation)
        {
            return;
        }

        SystemControl.ActivateWindow();
        Thread.Sleep(RemoteSessionActivationSettleMilliseconds);
    }
}
