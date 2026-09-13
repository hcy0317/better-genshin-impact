using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Model.Area;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

/// <summary>唯一连接截图/视觉/键鼠及原有角色状态的生产实现，不拥有战斗调度。</summary>
internal sealed class NativeCombatIo(CombatScenes scenes) : INativeCombatIo
{
    private static readonly CombatInputCoordinator Coordinator = new();
    public TimeProvider Clock => TimeProvider.System;
    public ILogger Logger => TaskControl.Logger;
    public CombatInputCoordinator InputCoordinator => Coordinator;
    public IReadOnlyList<NativeCombatActor> Actors => scenes.GetAvatars().Select(avatar => new NativeCombatActor(avatar.Name, avatar.Index)).ToArray();
    public double LastFinishCheckAge => Math.Max(0, (DateTime.Now - AutoFightTask.LastFightFinishCheckTime).TotalSeconds);
    private Avatar AvatarFor(NativeCombatActor actor) => scenes.SelectAvatar(actor.Name)
        ?? throw new InvalidOperationException("当前队伍缺少角色：" + actor.Name);

    public Task PrepareVisionAsync(CancellationToken ct) => Avatar.PrepareCombatVisionAsync(ct);
    public IDisposable BeginExclusive(bool allowPassiveObservation) => AvatarRecognition.BeginExclusiveOperation(allowPassiveObservation: allowPassiveObservation);
    public ImageRegion? Capture()
    {
        var frame = TaskControl.CaptureGameFrameNoRetry(TaskTriggerDispatcher.GlobalGameCapture);
        if (frame == null) return null;
        if (frame.Frame.Empty()) { frame.Dispose(); return null; }
        return new CaptureContent(frame, 0, 0).CaptureRectArea;
    }
    public bool IsCombatHud(ImageRegion frame) => Bv.IsCombatHud(frame);
    public bool IsMainUi(ImageRegion frame) => Bv.IsInMainUi(frame);
    public int ReadActive(ImageRegion frame, AvatarActiveCheckContext context) => scenes.GetActiveAvatarIndex(frame, context);
    public bool? IsActorActive(NativeCombatActor actor, ImageRegion frame)
    {
        var avatar = AvatarFor(actor);
        return avatar.IndexRect == default ? null : avatar.IsActive(frame);
    }
    public double ReadSkillCooldown(NativeCombatActor actor, ImageRegion frame) => AvatarFor(actor).ReadSkillCurrentCd(frame);
    public bool IsSkillReady(NativeCombatActor actor, ImageRegion frame, double cooldown) => AvatarFor(actor).IsSkillReadyFromCurrentFrame(frame, cooldown);
    public BurstObservation ReadBurst(ImageRegion frame, bool active) => Avatar.ObserveBurst(frame, active);
    public bool ReadLowHp(ImageRegion frame) => Bv.CurrentAvatarIsLowHp(frame);
    public HashSet<int> ReadSideBurstReady(ImageRegion frame) => CombatHudReader.ReadSideBurstReady(frame);
    public bool TryGetKnownSkillCooldown(string actor, out double cooldown) => ESkillCdTracker.TryGetKnownRemainingCd(actor, out cooldown);
    public void ConfirmSkill(NativeCombatActor actor, double cooldown, DateTime inputAtUtc) => AvatarFor(actor).ConfirmSkillUsed(cooldown, inputAtUtc);
    public Task WaitSkillCooldown(NativeCombatActor actor, CancellationToken ct) => AvatarFor(actor).WaitSkillCd(ct);
    public void SelectActor(int index, CancellationToken ct)
    {
        CombatActionScope.Current?.Check();
        ct.ThrowIfCancellationRequested();
        scenes.SelectAvatar(index).SimulateSwitchAction(index);
    }
    public void WaitForSelection(int milliseconds, CancellationToken ct) => TaskControl.Sleep(milliseconds, ct);
    public bool OnSelectionMismatch(NativeCombatActor actor, int attempt, int attempts, int observed, CancellationToken ct)
    {
        if (attempt == attempts - 1 && attempts == 4)
            Logger.LogWarning("切换角色失败，最后一次尝试，当前角色编号:{CurrentIndex}，期望角色编号:{ExpectedIndex}", observed, actor.Index);
        else if (attempt == 9 && AutoFightTask.FightStatusFlag)
        {
            AvatarFor(actor).PerformUnstuckAction(ct);
            return true;
        }
        return false;
    }
    public void ResolveSelectionRecovery(NativeCombatActor actor, AvatarSelectionProtocol.Result<ImageRegion> selection, CancellationToken ct)
    {
        using var request = new Avatar.AvatarRecoveryRequest(new(actor.Name, actor.Index), scenes,
            selection.TakeBefore(), selection.TakeFrame() ?? throw new InvalidOperationException("恢复缺少原始帧"), selection.InputFence);
        Avatar.ResolveSelectionRecovery(request, ct);
    }
    public void CheckDefeated(ImageRegion frame, CancellationToken ct) => Avatar.ThrowWhenDefeated(frame, ct);
    public void SendSkill(NativeCombatActor actor, bool hold) => AvatarFor(actor).SendSkillInput(hold);
    public void SendBurst(NativeCombatActor actor) => AvatarFor(actor).SendBurstInput();
    public void ExecutePrimitive(NativeCombatActor actor, CombatCommand command) => command.Execute(AvatarFor(actor));
    public void ReleaseInput() => Simulation.ReleaseAllKey();
    public Task DelayAsync(int milliseconds, CancellationToken ct) => Task.Delay(milliseconds, ct);
}
