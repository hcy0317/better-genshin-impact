using System;
using System.Threading;
using Fischless.GameCapture;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

/// <summary>物理输入和截图确认分离。调用返回只说明已发送，绝不签发施放成功。</summary>
internal static class CombatSkillInput
{
    internal static CombatFlowResult Send(CombatSkillAttempts attempts, CombatFlowAction action, string actor,
        CaptureFrameStamp before, Action send, CancellationToken ct, TimeProvider? clock = null)
    {
        clock ??= TimeProvider.System;
        ct.ThrowIfCancellationRequested();
        if (attempts.IsOccupied(actor, action.Command.Method)) return CombatFlowResult.Pending;
        if (!before.IsFresh(clock, TimeSpan.FromMilliseconds(150))) return CombatFlowResult.Unknown;
        if (!action.TryBeginInput()) return CombatFlowResult.Skipped;
        // 准入条件本身也可能花时间；不能拿其执行前的新鲜度替代发送时的新鲜度。
        if (!before.IsFresh(clock, TimeSpan.FromMilliseconds(150))) return CombatFlowResult.Unknown;
        var attempt = attempts.TryBegin(actor, action.Command.Method, action.CommandId, action.InputAt!.Value, action.AbsoluteDeadline);
        if (attempt == null) return CombatFlowResult.Pending;
        if (!action.RegisterPendingAttempt(attempt)) throw new InvalidOperationException("物理技能请求与当前动作身份不一致");
        action.DiagnosticAttemptId = attempt.AttemptId;
        try
        {
            ct.ThrowIfCancellationRequested();
            send();
        }
        finally
        {
            // 输入抛错也保留原请求，不能假定系统完全没有收到按键而立即重发。
            attempts.MarkInputCompleted(attempt.AttemptId, new(before, clock.GetTimestamp()));
        }
        ct.ThrowIfCancellationRequested();
        action.DiagnosticReason = "物理输入已结束，等待原请求的输入后新帧；没有同步轮询或重发";
        return CombatFlowResult.Pending;
    }
}
