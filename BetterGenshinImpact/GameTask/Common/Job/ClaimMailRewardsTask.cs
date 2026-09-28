using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Common.Ui;
using Microsoft.Extensions.Logging;
using Vanara.PInvoke;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.Common.Job;

/// <summary>
/// 领取邮件奖励
/// </summary>
public class ClaimMailRewardsTask
{
    // 邮件页渲染可能明显滞后于点击邮件图标（实测超过 30 秒），单次抓图会把"页面未就绪"误判成"没有奖励"。
    internal static readonly TimeSpan ClaimAllWaitTimeout = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan ClaimAllPollInterval = TimeSpan.FromMilliseconds(500);
    // 轮询保留给收尾 return-main 的最小预算，避免领到了奖励却把失败归因到领取步骤。
    private static readonly TimeSpan ReturnMainReserve = TimeSpan.FromSeconds(8);

    // 退出失败必须交给持锁的一条龙恢复边界，不能在这里吞错并继续下一项。
    public Task Start(CancellationToken ct) => DoOnce(ct);

    public async Task DoOnce(CancellationToken ct)
    {
        using var driver = new NativeUiDriver();
        await RunAsync(driver, async token =>
        {
            await Delay(200, token);
            TaskContext.Instance().PostMessageSimulator.SimulateAction(GIActions.OpenPaimonMenu);
        }, token =>
        {
            token.ThrowIfCancellationRequested();
            using var ra = CaptureToRectArea();
            if (!NativeUiDriver.Read(ra).Matches(UiTarget.Menu))
                throw new InvalidOperationException("邮件入口所在菜单未确认，不能点击邮件图标");
            using var mailIcon = ra.Find(ElementRecognition.Get("EscMailReward", ra));
            if (mailIcon.IsExist())
            {
                token.ThrowIfCancellationRequested();
                UiOperation.Current?.Check();
                mailIcon.Click();
                return Task.FromResult(true);
            }
            Logger.LogInformation("邮件：{Text}", "没有邮件奖励");
            return Task.FromResult(false);
        }, async token =>
        {
            // 点击邮件图标后的输入延时：游戏内键鼠响应与截图图源都有延迟。
            await Delay(1000, token);
            var claimedAll = await WaitForClaimAllAsync(token, ClaimAllOnceAsync);
            UiOperation.Current?.Check();
            if (claimedAll)
            {
                UiOperation.Current?.Action(UiAction.ClaimMail, true, 1, 1);
                Logger.LogInformation("邮件：{Text}", "全部领取");
                await Delay(200, token);
                // 只关闭本次明确领取后产生的奖励遮罩，后续邮件页/派蒙菜单由状态恢复处理。
                TaskContext.Instance().PostMessageSimulator.KeyPress(User32.VK.VK_ESCAPE);
                return;
            }

            UiOperation.Current?.Action(UiAction.ClaimMail, false, 1, 1);
            // 未识别到"全部领取"既可能是没有可领取奖励，也可能是邮件页尚未渲染；这里只陈述观察到的证据，
            // 由收尾的返回主界面校验决定本次任务是否失败（无邮件页识别锚点，二者无法在离线侧区分）。
            Logger.LogWarning("邮件：{Text}",
                $"{ClaimAllWaitTimeout.TotalSeconds:0} 秒内未识别到“全部领取”，无法确认是否领取，交由返回主界面校验");

            async Task<bool> ClaimAllOnceAsync(CancellationToken inner)
            {
                inner.ThrowIfCancellationRequested();
                UiOperation.Current?.Check();
                using var claimArea = CaptureToRectArea();
                using var claimAll = claimArea.Find(ElementRecognition.Get("Collect", claimArea));
                if (!claimAll.IsExist()) return false;
                inner.ThrowIfCancellationRequested();
                UiOperation.Current?.Check();
                claimAll.Click();
                return true;
            }
        }, ct, Logger, captureFailure: (error, context) => TaskFailureDiagnostics.CaptureScreenshotOnce(error, context));
        Logger.LogInformation("邮件处理完成，已确认返回主界面");
    }

    /// <summary>
    /// 轮询等待"全部领取"可用：命中并点击返回 true，超时返回 false。
    /// 邮件页渲染滞后时单次抓图会把"页面未就绪"判成"没有奖励"，因此按间隔重试到超时。
    /// </summary>
    internal static async Task<bool> WaitForClaimAllAsync(CancellationToken ct,
        Func<CancellationToken, Task<bool>> tryClaimOnce,
        Func<int, CancellationToken, Task>? delay = null, TimeProvider? clock = null,
        TimeSpan? timeout = null, TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(tryClaimOnce);
        var limit = timeout ?? ClaimAllWaitTimeout;
        if (timeout is null && UiOperation.Current is { } budget)
        {
            // 轮询不得吃掉收尾"返回主界面"的预算；剩余不足时只探测一次（首次探测在超时判定之前）后交给状态恢复。
            var affordable = budget.Remaining - ReturnMainReserve;
            if (affordable < limit) limit = affordable;
        }
        var step = interval ?? ClaimAllPollInterval;
        var pause = delay ?? TaskControl.Delay;
        var now = clock ?? TimeProvider.System;
        var started = now.GetUtcNow();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            UiOperation.Current?.Check();
            if (await tryClaimOnce(ct)) return true;
            if (now.GetUtcNow() - started >= limit) return false;
            await pause((int)step.TotalMilliseconds, ct);
        }
    }

    /// <summary>截图、按键与领取为外部游戏边界；领取动作不因退出失败而重放。</summary>
    internal static Task<UiSnapshot> RunAsync(IUiDriver driver,
        Func<CancellationToken, Task> openMenu, Func<CancellationToken, Task<bool>> openMail,
        Func<CancellationToken, Task> collect, CancellationToken ct,
        ILogger? logger = null, TimeProvider? clock = null, Action<Exception, string>? captureFailure = null) =>
        UiOperation.RunAsync("claim-mail", TimeSpan.FromSeconds(60), ct, async operation =>
        {
            await UiRecovery.ToMainAsync(driver, operation.Token);
            await openMenu(operation.Token);
            operation.Check();
            operation.Action(UiAction.OpenMenu, true, 1, 1);
            await UiTransition.WaitAsync(operation, UiTarget.Menu, driver);
            var openedMail = await openMail(operation.Token);
            operation.Check();
            operation.Action(UiAction.OpenMail, openedMail, 1, 1);
            if (openedMail) await collect(operation.Token);
            var result = await UiRecovery.ToMainAsync(driver, operation.Token);
            operation.Observe(result, UiTarget.Main, "postcondition");
            return result;
        }, logger, clock, captureFailure);
}
