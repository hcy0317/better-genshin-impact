using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using Fischless.GameCapture;

namespace BetterGenshinImpact.GameTask.Common.Ui;

internal static class UiHandoffRecovery
{
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(90);

    internal static Task RecoverAsync(IUiDriver driver, Func<CancellationToken, Task> recoverAtStatue,
        CancellationToken ct, TimeProvider? clock = null) =>
        UiOperation.RunAsync("script-handoff", Budget, ct, async operation =>
        {
            var before = await UiRecovery.ToMainAsync(driver, operation.Token, requireOverworld: true, clock: clock);
            if (!before.SourceBound || !before.SourceStamp.IsKnown)
                throw new InvalidOperationException("跨脚本恢复缺少已绑定的采集来源");
            var session = before.SourceStamp.SessionId;
            var last = before;
            CaptureFrameFence? fence = null;
            var recoveryUsed = false;
            var ordinaryFrames = 0;
            var recoveryFrames = 0;
            while (true)
            {
                operation.Check();
                var observed = driver.Capture();
                operation.Check();
                operation.Observe("ordinary-overworld-handoff", observed.Describe(), observed.FrameId);
                if (observed.SourceBound && observed.SourceStamp.IsKnown && observed.SourceStamp.SessionId != session)
                    throw new InvalidOperationException("跨脚本恢复期间采集会话改变，禁止交接");
                var fresh = observed.SourceBound && observed.HasUsableEvidence && observed.IsAfter(last) &&
                    (fence == null || fence.Value.Accepts(observed.SourceStamp));
                if (!fresh) { ordinaryFrames = 0; recoveryFrames = 0; }
                else
                {
                    last = observed;
                    var world = observed.World;
                    var ready = observed.Matches(UiTarget.Overworld) && world is
                        { OrdinaryAvatarHud: true, Transformed: false, ControlObserved: true,
                          Controlled: false, KeyboardBreakout: false, LowHp: false, PartyRejected: false } &&
                        world.Value.Motion is not (MotionStatus.Fly or MotionStatus.Climb);
                    ordinaryFrames = ready ? ordinaryFrames + 1 : 0;
                    if (ordinaryFrames >= 2) return true;
                    var canRecover = !recoveryUsed && observed.Matches(UiTarget.Overworld) &&
                        world is { ControlObserved: true, KeyboardBreakout: false, PartyRejected: false } &&
                        world.Value.Motion != MotionStatus.Climb &&
                        (!world.Value.Controlled || world.Value.Motion == MotionStatus.Fly) &&
                        (world.Value.Transformed || world.Value.Motion == MotionStatus.Fly);
                    recoveryFrames = canRecover ? recoveryFrames + 1 : 0;
                    if (recoveryFrames >= 2)
                    {
                        operation.Check();
                        recoveryUsed = true;
                        // 委托仅使用既有神像传送：地图、目标与到达门禁仍由TpTask持有。
                        await recoverAtStatue(operation.Token);
                        operation.Check();
                        driver.MarkInputCompleted(observed);
                        fence = new(observed.SourceStamp, (clock ?? TimeProvider.System).GetTimestamp());
                        ordinaryFrames = recoveryFrames = 0;
                    }
                }
                await driver.DelayAsync(250, operation.Token);
            }
        }, clock: clock);
}
