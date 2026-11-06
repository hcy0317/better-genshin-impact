using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Input;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Model.Area;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.Common.Job;

// Only capture/perception/OS boundaries. The public task retains the traversal and handoff owners.
internal sealed class SwitchPartyTaskIo
{
    internal double Scale { get; init; } = 1;
    internal TimeProvider Clock { get; init; } = TimeProvider.System;
    internal ILogger Logger { get; init; } = NullLogger.Instance;
    internal bool NativeDiagnostics { get; init; }
    internal required Func<IUiDriver> Driver { get; init; }
    internal required Func<ImageRegion> Capture { get; init; }
    internal required Func<ImageRegion, UiSnapshot> Read { get; init; }
    internal required Func<ImageRegion, string, Region> Element { get; init; }
    internal required Func<ImageRegion, RecognitionObject, string> OcrOne { get; init; }
    internal required Func<ImageRegion, RecognitionObject, List<Region>> OcrMany { get; init; }
    internal required Action<ImageRegion, double, double> Click { get; init; }
    internal required Action<ImageRegion, Rect> ScrollDown { get; init; }
    internal required Func<CancellationToken, Task> ResetTop { get; init; }
    internal required Func<int, CancellationToken, Task> Delay { get; init; }
    internal required Func<CancellationToken, Task> ReturnMain { get; init; }
    internal required Func<ImageRegion, Rect, bool> Confirm { get; init; }
    internal required Action Prepare { get; init; }
    internal required Func<bool> SameTarget { get; init; }
    internal required Action ClearDrawings { get; init; }
    internal required Action ClearCombatScenes { get; init; }

    internal static SwitchPartyTaskIo CreateNative()
    {
        var runtime = TaskContext.Instance().Runtime;
        var window = runtime?.Window.Handle ?? IntPtr.Zero;
        return new()
        {
            Scale = TaskContext.Instance().SystemInfo.AssetScale, Logger = TaskControl.Logger, NativeDiagnostics = true,
            Driver = () => new NativeUiDriver(inspectWorld: true), Capture = () => CaptureToRectArea(),
            Read = image => NativeUiDriver.Read(image),
            Element = (image, name) => image.Find(ElementRecognition.Get(name, image)),
            OcrOne = (image, ro) => { using var result = image.Find(ro); return result.Text; },
            OcrMany = (image, ro) => image.FindMulti(ro), Click = (image, x, y) => image.ClickTo(x, y),
            ScrollDown = (image, roi) => { image.MoveTo(roi.X + roi.Width / 2, roi.Y + roi.Height / 2); InputHub.Foreground.Mouse.VerticalScroll(-3); },
            ResetTop = async token =>
            {
                await Task.Delay(50, token); GameCaptureRegion.GameRegion1080PPosClick(700, 125);
                await Task.Delay(50, token); InputHub.Foreground.Mouse.LeftButtonDown();
                try { await Task.Delay(450, token); } finally { InputHub.Foreground.Mouse.LeftButtonUp(); }
                await Task.Delay(100, token);
            },
            Delay = (ms, token) => TaskControl.Delay(ms, token), ReturnMain = token => new ReturnMainUiTask().Start(token),
            Confirm = (image, roi) => Bv.ClickWhiteConfirmButton(image, roi), Prepare = () => CheckAndSleep(0),
            SameTarget = () => runtime != null && ReferenceEquals(TaskContext.Instance().Runtime, runtime) && runtime.Window.Handle == window,
            ClearDrawings = () => TaskContext.Instance().Runtime?.MaskWindowDrawingBoard.ClearAll(),
            ClearCombatScenes = () => RunnerContext.Instance.ClearCombatScenes()
        };
    }
}
