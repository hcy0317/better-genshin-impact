using BetterGenshinImpact.Core.Recognition.ONNX;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.AutoFight.Assets;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static BetterGenshinImpact.GameTask.Common.TaskControl;
using BetterGenshinImpact.GameTask.Common.Job;
using OpenCvSharp;
using BetterGenshinImpact.GameTask.AutoPick.Assets;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.AutoPathing.Handler;
using BetterGenshinImpact.GameTask.AutoPathing.Model;

namespace BetterGenshinImpact.GameTask.AutoFight;

public class AutoFightJsonTask : ISoloTask
{
    public string Name => "自动战斗(JSON策略)";

    private readonly AutoFightParam _taskParam;
    private readonly JsonCombatStrategy _strategy;
    private CancellationToken _ct;

    private DateTime _lastFightFlagTime = DateTime.Now;

    private readonly ReturnMainUiTask _returnMainUiTask = new();
    private readonly double _assetScale = TaskContext.Instance().SystemInfo.AssetScale;
    private readonly double _dpi = TaskContext.Instance().DpiScale;


    /// <summary>
    /// 当前队伍中的角色名集合（用于过滤动作节点）
    /// </summary>
    private HashSet<string> _teamCharacterNames = new(StringComparer.OrdinalIgnoreCase);

    // 日志防刷：1秒内同一动作名至多输出一次日志
    private string _lastLoggedActionName = "";
    private DateTime _lastLogTime = DateTime.MinValue;

    /// <summary>
    /// 当前操作的角色名（私有状态，不污染全局 CurrentAvatarName）
    /// </summary>
    private string _currentAvatarName = "";

    /// <summary>
    /// 展开后的优先级动作条目
    /// 每个 JsonAction 展开为 1+N 个条目（1个主条件 + N个 morePriorities）
    /// </summary>
    private class PrioritizedAction
    {
        public JsonAction Action { get; set; }
        public string Expression { get; set; }
        public int Priority { get; set; }
    }

    // 战斗点位
    public static WaypointForTrack? FightWaypoint { get; set; } = null;

    private AutoFightTask.TaskFightFinishDetectConfig _finishDetectConfig;

    public AutoFightJsonTask(AutoFightParam taskParam)
    {
        _taskParam = taskParam;
        _strategy = JsonCombatStrategyParser.ParseFile(_taskParam.CombatStrategyPath);
        _finishDetectConfig = new AutoFightTask.TaskFightFinishDetectConfig(_taskParam);
    }

    /// <summary>
    /// 获取战斗场景；复用统一初始化与截图/场景生命周期。
    /// </summary>
    public CombatScenes GetCombatScenesWithRetry() => CombatScenes.GetCombatScenesWithRetry();

