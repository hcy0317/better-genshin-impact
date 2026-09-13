using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask.Common.Ui;

internal interface IUiDriver
{
    UiSnapshot Capture();
    Task DelayAsync(int milliseconds, CancellationToken ct);
    Task<bool> ActAsync(UiAction action, UiSnapshot observed, CancellationToken ct);
    void MarkInputCompleted(UiSnapshot before) { }
}

internal static class UiTransition
{
    internal static Task<UiSnapshot> WaitAsync(string name, UiTarget target, IUiDriver driver,
        CancellationToken ct, TimeSpan timeout, Func<UiSnapshot, UiAction?>? chooseAction = null,
        int maxActions = 8, ILogger? logger = null, TimeProvider? clock = null,
        Action<Exception, string>? captureFailure = null,
        Action<UiAction, bool, UiSnapshot>? actionCompleted = null) =>
        UiOperation.RunAsync(name, timeout, ct,
            operation => WaitAsync(operation, target, driver, chooseAction, maxActions, actionCompleted), logger, clock, captureFailure);

    internal static async Task<UiSnapshot> WaitAsync(UiOperation operation, UiTarget target, IUiDriver driver,
        Func<UiSnapshot, UiAction?>? chooseAction = null, int maxActions = 8,
        Action<UiAction, bool, UiSnapshot>? actionCompleted = null)
    {
        UiSnapshot? last = null;
        var confirmed = 0;
        int? confirmedSignature = null;
        var attempts = 0;
        while (true)
        {
            operation.Check();
            var observed = driver.Capture();
            operation.Check();
            operation.Observe(observed, target);
            if (last is { SourceBound: true } && observed.SourceBound &&
                last.SourceStamp.SessionId != observed.SourceStamp.SessionId)
            {
                last = null;
                confirmed = 0;
                confirmedSignature = null;
            }
            if (!observed.HasUsableEvidence)
            {
                confirmed = 0;
                confirmedSignature = null;
            }
            else if (last == null || observed.IsAfter(last))
            {
                last = observed;
                if (observed.Matches(target))
                {
                    confirmed = confirmedSignature == observed.Signature ? confirmed + 1 : 1;
                    confirmedSignature = observed.Signature;
                    if (confirmed >= 2) return observed;
                }
                else
                {
                    confirmed = 0;
                    confirmedSignature = null;
                    if (attempts < maxActions && chooseAction?.Invoke(observed) is { } action)
                    {
                        operation.Check();
                        var applied = await driver.ActAsync(action, observed, operation.Token);
                        operation.Check();
                        operation.Action(action, applied, ++attempts, maxActions);
                        actionCompleted?.Invoke(action, applied, observed);
                    }
                }
            }
            await driver.DelayAsync(250, operation.Token);
        }
    }
}
