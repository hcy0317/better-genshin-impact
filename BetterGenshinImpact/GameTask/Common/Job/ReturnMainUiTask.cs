using System;
using System.Diagnostics;
using BetterGenshinImpact.Core.Input;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.BgiVision;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using Microsoft.Extensions.Logging;
using Vanara.PInvoke;
using static BetterGenshinImpact.GameTask.Common.TaskControl;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.AutoTrackPath;

namespace BetterGenshinImpact.GameTask.Common.Job;

public class ReturnMainUiTask
{
    public string Name => "返回主界面";

    public Task Start(CancellationToken ct) => Start(ct, requireOverworld: false);

    internal async Task RecoverForNextScript(CancellationToken ct)
    {
        using var suspendedCombatBudget = CombatActionScope.Suspend();
        using var driver = new NativeUiDriver(inspectWorld: true);
        try { await UiHandoffRecovery.RecoverAsync(driver, token => new TpTask(token).TpToStatueOfTheSeven(), ct); }
        catch (Exception error) when (error is NetworkInterruptionException or TimeoutException || TaskFailureRecoveryPolicy.IsNoProgressFailure(error))
        {
            if (await WaitForNetworkAsync(error, ct))
            {
                TaskExecutionScope.EnsureNetworkReplaySafe(error);
                throw new NetworkTaskRetryException(error);
            }
            throw;
        }
    }

    internal async Task Start(CancellationToken ct, bool requireOverworld)
    {
        using var suspendedCombatBudget = CombatActionScope.Suspend();
        using var driver = new NativeUiDriver();
        try
        {
            await UiRecovery.ToMainAsync(driver, ct, requireOverworld, Logger,
                captureFailure: (error, context) => TaskFailureDiagnostics.CaptureScreenshotOnce(error, context));
        }
        catch (Exception error) when (error is NetworkInterruptionException or TimeoutException || TaskFailureRecoveryPolicy.IsNoProgressFailure(error))
        {
            if (await WaitForNetworkAsync(error, ct))
            {
                TaskExecutionScope.EnsureNetworkReplaySafe(error);
                throw new NetworkTaskRetryException(error);
            }
            throw;
        }
    }

    private static async Task<bool> WaitForNetworkAsync(Exception failure, CancellationToken ct)
    {
        // The failed UI operation keeps its original deadline. The separate passive wait
        // owns no inputs, and uses the actual user token rather than that expired deadline.
        var userToken = UiOperation.Current?.CancellationForRecovery(ct) ?? ct;
        using var recognition = new BetterGenshinImpact.Core.Recognition.RecognitionExecutionScope(userToken);
        var request = "network-recovery:" + Guid.NewGuid().ToString("N");
        var captured = false;
        var readyCandidateCaptured = false;
        UiSnapshot Capture()
        {
            using var image = CaptureToRectArea();
            var wait = WorldFrameAvailability.ReadConnectionWait(image, () => BetterGenshinImpact.Core.Recognition.OCR.OcrFactory.Paddle);
            if (wait != null)
            {
                if (!captured)
                {
                    captured = true;
                    try { DiagnosticEvidenceScope.Current?.RequestWindowFromFrame(request, "network-wait", image, wait); }
                    catch { }
                }
                return new UiSnapshot(image.FrameStamp.Sequence) { NetworkWaitReason = wait }
                    .WithSource(image.FrameStamp, TimeProvider.System, UiSnapshot.RecoveryMaximumAge);
            }
            // Read anchors only; no focus, Escape, retry input or extra full-frame OCR.
            var observed = new UiSnapshot(image.FrameStamp.Sequence)
            {
                MainHud = Bv.IsInMainUi(image), BigMap = Bv.IsInBigMapUi(image)
            }.WithSource(image.FrameStamp, TimeProvider.System, UiSnapshot.RecoveryMaximumAge);
            if (captured && !readyCandidateCaptured && (observed.MainReady || observed.MapReady))
            {
                readyCandidateCaptured = true;
                try { DiagnosticEvidenceScope.Current?.RequestWindowFromFrame(request, "network-ready-candidate", image,
                    "等待画面消失后的首张候选原帧，尚需同源稳定确认；不代表任务完成"); }
                catch { }
            }
            return observed;
        }
        return await NetworkRecovery.WaitAsync(failure is NetworkInterruptionException { Reason: "reconnect-panel" or "network-interface-offline" },
            Capture, InputHub.ReleaseAll, System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable,
            (ms, token) => Task.Delay(ms, token), userToken,
            trace: (state, observed) => Logger.LogInformation("NETWORK_RECOVERY request={Request} state={State} source={Source} reason={Reason}",
                request, state, observed?.SourceStamp, observed?.NetworkWaitReason ?? "unknown"));
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