    /// <summary>
    /// 启动自动战斗（JSON策略模式）
    /// </summary>
    /// <param name="ct">取消令牌</param>
    public async Task Start(CancellationToken ct)
    {
        _ct = ct;
        _finishDetectConfig.EndConfirmed = false;
        _finishDetectConfig.FinishEvidenceId = Guid.NewGuid().ToString();
        _finishDetectConfig.FinishEvidenceCount = 0;
        _finishDetectConfig.FinishFrameSequence = 0;
        CombatRuntimeMetrics.Shared.Reset();
        AvatarRecognition.SetCurrentAutoFightParam(_taskParam);
        AvatarRecognition.ClearLegendaryBarTracker();
        AvatarRecognition.ClearPassiveObservation();
        CancellationTokenSource? cts2 = null;
        ExperienceDetector? expDetector = null;

        async Task StopExperienceDetectorAsync()
        {
            var detector = expDetector;
            if (detector == null) return;

            expDetector = null;
            try
            {
                await detector.StopAsync();
            }
            finally
            {
                detector.Dispose();
            }
        }

        try
        {
            AutoFightTask.ValidateAndLogCombatSafetyConfiguration(Logger, _taskParam);
            LogScreenResolution();
            var combatScenes = CombatScenes.GetCombatScenesWithRetry();
            // 收集当前队伍角色名
            foreach (var avatar in combatScenes.GetAvatars())
            {
                _teamCharacterNames.Add(avatar.Name);
            }
            Logger.LogInformation("JSON 策略：当前队伍角色：{Names}", string.Join(", ", _teamCharacterNames));
            // 增强 JSON 一次编译全部根，缺角色或不合法依赖不能经旧过滤器静默裁剪。
            using var flow = NativeCombatFlowRunner.Create(_strategy, combatScenes, _taskParam)!;
            using var battleHost = NativeCombatBattleHostIo.Create(flow, combatScenes, _taskParam);
            _finishDetectConfig.FinishEvidenceId = flow.Context.BattleId.ToString();
            _finishDetectConfig.Diagnostics = new(Logger);

            // 角色过滤和全部优先级由JsonCombatFlowExecution一次编译，避免两个维护来源。

            // 新的取消token
            cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
            combatScenes.BeforeTask(cts2.Token);
            // 设置初始当前角色名（用于无 Character 字段的通用 action 回退）
            _currentAvatarName = CombatScriptParser.CurrentAvatarName;

            AutoFightSeek.ResetSeekState();
            AutoFightTask.FightStatusFlag = true;
            _fightEndFlag = false;
            _finishCheckRequested = false;
            _periodicFinishCheckRequested = false;

            var fightEndFlag = false;
            var skipPostFightPickupFlag = false;
            string lastFightName = "";
            // 基于经验值的战后拾取检测
            if (_taskParam.KazuhaPickupEnabled && _taskParam.ExpBasedPickupEnabled)
            {
                using var gameCaptureRegion = CaptureToRectArea();
                var expRos = AutoFightAssets.Get(gameCaptureRegion).ExperienceRecognitionObjects;
                expDetector = new ExperienceDetector(expRos, cts2.Token);
                expDetector.Start();
            }

            // 战斗前动作
            await RunPreActions(combatScenes);
            var fightTask = Task.Run(async () =>
            {
                try
                {
                    AutoFightTask.LastFightFinishCheckTime = DateTime.Now;
                    AutoFightTask.FightStartTime = DateTime.Now;

                    while (!cts2.Token.IsCancellationRequested)
                    {
                        var hostResult = await battleHost.AdvanceAsync(flow, cts2.Token);
                        lastFightName = flow.Context.LastObservedActor ?? lastFightName;
                        AutoFightTask.TraceFlowHost(_finishDetectConfig, flow, false, false, battleHost);
                        if (NativeCombatBattleHostIo.ApplyResult(battleHost, hostResult, _finishDetectConfig)) break;
                    }
                }
                catch (Exception e)
                {
                    Debug.WriteLine(e.Message);
                    Debug.WriteLine(e.StackTrace);
                    throw;
                }
                finally
                {
                    Simulation.ReleaseAllKey();
                    AutoFightTask.FightStatusFlag = false;
                }
            }, cts2.Token);

            // 在持续索敌循环启动前标记战斗进行中，避免索敌循环因 FightStatusFlag 仍为 false 而立即退出
            AutoFightTask.FightStatusFlag = true;

            // 启动持续索敌循环（异步后台运行，与战斗任务并发）
            // 使用独立的 CancellationTokenSource，以便在战后独立取消索敌循环，不影响 cts2 关联的其他组件（如 expDetector）
            using var targetingCts = CancellationTokenSource.CreateLinkedTokenSource(cts2.Token);
            Task? targetingTask = null;
            if (flow != null || _taskParam.EnableCombatTargeting)
            {
                targetingTask = Task.Run(async () =>
                {
                    try
                    {
                        await AvatarRecognition.ContinuousTargetingLoopAsync(targetingCts.Token, () => !AutoFightTask.FightStatusFlag,
                            _finishDetectConfig.FinishEvidenceId);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception e)
                    {
                        Logger.LogError(e, "持续索敌循环异常");
                    }
                }, targetingCts.Token);
            }

            try
            {
            try
            {
                await fightTask;
            }
            finally
            {
                // 战斗结束后（无论正常/异常），停止并等待索敌循环完成清理（ReleaseAllKey / MiddleButtonClick），
                // 避免其 finally 在拾取/切人过程中释放按键，干扰万叶E吸怪等操作
                if (targetingTask != null)
                {
                    await targetingCts.CancelAsync();
                    try { await targetingTask; } catch (OperationCanceledException) { }
                }
                battleHost?.Dispose();
                flow?.Dispose();
                AutoFightTask.FightStatusFlag = false;
            }

                ct.ThrowIfCancellationRequested();
                AutoFightTask.EnsureFightFinishConfirmed(_taskParam.FightFinishDetectEnabled, !skipPostFightPickupFlag,
                    AutoFightParam.IsSeekRotationLimitReached(AutoFightSeek.RotationCount) ? "找敌次数上限" : "战斗时间上限");
                if (skipPostFightPickupFlag)
                {
                    Logger.LogInformation("战斗被强制结束，跳过战后拾取");
                    return;
                }

                // 基于经验值检测结果的拾取判断
                if (_taskParam.KazuhaPickupEnabled && _taskParam.ExpBasedPickupEnabled && expDetector != null)
                {
                    if (!expDetector.HasDetectedExperience)
                    {
                        Logger.LogInformation("基于经验值判断：等待经验值检测结果");
                        var waitMs = 1100;
                        while (!expDetector.HasDetectedExperience && waitMs > 0)
                        {
                            await Delay(100, _ct);
                            waitMs -= 100;
                        }
                    }

                    var shouldPickup = expDetector.HasDetectedExperience;
                    Logger.LogInformation("基于经验值判断：{Result} 战后拾取", shouldPickup ? "执行" : "不执行");

                    if (!shouldPickup)
                    {
                        if (_taskParam is { PickDropsAfterFightEnabled: true })
                        {
                            await new ScanPickTask().Start(_ct);
                        }
                        return;
                    }
                }
            }
            finally
            {
                await StopExperienceDetectorAsync();
            }

            // 战后拾取（完全参照 AutoFightTask）
            await PostFightPickup(combatScenes, skipPostFightPickupFlag, lastFightName);
        }
        catch (Exception error)
        {
            TaskExecutionScope.RethrowCombatFailure(error, _taskParam.FightFinishDetectEnabled, _finishDetectConfig.EndConfirmed, ct);
            throw;
        }
        finally
        {
            // 战斗可能在创建 fightTask 前因复活/恢复异常退出，统一清理战斗状态。
            AutoFightTask.FightStatusFlag = false;
            AvatarRecognition.ClearCurrentAutoFightParam();
            try
            {
                await StopExperienceDetectorAsync();
            }
            catch (Exception e)
            {
                Logger.LogWarning(e, "停止 JSON 战斗经验检测时发生异常");
            }
            try
            {
                cts2?.Cancel();
            }
            catch (Exception e)
            {
                Logger.LogWarning(e, "取消 JSON 战斗令牌时发生异常");
            }
            finally
            {
                cts2?.Dispose();
            }
        }
    }

