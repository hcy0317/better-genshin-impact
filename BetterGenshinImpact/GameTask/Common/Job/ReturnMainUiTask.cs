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
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

namespace BetterGenshinImpact.GameTask.Common.Job;

public class ReturnMainUiTask
{
    public string Name => "返回主界面";

    public Task Start(CancellationToken ct) => Start(ct, requireOverworld: false);

    internal async Task Start(CancellationToken ct, bool requireOverworld)
    {
        using var suspendedCombatBudget = CombatActionScope.Suspend();
        using var driver = new NativeUiDriver();
        await UiRecovery.ToMainAsync(driver, ct, requireOverworld, Logger,
            captureFailure: (error, context) => TaskFailureDiagnostics.CaptureScreenshotOnce(error, context));
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
