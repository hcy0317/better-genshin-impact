using BetterGenshinImpact.Core.Input;
using BetterGenshinImpact.GameTask.Common.Map;
using BetterGenshinImpact.GameTask.Model.Area;
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using static BetterGenshinImpact.GameTask.Common.TaskControl;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using Fischless.GameCapture;

namespace BetterGenshinImpact.GameTask.AutoPathing;

public class CameraRotateTask(CancellationToken ct)
{
    internal PathMoveToIo? Io { get; set; }
    internal Func<DateTimeOffset?>? Deadline { get; set; }
    private PathMoveToIo NativeIo => Io ??= new PathMoveToIo(native: true);
    private double Dpi => TaskContext.Instance().DpiScale;

    /// <summary>
    /// 向目标角度旋转
    /// </summary>
    /// <param name="targetOrientation"></param>
    /// <param name="imageRegion"></param>
    /// <returns></returns>
    public float RotateToApproach(float targetOrientation, ImageRegion imageRegion)
        => RotateToApproach(targetOrientation, imageRegion, null);

    private float RotateToApproach(float targetOrientation, ImageRegion imageRegion, PathRecoveryScope? scope)
    {
        var io = scope?.Io ?? NativeIo;
        ct.ThrowIfCancellationRequested();
        if (!PathWorldAvailability.IsPlayable(io, imageRegion)) return float.NaN;
        var cao = io.CameraOrientation(imageRegion);
        if (!float.IsFinite(cao) || !float.IsFinite(targetOrientation) ||
            !imageRegion.FrameStamp.IsFresh(io.Clock, UiSnapshot.RecoveryMaximumAge)) return float.NaN;
        scope?.Check();
        ct.ThrowIfCancellationRequested();
        UiOperation.Current?.Check();
        if (Deadline?.Invoke() is { } observedDeadline && io.Clock.GetUtcNow() >= observedDeadline) return float.NaN;
        var diff = (cao - targetOrientation + 180) % 360 - 180;
        diff += diff < -180 ? 360 : 0;
        if (diff == 0)
        {
            return diff;
        }

        // 平滑的旋转视角
        // todo dpi 和分辨率都会影响转动速度
        double controlRatio = 1;
        if (Math.Abs(diff) > 90)
        {
            controlRatio = 4;
        }
        else if (Math.Abs(diff) > 30)
        {
            controlRatio = 3;
        }
        else if (Math.Abs(diff) > 5)
        {
            controlRatio = 2;
        }

        var movement = (int)Math.Round(-controlRatio * diff * io.Dpi());
        scope?.Check();
        ct.ThrowIfCancellationRequested();
        UiOperation.Current?.Check();
        if (Deadline?.Invoke() is { } deadline && io.Clock.GetUtcNow() >= deadline) return float.NaN;
        if (!imageRegion.FrameStamp.IsFresh(io.Clock, UiSnapshot.RecoveryMaximumAge)) return float.NaN;
        io.MouseMove(movement, 0);
        return diff;
    }

    /// <summary>
    /// 转动视角到目标角度
    /// </summary>
    /// <param name="targetOrientation">目标角度</param>
    /// <param name="maxDiff">最大误差</param>
    /// <param name="maxTryTimes">最大尝试次数（超时时间）</param>
    /// <returns></returns>
    public async Task<bool> WaitUntilRotatedTo(int targetOrientation, int maxDiff, int maxTryTimes = 50)
        => await WaitUntilRotatedTo(targetOrientation, maxDiff, maxTryTimes, null);

    internal async Task<bool> WaitUntilRotatedTo(int targetOrientation, int maxDiff, int maxTryTimes, PathRecoveryScope? scope)
    {
        var io = scope?.Io ?? NativeIo;
        var deadline = Deadline?.Invoke() ?? io.Clock.GetUtcNow().AddSeconds(15);
        int count = 0;
        var unchanged = 0;
        float? previousDiff = null;
        CaptureFrameStamp previous = default;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            scope?.Check();
            using var screen = await PathWorldAvailability.CapturePlayableAsync(io, deadline, ct, () => scope?.Check());
            scope?.Check();
            if (scope != null) await scope.BeforeInputAsync();
            if (previous.IsKnown && !screen.FrameStamp.IsAfter(previous))
            {
                await io.Delay(50, ct);
                continue;
            }
            previous = screen.FrameStamp;
            var diff = RotateToApproach(targetOrientation, screen, scope);
            if (!float.IsFinite(diff)) return false;
            if (Math.Abs(diff) < maxDiff) return true;
            unchanged = previousDiff.HasValue && Math.Abs(diff - previousDiff.Value) < 0.1f ? unchanged + 1 : 0;
            previousDiff = diff;
            if (++count >= maxTryTimes || unchanged >= 10)
            {
                io.Logger.LogWarning("视角转动未收敛，停止转动与前进");
                return false;
            }

            if (scope != null) await scope.DelayAsync(50);
            else await io.Delay(50, ct);
        }
    }
}
