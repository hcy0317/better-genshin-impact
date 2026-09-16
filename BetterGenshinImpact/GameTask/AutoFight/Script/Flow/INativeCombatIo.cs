using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.Model.Area;
using Microsoft.Extensions.Logging;
using BetterGenshinImpact.GameTask.Common.BgiVision;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

internal sealed record NativeCombatActor(string Name, int Index);

/// <summary>真实游戏的I/O边界；选择、在途管理、缓存和准入不得由回放另行实现。</summary>
internal interface INativeCombatIo
{
    TimeProvider Clock { get; }
    ILogger Logger { get; }
    CombatInputCoordinator InputCoordinator { get; }
    IReadOnlyList<NativeCombatActor> Actors { get; }
    double LastFinishCheckAge { get; }
    Task PrepareVisionAsync(CancellationToken ct);
    Task PrepareVisionAsync(bool needsBurst, CancellationToken ct) => PrepareVisionAsync(ct);
    Task PrepareVisionAsync(bool needsBurst, ImageRegion frame, CancellationToken ct) => PrepareVisionAsync(needsBurst, ct);
    IDisposable BeginExclusive(bool allowPassiveObservation);
    ImageRegion? Capture();
    bool IsCombatHud(ImageRegion frame);
    CombatControlObservation ReadControl(ImageRegion frame) => default;
    CannonUiObservation ReadCannonScene(ImageRegion frame) => default;
    ICombatHostInputDevice? ControlDevice => null;
    bool IsMainUi(ImageRegion frame);
    int ReadActive(ImageRegion frame, AvatarActiveCheckContext context);
    AvatarLayoutPreparation PrepareActorObservation(ImageRegion frame, AvatarActiveCheckContext context,
        CancellationToken ct) => AvatarLayoutPreparation.NotRequested;
    bool? IsActorActive(NativeCombatActor actor, ImageRegion frame);
    double ReadSkillCooldown(NativeCombatActor actor, ImageRegion frame);
    bool IsSkillReady(NativeCombatActor actor, ImageRegion frame, double cooldown);
    BurstObservation ReadBurst(ImageRegion frame, bool active);
    bool ReadLowHp(ImageRegion frame);
    HashSet<int> ReadSideBurstReady(ImageRegion frame);
    bool TryGetKnownSkillCooldown(string actor, out double cooldown);
    void ConfirmSkill(NativeCombatActor actor, double cooldown, DateTime inputAtUtc);
    Task WaitSkillCooldown(NativeCombatActor actor, CancellationToken ct);
    CombatBattleHostInputResult SelectActor(int index, CombatNativeInputRequest request, CancellationToken ct);
    void WaitForSelection(int milliseconds, CancellationToken ct);
    bool OnSelectionMismatch(NativeCombatActor actor, int attempt, int attempts, int observed, CancellationToken ct);
    void ResolveSelectionRecovery(NativeCombatActor actor, AvatarSelectionProtocol.Result<ImageRegion> selection, CancellationToken ct);
    void CheckDefeated(ImageRegion frame, CancellationToken ct);
    CombatBattleHostInputResult SubmitInput(NativeCombatActor actor, CombatCommand command,
        CombatNativeInputRequest request, Action beforeFirstNative, CancellationToken ct);
    CombatBattleHostInputResult SubmitPathingInput(CombatCommand command, CombatNativeInputRequest request,
        Action beforeFirstNative, CancellationToken ct, CannonUiObservation scene = default) =>
        throw new NotSupportedException("此输入端不支持路径匿名交互");
    void ReleaseInput();
    Task DelayAsync(int milliseconds, CancellationToken ct);
}
