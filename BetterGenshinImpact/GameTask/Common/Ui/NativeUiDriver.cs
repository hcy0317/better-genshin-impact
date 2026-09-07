using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Model.Area;
using Vanara.PInvoke;

namespace BetterGenshinImpact.GameTask.Common.Ui;

/// <summary>仅在关键UI边界抓图，所有图像在单次读取/输入调用结束前释放。</summary>
internal sealed class NativeUiDriver : IUiDriver, IDisposable
{
    private static long _frameSequence;
    private readonly IDisposable _exclusive = AvatarRecognition.BeginExclusiveOperation();
    private bool _disposed;

    public UiSnapshot Capture()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        UiOperation.Current?.Check();
        using var image = TaskControl.CaptureToRectArea();
        return Read(image);
    }

    internal static UiSnapshot Read(ImageRegion image)
    {
        bool Has(string name)
        {
            using var match = image.Find(ElementRecognition.Get(name, image));
            return match.IsExist();
        }
        using var menuBack = image.Find(RecognitionAssets.Get("UseRedeemCode", "MenuBack", image));
        return new UiSnapshot(Interlocked.Increment(ref _frameSequence))
        {
            CapturedAt = DateTimeOffset.UtcNow,
            MainHud = Has("PaimonMenu") || Has("FriendChat"),
            BigMap = Bv.IsInBigMapUi(image),
            Party = Bv.IsInPartyViewUi(image),
            PartyList = Has("PartyBtnDelete"),
            Talk = Bv.IsInTalkUi(image),
            Prompt = Bv.IsInPromptDialog(image),
            Revive = Bv.IsInRevivePrompt(image),
            InDomain = Bv.IsInDomainIncludingRevivePrompt(image),
            Closable = Bv.IsInAnyClosableUi(image),
            ExitDoor = Has("BtnExitDoor"),
            BlackConfirm = Has("BtnBlackConfirm"),
            MenuBack = menuBack.IsExist()
        };
    }

    public Task DelayAsync(int milliseconds, CancellationToken ct) => TaskControl.Delay(milliseconds, ct);

    public Task<bool> ActAsync(UiAction action, UiSnapshot observed, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        UiOperation.Current?.Check();
        // 输入前恢复焦点；该托管等待同样受当前UI预算约束。
        TaskControl.CheckAndSleep(0);
        using var image = TaskControl.CaptureToRectArea();
        var current = Read(image);
        if (UiOperation.Current is { } operation && Enum.TryParse<UiTarget>(operation.Expected, out var target))
            operation.Observe(current, target, "pre-input");
        ct.ThrowIfCancellationRequested();
        UiOperation.Current?.Check();
        switch (action)
        {
            case UiAction.Escape when observed.CanEscape && current.CanEscape:
            case UiAction.RequestDomainExit when observed.Matches(UiTarget.DomainMain) && current.Matches(UiTarget.DomainMain):
                Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_ESCAPE);
                return Task.FromResult(true);
            case UiAction.ConfirmDomainExit when observed.Prompt && observed.BlackConfirm && !observed.Revive
                && current.Prompt && current.BlackConfirm && !current.Revive:
                return Task.FromResult(Bv.ClickBlackConfirmButton(image));
            default:
                return Task.FromResult(false);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _exclusive.Dispose();
    }
}
