using BetterGenshinImpact.Core.Recognition;
using System.Collections.Generic;
using BetterGenshinImpact.Core.Input;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.Core.Recognition.OpenCv;
using BetterGenshinImpact.Core.Script.Dependence;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.AutoFight.Config;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Helpers;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.AutoTrackPath;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using Vanara.PInvoke;
using static BetterGenshinImpact.GameTask.Common.TaskControl;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.AutoFight.Assets;
using BetterGenshinImpact.ViewModel.Pages;
using BetterGenshinImpact.ViewModel.Windows;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.AutoPathing.Model.Enum;
using BetterGenshinImpact.Core.Recognition.ONNX;
using Compunet.YoloSharp;
using Compunet.YoloSharp.Data;
using Microsoft.Extensions.DependencyInjection;

using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.Common.Ui;
using Fischless.GameCapture;

namespace BetterGenshinImpact.GameTask.AutoFight.Model;

/// <summary>
/// 队伍内的角色
/// </summary>
public partial class Avatar
{
    private sealed record KnownReviveTarget(ReviveTarget Identity, CombatScenes Scene);
    /// <summary>
    /// 配置文件中的角色信息
    /// </summary>
    public readonly CombatAvatar CombatAvatar;

    /// <summary>
    /// 角色名称 中文
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// 队伍内序号
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    /// 最近一次OCR识别出的CD到期时间
    /// 是原始 E 技能（编号 "01"）的 CD 记录，
    /// </summary>
    private DateTime OcrSkillCd { get; set; }

    /// <summary>
    /// 手动配置的技能CD，有它就不使用OCR,小于0为自动
    /// </summary>
    public double ManualSkillCd { get; set; }

    /// <summary>
    /// 是否启用 E 技能 ONNX 分类识别（<see cref="IsESkillReadyByClassify"/>）。
    /// YoloSharp 分类器不支持并发调用，仅 AutoCombo 等串行调用方启用；
    /// 默认 false，<see cref="ReadSkillCdFromScreenshot"/> 回退为纯 OCR。
    /// </summary>
    public bool EnableESkillClassify { get; set; }

    /// <summary>
    /// 最近一次使用元素战技的时间
    /// </summary>
    public DateTime LastSkillTime { get; set; }

    internal DateTime LastConfirmedSkillCastAtUtc { get; private set; }

    internal bool HasConfirmedSkillCooldown =>
        LastConfirmedSkillCastAtUtc != default &&
        !ESkillCdTracker.IsReady(Name);

    /// <summary>
    /// 元素爆发是否就绪
    /// </summary>
    public bool IsBurstReady { get; set; }

    /// <summary>
    /// 名字所在矩形位置
    /// </summary>
    public Rect NameRect { get; set; }

    /// <summary>
    /// 名字右边的编号位置
    /// </summary>
    public Rect IndexRect { get; set; }

    /// <summary>
    /// 任务取消令牌
    /// </summary>
    public CancellationToken Ct { get; set; }

    /// <summary>
    /// 战斗场景
    /// </summary>
    public CombatScenes CombatScenes { get; set; }

    /// <summary>
    /// 脱困方向数组（前/后/左/右）
    /// </summary>
    private static readonly GIActions[] UnstuckDirections =
    {
        GIActions.MoveForward,
        GIActions.MoveBackward,
        GIActions.MoveLeft,
        GIActions.MoveRight
    };

    private static readonly Random UnstuckRandom = new();

    private static BgiYoloPredictor QBurstClassifier
    {
        get
        {
            var factory = App.ServiceProvider.GetRequiredService<BgiOnnxFactory>();
            if (!RecognitionReadinessScope.IsNonBlocking) return factory.GetOrCreateYoloPredictor(BgiOnnxModel.BgiQClassify);
            return factory.TryGetCachedYoloPredictor(BgiOnnxModel.BgiQClassify, out var predictor)
                ? predictor! : throw new RecognitionNotReadyException("Q模型未准备，实时观察不创建预测器");
        }
    }

    internal static bool IsBurstVisionPrepared(int width, int height) =>
        App.ServiceProvider.GetRequiredService<BgiOnnxFactory>().TryGetCachedYoloPredictor(BgiOnnxModel.BgiQClassify, out var predictor) &&
        predictor!.IsClassificationPrepared(width, height);

    internal static async Task PrepareCombatVisionAsync(CancellationToken ct, bool needsBurst = true, ImageRegion? preparationFrame = null)
    {
        ct.ThrowIfCancellationRequested();
        if (needsBurst)
        {
            if (preparationFrame == null) await QBurstClassifier.WarmUpAsync(Logger, ct);
            else
            {
                using var area = preparationFrame.DeriveCrop(AutoFightAssets.Get(preparationFrame).QRectForClassify);
                await QBurstClassifier.PrepareClassificationAsync(area.CacheImage, Logger, ct);
            }
        }
        await OcrFactory.PreparePaddleAsync(ct);
        ct.ThrowIfCancellationRequested();
    }

    private static readonly Lazy<BgiYoloPredictor> ESkillClassifierLazy = new(() =>
        App.ServiceProvider.GetRequiredService<BgiOnnxFactory>().CreateYoloPredictor(BgiOnnxModel.BgiEClassify));

    public Avatar(CombatScenes combatScenes, string name, int index, Rect nameRect, double manualSkillCd = -1)
    {
        CombatScenes = combatScenes;
        Name = name;
        Index = index;
        NameRect = nameRect;
        CombatAvatar = DefaultAutoFightConfig.CombatAvatarMap[name];
        ManualSkillCd = manualSkillCd;
        LastConfirmedSkillCastAtUtc = ESkillCdTracker.GetLastConfirmedCastAtUtc(name);
        AutoFightTask.FightStatusFlag = false;
    }


    /// <summary>
    /// 是否存在角色被击败
    /// 通过判断确认按钮
    /// </summary>
    /// <param name="region"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    public static void ThrowWhenDefeated(ImageRegion region, CancellationToken ct)
    {
        var swimming = AutoFightParam.SwimmingEnabled && AutoFightTask.FightStatusFlag && SwimmingConfirm(region);
        if (Bv.IsCombatHud(region) && !swimming) return;
        ct.ThrowIfCancellationRequested();
        BetterGenshinImpact.Core.Input.InputHub.ReleaseAll();
        using var suspension = CombatActionScope.Suspend();
        ThrowWhenDefeated(region, ct, null);
    }

