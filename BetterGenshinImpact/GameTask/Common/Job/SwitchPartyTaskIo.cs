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
    internal required Action<ImageRegion, Point2d> MovePointer { get; init; }
    internal required Func<(Action Down, Action Up, Func<bool> Current)> BindLeftButton { get; init; }

    internal async Task DragPageAsync(ImageRegion image, PartyListDragPlan plan, CancellationToken token, Action check)
    {
        var button = BindLeftButton();
        void Admit()
        {
            token.ThrowIfCancellationRequested(); check();
            if (!button.Current()) throw new InvalidOperationException("队伍拖动输入通道已改变");
        }
        Admit();
        if (plan.Source != image.FrameStamp || !image.FrameStamp.IsFresh(Clock, UiSnapshot.RecoveryMaximumAge) ||
            !double.IsFinite(plan.Start.X) || !double.IsFinite(plan.Start.Y) || !double.IsFinite(plan.Finish.X) ||
            !double.IsFinite(plan.Finish.Y) || plan.Finish.X != plan.Start.X || plan.Finish.Y <= plan.Start.Y)
            throw new InvalidOperationException("队伍拖动计划的来源或几何无效");
        async Task Settle()
        {
            Admit();
            if (UiOperation.Current is { } operation && operation.Remaining <= TimeSpan.FromMilliseconds(60))
                throw new TimeoutException("队伍拖动的原期限不足以等待游戏响应");
            await Delay(60, token); Admit();
        }
        MovePointer(image, plan.Start); Admit();
        var held = false; Exception? failure = null;
        try
        {
            Admit(); held = true; button.Down(); Admit();
            await Settle();
            Admit(); MovePointer(image, plan.Finish); Admit();
            await Settle();
        }
        catch (Exception error) { failure = error; }
        finally
        {
            if (held)
            {
                try { Admit(); } catch (Exception error) { failure ??= error; }
                try { button.Up(); } catch (Exception error) { failure ??= error; }
                try { Admit(); } catch (Exception error) { failure ??= error; }
            }
        }
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Admit();
    }
    internal required Func<CancellationToken, Task> ResetTop { get; init; }
    internal required Func<int, CancellationToken, Task> Delay { get; init; }
    internal required Func<CancellationToken, Task> ReturnMain { get; init; }
    internal required Func<ImageRegion, Rect, bool> Confirm { get; init; }
    internal required Action Prepare { get; init; }
    internal required Func<bool> SameTarget { get; init; }
    internal required Func<bool> ForegroundReady { get; init; }
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
            MovePointer = (image, point) => image.MoveTo((int)Math.Round(point.X, MidpointRounding.AwayFromZero), (int)Math.Round(point.Y, MidpointRounding.AwayFromZero)),
            BindLeftButton = () =>
            {
                var mouse = InputHub.Foreground.Mouse;
                return (() => mouse.LeftButtonDown(), () => mouse.LeftButtonUp(),
                    () => ReferenceEquals(InputHub.Foreground.Mouse, mouse));
            },
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
            ForegroundReady = () => runtime != null && Vanara.PInvoke.User32.GetForegroundWindow() == window,
            ClearDrawings = () => TaskContext.Instance().Runtime?.MaskWindowDrawingBoard.ClearAll(),
            ClearCombatScenes = () => RunnerContext.Instance.ClearCombatScenes()
        };
    }
}
