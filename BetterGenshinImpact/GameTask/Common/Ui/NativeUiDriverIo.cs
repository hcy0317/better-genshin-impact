using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.AutoDomain;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Helpers;
using Microsoft.Extensions.Localization;
using OpenCvSharp;
using Vanara.PInvoke;

namespace BetterGenshinImpact.GameTask.Common.Ui;

// External image/OCR/input boundaries only. Scene and action rules remain in the driver.
internal sealed class NativeUiDriverIo
{
    internal Func<ImageRegion> Capture { get; init; } = null!;
    internal Action Focus { get; init; } = null!;
    internal Func<ImageRegion, UiSnapshot> ReadScene { get; init; } = null!;
    internal Func<IOcrService> Ocr { get; init; } = null!;
    internal Func<DomainTipTexts> Texts { get; init; } = null!;
    internal Action<ImageRegion, Rect, Action> Click { get; init; } = null!;
    internal Func<UiAction, ImageRegion, Action, bool> OtherAction { get; init; } = null!;
    internal Func<int, CancellationToken, Task> Delay { get; init; } = null!;
    internal TimeProvider Clock { get; init; } = null!;
    internal Func<IDisposable> BeginExclusive { get; init; } = null!;
    internal Func<bool> NetworkAvailable { get; init; } = () => true;

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Capture);
        ArgumentNullException.ThrowIfNull(Focus);
        ArgumentNullException.ThrowIfNull(ReadScene);
        ArgumentNullException.ThrowIfNull(Ocr);
        ArgumentNullException.ThrowIfNull(Texts);
        ArgumentNullException.ThrowIfNull(Click);
        ArgumentNullException.ThrowIfNull(OtherAction);
        ArgumentNullException.ThrowIfNull(Delay);
        ArgumentNullException.ThrowIfNull(Clock);
        ArgumentNullException.ThrowIfNull(BeginExclusive);
    }

    internal static NativeUiDriverIo CreateNative(bool inspectWorld)
    {
        // Must precede exclusive acquisition, localization and all service lookups.
        ApplicationHostBootstrapGuard.EnsureAllowed();
        return new()
        {
            Capture = () => TaskControl.CaptureToRectArea(),
            NetworkAvailable = System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable,
            Focus = () => TaskControl.CheckAndSleep(0),
            ReadScene = image => NativeUiDriver.ReadNativeScene(image, inspectWorld),
            Ocr = () => OcrFactory.Paddle,
            Texts = ReadNativeTexts,
            Click = (image, bounds, admission) =>
            {
                using var target = image.DeriveCrop(bounds);
                DomainTipClick.Run(admission, target.Move,
                    () => BetterGenshinImpact.Core.Input.InputHub.Foreground.Mouse.LeftButtonDown(),
                    () => BetterGenshinImpact.Core.Input.InputHub.Foreground.Mouse.LeftButtonUp(), Thread.Sleep);
            },
            OtherAction = SendNativeAction,
            Delay = TaskControl.Delay,
            Clock = TimeProvider.System,
            BeginExclusive = () => AvatarRecognition.BeginExclusiveOperation()
        };
    }

    private static DomainTipTexts ReadNativeTexts()
    {
        var localizer = App.GetService<IStringLocalizer<AutoDomainTask>>()
            ?? throw new InvalidOperationException("Domain tip localization is unavailable.");
        var culture = new CultureInfo(TaskContext.Instance().Config.OtherConfig.GameCultureInfoName);
        return new(localizer.WithCultureGet(culture, "地脉异常"),
            localizer.WithCultureGet(culture, "点击任意位置关闭"));
    }

    private static bool SendNativeAction(UiAction action, ImageRegion image, Action admission)
    {
        switch (action)
        {
            case UiAction.DetachClimb:
                var dropKey = GIActions.Drop.ToActionKey().ToVK();
                UiEscapeInput.Run(admission,
                    () => BetterGenshinImpact.Core.Input.InputHub.Foreground.Keyboard.KeyDown(dropKey),
                    () => BetterGenshinImpact.Core.Input.InputHub.Foreground.Keyboard.KeyUp(dropKey), Thread.Sleep, "攀爬脱离");
                return true;
            case UiAction.OpenParty:
                BetterGenshinImpact.Core.Input.InputHub.Foreground.SimulateActionPulse(GIActions.OpenPartySetupScreen);
                return true;
            case UiAction.Escape:
            case UiAction.EscapeProbe:
                UiEscapeInput.Run(admission,
                    () => BetterGenshinImpact.Core.Input.InputHub.Foreground.Keyboard.KeyDown(User32.VK.VK_ESCAPE),
                    () => BetterGenshinImpact.Core.Input.InputHub.Foreground.Keyboard.KeyUp(User32.VK.VK_ESCAPE), Thread.Sleep);
                return true;
            case UiAction.RequestDomainExit:
                BetterGenshinImpact.Core.Input.InputHub.Foreground.Keyboard.KeyPress(User32.VK.VK_ESCAPE);
                return true;
            default:
                throw new InvalidOperationException("Unsupported native UI action.");
        }
    }
}
