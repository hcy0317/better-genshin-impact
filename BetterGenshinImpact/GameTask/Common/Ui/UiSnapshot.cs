using System;
using Fischless.GameCapture;
using BetterGenshinImpact.GameTask.Common.BgiVision;

namespace BetterGenshinImpact.GameTask.Common.Ui;

internal enum UiTarget { Main, Overworld, DomainMain, Party, PartyList, PartyOrMain, Menu, Crafting }
internal enum UiAction { Escape, RequestDomainExit, ConfirmDomainExit, SelectParty, ApplyParty, OpenMenu, OpenMail, ClaimMail, ReviveParty, OpenParty, DismissDomainTip, DismissReward }

internal enum UiReadinessKind { Ready, TemporarilyUnavailable, Unknown, Terminal }
internal readonly record struct UiReadiness(UiReadinessKind Kind, string Reason, bool CanProbe = false);
internal readonly record struct UiWorldObservation(bool Controlled, bool LowHp, bool PartyRejected)
{
    public bool OrdinaryAvatarHud { get; init; }
    public bool Transformed { get; init; }
    public bool ControlObserved { get; init; }
    public bool KeyboardBreakout { get; init; }
    public MotionStatus Motion { get; init; }
}

/// <summary>同一次截图的特征证据；主HUD与秘境上下文是不同维度。</summary>
internal sealed record UiSnapshot(long FrameId)
{
    internal static readonly TimeSpan RecoveryMaximumAge = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan CombatMaximumAge = TimeSpan.FromMilliseconds(150);
    private TimeProvider? EvidenceClock { get; init; }
    private TimeSpan MaximumAge { get; init; }
    private bool MeetsInputFence { get; init; } = true;
    public CaptureFrameStamp SourceStamp { get; private init; }
    public bool SourceBound => EvidenceClock != null;
    // 未绑定的特征用于逻辑回放；原生驱动必须绑定，未知来源不能授予输入。
    public bool HasUsableEvidence => MeetsInputFence && (SourceBound
        ? SourceStamp.IsFresh(EvidenceClock!, MaximumAge)
        : FrameId > 0);

    public UiSnapshot AfterInput(CaptureFrameFence fence) => this with
    { MeetsInputFence = SourceBound && fence.Accepts(SourceStamp) };

    public UiSnapshot WithSource(CaptureFrameStamp source, TimeProvider clock, TimeSpan maximumAge) => this with
    {
        SourceStamp = source, FrameId = source.Sequence, CapturedAt = source.CapturedAt,
        EvidenceClock = clock, MaximumAge = maximumAge
    };

    public bool IsAfter(UiSnapshot earlier) => SourceBound || earlier.SourceBound
        ? SourceBound && earlier.SourceBound && SourceStamp.IsAfter(earlier.SourceStamp)
        : FrameId > earlier.FrameId;

    public DateTimeOffset CapturedAt { get; init; }
    public bool MainHud { get; init; }
    public bool BigMap { get; init; }
    public bool Party { get; init; }
    public bool PartyList { get; init; }
    public bool Talk { get; init; }
    public bool Prompt { get; init; }
    public bool Revive { get; init; }
    public bool FullPartyDefeat { get; init; }
    public bool InDomain { get; init; }
    public bool Closable { get; init; }
    public bool ExitDoor { get; init; }
    public bool BlackConfirm { get; init; }
    public bool MenuBack { get; init; }
    public bool Crafting { get; init; }
    public bool Handbook { get; init; }
    public bool TimeSetting { get; init; }
    public bool Cannon { get; init; }
    public RewardObservation Reward { get; init; }
    public bool CanDismissReward => HasUsableEvidence && Reward.IsFor(SourceStamp) && Reward.CanDismiss &&
        !Prompt && !Revive && !FullPartyDefeat && !BlackConfirm && !Talk && !Party && !PartyList &&
        !Crafting && !Cannon && !TimeSetting && !MenuBack && !ExitDoor && !DomainExit.Visible && !DomainTip.IsCandidate;
    public DomainExitObservation DomainExit { get; init; }
    public bool CanConfirmDomainExit => HasUsableEvidence && DomainExit.IsFor(SourceStamp) && MainHud && InDomain && BlackConfirm &&
        !Revive && !FullPartyDefeat && !BigMap && !Party && !PartyList && !Talk && !MenuBack && !Crafting && !Handbook && !TimeSetting && !Cannon;
    public DomainTipObservation DomainTip { get; init; }
    public bool CanDismissDomainTip => HasUsableEvidence && DomainTip.IsFor(SourceStamp) && DomainTip.CanDismiss &&
        !BigMap && !Party && !PartyList && !Talk && !Prompt && !Revive && !FullPartyDefeat && !BlackConfirm &&
        !MenuBack && !Crafting && !Handbook && !TimeSetting && !Cannon && !ExitDoor && !Closable && !DomainExit.Visible;
    public UiWorldObservation? World { get; init; }