    private bool _fightEndFlag;
    private bool _finishCheckRequested;
    private bool _periodicFinishCheckRequested;

    private bool ShouldRequestFinishCheckBeforeAction(JsonAction action)
    {
        if (!_finishDetectConfig.RotateFindEnemyEnabled || !_taskParam.CheckBeforeBurst)
        {
            return false;
        }

        try
        {
            var character = string.IsNullOrEmpty(action.Character)
                ? _currentAvatarName
                : action.Character;
            return CombatScriptParser.ParseLinePart(action.Action, character).Any(IsBurstFinishCheckCommand);
        }
        catch (Exception e)
        {
            Logger.LogWarning("自动战斗：{Name} 预扫描战斗结束检测失败：{Msg}", action.Name, e.Message);
            return false;
        }
    }

    private static bool IsBurstFinishCheckCommand(CombatCommand command)
    {
        return command.Method == Method.Burst || command.Args.Contains("q") || command.Args.Contains("Q");
    }


    /// <summary>执行战斗前动作</summary>
    private async Task RunPreActions(CombatScenes combatScenes)
    {
        if (_strategy.Info.PreActions == null || _strategy.Info.PreActions.Count == 0)
            return;

        Logger.LogInformation("JSON 策略：执行战斗前动作");

        await RunPreActionSequenceAsync(_strategy.Info.PreActions, async preAction =>
        {
            var firstSpaceIndex = preAction.IndexOf(' ');
            var character = _currentAvatarName;
            var commands = preAction;
            if (firstSpaceIndex > 0)
            {
                character = preAction[..firstSpaceIndex];
                commands = preAction[(firstSpaceIndex + 1)..];
            }

            var cmdList = CombatScriptParser.ParseLineCommands(commands, character);
            var combatScript = new CombatScript([character], cmdList);

            await CombatScriptExecutor.ExecuteAsync(combatScript, _ct, Logger, combatScenes);
        }, () => Delay(300, _ct), Logger, _ct);
    }

    internal static async Task RunPreActionSequenceAsync(IEnumerable<string> actions, Func<string, Task> execute,
        Func<Task> betweenActions, ILogger logger, CancellationToken ct)
    {
        foreach (var preAction in actions)
        {
            ct.ThrowIfCancellationRequested();
            try { await execute(preAction); }
            catch (RetryException e) when (e is not CombatRecoveryCompletedException and not DomainDefeatedRetryException)
            {
                logger.LogWarning("战斗前动作重试异常，跳过此动作继续：{Msg}", e.Message);
            }
            catch (RetryException e)
            {
                logger.LogWarning("战斗前动作要求中断当前战斗：{Msg}", e.Message);
                throw;
            }
            logger.LogInformation("战斗前动作：{Action}", preAction);
            await betweenActions();
        }
    }

