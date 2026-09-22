using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoFight.Assets;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Model.Area;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

/// <summary>唯一连接截图/视觉/键鼠及原有角色状态的生产实现，不拥有战斗调度。</summary>
internal sealed class NativeCombatIo(CombatScenes scenes) : INativeCombatIo
{
    private static readonly CombatInputCoordinator Coordinator = new();
    private bool _visionRequested, _burstRequested;
    private int _burstWidth, _burstHeight;
    public bool IsVisionPrepared => _visionRequested && OcrFactory.IsPaddlePrepared &&
        (!_burstRequested || Avatar.IsBurstVisionPrepared(_burstWidth, _burstHeight));
    public TimeProvider Clock => TimeProvider.System;
    public double DpiScale => TaskContext.Instance().DpiScale;
    public ILogger Logger => TaskControl.Logger;
    public ICombatHostInputDevice ControlDevice { get; } = new NativeCombatHostInputDevice();
    public CombatInputCoordinator InputCoordinator => Coordinator;
    public IReadOnlyList<NativeCombatActor> Actors => scenes.GetAvatars().Select(avatar => new NativeCombatActor(avatar.Name, avatar.Index)).ToArray();
    public double LastFinishCheckAge => Math.Max(0, (DateTime.Now - AutoFightTask.LastFightFinishCheckTime).TotalSeconds);
    private Avatar AvatarFor(NativeCombatActor actor) => scenes.SelectAvatar(actor.Name)
        ?? throw new InvalidOperationException("当前队伍缺少角色：" + actor.Name);

