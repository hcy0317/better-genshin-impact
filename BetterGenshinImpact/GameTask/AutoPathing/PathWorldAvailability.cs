using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;

namespace BetterGenshinImpact.GameTask.AutoPathing;

internal static class PathWorldAvailability
{
    internal static bool IsPlayable(PathMoveToIo io, ImageRegion frame) =>
        (io.Availability == null || io.Availability(frame) == WorldFrameKind.Playable) &&
        frame.FrameStamp.IsFresh(io.Clock, UiSnapshot.RecoveryMaximumAge);

    // The caller owns the returned image. Blocked images are disposed here.
    internal static async Task<ImageRegion> CapturePlayableAsync(PathMoveToIo io,
        DateTimeOffset deadline, CancellationToken ct, Action? check = null, Action? onBlocked = null)
    {
        var blocked = false;
        var stable = 0;
        CaptureFrameStamp previous = default;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            UiOperation.Current?.Check();
            check?.Invoke();
            if (io.Clock.GetUtcNow() >= deadline)
                throw new RetryException("PATH_WORLD_UNAVAILABLE: 重连、加载或无有效新帧，路径原预算已耗尽");
            var frame = io.Capture();
            try
            {
                var ready = IsPlayable(io, frame);
                if (blocked && ready)
                    ready = float.IsFinite(io.CameraOrientation(frame)) &&
                        frame.FrameStamp.IsFresh(io.Clock, UiSnapshot.RecoveryMaximumAge);
                ct.ThrowIfCancellationRequested();
                UiOperation.Current?.Check();
                check?.Invoke();
                if (io.Clock.GetUtcNow() >= deadline)
                    throw new RetryException("PATH_WORLD_UNAVAILABLE: 页面识别已耗尽路径原预算");
                if (ready && (!blocked || frame.FrameStamp.IsAfter(previous)))
                {
                    if (!blocked || ++stable >= 2) return frame;
                }
                else stable = 0;
                previous = frame.FrameStamp;
                if (!blocked)
                {
                    foreach (var action in new[] { GIActions.MoveForward, GIActions.MoveBackward,
                        GIActions.MoveLeft, GIActions.MoveRight, GIActions.SprintMouse })
                        io.Send(action, KeyType.KeyUp);
                }
                onBlocked?.Invoke();
                blocked = true;
            }
            catch { frame.Dispose(); throw; }
            frame.Dispose();
            await io.Delay((int)Math.Max(1, Math.Min(150, (deadline - io.Clock.GetUtcNow()).TotalMilliseconds)), ct);
        }
    }
}
