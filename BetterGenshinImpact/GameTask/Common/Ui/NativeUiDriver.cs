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
    private readonly KnownHandbookFrame _handbook = new();
    private bool _disposed;

    internal NativeUiDriver(bool inspectWorld = false) : this(NativeUiDriverIo.CreateNative(inspectWorld)) { }

    internal NativeUiDriver(NativeUiDriverIo io)
    {
        ArgumentNullException.ThrowIfNull(io);
        io.Validate();
        _io = io;
        _exclusive = io.BeginExclusive();
    }

    private UiSnapshot ReadCurrent(ImageRegion image)
    {
        if (_handbook.Matches(image))
            return new UiSnapshot(image.FrameStamp.Sequence) { Handbook = true }
                .WithSource(image.FrameStamp, _io.Clock, UiSnapshot.RecoveryMaximumAge);
        _handbook.Dispose();
        var observed = Read(image, ocr: _io.Ocr(), domainTipTexts: _io.Texts(),
            clock: _io.Clock, readScene: _io.ReadScene,
            inspectReward: UiOperation.Current?.Name == "return-main");
        _handbook.Remember(image, observed);
        return observed;
    }

    public UiSnapshot Capture()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        UiOperation.Current?.Check();
        ImageRegion CaptureMeasured()
        {
            using var measured = UiOperation.Current?.Measure(UiOperationPhase.Capture);
            return _io.Capture();
        }
        using var image = CaptureMeasured();
        var snapshot = ReadCurrent(image);
        return _inputFence is { } fence ? snapshot.AfterInput(fence) : snapshot;
    }

    internal static UiSnapshot Read(ImageRegion image, bool inspectWorld = false, IOcrService? ocr = null,
        ReviveUiDetector? reviveDetector = null, DomainTipTexts? domainTipTexts = null,
        TimeProvider? clock = null, Func<ImageRegion, UiSnapshot>? readScene = null, bool inspectReward = false)
    {
        using var measured = UiOperation.Current?.Measure(UiOperationPhase.SceneRecognition);
        var snapshot = readScene == null ? ReadNativeScene(image, inspectWorld, ocr, reviveDetector) : readScene(image);
        // A reward overlay can leave background HUD/map/handbook features visible.
        // Check it before recovery chooses a target or input, regardless of those features.
        if (inspectReward)
            snapshot = snapshot with { Reward = RewardUiReader.Read(image, ocr ?? OcrFactory.Paddle) };
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
        var revive = reviveDetector?.Observe(image) ?? Bv.ReadReviveObservation(image);
        var snapshot = new UiSnapshot(image.FrameStamp.Sequence)
        {
            MainHud = Has("PaimonMenu") || Has("FriendChat"),
            BigMap = Bv.IsInBigMapUi(image),
            Party = Bv.IsInPartyViewUi(image),
            PartyList = Has("PartyBtnDelete"),
            Talk = Bv.IsInTalkUi(image),
            Prompt = Bv.IsInPromptDialog(image),
            Revive = revive.State != ReviveUiState.None,
            FullPartyDefeat = revive.State == ReviveUiState.FullPartyDefeat,
            ReviveButtonBounds = revive.ButtonBounds,
            DefeatOverlay = revive.DefeatOverlay,
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
            var transformed = SaurianUiReader.IsKnownTransformation(image);
            var ordinary = !transformed && (reviveDetector?.IsCombatHud(image) ?? Bv.IsCombatHud(image));
            var messages = image.FindMulti(RecognitionObject.Ocr(image.Width * .15, image.Height * .2,
                image.Width * .7, image.Height * .5), ocrService: reader);
            try
            {
                var rejected = messages.Any(message =>
                    message.Text.Contains("当前状态不可进行队伍配置", StringComparison.Ordinal) ||
                    message.Text.Contains("战斗中无法", StringComparison.Ordinal));
                snapshot = snapshot with { World = new(control.KeyboardBreakoutRequested ||
                    control.Motion is MotionStatus.Fly or MotionStatus.Climb,
                    Bv.CurrentAvatarIsLowHp(image, image.Height / 1080d), rejected)
                    { OrdinaryAvatarHud = ordinary, Transformed = transformed,
                        ControlObserved = control.IsObserved, KeyboardBreakout = control.KeyboardBreakoutRequested,
                        Motion = control.Motion } };
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
        // 兜底探测只允许在"没有任何已识别界面"的空转状态发出，且同样要求后继新帧仍然空转。
        var escapeProbeProposed = action == UiAction.EscapeProbe && observed.SourceBound && observed.HasUsableEvidence &&
            observed.SourceStamp.IsFresh(_io.Clock, UiSnapshot.RecoveryMaximumAge) && UiStallEscapeProbe.IsUnrecognizedStall(observed);
        if (action == UiAction.EscapeProbe && !escapeProbeProposed) return Task.FromResult(false);
        var domainExitProposed = action == UiAction.ConfirmDomainExit && observed.SourceBound &&
            observed.CanConfirmDomainExit && observed.SourceStamp.IsFresh(_io.Clock, UiSnapshot.RecoveryMaximumAge);
        if (action == UiAction.ConfirmDomainExit && !domainExitProposed) return Task.FromResult(false);
        using var image = _io.Capture();
        using var fallbackInput = DiagnosticInputAttempt.Current == null ? new DiagnosticInputAttempt(_io.Clock) : null;
        try
        {
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
            case UiAction.DetachClimb when observed.CanDetachClimb && current.CanDetachClimb && current.IsAfter(observed):
                void AdmitDetachInput()
                {
                    ct.ThrowIfCancellationRequested();
                    UiOperation.Current?.Check();
                    if (!current.CanDetachClimb || !current.SourceStamp.IsFresh(_io.Clock, UiSnapshot.RecoveryMaximumAge))
                        throw new InvalidOperationException("攀爬脱离来源在输入前失效。");
                }
                return Task.FromResult(Completed(_io.OtherAction(action, image, AdmitDetachInput)));
            case UiAction.DismissReward when observed.CanDismissReward && current.CanDismissReward && current.IsAfter(observed) &&
                observed.SourceStamp.IsFresh(_io.Clock, UiSnapshot.RecoveryMaximumAge) &&
                current.SourceStamp.IsFresh(_io.Clock, UiSnapshot.RecoveryMaximumAge):
                void AdmitRewardInput()
                {
                    ct.ThrowIfCancellationRequested();
                    UiOperation.Current?.Check();
                    if (!observed.SourceStamp.IsFresh(_io.Clock, UiSnapshot.RecoveryMaximumAge) ||
                        !current.SourceStamp.IsFresh(_io.Clock, UiSnapshot.RecoveryMaximumAge))
                        throw new InvalidOperationException("Reward source expired before native input.");
                }
                _io.Click(image, current.Reward.CloseBounds, AdmitRewardInput);
                return Task.FromResult(Completed(true));
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
            case UiAction.ReviveParty when observed.FullPartyDefeat && current.FullPartyDefeat && current.DefeatOverlay &&
                current.IsAfter(observed) && UsableReviveButton(current, image):
                void AdmitReviveInput()
                {
                    ct.ThrowIfCancellationRequested();
                    UiOperation.Current?.Check();
                    if (!current.SourceStamp.IsFresh(_io.Clock, UiSnapshot.RecoveryMaximumAge))
                        throw new InvalidOperationException("Revive source expired before native input.");
                }
                _io.Click(image, current.ReviveButtonBounds, AdmitReviveInput);
                return Task.FromResult(Completed(true));
            case UiAction.Escape when escapeProposed && current.IsAfter(observed) && current.CanEscape:
                void AdmitEscapeInput()
                {
                    ct.ThrowIfCancellationRequested();
                    UiOperation.Current?.Check();
                    if (!current.CanEscape || !current.SourceStamp.IsFresh(_io.Clock, UiSnapshot.RecoveryMaximumAge))
                        throw new InvalidOperationException("Escape source expired before native input.");
                }
                return Task.FromResult(Completed(_io.OtherAction(action, image, AdmitEscapeInput)));
            case UiAction.EscapeProbe when escapeProbeProposed && current.IsAfter(observed) && UiStallEscapeProbe.IsUnrecognizedStall(current):
                void AdmitEscapeProbeInput()
                {
                    ct.ThrowIfCancellationRequested();
                    UiOperation.Current?.Check();
                    if (!UiStallEscapeProbe.IsUnrecognizedStall(current) ||
                        !current.SourceStamp.IsFresh(_io.Clock, UiSnapshot.RecoveryMaximumAge))
                        throw new InvalidOperationException("兜底 Escape 来源在原生输入前失效。");
                }
                return Task.FromResult(Completed(_io.OtherAction(action, image, AdmitEscapeProbeInput)));
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
        catch (Exception error)
        {
            try
            {
                var receipt = DiagnosticInputAttempt.Current!.Complete(false, error);
                DiagnosticEvidenceScope.Current?.RequestWindowFromFrame("ui-input:" + receipt.RequestId.ToString("N"),
                    "ui-input-failed", image, $"action={action}; original input exception preserved",
                    fields: new System.Collections.Generic.Dictionary<string, string> { ["nativeInput"] = receipt.Describe() });
            }
            catch { /* 原图取证失败不改变输入异常。 */ }
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _handbook.Dispose();
        _exclusive.Dispose();
    }

    private static bool UsableReviveButton(UiSnapshot current, ImageRegion image)
    {
        var bounds = current.ReviveButtonBounds;
        return bounds.Width > 0 && bounds.Height > 0 && bounds.X >= image.Width / 4 &&
            bounds.Right <= image.Width * 3 / 4 && bounds.Y >= image.Height * 2 / 3 &&
            bounds.Bottom <= image.Height;
    }
}
