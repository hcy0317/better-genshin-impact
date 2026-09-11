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
            await Delay(1000, token);
            using var claimArea = CaptureToRectArea();
            using var claimAll = claimArea.Find(ElementRecognition.Get("Collect", claimArea));
            UiOperation.Current?.Check();
            if (claimAll.IsExist())
            {
                claimAll.Click();
                UiOperation.Current?.Action(UiAction.ClaimMail, true, 1, 1);
                Logger.LogInformation("邮件：{Text}", "全部领取");
                await Delay(200, token);
                // 只关闭本次明确领取后产生的奖励遮罩，后续邮件页/派蒙菜单由状态恢复处理。
                TaskContext.Instance().PostMessageSimulator.KeyPress(User32.VK.VK_ESCAPE);
            }
            else UiOperation.Current?.Action(UiAction.ClaimMail, false, 1, 1);
        }, ct, Logger, captureFailure: (error, context) => TaskFailureDiagnostics.CaptureScreenshotOnce(error, context));
        Logger.LogInformation("邮件处理完成，已确认返回主界面");
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
