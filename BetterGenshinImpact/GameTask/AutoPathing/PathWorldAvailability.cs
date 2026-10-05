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
        var evidence = new PathWorldEvidence(io);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            UiOperation.Current?.Check();
            check?.Invoke();
            if (io.Clock.GetUtcNow() >= deadline)
            {
                var error = new RetryException("PATH_WORLD_UNAVAILABLE: 重连、加载或无有效新帧，路径原预算已耗尽");
                evidence.Failed(null, error);
                throw error;
            }
            ImageRegion frame;
            try { using (evidence.Measure("capture")) frame = io.Capture(); }
            catch (Exception error) { evidence.Failed(null, error); throw; }
            try
            {
                WorldFrameKind kind;
                using (evidence.Measure("world-recognition")) kind = io.Availability?.Invoke(frame) ?? WorldFrameKind.Playable;
                var fresh = frame.FrameStamp.IsFresh(io.Clock, UiSnapshot.RecoveryMaximumAge);
                var ready = kind == WorldFrameKind.Playable && fresh;
                float? orientation = null;
                if (blocked && ready)
                {
                    using (evidence.Measure("camera-orientation")) orientation = io.CameraOrientation(frame);
                    fresh = frame.FrameStamp.IsFresh(io.Clock, UiSnapshot.RecoveryMaximumAge);
                    ready = float.IsFinite(orientation.Value) && fresh;
                }
                evidence.Observed(frame, kind, fresh, orientation, ready);
                ct.ThrowIfCancellationRequested();
                UiOperation.Current?.Check();
                check?.Invoke();
                if (io.Clock.GetUtcNow() >= deadline)
                    throw new RetryException("PATH_WORLD_UNAVAILABLE: 页面识别已耗尽路径原预算");
                if (ready && (!blocked || frame.FrameStamp.IsAfter(previous)))
                {
                    if (!blocked || ++stable >= 2) { evidence.Recovered(frame); return frame; }
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
            catch (Exception error) { evidence.Failed(frame, error); frame.Dispose(); throw; }
            frame.Dispose();
            await io.Delay((int)Math.Max(1, Math.Min(150, (deadline - io.Clock.GetUtcNow()).TotalMilliseconds)), ct);
        }
    }
}
