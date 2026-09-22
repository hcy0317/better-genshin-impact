using System;
using System.Threading;
using Fischless.GameCapture;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

internal sealed record CombatSkillRecoveryPulse(CombatSkillAttempt Attempt, Guid RequestId, CaptureFrameStamp Source, double WindowEnd);

/// <summary>物理输入和截图确认分离。调用返回只说明已发送，绝不签发施放成功。</summary>
internal static class CombatSkillInput
{
    internal static CombatFlowResult Send(CombatSkillAttempts attempts, CombatFlowAction action, string actor,
        CaptureFrameStamp before, Func<CombatNativeInputRequest, Action, CombatBattleHostInputResult> send,
        CancellationToken ct, TimeProvider? clock = null)
    {
        clock ??= TimeProvider.System;
        ct.ThrowIfCancellationRequested();
        if (attempts.IsOccupied(actor, action.Command.Method)) return CombatFlowResult.Pending;
        if (!before.IsFresh(clock, TimeSpan.FromMilliseconds(150))) return CombatFlowResult.Unknown;
        if (!action.CanStart) return CombatFlowResult.Skipped;
        CombatSkillAttempt? attempt = null;
        var request = new CombatNativeInputRequest(action.InputRequestId, before,
            checked(clock.GetTimestamp() + (long)(action.RemainingBudget * clock.TimestampFrequency)));
        void BeginNative()
        {
            ct.ThrowIfCancellationRequested();
            if (!action.TryBeginInput()) throw new CombatInputNotAdmittedException();
            attempt = attempts.TryBegin(actor, action.Command.Method, action.CommandId, action.InputAt!.Value, action.AbsoluteDeadline);
            if (attempt == null) throw new CombatInputNotAdmittedException();
            if (!action.RegisterPendingAttempt(attempt)) throw new InvalidOperationException("物理技能请求与当前动作身份不一致");
            action.DiagnosticAttemptId = attempt.AttemptId;
        }
        CombatBattleHostInputResult receipt;
        try
        {
            receipt = send(request, BeginNative);
        }
        catch
        {
            if (attempt != null)
            {
                action.RecordInputSubmission(request.Id);
                attempts.MarkInputCompleted(attempt.AttemptId, new(before, clock.GetTimestamp()));
            }
            throw;
        }
        var possiblySubmitted = receipt.Status is CombatBattleHostInputStatus.Sent or CombatBattleHostInputStatus.Unknown || receipt.NativeSubmitted > 0;
        if (!possiblySubmitted)
        {
            if (attempt != null && !attempts.DiscardUnsubmitted(attempt.AttemptId))
                throw new InvalidOperationException("不能撤回已经取得输入证据的技能槽");
            action.ClearUnsubmittedInput(attempt);
            if (receipt.Status == CombatBattleHostInputStatus.Failed)
                throw receipt.Error ?? new InvalidOperationException(receipt.Reason);
            action.DiagnosticReason = "技能未发送，保留原意图等待新证据：" + receipt.Reason;
            return CombatFlowResult.AwaitingObservation;
        }
        if (attempt == null) throw new InvalidOperationException("输入端未经过原生前置准入便报告了发送");
        action.RecordInputSubmission(request.Id);
        var completed = receipt.CompletedTimestamp ?? receipt.ObservableAfterTimestamp ?? clock.GetTimestamp();
        if (completed < before.CapturedTimestamp || completed > clock.GetTimestamp())
            throw new InvalidOperationException("技能输入回执时间边界无效");
        attempts.MarkInputCompleted(attempt.AttemptId, new(before, completed));
        attempts.MarkOriginalReceipt(attempt.AttemptId, receipt, action.Now, clock);
        ct.ThrowIfCancellationRequested();
        if (receipt.Status == CombatBattleHostInputStatus.Failed)
            throw receipt.Error ?? new InvalidOperationException(receipt.Reason);
        action.DiagnosticReason = $"技能提交状态{receipt.Status}，等待原请求的输入后新帧，不重发";
        return CombatFlowResult.Pending;
    }

    internal static void SendRecovery(CombatSkillAttempts attempts, CombatFlowAction action, CombatSkillRecoveryPulse pulse,
        Func<CombatNativeInputRequest, Action, CombatBattleHostInputResult> send, CancellationToken ct, TimeProvider clock)
    {
        if (action.PendingAttempt?.AttemptId != pulse.Attempt.AttemptId || action.BattleId != pulse.Attempt.BattleId ||
            action.CommandId != pulse.Attempt.CommandId || !attempts.TryStartRecovery(pulse)) return;
        var request = new CombatNativeInputRequest(pulse.RequestId, pulse.Source,
            checked(clock.GetTimestamp() + (long)(Math.Min(action.RemainingBudget, pulse.Attempt.Deadline - action.Now) * clock.TimestampFrequency)));
        void Begin()
        {
            ct.ThrowIfCancellationRequested();
            if (!action.CanStart || !action.CanContinue || !attempts.CanSendRecovery(pulse) ||
                action.Now >= pulse.WindowEnd || action.Now >= pulse.Attempt.Deadline ||
                action.RemainingBudget < CombatFlowPolicy.ActionSeconds(action.Command) ||
                !pulse.Source.IsFresh(clock, TimeSpan.FromMilliseconds(150)))
                throw new CombatInputNotAdmittedException();
        }
        CombatBattleHostInputResult receipt;
        var entered = false;
        try { receipt = send(request, () => { Begin(); entered = true; }); }
        catch
        {
            if (entered)
            {
                action.RecordInputSubmission(request.Id);
                attempts.CompleteRecovery(pulse, new(pulse.Source, clock.GetTimestamp()));
            }
            throw;
        }
        if (receipt.Status is CombatBattleHostInputStatus.Sent or CombatBattleHostInputStatus.Unknown || receipt.NativeSubmitted > 0)
        {
            if (!entered) throw new InvalidOperationException("恢复输入绕过原生前置准入");
            action.RecordInputSubmission(request.Id);
            var completed = receipt.CompletedTimestamp ?? receipt.ObservableAfterTimestamp ?? clock.GetTimestamp();
            if (completed < pulse.Source.CapturedTimestamp || completed > clock.GetTimestamp())
                throw new InvalidOperationException("恢复输入回执时间边界无效");
            attempts.CompleteRecovery(pulse, new(pulse.Source, completed));
        }
        ct.ThrowIfCancellationRequested();
        if (receipt.Status == CombatBattleHostInputStatus.Failed) throw receipt.Error ?? new InvalidOperationException(receipt.Reason);
        action.Trace("skill-recovery", $"attempt={pulse.Attempt.AttemptId} request={request.Id} pulseOrdinal=1 status={receipt.Status} deadline={pulse.Attempt.Deadline:F3}; confirmation still required");
    }
}
