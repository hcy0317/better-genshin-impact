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
            if (key is User32.VK.VK_LBUTTON or User32.VK.VK_RBUTTON or User32.VK.VK_MBUTTON)
            {
                continue;
            }

            // 检查键是否被按下
            if (IsKeyDown(key)) // 强制转换 VK 枚举为 int
            {
                TaskControl.Logger.LogDebug($"解除{key}的按下状态.");
                DispatchWithPostMessageFallback(
                    () => SendInput.Keyboard.KeyUp(key),
                    () => TaskContext.Instance().PostMessageSimulator.KeyUpBackground(key));
            }
        }

        ReleaseMouseButtonIfDown(
            User32.VK.VK_LBUTTON,
            () => SendInput.Mouse.LeftButtonUp(),
            () => TaskContext.Instance().PostMessageSimulator.LeftButtonUp());
        ReleaseMouseButtonIfDown(
            User32.VK.VK_RBUTTON,
            () => SendInput.Mouse.RightButtonUp(),
            () => TaskContext.Instance().PostMessageSimulator.RightButtonUp());
        ReleaseMouseButtonIfDown(
            User32.VK.VK_MBUTTON,
            () => SendInput.Mouse.MiddleButtonUp(),
            () => TaskContext.Instance().PostMessageSimulator.MiddleButtonUp());
    }

    private static void ReleaseMouseButtonIfDown(
        User32.VK key,
        Action sendInputAction,
        Action postMessageAction)
    {
        if (IsKeyDown(key))
        {
            DispatchWithPostMessageFallback(sendInputAction, postMessageAction);
        }
    }

    internal static bool DispatchWithPostMessageFallback(
        Action sendInputAction,
        Action postMessageAction)
    {
        ArgumentNullException.ThrowIfNull(sendInputAction);
        ArgumentNullException.ThrowIfNull(postMessageAction);

        try
        {
            sendInputAction();
            return false;
        }
        catch (InputDispatchException)
        {
            postMessageAction();
            return true;
        }
    }

    internal static bool ShouldReleaseInput(short state)
    {
        return (state & 0x8000) != 0;
    }

    public static bool IsKeyDown(User32.VK key)
    {
        // 获取按键状态
        var state = User32.GetAsyncKeyState((int)key);

        // 检查高位是否为 1（表示按键被按下）
        return ShouldReleaseInput(state);
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
