using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.Model.Area;
using Microsoft.Extensions.Logging;

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
    IDisposable BeginExclusive(bool allowPassiveObservation);
    ImageRegion? Capture();
    bool IsCombatHud(ImageRegion frame);
    bool IsMainUi(ImageRegion frame);
    int ReadActive(ImageRegion frame, AvatarActiveCheckContext context);
    bool? IsActorActive(NativeCombatActor actor, ImageRegion frame);
    double ReadSkillCooldown(NativeCombatActor actor, ImageRegion frame);
    bool IsSkillReady(NativeCombatActor actor, ImageRegion frame, double cooldown);
    BurstObservation ReadBurst(ImageRegion frame, bool active);
    bool ReadLowHp(ImageRegion frame);
    HashSet<int> ReadSideBurstReady(ImageRegion frame);
    bool TryGetKnownSkillCooldown(string actor, out double cooldown);
    void ConfirmSkill(NativeCombatActor actor, double cooldown, DateTime inputAtUtc);
    Task WaitSkillCooldown(NativeCombatActor actor, CancellationToken ct);
    void SelectActor(int index, CancellationToken ct);
    void WaitForSelection(int milliseconds, CancellationToken ct);
    bool OnSelectionMismatch(NativeCombatActor actor, int attempt, int attempts, int observed, CancellationToken ct);
    void ResolveSelectionRecovery(NativeCombatActor actor, AvatarSelectionProtocol.Result<ImageRegion> selection, CancellationToken ct);
    void CheckDefeated(ImageRegion frame, CancellationToken ct);
    void SendSkill(NativeCombatActor actor, bool hold);
    void SendBurst(NativeCombatActor actor);
    void ExecutePrimitive(NativeCombatActor actor, CombatCommand command);
    void ReleaseInput();
    Task DelayAsync(int milliseconds, CancellationToken ct);
}