    private static void ThrowWhenDefeated(ImageRegion region, CancellationToken ct, KnownReviveTarget? target)
    {
        if (Bv.IsInRevivePrompt(region))
        {
            using var recoveryScope = CombatActionScope.Suspend();
            ct.ThrowIfCancellationRequested();
            if (Bv.IsInDomainIncludingRevivePrompt(region))
            {
                Logger.LogWarning("检测到秘境内复苏界面，跳过七天神像传送并交由自动秘境重试");
                throw new DomainDefeatedRetryException();
            }

            var cause = new InvalidOperationException("复苏界面触发神像恢复");
            TaskFailureDiagnostics.CaptureScreenshotOnce(cause,
                $"原始复苏现场，角色证据={target?.Identity.Name ?? "未知"}，slot={target?.Identity.Index.ToString() ?? "未知"}，尚未退出弹窗或传送");
            Logger.LogWarning("检测到复苏界面，角色证据={Actor}，前往七天神像恢复；未知身份不代表全队已复活", target?.Identity.Name ?? "未知");
            using var driver = new NativeUiDriver();
            UiRecovery.RecoverDefeatedAsync(driver, token =>
            {
                TpForRecover(token, cause, target);
                return Task.CompletedTask;
            }, ct, Logger).GetAwaiter().GetResult();
        }
        else if (AutoFightParam.SwimmingEnabled && AutoFightTask.FightStatusFlag && SwimmingConfirm(region))
        {
            using var recoveryScope = CombatActionScope.Suspend();
            ct.ThrowIfCancellationRequested();
            if (AutoFightTask.FightWaypoint is not null)
            {
                // 二次确认：延迟 800ms 后重新截屏，避免同帧误判
                Sleep(800, ct);
                using var ra = CaptureToRectArea();
                if (!SwimmingConfirm(ra))
                {
                    return;
                }

                Logger.LogInformation("游泳检测：尝试回到战斗地点");

                using (AvatarRecognition.BeginExclusiveOperation())
                {
                    // 保存原始 MoveMode，用于 finally 还原
                    var originalMoveMode = AutoFightTask.FightWaypoint.MoveMode;
                    // 链接外部取消令牌，确保外部取消时能及时响应；using 确保自动 Dispose
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

                    try
                    {
                        var pathExecutor = new PathExecutor(cts.Token);

                        // FaceTo 朝向战斗点，超时 2 秒
                        cts.CancelAfter(2000);
                        pathExecutor.FaceTo(AutoFightTask.FightWaypoint).GetAwaiter().GetResult();

                        // 重置超时，MoveTo 超时 15 秒
                        cts.CancelAfter(15000);
                        // 使用 Climb 模式：MoveTo 内部对 Climb 模式跳过卡死脱困检测，避免水中 TrapEscaper 死循环
                        AutoFightTask.FightWaypoint.MoveMode = MoveModeEnum.Climb.Code;
                        InputHub.Foreground.Mouse.RightButtonDown();
                        pathExecutor.MoveTo(AutoFightTask.FightWaypoint).GetAwaiter().GetResult();
                        Logger.LogInformation("游泳检测：移动结束");
                    }
                    catch (NormalEndException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (NormalEndException) when (cts.IsCancellationRequested)
                    {
                        Logger.LogWarning("游泳检测：回到战斗地点超时");
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (OperationCanceledException)
                    {
                        Logger.LogWarning("游泳检测：回到战斗地点超时");
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "游泳检测：回到战斗地点异常");
                    }
                    finally
                    {
                        // 确保所有资源和状态在任何路径都被正确清理
                        cts.Cancel(); // 终止 PathExecutor 内部截屏循环
                        AutoFightTask.FightWaypoint.MoveMode = originalMoveMode;
                        AutoFightTask.FightWaypoint = null;
                        InputHub.Foreground.Mouse.RightButtonUp();
                        InputHub.ReleaseAll();
                    }
                }

                using var bitmap2 = CaptureToRectArea();
                if (!SwimmingConfirm(bitmap2))
                {
                    Logger.LogInformation("游泳检测：游泳脱困成功");
                    return;
                }

                Logger.LogWarning("游泳检测：回到战斗地点失败");
            }

            Logger.LogWarning("战斗过程检测到游泳，前往七天神像重试");
            TpForRecover(ct, new RetryException("战斗过程检测到游泳，前往七天神像重试"));
        }
    }

    /// <summary>
    /// 游泳检测（色块连通性检测）
    /// 游泳时右下角会出现鼠标图标，带有黄色色块，不受改按键影响
    /// </summary>
    private static bool SwimmingConfirm(Region region)
    {
        var imageRegion = region.ToImageRegion();
        using var cropped = imageRegion.DeriveCrop(1819, 1025, 9, 11);
        using var mask = OpenCvCommonHelper.Threshold(cropped.SrcMat, new Scalar(242, 223, 39), new Scalar(255, 233, 44));
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();

        var numLabels = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids,
            connectivity: PixelConnectivity.Connectivity4, ltype: MatType.CV_32S);

        return numLabels > 1;
    }

    /// <summary>
    /// tp 到七天神像恢复
    /// </summary>
    /// <param name="ct"></param>
    /// <param name="ex"></param>
    /// <exception cref="RetryException"></exception>
    public static void TpForRecover(CancellationToken ct, Exception ex) => TpForRecover(ct, ex, null);

    private static void TpForRecover(CancellationToken ct, Exception ex, KnownReviveTarget? target)
    {
        CombatRecoveryCompletedException.RecoverVerifiedAsync(
            () => RecoverAtStatueOfTheSeven(ct), () => VerifyRecoveredTarget(target, ct), ct).GetAwaiter().GetResult();
    }

    private static ImageRegion? CaptureFreshUiFrame()
    {
        var frame = CaptureGameFrameNoRetry(TaskTriggerDispatcher.GlobalGameCapture);
        if (frame == null) return null;
        if (frame.Frame.Empty()) { frame.Dispose(); return null; }
        return new CaptureContent(frame, 0, 0).CaptureRectArea;
    }

    private static ReviveRecoveryFrame CaptureRecoveryFrame(KnownReviveTarget? target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        UiOperation.Current?.Check();
        using var frame = CaptureFreshUiFrame();
        if (frame == null) return new(0, false, ReviveUiState.None, -1, new Dictionary<int, string>());
        if (target == null)
            return new ReviveRecoveryFrame(frame.FrameStamp.Sequence, Bv.IsInMainUi(frame), Bv.ReadReviveState(frame), -1, new Dictionary<int, string>())
                .WithSource(frame.FrameStamp, TimeProvider.System);
        try
        {
            var observed = target.Scene.ReadRecoveryFrame(frame, target.Identity);
            try
            {
                BetterGenshinImpact.GameTask.Common.DiagnosticEvidenceScope.Current?.TryCapture(frame, "statue-confirm:" + UiOperation.Current?.Id,
                    $"state-{observed.Revive}-{observed.ActiveIndex}",
                    $"target={target.Identity.Name} slot={target.Identity.Index} active={observed.ActiveIndex} hud={observed.MainReady} revive={observed.Revive}; 原始恢复判定帧",
                    Logger);
            }
            catch { }
            return observed;
        }
        catch (Exception error) when (error is not OperationCanceledException and not CombatNotFinishedException and not CombatActionInterruptedException)
        {
            Logger.LogDebug(error, "神像恢复角色身份读取失败，保留未知");
            return new(0, false, ReviveUiState.None, -1, new Dictionary<int, string>());
        }
    }

    private static Task VerifyRecoveredTarget(KnownReviveTarget? target, CancellationToken ct) =>
        UiOperation.RunAsync("statue-revive-confirm", TimeSpan.FromSeconds(8), ct, async operation =>
        {
            using var exclusive = AvatarRecognition.BeginExclusiveOperation();
            ReviveRecoveryFrame? last = null;
            await RecoveredAvatarConfirmation.WaitAsync(target?.Identity, () =>
            {
                last = CaptureRecoveryFrame(target, operation.Token);
                operation.Observe("原败北角色恢复并可出战",
                    $"actor={target?.Identity.Name ?? "未知"},slot={target?.Identity.Index},mapped={target != null && last.HasTarget(target.Identity)},active={last.ActiveIndex},hud={last.MainReady},revive={last.Revive}", last.FrameId);
                return last;
            }, index =>
            {
                operation.Check();
                if (target != null && last is { MainReady: true, Revive: ReviveUiState.None } && last.HasTarget(target.Identity))
                    SimulateSwitchKey(index);
            }, ms => operation.DelayAsync(ms, ct), operation.Remaining, operation.Token);
            operation.Check();
            if (target != null) Logger.LogInformation("神像恢复已确认原角色 {Actor} 可出战", target.Identity.Name);
            else Logger.LogInformation("复苏目标身份未知，本次只核实恢复后的主界面，不签发指定角色或全队复活证明");
            return true;
        }, Logger, captureFailure: (error, context) => TaskFailureDiagnostics.CaptureScreenshotOnce(error, context));

