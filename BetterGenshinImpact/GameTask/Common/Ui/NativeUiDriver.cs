using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using System.Linq;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using Vanara.PInvoke;

namespace BetterGenshinImpact.GameTask.Common.Ui;

/// <summary>仅在关键UI边界抓图，所有图像在单次读取/输入调用结束前释放。</summary>
internal sealed class NativeUiDriver : IUiDriver, IDisposable
{
    private readonly NativeUiDriverIo _io;
    private readonly IDisposable _exclusive;
    private CaptureFrameFence? _inputFence;
    private bool _disposed;

    internal NativeUiDriver(bool inspectWorld = false) : this(NativeUiDriverIo.CreateNative(inspectWorld)) { }

    internal NativeUiDriver(NativeUiDriverIo io)
    {
        ArgumentNullException.ThrowIfNull(io);
        io.Validate();
        _io = io;
        _exclusive = io.BeginExclusive();
    }

    private UiSnapshot ReadCurrent(ImageRegion image) => Read(image, ocr: _io.Ocr(),
        domainTipTexts: _io.Texts(), clock: _io.Clock, readScene: _io.ReadScene);

    public UiSnapshot Capture()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        UiOperation.Current?.Check();
        using var image = _io.Capture();
        var snapshot = ReadCurrent(image);
        return _inputFence is { } fence ? snapshot.AfterInput(fence) : snapshot;
    }

    internal static UiSnapshot Read(ImageRegion image, bool inspectWorld = false, IOcrService? ocr = null,
        ReviveUiDetector? reviveDetector = null, DomainTipTexts? domainTipTexts = null,
        TimeProvider? clock = null, Func<ImageRegion, UiSnapshot>? readScene = null)
    {
        var snapshot = readScene == null ? ReadNativeScene(image, inspectWorld, ocr, reviveDetector) : readScene(image);
        // Legacy static callers need not initialize localization services. Native
        // driver instances always supply the existing game-culture text pair.
        if (domainTipTexts is { } texts)
            snapshot = snapshot with { DomainTip = DomainTipUiReader.Read(image, ocr ?? OcrFactory.Paddle, texts) };
        return snapshot.WithSource(image.FrameStamp, clock ?? TimeProvider.System, UiSnapshot.RecoveryMaximumAge);
    }

    internal static UiSnapshot ReadNativeScene(ImageRegion image, bool inspectWorld = false, IOcrService? ocr = null,
        ReviveUiDetector? reviveDetector = null)
    {
        bool Has(string name)
        {
            using var match = image.Find(ElementRecognition.Get(name, image));
            return match.IsExist();
        }
        using var menuBack = image.Find(RecognitionAssets.Get("UseRedeemCode", "MenuBack", image));
        var revive = reviveDetector?.Read(image) ?? Bv.ReadReviveState(image);
        var snapshot = new UiSnapshot(image.FrameStamp.Sequence)
        {
            MainHud = Has("PaimonMenu") || Has("FriendChat"),
            BigMap = Bv.IsInBigMapUi(image),
            Party = Bv.IsInPartyViewUi(image),
            PartyList = Has("PartyBtnDelete"),
            Talk = Bv.IsInTalkUi(image),
            Prompt = Bv.IsInPromptDialog(image),
            Revive = revive != ReviveUiState.None,
            FullPartyDefeat = revive == ReviveUiState.FullPartyDefeat,
            InDomain = Bv.IsInDomainIncludingRevivePrompt(image),
            Closable = Bv.IsInAnyClosableUi(image),
            ExitDoor = Has("BtnExitDoor"),
            BlackConfirm = Has("BtnBlackConfirm"),
            MenuBack = menuBack.IsExist()
        };
        if (snapshot.MainHud && snapshot.InDomain && snapshot.BlackConfirm && !snapshot.Revive && !snapshot.FullPartyDefeat)
            snapshot = snapshot with { DomainExit = DomainExitUiReader.Read(image, ocr ?? OcrFactory.Paddle) };
        if (!snapshot.MainHud && !snapshot.CanEscape && !snapshot.BlackConfirm)
            snapshot = snapshot with { Cannon = CannonUiReader.Read(image, ocr ?? OcrFactory.Paddle).CanExit };
        if (!snapshot.MainHud && !snapshot.CanEscape && !snapshot.BlackConfirm)
            snapshot = snapshot with { Handbook = HandbookUiRecognition.Read(image, ocr) };
        if (!snapshot.MainHud && !snapshot.CanEscape && !snapshot.BlackConfirm)
            snapshot = snapshot with { TimeSetting = TimeSettingUiReader.Read(image, ocr).Visible };
        if (inspectWorld && snapshot.MainReady && image.Width * 9 == image.Height * 16)
        {
            var reader = ocr ?? OcrFactory.Paddle;
            var control = CombatMotionReader.ReadControl(image, true, reader);
            var messages = image.FindMulti(RecognitionObject.Ocr(image.Width * .15, image.Height * .2,
                image.Width * .7, image.Height * .5), ocrService: reader);
            try
            {
                var rejected = messages.Any(message =>
                    message.Text.Contains("当前状态不可进行队伍配置", StringComparison.Ordinal) ||
                    message.Text.Contains("战斗中无法", StringComparison.Ordinal));
                snapshot = snapshot with { World = new(control.KeyboardBreakoutRequested ||
                    control.Motion is MotionStatus.Fly or MotionStatus.Climb,
                    Bv.CurrentAvatarIsLowHp(image, image.Height / 1080d), rejected) };
            }
            finally { foreach (var message in messages) message.Dispose(); }
        }
        return snapshot;
    }

    public Task DelayAsync(int milliseconds, CancellationToken ct) => _io.Delay(milliseconds, ct);

    public void MarkInputCompleted(UiSnapshot before) =>
        _inputFence = new(before.SourceStamp, _io.Clock.GetTimestamp());

    public Task<bool> ActAsync(UiAction action, UiSnapshot observed, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        UiOperation.Current?.Check();
        // 输入前恢复焦点；该托管等待同样受当前UI预算约束。
        _io.Focus();
        ct.ThrowIfCancellationRequested();
        UiOperation.Current?.Check();
        // Escape和专用退出秘境的旧帧只提出意图，最终发送由同会话后继新帧决定。
        // 二次识别不反过来使已准入的意图过期；其他动作保持原双帧约束。
        var escapeProposed = action == UiAction.Escape && observed.SourceBound && observed.HasUsableEvidence &&
            observed.SourceStamp.IsFresh(_io.Clock, UiSnapshot.RecoveryMaximumAge) && observed.CanEscape;
        if (action == UiAction.Escape && !escapeProposed) return Task.FromResult(false);
        var domainExitProposed = action == UiAction.ConfirmDomainExit && observed.SourceBound &&
            observed.CanConfirmDomainExit && observed.SourceStamp.IsFresh(_io.Clock, UiSnapshot.RecoveryMaximumAge);
        if (action == UiAction.ConfirmDomainExit && !domainExitProposed) return Task.FromResult(false);
        using var image = _io.Capture();
        var current = ReadCurrent(image);
        if (UiOperation.Current is { } operation && Enum.TryParse<UiTarget>(operation.Expected, out var target))
            operation.Observe(current, target, "pre-input");
        ct.ThrowIfCancellationRequested();
        UiOperation.Current?.Check();
        if (!observed.SourceBound || (!escapeProposed && !domainExitProposed && !observed.HasUsableEvidence) || !current.HasUsableEvidence ||
            observed.SourceStamp.SessionId != current.SourceStamp.SessionId ||
            (_inputFence is { } fence && !fence.Accepts(current.SourceStamp))) return Task.FromResult(false);
        bool Completed(bool applied)
        {
            if (applied) MarkInputCompleted(current);
            return applied;
        }
        switch (action)
        {
            case UiAction.DismissDomainTip when observed.CanDismissDomainTip && current.CanDismissDomainTip && current.IsAfter(observed) &&
                observed.SourceStamp.IsFresh(_io.Clock, UiSnapshot.RecoveryMaximumAge) &&
                current.SourceStamp.IsFresh(_io.Clock, UiSnapshot.RecoveryMaximumAge):
                void AdmitTipInput()
                {
                    ct.ThrowIfCancellationRequested();
                    UiOperation.Current?.Check();
                    if (!observed.SourceStamp.IsFresh(_io.Clock, UiSnapshot.RecoveryMaximumAge) ||
                        !current.SourceStamp.IsFresh(_io.Clock, UiSnapshot.RecoveryMaximumAge))
                        throw new InvalidOperationException("Domain tip source expired before native input.");
                }
                _io.Click(image, current.DomainTip.CloseBounds, AdmitTipInput);
                return Task.FromResult(Completed(true));
            case UiAction.OpenParty when observed.PartyEntryReadiness().CanProbe && current.PartyEntryReadiness().CanProbe:
                return Task.FromResult(Completed(_io.OtherAction(action, image, () => { })));
            case UiAction.ReviveParty when observed.FullPartyDefeat && current.FullPartyDefeat:
                return Task.FromResult(Completed(_io.OtherAction(action, image, () => { })));
            case UiAction.Escape when escapeProposed && current.IsAfter(observed) && current.CanEscape:
                void AdmitEscapeInput()
                {
                    ct.ThrowIfCancellationRequested();
                    UiOperation.Current?.Check();
                    if (!current.CanEscape || !current.SourceStamp.IsFresh(_io.Clock, UiSnapshot.RecoveryMaximumAge))
                        throw new InvalidOperationException("Escape source expired before native input.");
                }
                return Task.FromResult(Completed(_io.OtherAction(action, image, AdmitEscapeInput)));
            case UiAction.RequestDomainExit when observed.Matches(UiTarget.DomainMain) && current.Matches(UiTarget.DomainMain):
                return Task.FromResult(Completed(_io.OtherAction(action, image, () => { })));
            case UiAction.ConfirmDomainExit when domainExitProposed && current.IsAfter(observed) && current.CanConfirmDomainExit:
                void AdmitDomainExitInput()
                {
                    ct.ThrowIfCancellationRequested();
                    UiOperation.Current?.Check();
                    if (!current.CanConfirmDomainExit || !current.SourceStamp.IsFresh(_io.Clock, UiSnapshot.RecoveryMaximumAge))
                        throw new InvalidOperationException("Domain exit source expired before native input.");
                }
                _io.Click(image, current.DomainExit.ConfirmBounds, AdmitDomainExitInput);
                return Task.FromResult(Completed(true));
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
