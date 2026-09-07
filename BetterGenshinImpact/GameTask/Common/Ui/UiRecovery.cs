using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask.Common.Ui;

internal static class UiRecovery
{
    internal static Task<UiSnapshot> ConfirmPartyAsync(IUiDriver driver,
        Func<CancellationToken, Task<bool>> select, Func<CancellationToken, Task<bool>> apply,
        CancellationToken ct, bool deferApplyToCaller = false, ILogger? logger = null,
        TimeProvider? clock = null) =>
        UiOperation.RunAsync("party-confirm", TimeSpan.FromSeconds(20), ct, async operation =>
        {
            await UiTransition.WaitAsync(operation, UiTarget.PartyList, driver);
            var selected = await select(operation.Token);
            operation.Check();
            operation.Action(UiAction.SelectParty, selected, 1, 1);
            if (!selected) throw new InvalidOperationException("未找到或未点击队伍选择确认按钮，不能记录切队成功");
            var party = await UiTransition.WaitAsync(operation, UiTarget.Party, driver);
            // 秘境调用方拥有“开始挑战”，不能在通用切队过程中提前触发加载。
            if (deferApplyToCaller) return party;
            var applied = await apply(operation.Token);
            operation.Check();
            operation.Action(UiAction.ApplyParty, applied, 1, 1);
            if (!applied) throw new InvalidOperationException("未找到或未点击队伍出战按钮，不能记录切队成功");
            return await UiTransition.WaitAsync(operation, UiTarget.PartyOrMain, driver);
        }, logger, clock);

    internal static Task<T> TeleportAsync<T>(IUiDriver driver, Func<CancellationToken, Task<T>> teleport,
        CancellationToken ct, ILogger? logger = null, TimeProvider? clock = null,
        Action<Exception, string>? captureFailure = null) =>
        UiOperation.RunAsync("teleport", TimeSpan.FromSeconds(60), ct, async operation =>
        {
            var before = driver.Capture();
            operation.Check();
            operation.Observe(before, UiTarget.Overworld);
            if (before.InDomain || before.Revive)
                throw new InvalidOperationException("传送前仍识别到秘境或复苏界面，禁止继续打开大地图");
            if (!before.MapReady)
                await ToMainAsync(driver, operation.Token, requireOverworld: true, logger: logger, clock: clock);
            operation.Check();
            var result = await teleport(operation.Token);
            await UiTransition.WaitAsync(operation, UiTarget.Overworld, driver);
            return result;
        }, logger, clock, captureFailure);

    internal static async Task<T> RunWithRecoveryAsync<T>(Func<CancellationToken, Task<T>> attempt,
        Func<CancellationToken, Task> recover, CancellationToken ct,
        Func<Exception, bool>? canRetry = null, ILogger? logger = null,
        Action<Exception>? captureFailure = null, int maxAttempts = 3)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        for (var index = 0; index < maxAttempts; index++)
        {
            ct.ThrowIfCancellationRequested();
            UiOperation.Current?.Check();
            try { return await attempt(ct); }
            catch (Exception failure) when (failure is not OperationCanceledException and not NormalEndException
                && !TaskFailureRecoveryPolicy.IsRecoveryFailure(failure))
            {
                try
                {
                    logger?.LogDebug(failure, "UI_RETRY attempt={Attempt}/{MaxAttempts} root={RootId}",
                        index + 1, maxAttempts, UiOperation.Current?.RootId ?? "-");
                    captureFailure?.Invoke(failure);
                }
                catch { /* 诊断不能改变失败归属。 */ }
                await TaskFailureRecoveryPolicy.RecoverOrThrowAsync(failure, () => recover(ct), ct, logger);
                if (index + 1 == maxAttempts || canRetry?.Invoke(failure) == false)
                    ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }
        throw new InvalidOperationException("界面操作重试未完成");
    }

    internal static Task<UiSnapshot> ToMainAsync(IUiDriver driver, CancellationToken ct,
        bool requireOverworld = false, ILogger? logger = null, TimeProvider? clock = null,
        Action<Exception, string>? captureFailure = null)
    {
        return UiTransition.WaitAsync("return-main", requireOverworld ? UiTarget.Overworld : UiTarget.Main,
            driver, ct, TimeSpan.FromSeconds(20),
            // 退出门图标只能证明菜单存在，不能证明点击会返回HUD；使用已知的关闭动作。
            observed => observed.CanEscape ? UiAction.Escape : null,
            logger: logger, clock: clock, captureFailure: captureFailure);
    }

    internal static Task<UiSnapshot> ExitDomainAsync(IUiDriver driver, CancellationToken ct,
        ILogger? logger = null, TimeProvider? clock = null, Action<Exception, string>? captureFailure = null)
    {
        var requested = false;
        var confirmed = false;
        var domainFrames = 0;
        long requestFrame = 0;
        return UiTransition.WaitAsync("exit-domain", UiTarget.Overworld, driver, ct, TimeSpan.FromSeconds(20),
            observed =>
            {
                if (requested)
                    return !confirmed && observed.FrameId > requestFrame && observed.Prompt && observed.BlackConfirm && !observed.Revive
                        ? UiAction.ConfirmDomainExit : null;
                domainFrames = observed.Matches(UiTarget.DomainMain) ? domainFrames + 1 : 0;
                if (domainFrames >= 2) return UiAction.RequestDomainExit;
                return observed.CanEscape ? UiAction.Escape : null;
            }, logger: logger, clock: clock, captureFailure: captureFailure,
            actionCompleted: (action, applied, observed) =>
            {
                if (!applied) return;
                if (action == UiAction.RequestDomainExit) { requested = true; requestFrame = observed.FrameId; }
                if (action == UiAction.ConfirmDomainExit) confirmed = true;
            });
    }
}
