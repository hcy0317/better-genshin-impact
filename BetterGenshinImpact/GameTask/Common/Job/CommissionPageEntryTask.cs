using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.Common.Ui;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask.Common.Job;

// 返回主界面、F1和委托页确认共用入口最初的10秒预算。
internal sealed class CommissionPageEntryTask
{
    private readonly NativeUiDriver _driver;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;

    internal CommissionPageEntryTask(NativeUiDriver driver, TimeProvider clock, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        if (!ReferenceEquals(driver.CommissionClock, clock))
            throw new ArgumentException("委托入口与设备驱动必须共用同一时钟。", nameof(clock));
        _driver = driver;
        _clock = clock;
        _logger = logger;
    }

    internal async Task EnterAsync(CancellationToken ct)
    {
        await UiOperation.RunAsync("commission-page-entry", TimeSpan.FromSeconds(10), ct, async operation =>
        {
            var main = await UiRecovery.ToMainAsync(_driver, operation.Token, logger: _logger, clock: _clock,
                budget: operation.Remaining);
            operation.Check();
            var opened = await operation.InvokeActionAsync(UiAction.OpenAdventurerHandbook,
                () => _driver.ActAsync(UiAction.OpenAdventurerHandbook, main, operation.Token), 1, 1);
            operation.Check();
            if (!opened) throw new InvalidOperationException("冒险之证输入未获准，不能认定委托页面已打开。");
            var tabSelected = false;
            UiSnapshot? previousReward = null;
            while (true)
            {
                operation.Check();
                var observed = _driver.CaptureCommission(operation.Token);
                operation.Check();
                if (!tabSelected)
                {
                    if (observed.TabVisible)
                    {
                        tabSelected = await _driver.SelectCommissionTabAsync(observed, operation.Token);
                        operation.Check();
                    }
                }
                else if (observed.RewardVisible && observed.Scene.HasUsableEvidence)
                {
                    if (previousReward != null && observed.Scene.IsAfter(previousReward) &&
                        observed.Scene.SourceStamp.CapturedTimestamp > previousReward.SourceStamp.CapturedTimestamp)
                        return true;
                    previousReward = observed.Scene;
                }
                else previousReward = null;
                await _driver.DelayAsync(250, operation.Token);
            }
        }, _logger, _clock);
    }
}
