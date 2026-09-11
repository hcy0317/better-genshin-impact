using System;

namespace BetterGenshinImpact.GameTask.Common.Ui;

internal enum UiTarget { Main, Overworld, DomainMain, Party, PartyList, PartyOrMain, Menu, Crafting }
internal enum UiAction { Escape, RequestDomainExit, ConfirmDomainExit, SelectParty, ApplyParty, OpenMenu, OpenMail, ClaimMail, ReviveParty }

/// <summary>同一次截图的特征证据；主HUD与秘境上下文是不同维度。</summary>
internal sealed record UiSnapshot(long FrameId)
{
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

    public bool MainReady => MainHud && !BigMap && !Party && !PartyList && !Talk && !Prompt
        && !Revive && !FullPartyDefeat && !Closable && !ExitDoor && !BlackConfirm && !MenuBack && !Crafting && !Handbook;
    public bool MapReady => BigMap && !Party && !PartyList && !Talk && !Prompt && !Revive
        && !FullPartyDefeat && !InDomain && !ExitDoor && !BlackConfirm && !MenuBack && !Handbook;
    public bool CanEscape => !FullPartyDefeat && !MainReady && (BigMap || Party || PartyList || Talk || Prompt || Revive || Closable || ExitDoor || MenuBack || Handbook);
    public bool Matches(UiTarget target) => FrameId > 0 && !FullPartyDefeat && target switch
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
        | (InDomain ? 128 : 0) | (Closable ? 256 : 0) | (ExitDoor ? 512 : 0) | (BlackConfirm ? 1024 : 0) | (MenuBack ? 2048 : 0) | (Crafting ? 4096 : 0) | (Handbook ? 8192 : 0) | (FullPartyDefeat ? 16384 : 0);
    public string Describe() => $"hud={MainHud},map={BigMap},party={Party},list={PartyList},talk={Talk},prompt={Prompt},revive={Revive},domain={InDomain},closable={Closable},exitDoor={ExitDoor},blackConfirm={BlackConfirm},menuBack={MenuBack},crafting={Crafting},handbook={Handbook},fullPartyDefeat={FullPartyDefeat}";
}