    public UiReadiness PartyEntryReadiness()
    {
        if (!HasUsableEvidence) return new(UiReadinessKind.Unknown, "source-unavailable");
        if (FullPartyDefeat || Revive) return new(UiReadinessKind.TemporarilyUnavailable, "revive-required");
        if (Matches(UiTarget.Party)) return new(UiReadinessKind.Ready, "party-visible");
        if (!MainReady || World is not { } world) return new(UiReadinessKind.Unknown, "world-not-observed");
        if (InDomain) return new(UiReadinessKind.TemporarilyUnavailable, "domain-context");
        if (world.Controlled) return new(UiReadinessKind.TemporarilyUnavailable, "control-interrupted");
        if (world.LowHp) return new(UiReadinessKind.TemporarilyUnavailable, "low-hp");
        if (world.PartyRejected) return new(UiReadinessKind.TemporarilyUnavailable, "party-rejected");
        // 只有进入菜单后的新帧才能证明就绪；当前只允许一次无消费探测。
        return new(UiReadinessKind.Unknown, "awaiting-party-page", CanProbe: true);
    }

    public bool MainReady => HasUsableEvidence && MainHud && !BigMap && !Party && !PartyList && !Talk && !Prompt
        && !Revive && !FullPartyDefeat && !Closable && !ExitDoor && !BlackConfirm && !MenuBack && !Crafting && !Handbook && !TimeSetting && !Cannon && !DomainTip.IsCandidate && !DomainExit.Visible && !Reward.IsCandidate;
    public bool MapReady => HasUsableEvidence && BigMap && !Party && !PartyList && !Talk && !Prompt && !Revive
        && !FullPartyDefeat && !InDomain && !ExitDoor && !BlackConfirm && !MenuBack && !Handbook && !TimeSetting && !Reward.IsCandidate;
    // Domain-tip words alone do not grant Escape. A reward candidate blocks
    // background-page input until the overlay is positively identified or gone.
    public bool CanEscape => HasUsableEvidence && !FullPartyDefeat && !Reward.IsCandidate && !MainReady && (BigMap || Party || PartyList || Talk || Prompt || Revive || Closable || ExitDoor || MenuBack || Handbook || TimeSetting || Cannon || CanConfirmDomainExit);
    public bool Matches(UiTarget target) => HasUsableEvidence && !FullPartyDefeat && !Reward.IsCandidate && target switch
    {
        UiTarget.Main => MainReady,
        UiTarget.Overworld => MainReady && !InDomain,
        UiTarget.DomainMain => MainReady && InDomain,
        // 编队页自身的选择/出战按钮也使用黑色确认图标；弹窗由独立的 Prompt 等证据否决。
        UiTarget.Party => Party && !PartyList && !BigMap && !Talk && !Prompt && !Revive && !ExitDoor && !MenuBack,
        UiTarget.PartyList => PartyList && !BigMap && !Talk && !Prompt && !Revive && !ExitDoor && !MenuBack,
        UiTarget.PartyOrMain => Matches(UiTarget.Party) || MainReady,
        UiTarget.Menu => MenuBack && !BigMap && !Party && !PartyList && !Talk && !Prompt && !Revive && !BlackConfirm,
        UiTarget.Crafting => Crafting && !Prompt && !Revive && !BigMap && !Party && !PartyList && !Talk,
        _ => false
    };

    public int Signature => (MainHud ? 1 : 0) | (BigMap ? 2 : 0) | (Party ? 4 : 0)
        | (PartyList ? 8 : 0) | (Talk ? 16 : 0) | (Prompt ? 32 : 0) | (Revive ? 64 : 0)
        | (InDomain ? 128 : 0) | (Closable ? 256 : 0) | (ExitDoor ? 512 : 0) | (BlackConfirm ? 1024 : 0) | (MenuBack ? 2048 : 0) | (Crafting ? 4096 : 0) | (Handbook ? 8192 : 0) | (FullPartyDefeat ? 16384 : 0) | (Cannon ? 32768 : 0)
        | (DomainTip.IsCandidate ? 65536 : 0) | (DomainTip.CanDismiss ? 131072 : 0) | (TimeSetting ? 262144 : 0) | (DomainExit.Visible ? 524288 : 0)
        | (Reward.IsCandidate ? 1048576 : 0) | (CanDismissReward ? 2097152 : 0);
    public string Describe() => $"hud={MainHud},map={BigMap},party={Party},list={PartyList},talk={Talk},prompt={Prompt},revive={Revive},domain={InDomain},closable={Closable},exitDoor={ExitDoor},blackConfirm={BlackConfirm},menuBack={MenuBack},crafting={Crafting},handbook={Handbook},timeSetting={TimeSetting},cannon={Cannon},reward={Reward.IsCandidate},rewardDismiss={CanDismissReward},domainTip={DomainTip.IsCandidate},domainTipDismiss={CanDismissDomainTip},domainExit={DomainExit.Visible},fullPartyDefeat={FullPartyDefeat},partyReadiness={PartyEntryReadiness().Reason},ordinaryAvatar={World?.OrdinaryAvatarHud},transformed={World?.Transformed},controlObserved={World?.ControlObserved},motion={World?.Motion}";
}