    public Task PrepareVisionAsync(CancellationToken ct) => Avatar.PrepareCombatVisionAsync(ct);
    public Task PrepareCommonVisionAsync(CancellationToken ct) => OcrFactory.PreparePaddleAsync(ct);
    public Task PrepareVisionAsync(bool needsBurst, CancellationToken ct) => Avatar.PrepareCombatVisionAsync(ct, needsBurst);
    public Task PrepareVisionAsync(bool needsBurst, ImageRegion frame, CancellationToken ct)
    {
        _visionRequested = true;
        _burstRequested = needsBurst;
        if (needsBurst) RequireBurstSize(frame);
        // 调用方持有样本至Task完成。实际首推理和预测锁等待只在受管准备工作里执行。
        return Task.Run(() => Avatar.PrepareCombatVisionAsync(ct, needsBurst, frame), CancellationToken.None);
    }
    private void RequireBurstSize(ImageRegion frame)
    {
        var rect = AutoFightAssets.Get(frame).QRectForClassify;
        _burstWidth = rect.Width;
        _burstHeight = rect.Height;
    }
    public IDisposable BeginExclusive(bool allowPassiveObservation) => AvatarRecognition.BeginExclusiveOperation(allowPassiveObservation: allowPassiveObservation);
    public ImageRegion? Capture()
    {
        var frame = TaskControl.CaptureGameFrameNoRetry(TaskTriggerDispatcher.GlobalGameCapture);
        if (frame == null) return null;
        if (frame.Frame.Empty()) { frame.Dispose(); return null; }
        return new CaptureContent(frame, 0, 0).CaptureRectArea;
    }
    public bool IsCombatHud(ImageRegion frame) => Bv.IsCombatHud(frame);
    public CombatControlObservation ReadControl(ImageRegion frame) =>
        CombatMotionReader.ReadControl(frame, combatHud: true, Core.Recognition.OCR.OcrFactory.Paddle);
    public CannonUiObservation ReadCannonScene(ImageRegion frame) =>
        CannonUiReader.Read(frame, Core.Recognition.OCR.OcrFactory.Paddle);
    public bool IsMainUi(ImageRegion frame) => Bv.IsInMainUi(frame);
    public int ReadActive(ImageRegion frame, AvatarActiveCheckContext context) => scenes.GetActiveAvatarIndex(frame, context);
    public AvatarLayoutPreparation PrepareActorObservation(ImageRegion frame, AvatarActiveCheckContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        CombatActionScope.Current?.Check();
        return scenes.PrepareAvatarObservation(frame, context, TimeSpan.FromMilliseconds(150));
    }
    public bool? IsActorActive(NativeCombatActor actor, ImageRegion frame)
    {
        var avatar = AvatarFor(actor);
        return avatar.IndexRect == default ? null : avatar.IsActive(frame);
    }
    public double ReadSkillCooldown(NativeCombatActor actor, ImageRegion frame) => AvatarFor(actor).ReadSkillCurrentCd(frame);
    public bool IsSkillReady(NativeCombatActor actor, ImageRegion frame, double cooldown) => AvatarFor(actor).IsSkillReadyFromCurrentFrame(frame, cooldown);
    public BurstObservation ReadBurst(ImageRegion frame, bool active)
    {
        RequireBurstSize(frame);
        var started = Clock.GetTimestamp();
        try { return Avatar.ObserveBurst(frame, active); }
        catch (RecognitionNotReadyException) { return default; }
        finally
        {
            var elapsed = Clock.GetElapsedTime(started).TotalMilliseconds;
            if (elapsed > 75)
                try { Logger.LogDebug("NATIVE_OBSERVATION_COST phase=q-classification ms={Elapsed:F2} source={Session}/{Sequence} sourceAgeMs={Age:F2}",
                    elapsed, frame.FrameStamp.SessionId, frame.FrameStamp.Sequence,
                    Clock.GetElapsedTime(frame.FrameStamp.CapturedTimestamp).TotalMilliseconds); } catch { }
        }
    }
    public bool ReadLowHp(ImageRegion frame) => Bv.CurrentAvatarIsLowHp(frame);
    public HashSet<int> ReadSideBurstReady(ImageRegion frame) => CombatHudReader.ReadSideBurstReady(frame);
    public bool TryGetKnownSkillCooldown(string actor, out double cooldown) => ESkillCdTracker.TryGetKnownRemainingCd(actor, out cooldown);
    public void ConfirmSkill(NativeCombatActor actor, double cooldown, DateTime inputAtUtc) => AvatarFor(actor).ConfirmSkillUsed(cooldown, inputAtUtc);
    public Task WaitSkillCooldown(NativeCombatActor actor, CancellationToken ct) => AvatarFor(actor).WaitSkillCd(ct);
    public CombatBattleHostInputResult SelectActor(int index, CombatNativeInputRequest request, CancellationToken ct)
    {
        CombatActionScope.Current?.Check();
        ct.ThrowIfCancellationRequested();
        return scenes.SelectAvatar(index).SubmitSwitchAction(index, request, ct);
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
    public CombatBattleHostInputResult SubmitInput(NativeCombatActor actor, CombatCommand command,
        CombatNativeInputRequest request, Action beforeFirstNative, CancellationToken ct)
        => SubmitCore(command, request, beforeFirstNative, ct, () =>
        {
            if (command.NativeSkillSequence is { } sequence) SendSkillSequence(actor, command, sequence, ct);
            else if (command.Method == Method.Skill) AvatarFor(actor).SendSkillInput(command.HasFlag("hold"));
            else if (command.Method == Method.Burst) AvatarFor(actor).SendBurstInput();
            else command.Execute(AvatarFor(actor));
        });

    private void SendSkillSequence(NativeCombatActor actor, CombatCommand command, IReadOnlyList<CombatCommand> sequence, CancellationToken ct)
    {
        static bool E(CombatCommand item) => item.Args?.FirstOrDefault() is "E" or "VK_E";
        if (command.Method != Method.Skill || sequence.Count < 2 || sequence[0].Method != Method.KeyDown || !E(sequence[0]) ||
            sequence[^1].Method != Method.KeyUp || !E(sequence[^1]) ||
            sequence.Any(item => item.Name != actor.Name || item.NativeSkillSequence != null) ||
            sequence.Skip(1).Take(sequence.Count - 2).Any(item => item.Method != Method.Wait && item.Method != Method.MoveBy))
            throw new InvalidOperationException("采集技能的原生序列边界无效，禁止执行");
        try
        {
            foreach (var primitive in sequence)
            {
                ct.ThrowIfCancellationRequested();
                CombatActionScope.Current?.Check();
                if (primitive.Method == Method.KeyDown) Simulation.SendInput.SimulateAction(GIActions.ElementalSkill, KeyType.KeyDown);
                else if (primitive.Method == Method.KeyUp) Simulation.SendInput.SimulateAction(GIActions.ElementalSkill, KeyType.KeyUp);
                else primitive.Execute(AvatarFor(actor));
                ct.ThrowIfCancellationRequested();
                command.NativeSkillObserver?.Invoke();
            }
        }
        finally { ReleaseInput(); }
    }

    public CombatBattleHostInputResult SubmitPathingInput(CombatCommand command, CombatNativeInputRequest request,
        Action beforeFirstNative, CancellationToken ct, CannonUiObservation scene = default) =>
        SubmitCore(command, request, beforeFirstNative, ct, () => PathingPrimitiveInput.Send(command, ct, scene, request.Source));

    private CombatBattleHostInputResult SubmitCore(CombatCommand command, CombatNativeInputRequest request,
        Action beforeFirstNative, CancellationToken ct, Action send)
    {
        var receipt = new CombatNativeInput(Clock, Logger, () => TaskControl.CheckAndSleep(0)).Submit(request,
            command.Method.Alias[0], send, ct, beforeFirstNative);
        if (receipt.Status == CombatBattleHostInputStatus.Unknown)
        {
            ReleaseInput();
            receipt = receipt with { ObservableAfterTimestamp = Clock.GetTimestamp() };
        }
        return receipt;
    }
    public void ReleaseInput() => Simulation.ReleaseAllKey();
    public Vanara.PInvoke.User32.VK? HeldEPhysicalKey() =>
        ResolveHeldEPhysicalKey(ViewModel.Pages.KeyBindingsSettingsPageViewModel.MappingKey);

    internal static Vanara.PInvoke.User32.VK? ResolveHeldEPhysicalKey(Func<Vanara.PInvoke.User32.VK, Vanara.PInvoke.User32.VK> mapping)
    {
        var key = mapping(Vanara.PInvoke.User32.VK.VK_E);
        if (!Enum.IsDefined(key) || (int)key <= 6) return null;
        for (var index = 0; index < 5; index++)
            if (key == mapping((Vanara.PInvoke.User32.VK)((int)Vanara.PInvoke.User32.VK.VK_1 + index))) return null;
        return key;
    }
    public CombatBattleHostInputResult SubmitHeldE(Vanara.PInvoke.User32.VK key, bool down,
        CombatNativeInputRequest request, Action beforeFirstNative, CancellationToken ct) =>
        new CombatNativeInput(Clock, Logger, () => TaskControl.CheckAndSleep(0)).Submit(request,
            down ? "held-e-down" : "held-e-up", () =>
            {
                if (down) Simulation.SendInput.Keyboard.KeyDown(key);
                else Simulation.SendInput.Keyboard.KeyUp(key);
            }, ct, beforeFirstNative);
    public void ReleaseHeldE(Vanara.PInvoke.User32.VK key) => Simulation.SendInput.Keyboard.KeyUp(key);
    public Task DelayAsync(int milliseconds, CancellationToken ct) => Task.Delay(milliseconds, ct);
}
