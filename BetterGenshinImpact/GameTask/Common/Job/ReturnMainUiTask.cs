using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.BgiVision;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using Microsoft.Extensions.Logging;
using Vanara.PInvoke;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.Common.Job;

public class ReturnMainUiTask
{
    public string Name => "返回主界面";

    public async Task Start(CancellationToken ct)
    {
        var isInMainUi = CaptureScope.Use(CaptureToRectArea(), Bv.IsInMainUi);
        if (isInMainUi)
        {
            return;
        }

        for (var i = 0; i < 8; i++)
        {
            Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_ESCAPE);
            await Delay(900, ct);

            var region = CaptureToRectArea();

            try
            {
                var exitDoor = region.Find(ElementRecognition.Get("BtnExitDoor", region));
                if (exitDoor.IsExist())
                {
                    exitDoor.Click();
                    await Delay(5000, ct);
                    region.Dispose();
                    region = CaptureToRectArea();
                }

                if (Bv.IsInMainUi(region))
                {
                    return;
                }
            }
            finally
            {
                region.Dispose();
            }
        }
        await Delay(500, ct);
        Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_RETURN);
        await Delay(500, ct);
        Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_ESCAPE);
        await Delay(900, ct);
        var recovered = CaptureScope.Use(CaptureToRectArea(), Bv.IsInMainUi);
        if (recovered)
        {
            return;
        }

        Logger.LogInformation("主动退出界面后仍未确认主界面，进入 10 秒被动加载恢复窗口");
        var recoveryStopwatch = Stopwatch.StartNew();
        var recoveryPolicy = new ReturnMainUiPassiveRecoveryPolicy(
            timeout: TimeSpan.FromSeconds(10),
            heartbeatInterval: TimeSpan.FromSeconds(5));
        while (!recoveryPolicy.IsTimedOut(recoveryStopwatch.Elapsed))
        {
            await Delay(500, ct);
            recovered = CaptureScope.Use(CaptureToRectArea(), Bv.IsInMainUi);
            if (recovered)
            {
                Logger.LogInformation(
                    "被动等待游戏加载后已恢复主界面，额外等待 {ElapsedSeconds:F1} 秒",
                    recoveryStopwatch.Elapsed.TotalSeconds);
                return;
            }

            if (recoveryPolicy.ShouldLogHeartbeat(recoveryStopwatch.Elapsed))
            {
                Logger.LogInformation(
                    "仍在等待主界面恢复：额外等待 {ElapsedSeconds:F1}/10.0 秒",
                    recoveryStopwatch.Elapsed.TotalSeconds);
            }
        }

        ReturnMainUiRecoveryGuard.ThrowIfNotRecovered(recovered, 8);
    }
}

internal static class ReturnMainUiRecoveryGuard
{
    internal static void ThrowIfNotRecovered(bool isInMainUi, int escapeAttempts)
    {
        if (!isInMainUi)
        {
            throw new InvalidOperationException($"尝试返回主界面 {escapeAttempts} 次后仍未识别到主界面。");
        }
    }
}
