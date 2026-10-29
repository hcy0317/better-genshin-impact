using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Ui;
using Fischless.GameCapture;

namespace BetterGenshinImpact.GameTask;

internal sealed record ReviveTarget(string Name, int Index)
{
    internal static ReviveTarget? FromSelection(ReviveTarget requested, bool inputSubmitted,
        ReviveRecoveryFrame? before, ReviveRecoveryFrame after) =>
        inputSubmitted && before is { FrameId: > 0, MainReady: true, Revive: ReviveUiState.None } &&
        before.HasUsableEvidence && after.HasUsableEvidence && after.IsAfter(before) && after.Revive == ReviveUiState.FoodPrompt &&
        before.HasTarget(requested) && after.HasTarget(requested) ? requested : null;
}
internal sealed record ReviveRecoveryFrame(long FrameId, bool MainReady, ReviveUiState Revive,
    int ActiveIndex, IReadOnlyDictionary<int, string> Party)
{
    private TimeProvider? EvidenceClock { get; init; }
    internal CaptureFrameStamp SourceStamp { get; private init; }
    internal bool SourceBound => EvidenceClock != null;
    internal bool HasUsableEvidence => SourceBound
        ? SourceStamp.IsFresh(EvidenceClock!, UiSnapshot.RecoveryMaximumAge) : FrameId > 0;
    internal ReviveRecoveryFrame WithSource(CaptureFrameStamp source, TimeProvider clock) => this with
    { FrameId = source.Sequence, SourceStamp = source, EvidenceClock = clock };
    internal bool IsAfter(ReviveRecoveryFrame before) => SourceBound || before.SourceBound
        ? SourceBound && before.SourceBound && SourceStamp.IsAfter(before.SourceStamp) : FrameId > before.FrameId;
    internal bool HasTarget(ReviveTarget target) => target.Index > 0 && !string.IsNullOrWhiteSpace(target.Name) &&
        Party.TryGetValue(target.Index, out var name) && name == target.Name;
    internal bool Confirms(ReviveTarget? target) => HasUsableEvidence && MainReady && Revive == ReviveUiState.None &&
        (target == null || HasTarget(target) && ActiveIndex == target.Index);
}

// 战斗仍按原截止推进；只有既有复苏入口确认后才退役战斗并接管父取消。
internal sealed class CombatRecoveryOwner : IDisposable
{
    private static readonly AsyncLocal<CombatRecoveryOwner?> Active = new();
    private readonly CombatRecoveryOwner? _previous;
    private readonly CancellationToken _parent;
    private readonly Action _retireCombat;
    private bool _started, _disposed;

    private CombatRecoveryOwner(CancellationToken parent, Action retireCombat)
    {
        _previous = Active.Value;
        _parent = parent;
        _retireCombat = retireCombat;
        Active.Value = this;
    }

    internal static CombatRecoveryOwner Enter(CancellationToken parent, Action retireCombat) => new(parent, retireCombat);

    // 仅由新帧确认的复苏入口调用；普通HUD缺失和游泳恢复继续使用原战斗令牌。
    internal static CancellationToken Start(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        TaskExecutionScope.ThrowIfFailed();
        if (Active.Value is not { } owner) return ct;
        ObjectDisposedException.ThrowIf(owner._disposed, owner);
        owner._parent.ThrowIfCancellationRequested();
        if (owner._started) return ct; // 保留恢复入口已建立的60秒等子预算。
        owner._retireCombat();
        owner._started = true;
        return owner._parent;
    }

    public void Dispose()
    {
        _disposed = true;
        if (ReferenceEquals(Active.Value, this)) Active.Value = _previous;
    }
}

// Only the completed recovery path can create this signal. It is not victory:
// the caller must retry the original route/commission within its existing budget.
internal sealed class CombatRecoveryCompletedException : RetryException
{
    private CombatRecoveryCompletedException() : base("[BGI_RECOVERY_COMPLETED] 神像恢复流程已完成并确认返回主界面，请重试原路线或委托") { }

    internal static async Task RecoverVerifiedAsync(Func<Task> recover, Func<Task> verify, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        TaskExecutionScope.ThrowIfFailed();
        await recover();
        ct.ThrowIfCancellationRequested();
        await verify();
        ct.ThrowIfCancellationRequested();
        TaskExecutionScope.ThrowIfFailed();
        throw new CombatRecoveryCompletedException();
    }

    internal static Task RecoverAsync(Func<Task> recover, Func<ReviveRecoveryFrame> capture,
        ReviveTarget? target, Func<Task> waitForFreshFrame, CancellationToken ct)
    {
        ReviveRecoveryFrame? lastFrame = null;
        long recoveryCompleted = 0;
        return RecoverAsync(async () => { await recover(); recoveryCompleted = TimeProvider.System.GetTimestamp(); }, () =>
        {
            var frame = capture();
            if (!frame.HasUsableEvidence || (frame.SourceBound && frame.SourceStamp.CapturedTimestamp <= recoveryCompleted) ||
                lastFrame != null && !frame.IsAfter(lastFrame)) return false;
            lastFrame = frame;
            return frame.Confirms(target);
        }, waitForFreshFrame, ct);
    }

    internal static async Task RecoverAsync(Func<Task> recover, Func<bool> confirm,
        Func<Task> waitForFreshFrame, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        TaskExecutionScope.ThrowIfFailed();
        await recover();
        ct.ThrowIfCancellationRequested();
        if (!confirm()) throw new InvalidOperationException("神像恢复后未确认主界面，不允许继续路线");
        await waitForFreshFrame();
        ct.ThrowIfCancellationRequested();
        if (!confirm()) throw new InvalidOperationException("神像恢复后主界面确认不稳定，不允许继续路线");
        TaskExecutionScope.ThrowIfFailed();
        throw new CombatRecoveryCompletedException();
    }
}