    public static async Task RecoverAtStatueOfTheSeven(CancellationToken ct)
    {
        using var recoveryScope = CombatActionScope.Suspend();
        ct.ThrowIfCancellationRequested();
        var tpTask = new TpTask(ct);
        await tpTask.TpToStatueOfTheSeven();
        Logger.LogInformation("神像回血等待结束，由调用方继续确认恢复结果。【设置】-【七天神像设置】可以修改回血相关配置。");
    }

    /// <summary>
    /// 切换到本角色
    /// 切换cd是1秒，如果切换失败，会尝试再次切换，最多尝试5次
    /// </summary>
    public bool Switch()
    {
        return TrySwitch(30);
    }

    /// <summary>
    /// 尝试切换到本角色
    /// </summary>
    /// <param name="tryTimes"></param>
    /// <param name="needLog"></param>
    /// <returns></returns>
    public bool TrySwitch(int tryTimes = 4)
    {
        using var result = Select(tryTimes);
        if (result.Recovery != null) ResolveSelectionRecovery(result.Recovery, Ct);
        if (result.Confirmed && EnableESkillClassify) ESkillClassifyViewModel.Instance.Result = null;
        return result.Confirmed;
    }

    internal AvatarSelectionResult Select(int tryTimes)
    {
        var context = new AvatarActiveCheckContext();
        var requested = new ReviveTarget(Name, Index);
        var maximumAge = CombatActionScope.Current != null ? UiSnapshot.CombatMaximumAge : UiSnapshot.RecoveryMaximumAge;
        ImageRegion? CaptureSelectionFrame()
        {
            var frame = CaptureFreshUiFrame();
            try
            {
                if (frame != null && context.NeedsLayoutPreparation)
                    CombatScenes.PrepareAvatarObservation(frame, context, maximumAge);
                return frame;
            }
            catch { frame?.Dispose(); throw; }
        }
        using var result = AvatarSelectionProtocol.Select(requested.Index, tryTimes, CaptureSelectionFrame,
            region => region.FrameStamp, Bv.IsCombatHud,
            region => region.ReadOnce((CombatScenes, typeof(AvatarActiveCheckContext)),
                () => CombatScenes.GetActiveAvatarIndex(region, context)),
            region => new ImageRegion(region.SrcMat.Clone(), 0, 0) { FrameStamp = region.FrameStamp },
            (index, request) =>
            {
                CombatActionScope.Current?.Check();
                Ct.ThrowIfCancellationRequested();
                return SubmitSwitchAction(index, request, Ct);
            }, milliseconds => Sleep(milliseconds, Ct), Ct,
            maximumAge: maximumAge,
            trace: observed => CombatActionScope.Current?.Trace("switch-frame", $"expected={requested.Index} observed={observed}"));
        var frame = result.TakeFrame();
        if (result.NeedsRecovery && frame != null)
            return new(false, result.Source, new(requested, CombatScenes, result.TakeBefore(), frame, result.InputFence));
        if (!result.Confirmed && !Ct.IsCancellationRequested) Logger.LogWarning("切换角色失败:{Name}", requested.Name);
        return new(result.Confirmed, result.Source, null, frame);
    }