    /// <summary>战后拾取</summary>
    private async Task PostFightPickup(
        CombatScenes combatScenes,
        bool skipPostFightPickupFlag,
        string lastFightName)
    {
        if (skipPostFightPickupFlag)
        {
            Logger.LogInformation("战斗被强制结束，跳过战后拾取");
            return;
        }

        if (_taskParam.KazuhaPickupEnabled)
        {
            var picker = combatScenes.SelectAvatar("枫原万叶") ?? combatScenes.SelectAvatar("琴");

            string? oldPartyName = null;
            if (RunnerContext.Instance.PartyName is not null)
            {
                oldPartyName = RunnerContext.Instance.PartyName;
            }
            else if (picker is null && !string.IsNullOrEmpty(_taskParam.KazuhaPartyName))
            {
                Logger.LogWarning("换队拾取：当前队伍名称为空，尝试读取！");
                await Delay(1000, _ct);
                await _returnMainUiTask.Start(_ct);

                for (int attempt = 0; attempt < 6; attempt++)
                {
                    Simulation.SendInput.SimulateAction(GIActions.OpenPartySetupScreen);
                    var enterGameAppear = await NewRetry.WaitForElementAppear(
                        ElementRecognition.Get("PartyBtnChooseView"),
                        () => { },
                        _ct,
                        15,
                        500
                    );
                    if (attempt == 5 && !enterGameAppear)
                    {
                        Logger.LogWarning("换队拾取：读取队伍名称失败，跳过换队拾取步骤");
                        return;
                    }
                }
            }

            if (!string.IsNullOrEmpty(_taskParam.KazuhaPartyName))
            {
                await Delay(1000, _ct);

                var timeWaitStart = 0;
                while (timeWaitStart < 6000)
                {
                    using var ra = CaptureToRectArea();
                    var partyViewBtn = ra.Find(ElementRecognition.Get("PartyBtnChooseView"));
                    if (partyViewBtn.IsExist())
                    {
                        var rawPartyName = ra.Find(new RecognitionObject
                        {
                            RecognitionType = RecognitionTypes.Ocr,
                            RegionOfInterest = new Rect(partyViewBtn.Right, partyViewBtn.Top, (int)(350 * _assetScale),
                                partyViewBtn.Height)
                        }).Text;

                        if (string.IsNullOrWhiteSpace(rawPartyName))
                        {
                            oldPartyName = string.Empty;
                        }
                        else
                        {
                            var tempName = rawPartyName
                                .Replace("\"", "")
                                .Replace("\r\n", "")
                                .Replace("\r", "");

                            int firstNewLineIndex = tempName.IndexOf('\n');
                            if (firstNewLineIndex != -1)
                            {
                                tempName = tempName.Substring(0, firstNewLineIndex);
                            }

                            oldPartyName = tempName.Trim();
                        }

                        Logger.LogInformation("换队拾取：当前队伍名称读取为：{oldPartyName}", oldPartyName);
                        Logger.LogDebug("OCR原始识别文本（含转义）：{rawPartyName}", rawPartyName);
                        RunnerContext.Instance.PartyName = oldPartyName;
                        break;
                    }
                    await Delay(200, _ct);
                    timeWaitStart += 200;
                }
            }

            var switchPartyFlag = false;
            if (picker == null && !skipPostFightPickupFlag && !string.IsNullOrEmpty(_taskParam.KazuhaPartyName) && oldPartyName != _taskParam.KazuhaPartyName)
            {
                try
                {
                    Logger.LogInformation($"切换为拾取队伍：{_taskParam.KazuhaPartyName}");
                    var success = await new SwitchPartyTask().Start(_taskParam.KazuhaPartyName, _ct);
                    if (success)
                    {
                        Logger.LogInformation($"成功切换队伍为{_taskParam.KazuhaPartyName}");
                        switchPartyFlag = true;
                        RunnerContext.Instance.PartyName = _taskParam.KazuhaPartyName;
                        RunnerContext.Instance.ClearCombatScenes();
                        var cs = await RunnerContext.Instance.GetCombatScenes(_ct);
                        picker = cs.SelectAvatar("枫原万叶") ?? cs.SelectAvatar("琴");
                    }
                }
                catch (Exception e)
                {
                    Logger.LogWarning("切换队伍异常，跳过此步骤！{Msg}", e.Message);
                }
            }

            if (picker != null)
            {
                Simulation.ReleaseAllKey();

                if (picker.Name == "枫原万叶")
                {
                    var time = TimeSpan.FromSeconds(picker.GetSkillCdSeconds());

                    bool shouldSkip = lastFightName == picker.Name && time.TotalSeconds > 3;
                    bool forcePickup = _taskParam.QinDoublePickUp;

                    if (forcePickup || !shouldSkip)
                    {
                        Logger.LogInformation("使用 枫原万叶-长E 拾取掉落物");
                        if (picker.TrySwitch(10))
                        {
                            await Delay(100, _ct);
                            await picker.WaitSkillCd(_ct);
                            await SimulateHoldElementalSkillAsync(800, _ct);
                            await SimulateMouseLeftClickLoopAsync(6, _ct);
                            await Delay(1500, _ct);
                            picker.AfterUseSkill();
                            if (AutoFightParam.ShouldRunKazuhaGatheredDropsScan(
                                    _taskParam.KazuhaPickupEnabled,
                                    _taskParam.PickDropsAfterFightEnabled))
                            {
                                await new ScanPickTask().Start(_ct, AutoFightParam.KazuhaGatheredDropsScanSeconds);
                            }
                        }
                    }
                    else
                    {
                        Logger.LogInformation("距最近一次万叶出招，时间过短，跳过此次万叶拾取！");
                    }
                }
                else if (picker.Name == "琴")
                {
                    Logger.LogInformation("准备执行 琴-长E 聚物，尚未确认动作完成");

                    var actionsToUse = PickUpCollectHandler.PickUpActions
                        .Where(action => action.StartsWith("琴-长E" + " ", StringComparison.OrdinalIgnoreCase))
                        .Select(action => action.Replace("琴-长E", "琴", StringComparison.OrdinalIgnoreCase))
                        .ToArray();

                    var find = _taskParam.QinDoublePickUp;
                    var gatheringSucceeded = false;
                    if (picker.TrySwitch(10))
                    {
                        await Delay(100, _ct);
                        foreach (var miningActionStr in actionsToUse)
                        {
                            var pickUpAction = CombatScriptParser.ParseContext(miningActionStr);

                            for (int i = 0; i < 2; i++)
                            {
                                await picker.WaitSkillCd(_ct);
                                gatheringSucceeded = GatheredLootCommands.Run(picker, pickUpAction.CombatCommands,
                                    () =>
                                    {
                                        if (!find) return;
                                        using var imagePick = CaptureToRectArea();
                                        if (imagePick.Find(AutoPickAssets.Get(imagePick, TaskContext.Instance().Config.AutoPickConfig.PickKey).PickRo).IsExist())
                                            find = false;
                                    }, () => Simulation.ReleaseAllKey(), _ct);
                                if (!gatheringSucceeded)
                                {
                                    Logger.LogWarning("琴聚物命令未确认执行，停止后续动作及成功短扫");
                                    break;
                                }

                                if (!find)
                                {
                                    break;
                                }

                                if (i == 0)
                                {
                                    Logger.LogInformation("自动拾取；尝试再次执行 琴-长E 拾取");
                                    picker.AfterUseSkill();
                                }
                                else
                                {
                                    break;
                                }
                            }

                            Simulation.ReleaseAllKey();
                        }
                    }
                    if (gatheringSucceeded && AutoFightParam.ShouldRunKazuhaGatheredDropsScan(
                        _taskParam.KazuhaPickupEnabled, _taskParam.PickDropsAfterFightEnabled))
                    {
                        Logger.LogInformation("琴聚物动作完成，执行3秒短时扫描拾取");
                        await new ScanPickTask().Start(_ct, AutoFightParam.KazuhaGatheredDropsScanSeconds);
                    }
                }
            }

            if (switchPartyFlag && !string.IsNullOrEmpty(oldPartyName))
            {
                try
                {
                    Logger.LogInformation($"切换为原队伍：{oldPartyName}");
                    var success = await new SwitchPartyTask().Start(oldPartyName, _ct);
                    if (success)
                    {
                        Logger.LogInformation($"切换为原队伍{oldPartyName}");
                        switchPartyFlag = true;
                        RunnerContext.Instance.PartyName = oldPartyName;
                        RunnerContext.Instance.ClearCombatScenes();
                        await RunnerContext.Instance.GetCombatScenes(_ct);
                    }
                }
                catch (Exception e)
                {
                    Logger.LogWarning("恢复原队伍失败，跳过此步骤！{Msg}", e.Message);
                }
            }
        }

        if (_taskParam is { PickDropsAfterFightEnabled: true })
        {
            await new ScanPickTask().Start(_ct);
        }
    }

    /// <summary>
    /// 检查并记录屏幕分辨率
    /// </summary>
    private void LogScreenResolution()
    {
        AssertUtils.CheckGameResolution("自动战斗");
    }
}
