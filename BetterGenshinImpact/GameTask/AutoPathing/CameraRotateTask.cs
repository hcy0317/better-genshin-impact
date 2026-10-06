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
using BetterGenshinImpact.GameTask.Common;

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

    private float RotateToApproach(float targetOrientation, ImageRegion imageRegion, PathRecoveryScope? scope,
        int tolerance = 0, Action<CaptureFrameFence>? onInput = null, DateTimeOffset? waitDeadline = null)
    {
        var io = scope?.Io ?? NativeIo;
        bool Expired() => waitDeadline is { } local && io.Clock.GetUtcNow() >= local ||
            Deadline?.Invoke() is { } parent && io.Clock.GetUtcNow() >= parent;
        ct.ThrowIfCancellationRequested();
        if (!PathWorldAvailability.IsPlayable(io, imageRegion)) return float.NaN;
        var reading = io.CameraReading?.Invoke(imageRegion) ?? new CameraOrientation.Reading(io.CameraOrientation(imageRegion), null, null, false);
        var cao = reading.Angle;
        if (!float.IsFinite(cao) || !float.IsFinite(targetOrientation) ||
            !imageRegion.FrameStamp.IsFresh(io.Clock, UiSnapshot.RecoveryMaximumAge)) return float.NaN;
        scope?.Check();
        ct.ThrowIfCancellationRequested();
        UiOperation.Current?.Check();
        if (Expired()) return float.NaN;
        var diff = (cao - targetOrientation + 180) % 360 - 180;
        diff += diff < -180 ? 360 : 0;
        try { if (onInput != null) io.Logger.LogDebug("CAMERA_ROTATION_OBSERVATION source={Session}/{Sequence} angle={Angle} primaryAngle={Primary} confidence={Confidence} fallbackUsed={Fallback} target={Target} diff={Diff}",
            imageRegion.FrameStamp.SessionId, imageRegion.FrameStamp.Sequence, cao, reading.PrimaryAngle, reading.Confidence,
            reading.FallbackUsed, targetOrientation, diff); } catch { }
        if (diff == 0 || Math.Abs(diff) < tolerance)
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

        var dpi = io.Dpi();
        var movement = (int)Math.Round(-controlRatio * diff * dpi);
        scope?.Check();
        ct.ThrowIfCancellationRequested();
        UiOperation.Current?.Check();
        if (Expired()) return float.NaN;
        if (!imageRegion.FrameStamp.IsFresh(io.Clock, UiSnapshot.RecoveryMaximumAge)) return float.NaN;
        if (onInput == null) { io.MouseMove(movement, 0); return diff; }
        using var input = new DiagnosticInputAttempt(io.Clock);
        using var admission = new Fischless.WindowsInput.InputDispatchCapture(() =>
        {
            ct.ThrowIfCancellationRequested();
            scope?.Check();
            UiOperation.Current?.Check();
            if (Expired()) throw new AutoGeniusInvokation.Exception.RetryException("视角输入准备已耗尽原期限");
            if (!imageRegion.FrameStamp.IsFresh(io.Clock, UiSnapshot.RecoveryMaximumAge))
                throw new AutoGeniusInvokation.Exception.RetryException("视角输入准备后原帧过期");
        });
        Exception? failure = null;
        try { io.MouseMove(movement, 0); }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            var receipt = input.Complete(failure == null, failure);
            try
            {
                io.Logger.LogDebug("CAMERA_ROTATION_INPUT source={Session}/{Sequence} from={Angle} target={Target} diff={Diff} pixels={Pixels} dpi={Dpi} receipt={Receipt}",
                    imageRegion.FrameStamp.SessionId, imageRegion.FrameStamp.Sequence, cao, targetOrientation, diff,
                    movement, dpi, receipt.Describe());
            }
            catch { }
        }
        onInput?.Invoke(new(imageRegion.FrameStamp, io.Clock.GetTimestamp()));
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
        CaptureFrameFence? inputFence = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            scope?.Check();
            using var screen = await PathWorldAvailability.CapturePlayableAsync(io, deadline, ct, () => scope?.Check());
            scope?.Check();
            if (scope != null) await scope.BeforeInputAsync();
            if (previous.IsKnown && !screen.FrameStamp.IsAfter(previous) ||
                inputFence is { } required && !required.Accepts(screen.FrameStamp))
            {
                await io.Delay(50, ct);
                continue;
            }
            previous = screen.FrameStamp;
            var diff = RotateToApproach(targetOrientation, screen, scope, maxDiff, fence => inputFence = fence, deadline);
            if (!float.IsFinite(diff)) return false;
            if (Math.Abs(diff) < maxDiff)
            {
                try { io.Logger.LogDebug("CAMERA_ROTATION_FEEDBACK source={Session}/{Sequence} target={Target} diff={Diff} inputFenceAccepted={Accepted}",
                    screen.FrameStamp.SessionId, screen.FrameStamp.Sequence, targetOrientation, diff, inputFence?.Accepts(screen.FrameStamp)); }
                catch { }
                return true;
            }
            unchanged = previousDiff.HasValue && Math.Abs(diff - previousDiff.Value) < 0.1f ? unchanged + 1 : 0;
            previousDiff = diff;
            if (++count >= maxTryTimes || unchanged >= 10)
            {
                io.Logger.LogWarning("视角转动未收敛，停止转动与前进");
                return false;
            }

            if (scope != null) await scope.DelayAsync(60);
            else await io.Delay(60, ct);
        }
    }
}
