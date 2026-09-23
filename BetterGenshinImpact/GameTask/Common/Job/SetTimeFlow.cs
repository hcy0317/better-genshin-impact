using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.Common.Ui;
using Fischless.GameCapture;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask.Common.Job;

internal enum SetTimeResult { SatisfiedExistingNearTarget, Adjusted }
internal readonly record struct TimeSettingObservation(bool Visible, int? CurrentMinutes, int? SelectedMinutes,
    bool TooClose, CaptureFrameStamp Source);

internal sealed class SetTimeFlowIo
{
    internal TimeProvider Clock { get; init; } = TimeProvider.System;
    internal ILogger Logger { get; init; } = null!;
    internal Func<CancellationToken, Task> ReturnMain { get; init; } = null!;
    internal Func<CancellationToken, Task> Open { get; init; } = null!;
    internal Func<TimeSettingObservation> Observe { get; init; } = null!;
    internal Func<int, int, CancellationToken, Task> SetDial { get; init; } = null!;
    internal Func<Action, CancellationToken, Task> Confirm { get; init; } = null!;
    internal Func<CancellationToken, Task> SkipAnimation { get; init; } = null!;
    internal Func<int, CancellationToken, Task> Delay { get; init; } = null!;
}

/// <summary>30分钟是游戏禁用条件，不是任务成功容差；所有结束都先确认退出界面。</summary>
internal static class SetTimeFlow
{
    internal static int Normalize(int hour, int minute) => (int)(((long)hour * 60 + minute) % 1440 + 1440) % 1440;
    internal static int Distance(int actual, int target) => Math.Min(Math.Abs(actual - target), 1440 - Math.Abs(actual - target));
    private static bool Near(int? actual, int target) => actual is >= 0 and < 1440 && Distance(actual.Value, target) <= 2;

    internal static async Task<SetTimeResult> ExecuteAsync(int hour, int minute, bool skipAnimation,
        SetTimeFlowIo io, CancellationToken ct)
    {
        var target = Normalize(hour, minute);
        TimeSettingObservation confirmed = default;
        try
        {
            var result = await UiOperation.RunAsync("set-time", TimeSpan.FromSeconds(45), ct, async operation =>
            {
                var token = operation.Token;
                await io.ReturnMain(token);
                var beforeOpen = io.Observe();
                operation.Check();
                await io.Open(token);
                var fence = new CaptureFrameFence(beforeOpen.Source, io.Clock.GetTimestamp());
                await io.Delay(100, token);
                var observation = await ReadClockAsync(io, operation, fence);
                if (Near(observation.CurrentMinutes, target))
                {
                    confirmed = observation;
                    return await FinishAsync(SetTimeResult.SatisfiedExistingNearTarget, observation, target, io, token);
                }

                var ready = false;
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    operation.Check();
                    await io.SetDial(target / 60, target % 60, token);
                    fence = new(observation.Source, io.Clock.GetTimestamp());
                    await io.Delay(100, token);
                    observation = await ReadClockAsync(io, operation, fence);
                    if (Near(observation.CurrentMinutes, target))
                    {
                        confirmed = observation;
                        return await FinishAsync(SetTimeResult.SatisfiedExistingNearTarget, observation, target, io, token);
                    }
                    if (!observation.TooClose && Near(observation.SelectedMinutes, target)) { ready = true; break; }
                }
                if (!ready) throw new InvalidOperationException("调时目标未准入：仍不足30分钟或拨盘未到请求时间；退出页面，不盲加一天");
                operation.Check();
                if (!observation.Source.IsFresh(io.Clock, UiSnapshot.RecoveryMaximumAge))
                    throw new InvalidOperationException("调时确认源帧过期，未点击确认");
                var confirmation = observation;
                await io.Confirm(() =>
                {
                    operation.Check();
                    if (!confirmation.Source.IsFresh(io.Clock, UiSnapshot.RecoveryMaximumAge))
                        throw new InvalidOperationException("调时确认源帧在原生输入前过期");
                }, token);
                fence = new(observation.Source, io.Clock.GetTimestamp());
                await io.Delay(100, token);
                while (operation.Remaining > TimeSpan.FromSeconds(5))
                {
                    operation.Check();
                    observation = io.Observe();
                    operation.Check();
                    if (Usable(observation, io, fence) && Near(observation.CurrentMinutes, target))
                    {
                        confirmed = observation;
                        // 目标已经有真实当前时间证据；跳动画只是优化，不能替代目标确认。
                        if (skipAnimation) { await io.SkipAnimation(token); await io.Delay(100, token); }
                        return await FinishAsync(SetTimeResult.Adjusted, observation, target, io, token);
                    }
                    await io.Delay(150, token);
                }
                throw new TimeoutException("调时后未观察到请求时间，退出页面并保留失败");
            }, io.Logger, io.Clock);
            // 成功判定与本层预算已退役后再记录。嵌套时不让额外日志挤占父UI预算。
            if (UiOperation.Current == null)
                try { io.Logger.LogInformation("SET_TIME_RESULT result={Result} requestedMinutes={Target} currentMinutes={Current} source={Session}/{Sequence}",
                    result, target, confirmed.CurrentMinutes, confirmed.Source.SessionId, confirmed.Source.Sequence); } catch { }
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception original)
        {
            ct.ThrowIfCancellationRequested();
            // 原set-time已退役；恢复仍尊重调用方父预算，不绕过取消或建立无限重试。
            try
            {
                await UiOperation.RunAsync("set-time-recovery", TimeSpan.FromSeconds(20), ct,
                    async op => { await io.ReturnMain(op.Token); return true; }, io.Logger, io.Clock);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception recovery) { throw new AggregateException("调时失败且退出页面失败", original, recovery); }
            ExceptionDispatchInfo.Capture(original).Throw();
            throw;
        }
    }

    private static bool Usable(TimeSettingObservation observation, SetTimeFlowIo io, CaptureFrameFence fence) =>
        observation.Visible && observation.Source.IsFresh(io.Clock, UiSnapshot.RecoveryMaximumAge) && fence.Accepts(observation.Source);

    private static async Task<TimeSettingObservation> ReadClockAsync(SetTimeFlowIo io, UiOperation operation, CaptureFrameFence fence)
    {
        var started = io.Clock.GetTimestamp();
        while (io.Clock.GetElapsedTime(started) < TimeSpan.FromSeconds(5))
        {
            operation.Check();
            var observation = io.Observe();
            operation.Check();
            if (Usable(observation, io, fence)) return observation;
            await io.Delay(100, operation.Token);
        }
        throw new InvalidOperationException("未取得调时页面的新鲜原帧，不继续点击");
    }

    private static async Task<SetTimeResult> FinishAsync(SetTimeResult result, TimeSettingObservation observation,
        int target, SetTimeFlowIo io, CancellationToken ct)
    {
        await io.ReturnMain(ct);
        ct.ThrowIfCancellationRequested();
        return result;
    }
}
