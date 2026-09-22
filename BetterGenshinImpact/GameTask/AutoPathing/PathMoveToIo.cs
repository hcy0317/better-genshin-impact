using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Model.Area;
using OpenCvSharp;
using Fischless.GameCapture;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask.AutoPathing;

// Native boundaries only. Arrival, flight and recovery decisions stay in MoveTo.
internal sealed class PathMoveToIo
{
    // Creating a replay boundary must not eagerly initialize the application host.
    internal Func<ILogger> LoggerFactory { get; init; } = () => Missing<ILogger>(nameof(LoggerFactory));
    internal ILogger Logger => LoggerFactory();
    internal TimeProvider Clock { get; init; } = TimeProvider.System;
    internal Func<ImageRegion> Capture { get; init; } = () => Missing<ImageRegion>(nameof(Capture));
    internal Func<ImageRegion, WaypointForTrack, Task<PathPosition>> Locate { get; init; } = null!;
    internal Func<ImageRegion, WaypointForTrack, Task<PathPosition>>? LocateDirect { get; init; }
    internal Func<ImageRegion, float> CameraOrientation { get; init; } = _ => Missing<float>(nameof(CameraOrientation));
    internal Action<int, int> MouseMove { get; init; } = (_, _) => Missing<bool>(nameof(MouseMove));
    internal Func<double> Dpi { get; init; } = () => Missing<double>(nameof(Dpi));
    internal Func<string, Task> SwitchAvatar { get; init; } = null!;
    internal Action<ImageRegion> EndJudgment { get; init; } = null!;
    internal Func<int, int, Task<bool>> RotateUntil { get; init; } = null!;
    internal Func<float, ImageRegion, float> RotateStep { get; init; } = null!;
    internal Func<ImageRegion, MotionStatus> Motion { get; init; } = _ => Missing<MotionStatus>(nameof(Motion));
    internal Func<ImageRegion, bool> CombatHud { get; init; } = _ => Missing<bool>(nameof(CombatHud));
    internal Action<GIActions, KeyType> Send { get; init; } = (_, _) => Missing<bool>(nameof(Send));
    internal Func<GIActions, bool> IsDown { get; init; } = _ => Missing<bool>(nameof(IsDown));
    internal Func<int, CancellationToken, Task> Delay { get; init; } = (_, _) => Missing<Task>(nameof(Delay));
    internal Action CheckInput { get; init; } = () => Missing<bool>(nameof(CheckInput));
    internal Func<float, WaypointForTrack, double, ImageRegion, int, Task<bool>>? HurryOn { get; init; } = (_, _, _, _, _) => Missing<Task<bool>>(nameof(HurryOn));

    internal PathMoveToIo(bool native = false)
    {
        if (!native) return;
        Helpers.ApplicationHostBootstrapGuard.EnsureAllowed();
        HurryOn = null;
        LoggerFactory = () => Common.TaskControl.Logger;
        Capture = () => Common.TaskControl.CaptureToRectArea();
        CameraOrientation = image => Common.Map.CameraOrientation.Compute(image.SrcMat);
        MouseMove = (x, y) => Simulation.SendInput.Mouse.MoveMouseBy(x, y);
        Dpi = () => TaskContext.Instance().DpiScale;
        Motion = Bv.GetMotionStatus;
        CombatHud = Bv.IsCombatHud;
        Send = (action, type) => Simulation.SendInput.SimulateAction(action, type);
        IsDown = action => Simulation.IsKeyDown(action.ToActionKey().ToVK());
        Delay = Common.TaskControl.Delay;
        CheckInput = () => Common.TaskControl.CheckAndSleep(0);
    }

    private static T Missing<T>(string boundary) => throw new InvalidOperationException($"Isolated path I/O requires an explicit {boundary} boundary.");
}

internal readonly record struct PathPosition(Point2f Point, int AdditionalTimeInMs, bool IsDirect);
internal readonly record struct PathMoveObservation(CaptureFrameStamp Stamp, Point2f Position, bool Valid, MotionStatus Motion, bool SourceUsable);