    internal void QueueSkillCooldownObservation()
    {
        // 只把不可变的编号框快照交给后台。观察结果不更改当前角色或护盾时间戳，
        // 且切到其他角色后丢弃结果，防止把别人的 E 冷却记在本角色上。
        var rects = CombatScenes.GetAvatarIndexRectSnapshot();
        var index = Index;
        var cancellationToken = Ct;
        ESkillCdTracker.TriggerEObservation(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var region = CaptureToRectArea();
            var observed = PartyAvatarSideIndexHelper.GetAvatarIndexIsActiveWithContext(
                region, rects, new AvatarActiveCheckContext());
            return observed == index ? ReadSkillCurrentCd(region) : (double?)null;
        }, Name, cancellationToken);
    }

    internal double ReadSkillCurrentCd(ImageRegion imageRegion)
    {
        return GetSkillCurrentCd(imageRegion, updateState: false);
    }

    internal void ConfirmSkillUsed(double detectedCd, DateTime? inputAtUtc = null)
    {
        var now = DateTime.UtcNow;
        var castAt = inputAtUtc ?? now;
        LastSkillTime = castAt;
        LastConfirmedSkillCastAtUtc = castAt;
        ManualSkillCd = -1;
        var effectiveCd = detectedCd > 0
            ? detectedCd
            : Math.Max(CombatAvatar.SkillHoldCd, CombatAvatar.SkillCd);
        if (detectedCd > 0)
        {
            OcrSkillCd = now.AddSeconds(detectedCd);
        }
        ESkillCdTracker.RecordConfirmedCast(Name, effectiveCd + (now - castAt).TotalSeconds, castAt);
    }

    internal void SimulateSwitchAction(int index)
    {
        SimulateSwitchKey(index);
    }

    internal CombatBattleHostInputResult SubmitSwitchAction(int index, CombatNativeInputRequest request, CancellationToken ct)
    {
        var receipt = new CombatNativeInput(TimeProvider.System, Logger, () => CheckAndSleep(0),
            () => BetterGenshinImpact.GameTask.Common.NativeInputEnvironment.Read((Vanara.PInvoke.User32.VK)((int)Vanara.PInvoke.User32.VK.VK_1 + index - 1)))
            .Submit(request, "switch", () => SimulateSwitchAction(index), ct);
        if (receipt.Status == CombatBattleHostInputStatus.Unknown ||
            receipt.Status == CombatBattleHostInputStatus.Failed && receipt.NativeSubmitted > 0)
        {
            BetterGenshinImpact.Core.Input.InputHub.ReleaseAll();
            receipt = receipt with { ObservableAfterTimestamp = TimeProvider.System.GetTimestamp() };
        }
        return receipt;
    }

    private static void SimulateSwitchKey(int index)
    {
        switch (index)
        {
            case 1:
                InputHub.Foreground.SimulateActionPulse(GIActions.SwitchMember1);
                break;
            case 2:
                InputHub.Foreground.SimulateActionPulse(GIActions.SwitchMember2);
                break;
            case 3:
                InputHub.Foreground.SimulateActionPulse(GIActions.SwitchMember3);
                break;
            case 4:
                InputHub.Foreground.SimulateActionPulse(GIActions.SwitchMember4);
                break;
            case 5:
                InputHub.Foreground.SimulateActionPulse(GIActions.SwitchMember5);
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// 战斗中切换角色卡住时的脱困动作：跳跃 → 随机方向移动+切换 → 攻击 → 释放按键
    /// </summary>
    internal void PerformUnstuckAction(CancellationToken ct)
    {
        var direction = UnstuckDirections[UnstuckRandom.Next(4)];
        Logger.LogWarning("切换角色卡住，执行脱困（方向：{Dir}）", direction);

        InputHub.Foreground.SimulateAction(GIActions.Jump);
        Sleep(200, ct);
        InputHub.Foreground.SimulateAction(direction, KeyType.KeyDown);
        SimulateSwitchAction(Index);
        Sleep(1000, ct);
        InputHub.Foreground.SimulateAction(GIActions.NormalAttack);
        InputHub.ReleaseAll();
    }

    /// <summary>
    /// 切换到本角色
    /// 切换cd是1秒，如果切换失败，会尝试再次切换，最多尝试5次
    /// </summary>
    public void SwitchWithoutCts()
    {
        _ = TrySwitch(10);
    }

    /// <summary>
    /// 是否出战状态
    /// </summary>
    /// <returns></returns>
    public bool IsActive(ImageRegion region)
    {
        if (IndexRect == default)
        {
            throw new Exception("IndexRect为空");
        }
        else
        {
            var white = IsIndexRectWhite(region, IndexRect);
            return !white;
        }
    }

    private bool IsIndexRectWhite(ImageRegion region, Rect rect)
    {
        // 剪裁出IndexRect区域
        using var indexRa = region.DeriveCrop(rect);
        using var mat = indexRa.CacheGreyMat;
        var count = OpenCvCommonHelper.CountGrayMatColor(mat, 251, 255);
        if (count * 1.0 / (mat.Width * mat.Height) > 0.5)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// 是否出战状态
    /// </summary>
    /// <returns></returns>
    [Obsolete]
    public bool IsActiveNoIndexRect(ImageRegion region)
    {
        // 通过寻找右侧人物编号来判断是否出战
        if (IndexRect == default)
        {
            var assetScale = TaskContext.Instance().SystemInfo.AssetScale;
            // 剪裁出队伍区域
            var teamRa = region.DeriveCrop(AutoFightAssets.Get(region).TeamRect);
            var blockX = NameRect.X + NameRect.Width * 2 - 10;
            var block = teamRa.DeriveCrop(new Rect(blockX, NameRect.Y, teamRa.Width - blockX, NameRect.Height * 2));
            // Cv2.ImWrite($"block_{Name}.png", block.SrcMat);
            // 取白色区域
            var bMat = OpenCvCommonHelper.Threshold(block.SrcMat, new Scalar(255, 255, 255), new Scalar(255, 255, 255));
            // Cv2.ImWrite($"block_b_{Name}.png", bMat);
            // 矩形识别
            Cv2.FindContours(bMat, out var contours, out _, RetrievalModes.External,
                ContourApproximationModes.ApproxSimple);
            if (contours.Length > 0)
            {
                var boxes = contours.Select(Cv2.BoundingRect)
                    .Where(w => w.Width >= 20 * assetScale && w.Height >= 18 * assetScale)
                    .OrderByDescending(w => w.Width).ToList();
                if (boxes.Count is not 0)
                {
                    IndexRect = boxes.First();
                    return false;
                }
            }
        }
        else
        {
            // 剪裁出IndexRect区域
            var teamRa = region.DeriveCrop(AutoFightAssets.Get(region).TeamRect);
            var blockX = NameRect.X + NameRect.Width * 2 - 10;
            var indexBlock = teamRa.DeriveCrop(new Rect(blockX + IndexRect.X, NameRect.Y + IndexRect.Y, IndexRect.Width,
                IndexRect.Height));
            // Cv2.ImWrite($"indexBlock_{Name}.png", indexBlock.SrcMat);
            var count = OpenCvCommonHelper.CountGrayMatColor(indexBlock.CacheGreyMat, 255);
            if (count * 1.0 / (IndexRect.Width * IndexRect.Height) > 0.5)
            {
                return false;
            }
        }

        Logger.LogInformation("{Name} 当前出战", Name);
        return true;
    }

    /// <summary>
    /// 普通攻击
    /// </summary>
    /// <param name="ms">攻击时长，建议是200的倍数</param>
    public void Attack(int ms = 0)
    {
        while (ms >= 0)
        {
            if (Ct is { IsCancellationRequested: true })
            {
                return;
            }

            InputHub.Foreground.SimulateAction(GIActions.NormalAttack);
            ms -= 200;
            Sleep(200, Ct);
        }
    }

    /// <summary>
    /// 使用元素战技 E
    /// </summary>
    public void UseSkill(bool hold = false, bool observeCooldown = true, Func<bool>? tryBeginInput = null)
    {
        var skillReadyBeforeCast = IsSkillReady();
        CombatActionScope.Current?.Trace("e-preflight", $"actor={Name} trackerReady={skillReadyBeforeCast} hold={hold}");
        Ct.ThrowIfCancellationRequested();
        if (tryBeginInput != null && (!skillReadyBeforeCast || !tryBeginInput())) return;
        CombatActionScope.Current?.Trace("e-dispatch", $"actor={Name} hold={hold}");
        if (AvatarSpecialAction.ExecuteSpecializedAction(this, "UseSkill", Name, new ActionArgs(Hold: hold)))
        {
            LastSkillTime = DateTime.UtcNow;
            if (observeCooldown)
            {
                Sleep(200, Ct);
                using var confirmation = CaptureToRectArea();
                var observedCd = ReadSkillCurrentCd(confirmation);
                if (skillReadyBeforeCast && observedCd > 0) ConfirmSkillUsed(observedCd);
            }
            else QueueSkillCooldownObservation();
            CombatActionScope.Current?.Trace("e-special-return", $"confirmedAt={LastConfirmedSkillCastAtUtc:O}");
            return;
        }

        if (Ct is { IsCancellationRequested: true })
        {
            return;
        }

            // 只有动作前技能处于就绪态，动作后又观察到有效 CD，才把普通策略的 E
            // 计入“已确认护盾覆盖”。否则可能把本来就在冷却中的重复 E 错记为新护盾。
            CombatActionScope.Current?.Trace("e-input-call", $"actor={Name} hold={hold}");
            if (hold)
            {
                InputHub.Foreground.SimulateAction(GIActions.ElementalSkill, KeyType.Hold);
            }
            else
            {
                InputHub.Foreground.SimulateActionPulse(GIActions.ElementalSkill);
            }
            CombatActionScope.Current?.Trace("e-input-return", $"actor={Name}");

            if (!observeCooldown)
            {
                LastSkillTime = DateTime.UtcNow;
                QueueSkillCooldownObservation();
                return;
            }

        // 0.2 秒内循环检测（分类器优先、确认冷却后才 OCR），直到识别到 CD 或超时
        var recordedCd = 0d;
        var deadline = DateTime.UtcNow.AddMilliseconds(200);
        while (recordedCd <= 0 && DateTime.UtcNow < deadline)
        {
            Sleep(35, Ct);

            using (var region = CaptureToRectArea())
            {
                ThrowWhenDefeated(region, Ct);
                var cd = AfterUseSkill(region);
                CombatActionScope.Current?.Trace("e-post-input", $"cd={cd:F3} newCooldown={skillReadyBeforeCast && cd > 0}");
                recordedCd = ESkillCdTracker.Record(Name, cd);
                if (skillReadyBeforeCast && cd > 0) ConfirmSkillUsed(cd);
            }
        }

        // 慢速设备兜底：轮询中的检测可能跨过截止时间（如开始于0.035s、结束时已0.25s），CD 数字恰好在其后才出现，超时后再补检一次
        if (recordedCd <= 0)
        {
            using (var region = CaptureToRectArea())
            {
                ThrowWhenDefeated(region, Ct);
                var cd = AfterUseSkill(region);
                CombatActionScope.Current?.Trace("e-post-input", $"cd={cd:F3} newCooldown={skillReadyBeforeCast && cd > 0}");
                recordedCd = ESkillCdTracker.Record(Name, cd);
                if (skillReadyBeforeCast && cd > 0) ConfirmSkillUsed(cd);
            }
        }

        if (recordedCd <= 0)
        {
            recordedCd = ESkillCdTracker.ApplyFallback(Name);
        }

        if (recordedCd > 0)
        {
            Logger.LogInformation(hold ? "{Name} 长按元素战技，cd:{Cd} 秒" : "{Name} 点按元素战技，cd:{Cd} 秒", Name,
                Math.Round(recordedCd, 2));
        }
    }

    internal void SendSkillInput(bool hold)
    {
        Ct.ThrowIfCancellationRequested();
        if (!AvatarSpecialAction.ExecuteSpecializedAction(this, "UseSkill", Name, new ActionArgs(Hold: hold)))
        {
            if (hold) BetterGenshinImpact.Core.Input.InputHub.Foreground.SimulateAction(GIActions.ElementalSkill, KeyType.Hold);
            else BetterGenshinImpact.Core.Input.InputHub.Foreground.SimulateActionPulse(GIActions.ElementalSkill);
        }
    }

    internal void SendBurstInput()
    {
        Ct.ThrowIfCancellationRequested();
        BetterGenshinImpact.Core.Input.InputHub.Foreground.SimulateActionPulse(GIActions.ElementalBurst);
    }

    /// <summary>
    /// 使用完元素战技的回调,注意,不会在这里检测是不是需要跑七天神像 <br/>
    /// UseSkill 方法内会调用，如果没有使用UseSkill但是释放了技能之后记得调用一下这个方法
    /// </summary>
    /// <returns>当前技能CD</returns>
    public double AfterUseSkill(ImageRegion? givenRegion = null)
    {
        LastSkillTime = DateTime.UtcNow;
        if (ManualSkillCd > 0)
        {
            return GetSkillCdSeconds();
        }

        // 调用方传入的截图由调用方负责释放，方法只释放自己创建的
        // 公开契约：0 表示未记录到 CD（就绪或未读到数字），供调用方重试/兜底
        if (givenRegion != null)
        {
            return ReadSkillCdFromScreenshot(givenRegion).Cd ?? 0;
        }

        using var region = CaptureToRectArea();
        return ReadSkillCdFromScreenshot(region).Cd ?? 0;
    }

    /// <summary>
    /// 从截图中判定 E 技能状态并读取剩余 CD：<see cref="EnableESkillClassify"/> 启用时
    /// 先用 <see cref="IsESkillReadyByClassify"/> 分类判定，
    /// 仅在明确判定 Cooldown 时才 OCR 读取具体剩余秒数；就绪返回 0；
    /// 未启用时 State 恒为 Unknown，仅 OCR 提取 CD 数值；
    /// 未知（置信度不足/角色不匹配）时不 OCR，避免在不确定截图归属时误读并污染记录。
    /// 注意 Cooldown 状态下 OCR 可能读不到数字（<see cref="Cd"/> 为 <c>null</c>），调用方须以 State 为准。
    /// </summary>
    private (SkillCdState State, double? Cd) ReadSkillCdFromScreenshot(ImageRegion imageRegion, bool onlyCode01Ready = true)
    {
        // 未启用分类识别（默认）：不做状态分类，State 恒为 Unknown，仅 OCR 提取 CD 数
        if (!EnableESkillClassify)
        {
            var ocrCd = ReadSkillCdByOcr(imageRegion);
            if (ocrCd > 0)
            {
                ESkillCdTracker.Record(Name, ocrCd.Value);
            }
            return (SkillCdState.Unknown, ocrCd);
        }

        var (State, Code) = IsESkillReadyByClassify(imageRegion, onlyCode01Ready);
        if (State == SkillCdState.Ready)
        {
            // 只有原始 E（编号 "01"）的 Ready 才清零 OcrSkillCd
            // 特殊状态技能（02/03...）的 Ready 不清零
            if (string.Equals(Code, "01", StringComparison.OrdinalIgnoreCase))
            {
                OcrSkillCd = DateTime.UtcNow;
            }
            return (SkillCdState.Ready, 0);
        }

        // 仅在分类器明确判定 Cooldown 时才 OCR 读具体秒数
        // Unknown（置信度不足/角色不匹配）时截图归属存疑，不 OCR 避免误读污染记录
        if (State == SkillCdState.Cooldown)
        {
            var cd = ReadSkillCdByOcr(imageRegion);
            if (cd > 0)
            {
                ESkillCdTracker.Record(Name, cd.Value);
            }
            return (State, cd);
        }

        // Unknown
        return (State, null);
    }

    /// <summary>
    /// 根据Ocr识别元素战技是否正在CD中
    /// 右下 267x132
    /// 77x77
    /// </summary>
    private double GetSkillCurrentCd(ImageRegion imageRegion, bool updateState = true)
    {
        var reading = CombatHudReader.ReadCooldown(imageRegion, OcrFactory.Paddle);
        var cd = reading.Seconds;
        CombatActionScope.Current?.Trace("e-ocr", $"actor={Name} raw={reading.Raw} parsed={cd:F3}");
        if (updateState && cd > 0 && cd <= CombatAvatar.SkillCd)
            OcrSkillCd = DateTime.UtcNow.AddSeconds(cd);
        return cd;
    }

    private double? ReadSkillCdByOcr(ImageRegion imageRegion)
    {
        var cd = GetSkillCurrentCd(imageRegion);
        if (cd > 0 && cd <= CombatAvatar.SkillCd)
        {
            OcrSkillCd = DateTime.UtcNow.AddSeconds(cd);
            return cd;
        }

        return null;
    }


    /// <summary>
    /// 使用元素爆发 Q
    /// Q释放等待 2s 超时认为没有Q技能
    /// </summary>
    public void UseBurst(bool waitForConfirmation = true)
    {
        // 兼容旧调用签名；不再允许无确认路径把输入发送当成释放成功。
        TryUseBurst();
    }

    public BurstCastResult TryUseBurst(double timeoutSeconds = 2.4, int maxSamples = 16, Func<bool>? tryBeginInput = null)
    {
        using (AvatarRecognition.BeginExclusiveOperation())
        {
            return BurstCastProtocol.TryCast(() =>
                {
                    using var region = CaptureToRectArea();
                    ThrowWhenDefeated(region, Ct);
                    if (IsActive(region)) return ObserveBurst(region, expectedActorActive: true);
                    CombatActionScope.Current?.Trace("q-observation", $"actor={Name} actorActive=False classifier=not-observed");
                    return default;
                }, () =>
                {
                    CombatActionScope.Current?.Trace("q-input-call", $"actor={Name}");
                    BetterGenshinImpact.Core.Input.InputHub.Foreground.SimulateActionPulse(GIActions.ElementalBurst);
                    CombatActionScope.Current?.Trace("q-input-return", $"actor={Name}");
                },
                milliseconds => Sleep(milliseconds, Ct), Ct, timeoutSeconds: timeoutSeconds, maxSamples: maxSamples,
                tryBeginInput: tryBeginInput);
        }
    }

    internal static BurstObservation ObserveBurst(ImageRegion imageRegion, bool? expectedActorActive = null)
    {
        var reading = CombatHudReader.ReadBurst(imageRegion, QBurstClassifier);
        var observation = reading.Observation;
        CombatActionScope.Current?.Trace("q-classifier",
            $"label={reading.Label} confidence={reading.Confidence:F4} energy={observation.EnergyFull} cooling={observation.CoolingDown} ready={observation.Ready} actorActive={expectedActorActive}");
        return observation;
    }

    internal static BurstReadyState IsBurstReadyByClassify(ImageRegion imageRegion)
    {
        using var qRa = imageRegion.DeriveCrop(AutoFightAssets.Get(imageRegion).QRectForClassify);
        var topClass = QBurstClassifier.UsePredictor(p => p.Classify(qRa.CacheImage).GetTopClass());
        return ClassifyBurstReadiness(topClass.Name.Name, topClass.Confidence);
    }

    internal static BurstReadyState ClassifyBurstReadiness(string? topClassName, double confidence)
    {
        // 置信度不足时，直接返回未知，避免误判导致漏放/乱放
        if (string.IsNullOrWhiteSpace(topClassName) || !double.IsFinite(confidence) || confidence <= 0.7)
        {
            return BurstReadyState.Unknown;
        }

        if (topClassName.Contains("cd 1", StringComparison.OrdinalIgnoreCase))
        {
            return BurstReadyState.Cooldown;
        }

        if (topClassName.Contains("energy 1 cd 0", StringComparison.OrdinalIgnoreCase))
        {
            return BurstReadyState.Ready;
        }

        return BurstReadyState.Unknown;
    }

    /// <summary>
    /// 通过 ONNX 分类器判断当前场上角色的E技能（元素战技）是否就绪（仅对场上角色有效）
    /// </summary>
    /// <param name="onlyCode01Ready">
    /// 编号段（第 3 段）策略：
    /// <list type="bullet">
    /// <item><c>true</c>（默认）：仅 "01" 才视为就绪，其他编号（E 技能开启后的特殊状态图标）保守视为冷却中。</item>
    /// <item><c>false</c>：任意编号都参与就绪判定，不因编号挡掉 Ready。</item>
    /// </list>
    /// </param>
    public (SkillCdState State, string? Code) IsESkillReadyByClassify(ImageRegion imageRegion, bool onlyCode01Ready = true)
    {
        var eRect1080 = AutoFightAssets.Get(imageRegion).ERectForClassify;
        using var eRa = imageRegion.DeriveCrop(eRect1080);
        var result = ESkillClassifierLazy.Value.Predictor.Classify(eRa.CacheImage);
        var topClass = result.GetTopClass();
        var topClassName = topClass.Name.Name;
        // Logger.LogInformation("E技能就绪分类：{ClassName}，置信度：{Confidence:F2}", topClassName, topClass.Confidence);

        (SkillCdState State, string? Code) classifyResult;

        // 置信度不足时，直接返回未知，避免误判导致漏放/乱放
        if (topClass.Confidence <= 0.7)
        {
            // Logger.LogInformation("E技能就绪分类置信度不足：{Confidence:F2}，类别：{ClassName}", topClass.Confidence, topClassName);
            classifyResult = (SkillCdState.Unknown, null);
        }
        else
        {
            // e_classify_sim 模型实际输出类别名格式: "<前缀> <角色名> <编号> <状态>"，
            // 实测样本: "S Arlecchino 01 nocd" (confidence 1.0) 表示无冷却/就绪。
            // 第 2 段为角色英文名，与当前 Avatar 的 CombatAvatar.NameEn 做不区分大小写比对；
            // 不匹配说明分类结果不属于本角色（模型未覆盖该角色或截图与当前 Avatar 错位），返回未知避免误判。
            var parts = topClassName.Split(' ');
            if (parts.Length < 4 ||
                !string.Equals(parts[1], CombatAvatar.NameEn, StringComparison.OrdinalIgnoreCase))
            {
                classifyResult = (SkillCdState.Unknown, null);
            }
            else
            {
                // 编号段（第 3 段）必须为 "01" 才视为就绪；其他编号对应 E 技能开启后的特殊状态图标，
                // 保守视为冷却中，避免在该状态下误判为就绪而错放技能。
                if (onlyCode01Ready &&
                    !string.Equals(parts[2], "01", StringComparison.OrdinalIgnoreCase))
                {
                    classifyResult = (SkillCdState.Cooldown, parts[2]);
                }
                else if (topClassName.Contains("nocd", StringComparison.OrdinalIgnoreCase))
                {
                    classifyResult = (SkillCdState.Ready, parts[2]);
                }
                // 冷却状态实测样本: "S Arlecchino 01 cd"
                // 顺序重要：nocd 含 cd 子串，必须先判断 nocd 再判断 cd。
                else if (topClassName.Contains("cd", StringComparison.OrdinalIgnoreCase))
                {
                    classifyResult = (SkillCdState.Cooldown, parts[2]);
                }
                else
                {
                    classifyResult = (SkillCdState.Unknown, parts[2]);
                }
            }
        }

        // 识别结果写入 VM，由需要显示的模块（如 AutoComboRunTask）订阅 INPC 变更后绘制（数据与显示解耦）
        var domainToCaptureFactor = (double)TaskContext.Instance().SystemInfo.CaptureAreaRect.Width / imageRegion.Width;
        var eRectCapture = new Rect((int)(eRect1080.X * domainToCaptureFactor), (int)(eRect1080.Y * domainToCaptureFactor),
            (int)(eRect1080.Width * domainToCaptureFactor), (int)(eRect1080.Height * domainToCaptureFactor));
        ESkillClassifyViewModel.Instance.Result = new ESkillClassifyResult
        {
            State = classifyResult.State,
            Code = classifyResult.Code,
            AvatarName = CombatAvatar.Name,
            ClassifyRect = eRectCapture.ToWindowsRectangle(),
            TextPosition = new System.Windows.Point(eRectCapture.X, eRectCapture.Y - 24 * domainToCaptureFactor),
        };
        return classifyResult;
    }

    // /// <summary>
    // /// 元素爆发是否正在CD中
    // /// 右下 157x165
    // /// 110x110
    // /// </summary>
    // public double GetBurstCurrentCd(CaptureContent content)
    // {
    //     var qRa = content.CaptureRectArea.Crop(AutoFightAssets.Get(content.CaptureRectArea).QRect);
    //     var text = OcrFactory.Paddle.Ocr(qRa.SrcGreyMat);
    //     return StringUtils.TryParseDouble(text);
    // }

    /// <summary>
    /// 冲刺
    /// </summary>
    public void Dash(int ms = 0)
    {
        if (Ct is { IsCancellationRequested: true })
        {
            return;
        }

        if (ms == 0)
        {
            ms = 200;
        }

        InputHub.Foreground.SimulateAction(GIActions.SprintMouse, KeyType.KeyDown);
        try { Sleep(ms, Ct); }
        finally { InputHub.Foreground.SimulateAction(GIActions.SprintMouse, KeyType.KeyUp); }
    }

    public void Walk(string key, int ms)
    {
        if (Ct is { IsCancellationRequested: true })
        {
            return;
        }

        User32.VK vk = User32.VK.VK_NONAME;
        if (key == "w")
        {
            vk = GIActions.MoveForward.ToActionKey().ToVK();
        }
        else if (key == "s")
        {
            vk = GIActions.MoveBackward.ToActionKey().ToVK();
        }
        else if (key == "a")
        {
            vk = GIActions.MoveLeft.ToActionKey().ToVK();
        }
        else if (key == "d")
        {
            vk = GIActions.MoveRight.ToActionKey().ToVK();
        }

        if (vk == User32.VK.VK_NONAME)
        {
            return;
        }

        InputHub.Foreground.Keyboard.KeyDown(vk);
        try { Sleep(ms, Ct); }
        finally { InputHub.Foreground.Keyboard.KeyUp(vk); }
    }

    /// <summary>
    /// 移动摄像机
    /// </summary>
    /// <param name="pixelDeltaX">负数是左移，正数是右移</param>
    /// <param name="pixelDeltaY"></param>
    public void MoveCamera(int pixelDeltaX, int pixelDeltaY)
    {
        InputHub.Foreground.Mouse.MoveMouseBy(pixelDeltaX, pixelDeltaY);
    }

    /// <summary>
    /// 等待
    /// </summary>
    /// <param name="ms"></param>
    public void Wait(int ms)
    {
        Sleep(ms, Ct); // 增强执行链按预算分片；宏持有键由输入所有者负责 finally 释放。
    }

    /// <summary>
    /// 等待完成
    /// </summary>
    public void Ready()
    {
        Sleep(10, Ct);

        for (int i = 0; i < 20; i++)
        {
            if (Ct is { IsCancellationRequested: true })
            {
                return;
            }

            using var region = CaptureToRectArea();
            // 等待角色编号块出现
            if (PartyAvatarSideIndexHelper.HasAnyIndexRect(region))
            {
                region.Dispose();
                return;
            }

            Sleep(150, Ct);
        }
    }

    /// <summary>
    ///
    /// 根据cd推算E技能是否好了
    /// </summary>
    /// <param name="skillCd">强制指定技能CD</param>
    /// <param name="printLog">log是否输出</param>
    /// <returns>是否好了</returns>
    public bool IsSkillReady(bool printLog = false)
    {
        var cd = GetSkillCdSeconds();
        if (cd > 0)
        {
            if (printLog)
            {
                Logger.LogInformation("{Name}的E技能未准备好,CD还有{Seconds}秒", Name, Math.Round(cd, 2));
            }

            return false;
        }

        return true;
    }

    internal bool IsSkillReadyFromCurrentFrame()
    {
        Ct.ThrowIfCancellationRequested();
        using var capture = CaptureToRectArea();
        return IsSkillReadyFromCurrentFrame(capture);
    }

    internal bool IsSkillReadyFromCurrentFrame(ImageRegion capture, double? observedCooldown = null)
    {
        Ct.ThrowIfCancellationRequested();
        var active = IsActive(capture);
        CombatActionScope.Current?.Trace("e-actor", $"actor={Name} active={active}");
        if (!active) return false;
        var cooldown = observedCooldown ?? ReadSkillCurrentCd(capture);
        if (!double.IsFinite(cooldown) || cooldown < 0) return false;
        if (cooldown > 0)
        {
            ESkillCdTracker.Record(Name, cooldown);
            return false;
        }
        // 复用现有 E 冷却色块检测；只检查一次，不等待也不重复切人。
        var ready = !CombatHudReader.HasCooldownPixels(capture, burst: false);
        CombatActionScope.Current?.Trace("e-ready", $"actor={Name} ready={ready} cd={cooldown:F3}");
        return ready;
    }

    /// <summary>
    /// 纯 OCR 视角的 E 技能三态：只依据 OCR 记录（<see cref="OcrSkillCd"/>）与最近一次使用时间
    /// （<see cref="LastSkillTime"/>）的相对新旧判断记录可信度。
    /// </summary>
    private SkillCdState GetSkillCdStateFromRecord()
    {
        // OCR 记录晚于最近一次使用 → 记录可信
        if (OcrSkillCd > LastSkillTime)
        {
            return DateTime.UtcNow > OcrSkillCd ? SkillCdState.Ready : SkillCdState.Cooldown;
        }

        // 从未使用过（默认时间）→ 就绪；否则记录是过期的（用过但没读到 CD）
        return LastSkillTime == default ? SkillCdState.Ready : SkillCdState.Unknown;
    }

    /// <summary>
    /// 获取 E 技能的综合三态冷却状态（就绪 / 冷却中 / 未知）。
    /// 优先级：<see cref="ManualSkillCd"/> 手动配置 → 场上角色走视觉判定（<see cref="IsESkillReadyByClassify"/>）→ 复用截图跑 OCR 读 CD → <see cref="GetSkillCdStateFromRecord"/> OCR 视角推算。
    /// 视觉判定仅对场上角色有效；后台角色或视觉返回 Unknown 时降级到 OCR 推算。
    /// </summary>
    /// <param name="onlyCode01Ready">
    /// 透传给 <see cref="IsESkillReadyByClassify"/>：
    /// <c>true</c>（默认）仅原始 E（编号 "01"）就绪才返回 Ready，特殊状态技能（02/03...）视为 Cooldown；
    /// <c>false</c> 时特殊状态技能就绪也返回 Ready，但清零 <see cref="OcrSkillCd"/> 仍只对编号 "01" 生效（特殊状态技能 Ready 不污染原始 E 的 OCR 视角记录）。
    /// </param>
    public SkillCdState GetSkillCdState(bool onlyCode01Ready = true)
    {
        // 手动配置：直接按上次释放时间 + 手动 CD 判断
        if (ManualSkillCd > 0)
        {
            var dif = DateTime.UtcNow - LastSkillTime;
            return ManualSkillCd > dif.TotalSeconds ? SkillCdState.Cooldown : SkillCdState.Ready;
        }

        // 场上角色走视觉判定优先：用 ONNX 分类器判 E 技能状态，置信度足够时优先返回
        using (var region = CaptureToRectArea())
        {
            var context = new AvatarActiveCheckContext();
            if (CombatScenes.GetActiveAvatarIndex(region, context) == Index)
            {
                var (state, _) = ReadSkillCdFromScreenshot(region, onlyCode01Ready);
                if (state != SkillCdState.Unknown)
                {
                    return state;
                }

                // state == Unknown → 落到 OCR 记录推算
            }
            // 后台角色 → 走 OCR 记录推算
        }

        return GetSkillCdStateFromRecord();
    }

    /// <summary>
    /// 复位 E 技能的使用记录（<see cref="OcrSkillCd"/> 与 <see cref="LastSkillTime"/>）
    /// </summary>
    public void ResetSkillCdRecord()
    {
        OcrSkillCd = default;
        LastSkillTime = default;
    }

    /// <summary>
    /// 计算上一次使用技能到现在还剩下多长时间的cd
    /// </summary>
    /// <returns></returns>
    public double GetSkillCdSeconds()
    {
        switch (ManualSkillCd)
        {
            case < 0:
                {
                    var now = DateTime.UtcNow;
                    // 若未经过OCR的技能释放,上次时间加上最长的技能时间
                    var maxCd = Math.Max(CombatAvatar.SkillHoldCd, CombatAvatar.SkillCd);
                    var target =
                        LastSkillTime >= OcrSkillCd
                            ? LastSkillTime.AddSeconds(Math.Max(CombatAvatar.SkillHoldCd, CombatAvatar.SkillCd))
                            : OcrSkillCd;
                    var result = now > target ? 0d : (target - now).TotalSeconds;
                    if (!(result > maxCd)) return result;
                    Logger.LogWarning("{Name}的当前技能CD大于其最大技能CD{MaxCd}。如果你没有调整系统时间的话，这是一个bug。", Name, maxCd);
                    return maxCd;
                }
            case > 0:
                {
                    // 用户设置，所以直接通过上次释放技能的时间计算
                    var dif = DateTime.UtcNow - LastSkillTime;
                    if (ManualSkillCd > dif.TotalSeconds)
                    {
                        return ManualSkillCd - dif.TotalSeconds;
                    }

                    break;
                }
        }

        return 0;
    }

    /// <summary>
    /// 计算剩余技能 CD 的可信版本：与 <see cref="GetSkillCdState"/> 同源，只依据
    /// <see cref="ManualSkillCd"/> 手动配置与 <see cref="OcrSkillCd"/> OCR 记录，
    /// 不使用 <see cref="CombatAvatar.SkillCd"/> 做推算（对 CD 从持续时间结束后才起算的角色不准）。
    /// 返回值三态：&gt;0 冷却中剩余秒数；0 确定就绪；null 未知（用过但没读到 CD）。
    /// </summary>
    public double? GetSkillCdSecondsV2()
    {
        if (ManualSkillCd > 0)
        {
            // 用户设置，直接通过上次释放技能的时间计算；手动配置不存在未知态
            var dif = DateTime.UtcNow - LastSkillTime;
            return ManualSkillCd > dif.TotalSeconds ? ManualSkillCd - dif.TotalSeconds : 0;
        }

        // OCR 记录可信判定与 GetOcrSkillCdState 一致：只认晚于最近一次使用时间的记录
        if (OcrSkillCd > LastSkillTime)
        {
            var remaining = (OcrSkillCd - DateTime.UtcNow).TotalSeconds;
            return remaining > 0 ? remaining : 0;
        }

        // 从未使用过 → 就绪；用过但记录过期/缺失 → 未知
        return LastSkillTime == default ? 0 : null;
    }

    /// <summary>
    /// 等待技能CD
    /// </summary>
    /// <param name="ct">CancellationToken</param>
    public async Task WaitSkillCd(CancellationToken ct = default)
    {
        // 获取CD时间
        if (IsSkillReady())
        {
            return;
        }

        var s = GetSkillCdSeconds() + 0.2;
        Logger.LogInformation("{Name}的E技能CD未结束，等待{Seconds}秒", Name, Math.Round(s, 2));
        await Delay((int)Math.Ceiling(s * 1000), ct);
    }

    /// <summary>
    /// 跳跃
    /// </summary>
    public void Jump()
    {
        InputHub.Foreground.SimulateAction(GIActions.Jump);
    }

    /// <summary>
    /// 重击
    /// </summary>
    public void Charge(int ms = 0)
    {
        // 默认重击持续 1 秒；必须在特化分派前归一化，否则特化 handler 收到 ms=0 会异常
        if (ms == 0)
        {
            ms = 1000;
        }

        if (AvatarSpecialAction.ExecuteSpecializedAction(this, "Charge", Name, new ActionArgs(Ms: ms))) return;

        InputHub.Foreground.SimulateAction(GIActions.NormalAttack, KeyType.KeyDown);
        try { Sleep(ms, Ct); }
        finally { InputHub.Foreground.SimulateAction(GIActions.NormalAttack, KeyType.KeyUp); }
    }

    public void MouseDown(string key = "left")
    {
        key = key.ToLower();
        if (key == "left")
        {
            InputHub.Foreground.Mouse.LeftButtonDown();
        }
        else if (key == "right")
        {
            InputHub.Foreground.Mouse.RightButtonDown();
        }
        else if (key == "middle")
        {
            InputHub.Foreground.Mouse.MiddleButtonDown();
        }
    }

    public void MouseUp(string key = "left")
    {
        key = key.ToLower();
        if (key == "left")
        {
            InputHub.Foreground.Mouse.LeftButtonUp();
        }
        else if (key == "right")
        {
            InputHub.Foreground.Mouse.RightButtonUp();
        }
        else if (key == "middle")
        {
            InputHub.Foreground.Mouse.MiddleButtonUp();
        }
    }

    public void Click(string key = "left")
    {
        key = key.ToLower();
        if (key == "left")
        {
            InputHub.Foreground.Mouse.LeftButtonClick();
        }
        else if (key == "right")
        {
            InputHub.Foreground.Mouse.RightButtonClick();
        }
        else if (key == "middle")
        {
            InputHub.Foreground.Mouse.MiddleButtonClick();
        }
    }

    public void MoveBy(int x, int y)
    {
        using (AvatarRecognition.BeginExclusiveOperation())
        {
            GlobalMethod.MoveMouseBy(x, y);
        }
    }

    public void Scroll(int scrollAmountInClicks)
    {
        InputHub.Foreground.Mouse.VerticalScroll(scrollAmountInClicks);
    }

    // 鼠标键（VK_LBUTTON、VK_RBUTTON、VK_MBUTTON、VK_XBUTTON1、VK_XBUTTON2）由输入通道转成对应的鼠标键
    public void KeyDown(string key)
    {
        InputHub.Foreground.Keyboard.KeyDown(KeyBindingsSettingsPageViewModel.MappingKey(User32Helper.ToVk(key)));
    }

    public void KeyUp(string key)
    {
        InputHub.Foreground.Keyboard.KeyUp(KeyBindingsSettingsPageViewModel.MappingKey(User32Helper.ToVk(key)));
    }

    public void KeyPress(string key)
    {
        InputHub.Foreground.Keyboard.KeyPress(KeyBindingsSettingsPageViewModel.MappingKey(User32Helper.ToVk(key)));
    }

    /// <summary>
    /// 从配置字符串中查找角色cd
    /// 仅有角色名时返回 -1 ,没找到角色返回null
    /// </summary>
    /// <param name="avatarName">角色名</param>
    /// <param name="input">序列</param>
    /// <returns></returns>
    public static double? ParseActionSchedulerByCd(string avatarName, string input)
    {
        if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(avatarName))
            return null;

        var searchIndex = input.Length - 1;

        while (true)
        {
            // 逆向查找角色名最后一次出现的位置
            var foundIndex = input.LastIndexOf(avatarName, searchIndex, StringComparison.Ordinal);
            if (foundIndex == -1) return null;

            // 验证前向边界（分号或字符串起点）
            var startValid = foundIndex == 0 ||
                             input[foundIndex - 1] == ';';

            // 验证后向边界（逗号或分号/字符串终点）
            var endValid = foundIndex + avatarName.Length == input.Length ||
                           input[foundIndex + avatarName.Length] == ',' ||
                           input[foundIndex + avatarName.Length] == ';';

            if (startValid && endValid)
            {
                var valueStart = foundIndex + avatarName.Length;
                // 处理逗号后的数值部分
                if (valueStart >= input.Length || input[valueStart] != ',') return -1;
                var valueEnd = input.IndexOf(';', valueStart);
                if (valueEnd == -1) valueEnd = input.Length;

                if (double.TryParse(input.AsSpan(valueStart + 1, valueEnd - valueStart - 1),
                        out var result))
                {
                    return result;
                }

                // 存在角色名但没有数值的情况
                return -1;
            }

            // 更新搜索范围继续查找
            searchIndex = foundIndex - 1;
            if (searchIndex < 0) break;
        }

        return null;
    }
}
