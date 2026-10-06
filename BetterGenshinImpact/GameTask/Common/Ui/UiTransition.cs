using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using BetterGenshinImpact.GameTask.Common.Exceptions;

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
    internal static Task<UiSnapshot> EnterPartyAsync(IUiDriver driver, CancellationToken ct,
        ILogger? logger = null, TimeProvider? clock = null) =>
        UiOperation.RunAsync("party-entry", TimeSpan.FromSeconds(8.4), ct,
            operation => WaitAsync(operation, UiTarget.Party, driver,
                observed => observed.PartyEntryReadiness().CanProbe ? UiAction.OpenParty : null,
                maxActions: 1, validateObservation: observed =>
                {
                    var readiness = observed.PartyEntryReadiness();
                    if (readiness.Kind is UiReadinessKind.TemporarilyUnavailable or UiReadinessKind.Terminal)
                        throw new PartySetupFailedException($"当前不可进入队伍配置：{readiness.Reason}");
                }), logger, clock);

    internal static Task<UiSnapshot> WaitAsync(string name, UiTarget target, IUiDriver driver,
        CancellationToken ct, TimeSpan timeout, Func<UiSnapshot, UiAction?>? chooseAction = null,
        int maxActions = 8, ILogger? logger = null, TimeProvider? clock = null,
        Action<Exception, string>? captureFailure = null,
        Action<UiAction, bool, UiSnapshot>? actionCompleted = null,
        IReadOnlyDictionary<string, string>? evidenceContext = null) =>
        UiOperation.RunAsync(name, timeout, ct,
            operation => WaitAsync(operation, target, driver, chooseAction, maxActions, actionCompleted), logger, clock, captureFailure, evidenceContext);

    internal static async Task<UiSnapshot> WaitAsync(UiOperation operation, UiTarget target, IUiDriver driver,
        Func<UiSnapshot, UiAction?>? chooseAction = null, int maxActions = 8,
        Action<UiAction, bool, UiSnapshot>? actionCompleted = null,
        Action<UiSnapshot>? validateObservation = null)
    {
        UiSnapshot? last = null;
        var confirmed = 0;
        int? confirmedSignature = null;
        var attempts = 0;
        var admissionChecks = 0;
        UiAction? previousAction = null;
        int? actionSignature = null;
        var unchangedActions = 0;
        while (true)
        {
            operation.Check();
            var observed = driver.Capture();
            operation.Check();
            operation.Observe(observed, target);
            validateObservation?.Invoke(observed);
            if (last is { SourceBound: true } && observed.SourceBound &&
                last.SourceStamp.SessionId != observed.SourceStamp.SessionId)
            {
                last = null;
                confirmed = 0;
                confirmedSignature = null;
                previousAction = null;
                actionSignature = null;
                unchangedActions = 0;
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
                    if (previousAction is UiAction.ReviveParty or UiAction.Escape or UiAction.EscapeProbe or UiAction.CloseParty &&
                        actionSignature == observed.Signature && unchangedActions >= 2)
                    {
                        var error = new TimeoutException($"界面转换超时：{operation.Name}，期望={target}，实际={observed.Describe()}；恢复无进展：{previousAction} 已提交两次，页面仍未变化");
                        error.Data["UI_NO_PROGRESS"] = true;
                        throw error;
                    }
                    if (attempts < maxActions && admissionChecks < Math.Max(8, maxActions) &&
                        chooseAction?.Invoke(observed) is { } action)
                    {
                        operation.Check();
                        admissionChecks++;
                        var applied = await operation.InvokeActionAsync(action,
                            () => driver.ActAsync(action, observed, operation.Token), attempts + 1, maxActions);
                        operation.Check();
                        if (applied)
                        {
                            attempts++;
                            unchangedActions = previousAction == action && actionSignature == observed.Signature
                                ? unchangedActions + 1 : 1;
                            previousAction = action;
                            actionSignature = observed.Signature;
                        }
                        actionCompleted?.Invoke(action, applied, observed);
                        if (applied && action == UiAction.ReviveParty)
                            await driver.DelayAsync(1000, operation.Token);
                    }
                }
            }
            await driver.DelayAsync(250, operation.Token);
        }
    }
}
