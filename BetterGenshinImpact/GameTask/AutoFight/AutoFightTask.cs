using BetterGenshinImpact.Core.Recognition.ONNX;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.Model.Area;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using static BetterGenshinImpact.GameTask.Common.TaskControl;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Job;
using OpenCvSharp;
using BetterGenshinImpact.Helpers;
using Vanara;
using Microsoft.Extensions.DependencyInjection;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.AutoPathing.Handler;
using BetterGenshinImpact.GameTask.AutoPick.Assets;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.AutoFight.Assets;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.Common.Ui;

namespace BetterGenshinImpact.GameTask.AutoFight;

public class AutoFightTask : ISoloTask
{
    public string Name => "自动战斗";

    private readonly AutoFightParam _taskParam;

    private readonly CombatScriptBag _combatScriptBag;

    private CancellationToken _ct;


    private static DateTime _lastFightFlagTime = DateTime.Now; // 战斗标志最近一次出现的时间
    private static int _skipCheckCounter;

    /// <summary>
    /// 重置"敌人可见时跳过战斗结束检查"的连续跳过计数（每场战斗开始时调用，TXT 与 JSON 策略共用）
    /// </summary>
    public static void ResetSkipCheckCounter() => _skipCheckCounter = 0;

    internal static void ValidateAndLogCombatSafetyConfiguration(
        ILogger logger,
        AutoFightParam taskParam)
    {
        var guardianConfigured = !string.IsNullOrWhiteSpace(taskParam.GuardianAvatar);
        logger.LogInformation(
            "自动战斗生效安全配置：持续感知={ContinuousTargeting}，索敌模式={TargetingMode}，旋转寻敌入口={RotateSeek}，盾位={GuardianAvatar}，护盾策略={CoverageMode}，护盾时长={ShieldDurationSeconds}",
            taskParam.EnableCombatTargeting,
            taskParam.CombatTargetingMode,
            taskParam.FinishDetectConfig.RotateFindEnemyEnabled,
            guardianConfigured ? taskParam.GuardianAvatar : "未配置",
            taskParam.GuardianCoverageMode,
            taskParam.GuardianShieldDurationSeconds?.ToString("0.###", CultureInfo.InvariantCulture) ?? "未配置");

        if (taskParam.CombatTargetingMode == CombatTargetingMode.Legacy)
        {
            logger.LogWarning("当前战斗仍使用 Legacy 索敌，可能执行旧的锁定路线长前进；建议切换为 ClosedLoop");
        }

        if (!taskParam.EnableCombatTargeting)
        {
            logger.LogWarning("当前战斗已关闭持续感知，后台不会发布血条、伤害数字与目标轨迹观测");
        }

        if (!GuardianSkillSwitchPolicy.IsCoverageConfigurationValid(
                taskParam.GuardianCoverageMode,
                guardianConfigured,
                taskParam.GuardianShieldDurationSeconds))
        {
            throw new InvalidOperationException(
                "RequireKnownCoverage 需要配置盾奶位和大于 0 的 guardianShieldDurationSeconds");
        }

        if (!guardianConfigured)
        {
            logger.LogWarning("当前战斗未配置盾奶位，护盾边界与索敌预算约束不会启用");
        }
    }

    private readonly double _dpi = TaskContext.Instance().DpiScale;
    
    public static bool FightStatusFlag { get; set; } = false;
    
    
    private readonly double _assetScale = TaskContext.Instance().SystemInfo.AssetScale;
    
    private readonly ReturnMainUiTask _returnMainUiTask = new();

    // 战斗点位
    public static WaypointForTrack? FightWaypoint  {get; set;} = null;
    
    /// <summary>
    /// 最近一次战斗结束检查的时间（TXT 与 JSON 策略共用，供更快触发战斗结束检查判断间隔使用）
    /// </summary>
    public static DateTime LastFightFinishCheckTime { get; set; } = DateTime.Now;

    /// <summary>
    /// 本次战斗的开战时间（TXT 与 JSON 策略共用，供"开战后一段时间阻断战斗结束检查"使用）
    /// </summary>
    public static DateTime FightStartTime { get; set; } = DateTime.Now;

    public class TaskFightFinishDetectConfig
    {
        public int DelayTime = 1500;
        public int DetectDelayTime = 450;
        public Dictionary<string, int> DelayTimes = new();
        public double CheckTime = 5;
        public List<string> CheckNames = new();
        public bool FastCheckEnabled;
        public bool CheckAfterSwitchAvatar = false;
        public bool RotateFindEnemyEnabled = false;
        public bool SkipFightEndCheckWhenEnemyVisible = false;
        public double BlockCheckBeforeBattleSeconds = 0;
        public bool PaimonEndCheckEnabled = true;
        public int PaimonEndCheckDelayMs = 75;
        public CombatTargetingMode CombatTargetingMode = CombatTargetingMode.ClosedLoop;
        public int RotaryFactor = 12;
        internal Func<TimeSpan>? SeekBudgetProvider;
        internal string FinishEvidenceId = Guid.NewGuid().ToString();
        internal long FinishFrameSequence;
        internal int FinishEvidenceCount;
        internal bool EndConfirmed;
        internal CombatDecisionDiagnostics? Diagnostics;

        public TaskFightFinishDetectConfig(AutoFightParam taskParam) : this(taskParam.FinishDetectConfig)
        {
            CombatTargetingMode = taskParam.CombatTargetingMode;
            RotaryFactor = taskParam.RotaryFactor;
        }

        public TaskFightFinishDetectConfig(AutoFightParam.FightFinishDetectConfig finishDetectConfig)
        {
            FastCheckEnabled = finishDetectConfig.FastCheckEnabled;
            CheckAfterSwitchAvatar = finishDetectConfig.CheckAfterSwitchAvatar;
            ParseCheckTimeString(finishDetectConfig.FastCheckParams, out CheckTime, CheckNames);
            ParseFastCheckEndDelayString(finishDetectConfig.CheckEndDelay, out DelayTime, DelayTimes);
            DetectDelayTime =
                (int)((double.TryParse(finishDetectConfig.BeforeDetectDelay, out var result) ? result : 0.45) * 1000);
            RotateFindEnemyEnabled = finishDetectConfig.RotateFindEnemyEnabled;
            SkipFightEndCheckWhenEnemyVisible = finishDetectConfig.SkipFightEndCheckWhenEnemyVisible;
            // 开战阻断时间（秒）限制在 0-10 之间，超出范围时修饰到对应上下限
            BlockCheckBeforeBattleSeconds = Math.Clamp(finishDetectConfig.BlockCheckBeforeBattleSeconds, 0, 10);
            PaimonEndCheckEnabled = finishDetectConfig.PaimonEndCheckEnabled;
            // 派蒙检测延时（秒）限制在 0.05-0.4 之间，超出范围时修饰到对应上下限
            PaimonEndCheckDelayMs = (int)(Math.Clamp(finishDetectConfig.PaimonEndCheckDelay, 0.05, 0.4) * 1000);
        }

        internal TimeSpan GetSeekBudget()
        {
            var requested = SeekBudgetProvider?.Invoke() ?? BoundedSeekPolicy.MaximumBudget;
            return requested <= TimeSpan.Zero
                ? TimeSpan.Zero
                : BoundedSeekPolicy.NormalizeBudget(requested);
        }

        public static void ParseCheckTimeString(
            string input,
            out double checkTime,
            List<string> names)
        {
            checkTime = 5;
            if (string.IsNullOrEmpty(input))
            {
                return; // 直接返回
            }

            var uniqueNames = new HashSet<string>(); // 用于临时去重的集合

            // 按分号分割字符串
            var segments = input.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var segment in segments)
            {
                var trimmedSegment = segment.Trim();

                // 如果是纯数字部分
                if (double.TryParse(trimmedSegment, NumberStyles.Float, CultureInfo.InvariantCulture,
                        out double number))
                {
                    checkTime = number; // 更新 CheckTime
                }
                else if (!uniqueNames.Contains(trimmedSegment)) // 如果是非数字且不重复
                {
                    uniqueNames.Add(trimmedSegment); // 添加到集合
                }
            }

            names.AddRange(uniqueNames); // 将集合转换为列表
        }

        public static void ParseFastCheckEndDelayString(
            string input,
            out int delayTime,
            Dictionary<string, int> nameDelayMap)
        {
            delayTime = 1500;

            if (string.IsNullOrEmpty(input))
            {
                return; // 直接返回
            }

            // 分割字符串，以分号为分隔符
            var segments = input.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var segment in segments)
            {
                var parts = segment.Split(',');

                // 如果是纯数字部分
                if (parts.Length == 1)
                {
                    if (double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
                            out double number))
                    {
                        delayTime = (int)(number * 1000); // 更新 delayTime
                    }
                }
                // 如果是名字,数字格式
                else if (parts.Length == 2)
                {
                    string name = parts[0].Trim();
                    if (double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
                            out double value))
                    {
                        nameDelayMap[name] = (int)(value * 1000); // 更新字典，取最后一个值
                    }
                }
                // 其他格式，跳过不处理
            }
        }
    }

    private TaskFightFinishDetectConfig _finishDetectConfig;

    public AutoFightTask(AutoFightParam taskParam)
    {
        _taskParam = taskParam;
        _combatScriptBag = CombatScriptParser.ReadAndParse(_taskParam.CombatStrategyPath);


        _finishDetectConfig = new TaskFightFinishDetectConfig(_taskParam);
    }
    public CombatScenes GetCombatScenesWithRetry() => CombatScenes.GetCombatScenesWithRetry();

    internal async Task PrepareVisionAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var scenes = GetCombatScenesWithRetry();
        using var flow = Script.Flow.NativeCombatFlowRunner.Create(SelectCombatCommands(scenes), scenes, loop: true, param: _taskParam);
        await flow.PrepareVisionBeforeEntryAsync(ct);
    }

    private List<CombatCommand> SelectCombatCommands(CombatScenes scenes)
    {
        var commands = _combatScriptBag.FindCombatScript(scenes.GetAvatars());
        var names = commands.Select(command => command.Name).Distinct()
            .Select(name => scenes.SelectAvatar(name)?.Name).WhereNotNull().ToList();
        var selected = new CombatScript(new(names), commands);
        var applicable = selected.SelectForParty(scenes.GetAvatars().Select(avatar => avatar.Name));
        if (names.Count <= 0 && !selected.HasFlowCommands) throw new Exception("没有可用战斗脚本");
        return applicable;
    }
    // 方法1：判断是否是单个数字

    /*public int delayTime=1500;
    public Dictionary<string, int> delayTimes = new();
    public double checkTime = 5;
    public List<string> checkNames = new();*/
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
        // 每场新战斗重置"敌人可见时跳过战斗结束检查"的连续跳过计数，保证拥有完整的跳过次数
        ResetSkipCheckCounter();
        try
        {
            ValidateAndLogCombatSafetyConfiguration(Logger, _taskParam);
            LogScreenResolution();
        var combatScenes = CombatScenes.GetCombatScenesWithRetry();
        /*var combatScenes = new CombatScenes().InitializeTeam(CaptureToRectArea());
        if (!combatScenes.CheckTeamInitialized())
        {
            throw new Exception("识别队伍角色失败");
        }*/


        // var actionSchedulerByCd = ParseStringToDictionary(_taskParam.ActionSchedulerByCd);
        var combatCommands = SelectCombatCommands(combatScenes);

        // 新的取消token
        using var cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);

        combatScenes.BeforeTask(cts2.Token);
        using var flow = Script.Flow.NativeCombatFlowRunner.Create(combatCommands, combatScenes, loop: true, param: _taskParam);
        using var battleHost = NativeCombatBattleHostIo.Create(flow, combatScenes, _taskParam);
        _finishDetectConfig.FinishEvidenceId = flow.Context.BattleId.ToString();
        _finishDetectConfig.Diagnostics = new(Logger);
        FightStartTime = DateTime.Now;
        var skipPostFightPickupFlag = false;
        string lastFightName = "";
        // 原CD配置仍更新角色数据；调度由统一内核的显式fast/wait负责。
        combatScenes.UpdateActionSchedulerByCd(_taskParam.ActionSchedulerByCd);
        
        AutoFightSeek.ResetSeekState(); // 重置旋转次数与目标路线锁定
        
        // 基于经验值的战后拾取检测：在战斗过程中异步检测精英怪经验值图标
        // 仅在万叶拾取总开关开启时才启动经验值检测
        ExperienceDetector? expDetector = null;
        if (_taskParam.KazuhaPickupEnabled && _taskParam.ExpBasedPickupEnabled)
        {
            using var gameCaptureRegion = CaptureToRectArea();
            var expRos = AutoFightAssets.Get(gameCaptureRegion).ExperienceRecognitionObjects;
            expDetector = new ExperienceDetector(expRos, cts2.Token);
            expDetector.Start();
        }

        // 战斗操作
        var fightTask = Task.Run(async () =>
        {
            try
            {
                FightStatusFlag = true;
                
                while (!cts2.Token.IsCancellationRequested)
                {
                    var hostResult = await battleHost.AdvanceAsync(flow, cts2.Token);
                    lastFightName = flow.Context.LastObservedActor ?? lastFightName;
                    TraceFlowHost(_finishDetectConfig, flow, false, false, battleHost);
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
                FightStatusFlag = false;
            }
        }, cts2.Token);

        // 在持续索敌循环启动前标记战斗进行中，避免索敌循环因 FightStatusFlag 仍为 false 而立即退出
        FightStatusFlag = true;

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
            FightStatusFlag = false;
        }

            ct.ThrowIfCancellationRequested();
            EnsureFightFinishConfirmed(_taskParam.FightFinishDetectEnabled, !skipPostFightPickupFlag,
                AutoFightParam.IsSeekRotationLimitReached(AutoFightSeek.RotationCount) ? "找敌次数上限" : "战斗时间上限");
            if (skipPostFightPickupFlag)
            {
                Logger.LogInformation("战斗被强制结束，跳过战后拾取");
                return;
            }

            // 基于经验值检测结果的拾取判断
            if (_taskParam.KazuhaPickupEnabled && _taskParam.ExpBasedPickupEnabled && expDetector != null)
            {
                // 战斗结束与怪物死亡可能几乎同时发生，检测器可能还没来得及捕获经验值图标
                // 保持检测器运行，每 100ms 轮询一次结果，最多等待 1.1 秒
                if (!expDetector.HasDetectedExperience)
                {
                    Logger.LogInformation("基于经验值判断：等待经验值检测结果");
                    var waitMs = 1100;
                    while (!expDetector.HasDetectedExperience && waitMs > 0)
                    {
                        await Delay(100, ct);
                        waitMs -= 100;
                    }
                }

                await expDetector.StopAsync();
                var shouldPickup = expDetector.HasDetectedExperience;
                Logger.LogInformation("基于经验值判断：{Result} 战后拾取", shouldPickup ? "执行" : "不执行");

                if (!shouldPickup)
                {
                    // 经验值检测未通过，跳过拾取（但仍执行扫描拾取逻辑）
                    if (_taskParam is { PickDropsAfterFightEnabled: true })
                    {
                        await new ScanPickTask().Start(ct, _taskParam.PickDropsAfterFightSeconds);
                    }
                    return;
                }
            }
        }
        finally
        {
            // 确保检测器在任何路径（异常/取消/正常）都被停止和释放
            if (expDetector != null)
            {
                try { await expDetector.StopAsync(); }
                finally { expDetector.Dispose(); }
            }
        }

        var countFight = flow.Context.ActorTurns;
        if (_taskParam.BattleThresholdForLoot>=2 && countFight < _taskParam.BattleThresholdForLoot)
        {
            Logger.LogInformation($"战斗人次（{countFight}）低于配置人次（{_taskParam.BattleThresholdForLoot}），跳过此次拾取！");
            return;
        }
        
        if (_taskParam.KazuhaPickupEnabled)
        {
            // 队伍中存在万叶的时候使用一次长E
            var picker = combatScenes.SelectAvatar("枫原万叶") ?? combatScenes.SelectAvatar("琴");
            
            string? oldPartyName = null;
            if (RunnerContext.Instance.PartyName is not null)
            {
                oldPartyName = RunnerContext.Instance.PartyName;
            }
            else if(picker is null && !string.IsNullOrEmpty(_taskParam.KazuhaPartyName))
            {
                Logger.LogWarning("换队拾取：当前队伍名称为空，尝试读取！");
                await Delay(1000, ct);
                await _returnMainUiTask.Start(ct);

                for (int attempt = 0; attempt < 6; attempt++)
                {
                    Simulation.SendInput.SimulateAction(GIActions.OpenPartySetupScreen);
                    var enterGameAppear = await NewRetry.WaitForElementAppear(
                        ElementRecognition.Get("PartyBtnChooseView"),
                        () => { },
                        ct,
                        15,
                        500
                    );
                    if(attempt == 5 && !enterGameAppear)
                    {
                        Logger.LogWarning("换队拾取：读取队伍名称失败，跳过换队拾取步骤");
                    }
                }
            }

            if (!string.IsNullOrEmpty(_taskParam.KazuhaPartyName)){
                await Delay(1000, ct);
                    
                //等待寻找2秒队伍按钮出现
                var timeWaitStart = 0;
                while(timeWaitStart < 6000)
                {
                    using var ra = CaptureToRectArea();
                    var partyViewBtn = ra.Find(ElementRecognition.Get("PartyBtnChooseView", ra));
                    if (partyViewBtn.IsExist())
                    {
                        // OCR 当前队伍名称（无法单字，中间禁止空格）
                    // 读取OCR原始识别文本
                    var rawPartyName = ra.Find(new RecognitionObject
                    {
                        RecognitionType = RecognitionTypes.Ocr,
                        RegionOfInterest = new Rect(partyViewBtn.Right, partyViewBtn.Top, (int)(350 * _assetScale),
                            partyViewBtn.Height)
                    }).Text;
                    
                    // 核心处理逻辑：1.空值兜底 2.去首尾空白 3.移除末尾的“口”字（仅最后一个是口才删）
                    if (string.IsNullOrWhiteSpace(rawPartyName))
                    {
                        oldPartyName = string.Empty;
                    }
                    else
                    {
                        //有概率把编辑图标识别为字符，并且含有空格或换行符，需要过滤
                        var tempName = rawPartyName
                            .Replace("\"", "")        // 移除所有双引号（核心新增，解决日志里的""问题）
                            .Replace("\r\n", "")      // 清理Windows换行符
                            .Replace("\r", "");   // 先清理所有双引号，避免引号干扰后续处理
                            
                            // 核心逻辑：找到第一个换行符(\n)的位置，截断并删除换行+后面所有字符
                            int firstNewLineIndex = tempName.IndexOf('\n');
                            if (firstNewLineIndex != -1) // 存在换行符，截取到换行符前
                            {
                                tempName = tempName.Substring(0, firstNewLineIndex);
                            }
                        
                            // 最后统一去首尾所有空白（空格、制表符、回车符\r等），得到纯净队伍名
                            oldPartyName = tempName.Trim();
                    }
                    
                    // 后续原有逻辑不变
                    Logger.LogInformation("换队拾取：当前队伍名称读取为：{oldPartyName}", oldPartyName);
                    // 加在rawPartyName赋值后，打印原始文本的“原始形态”（转义符会显示）
                    Logger.LogDebug("OCR原始识别文本（含转义）：{rawPartyName}", rawPartyName);
                    RunnerContext.Instance.PartyName = oldPartyName;
                        // await _returnMainUiTask.Start(ct);
                        break;
                    }
                    await Delay(200, ct);
                    timeWaitStart += 200;
                }
            }

            var switchPartyFlag = false;
            if (picker == null && !skipPostFightPickupFlag &&!string.IsNullOrEmpty(_taskParam.KazuhaPartyName) && oldPartyName != _taskParam.KazuhaPartyName)
            {
                try
                {
                    Logger.LogInformation($"切换为拾取队伍：{_taskParam.KazuhaPartyName}");
                    var success = await new SwitchPartyTask().Start(_taskParam.KazuhaPartyName, ct);
                    if (success)
                    {
                        Logger.LogInformation($"成功切换队伍为{_taskParam.KazuhaPartyName}");
                        switchPartyFlag = true;
                        RunnerContext.Instance.PartyName = _taskParam.KazuhaPartyName;
                        RunnerContext.Instance.ClearCombatScenes();
                        var cs = await RunnerContext.Instance.GetCombatScenes(ct);
                        picker = cs.SelectAvatar("枫原万叶") ?? cs.SelectAvatar("琴");
                    }
                }
                catch (Exception e)
                {
                    Logger.LogInformation("切换队伍异常，跳过此步骤！");
                }

            }
            
            if (picker != null)
            {
                Simulation.ReleaseAllKey();

                if (picker.Name == "枫原万叶")
                {
                    var time = TimeSpan.FromSeconds(picker.GetSkillCdSeconds());

                    // 如果配置了二次拾取，或者不满足跳过条件（上次是万叶且冷却时间>3秒），则执行拾取
                    bool shouldSkip = lastFightName == picker.Name && time.TotalSeconds > 3;
                    bool forcePickup = _taskParam.QinDoublePickUp;
                    
                    if (forcePickup || !shouldSkip)
                    {
                        Logger.LogInformation("使用 枫原万叶-长E 拾取掉落物");
                        await PickUpCollectHandler.RunAfterBattleAsync(picker, false, ct);
                        if (AutoFightParam.ShouldRunKazuhaGatheredDropsScan(
                                _taskParam.KazuhaPickupEnabled, _taskParam.PickDropsAfterFightEnabled))
                            await new ScanPickTask().Start(ct, AutoFightParam.KazuhaGatheredDropsScanSeconds);
                    }
                    else
                    {
                        Logger.LogInformation("距最近一次万叶出招，时间过短，跳过此次万叶拾取！");
                    }
                }
                else if (picker.Name == "琴")
                {
                    Logger.LogInformation("准备执行 琴-长E 聚物，完成及冷却确认由统一执行器返回");
                    await PickUpCollectHandler.RunAfterBattleAsync(picker, _taskParam.QinDoublePickUp, ct);
                    if (AutoFightParam.ShouldRunKazuhaGatheredDropsScan(
                            _taskParam.KazuhaPickupEnabled, _taskParam.PickDropsAfterFightEnabled))
                        await new ScanPickTask().Start(ct, AutoFightParam.KazuhaGatheredDropsScanSeconds);
                }
            }
            //切换过队伍的，需要再切回来
            if (switchPartyFlag && !string.IsNullOrEmpty(oldPartyName))
            {
                try
                {
                    Logger.LogInformation($"切换为原队伍：{oldPartyName}");
                    var success = await new SwitchPartyTask().Start(oldPartyName, ct);
                    if (success)
                    {
                        Logger.LogInformation($"切换为原队伍{oldPartyName}");
                        switchPartyFlag = true;
                        RunnerContext.Instance.PartyName = oldPartyName;
                        RunnerContext.Instance.ClearCombatScenes();
                        await RunnerContext.Instance.GetCombatScenes(ct);
    
                    }
                }
                catch (Exception e)
                {
                    Logger.LogInformation("恢复原队伍失败，跳过此步骤！");
                }
                    
            }
        }

        if (_taskParam is { PickDropsAfterFightEnabled: true } )
        {
            // 执行扫描掉落物光柱并靠近的功能
            await new ScanPickTask().Start(ct, _taskParam.PickDropsAfterFightSeconds);
        }
    }
        catch (Exception error)
        {
            TaskExecutionScope.RethrowCombatFailure(error, _taskParam.FightFinishDetectEnabled, _finishDetectConfig.EndConfirmed, ct);
            throw;
        }
        finally
        {
            AvatarRecognition.ClearCurrentAutoFightParam();
        }
    }

    private void LogScreenResolution()
    {
        AssertUtils.CheckGameResolution("自动战斗");
    }

    internal static void EnsureFightFinishConfirmed(bool detectionEnabled, bool confirmed, string reason)
    {
        if (detectionEnabled && !confirmed) TaskExecutionScope.StopUnconfirmedCombat(reason);
    }

    public async Task<bool> CheckFightFinish(int delayTime = 1500, int detectDelayTime = 450)
    {
        return await CheckFightFinish(_finishDetectConfig, _ct, delayTime, detectDelayTime);
    }

    /// <summary>
    /// 战斗结束检测（统一实现，TXT 与 JSON 策略共用）
    /// </summary>
    public static async Task<bool> CheckFightFinish(TaskFightFinishDetectConfig finishDetectConfig,
        CancellationToken ct,
        int delayTime = 1500,
        int detectDelayTime = 450,
        bool allowSeek = true)
    {
        // 开战后一段时间阻断战斗结束检查：距离开战时间小于配置值时，提前返回并视为战斗未结束
        if (finishDetectConfig.BlockCheckBeforeBattleSeconds > 0 &&
            (DateTime.Now - FightStartTime).TotalSeconds < finishDetectConfig.BlockCheckBeforeBattleSeconds)
        {
            // 阻断期内同样刷新最近检查时间：否则当 CheckTime 小于阻断期时，检查间隔条件
            // (DateTime.Now - LastFightFinishCheckTime) > CheckTime 会反复成立并重复进入该路径，
            // 直至阻断期结束 LastFightFinishCheckTime 一直得不到刷新
            LastFightFinishCheckTime = DateTime.Now;
            return false;
        }

        // 记录最近一次战斗结束检查的时间（供更快触发战斗结束检查判断间隔使用）
        LastFightFinishCheckTime = DateTime.Now;
        using (AvatarRecognition.BeginExclusiveOperation())
        {
            // 辅助识别只读取后台快照。没有新结果时直接继续退出判断，
            // 不在出招线程等待转向、接近、截图复核或索敌预算。
            if ((finishDetectConfig.SkipFightEndCheckWhenEnemyVisible || finishDetectConfig.RotateFindEnemyEnabled) &&
                AutoFightSeek.TryCreatePassiveDecision(AvatarRecognition.LatestPassiveObservation,
                    DateTime.UtcNow, out _, out _, out _))
            {
                if (allowSeek) RunPassiveSeek(finishDetectConfig, ct);
                else finishDetectConfig.Diagnostics?.RecordSeek("root-boundary-blocked");
                return false;
            }

            if (!finishDetectConfig.RotateFindEnemyEnabled)
            {
                await Delay(delayTime, ct);
            }

            // Logger.LogInformation("打开编队界面检查战斗是否结束，延时{detectDelayTime}毫秒检查", detectDelayTime);
            Logger.LogInformation("打开编队界面检查战斗是否结束");
            using var finishOperation = UiOperation.Begin("fight-end-check", TimeSpan.FromSeconds(10), ct, Logger);
            ct.ThrowIfCancellationRequested();
            using var beforeCapture = CaptureToRectArea(forceNew: true);
            var before = ObservePartySetupBar(beforeCapture, Interlocked.Increment(ref finishDetectConfig.FinishFrameSequence));
            finishOperation.Check();
            // 最终方案确认战斗结束
            Simulation.SendInput.SimulateAction(GIActions.OpenPartySetupScreen);
            var probe = new PartySetupFinishDetector(before, DateTimeOffset.UtcNow);
            if (finishDetectConfig.PaimonEndCheckEnabled)
            {
                // 派蒙图标只作为“编队加载进度条”出现速度的提示，不再把它作为战斗未结束的否决条件。
                var paimonProbeDelayMs = GetPaimonEndCheckDelayMilliseconds(
                    finishDetectConfig.PaimonEndCheckDelayMs);
                await Delay(paimonProbeDelayMs, ct);
                using (var paimonRa = CaptureToRectArea())
                {
                    var paimonVisible = Bv.IsInMainUi(paimonRa);
                    if (paimonVisible)
                    {
                        Logger.LogInformation("派蒙图标仍可见，不否决战斗结束，继续等待编队加载进度条");
                        var remainingDelayMs = Math.Max(0, detectDelayTime - paimonProbeDelayMs);
                        if (remainingDelayMs > 0)
                        {
                            await Delay(remainingDelayMs, ct);
                        }
                    }
                    else
                    {
                        Logger.LogInformation("派蒙图标已消失，提前检测编队加载进度条");
                    }
                }
            }
            else
            {
                await Delay(detectDelayTime, ct);
            }

            // 不开启派蒙加速时也必须保留第二幅独立图像的确认机会。
            var progressBarCheckCount = 5;
            for (var attempt = 0; attempt < progressBarCheckCount; attempt++)
            {
                finishOperation.Check();
                using var ra = CaptureToRectArea(forceNew: true);
                var observed = ObservePartySetupBar(ra, Interlocked.Increment(ref finishDetectConfig.FinishFrameSequence));
                var confirmed = probe.Observe(observed);
                try
                {
                    Logger.LogDebug("FIGHT_END_PROBE battle={Battle} check={Check} frame={Frame} captured={Captured:O} size={Width}x{Height} beforeCandidate={Before} candidate={Candidate} confirmed={Confirmed} reason={Reason}",
                        finishDetectConfig.FinishEvidenceId, finishOperation.Id, observed.FrameId, observed.CapturedAt,
                        observed.Width, observed.Height, before.BarVisible, observed.BarVisible, confirmed, probe.Reason);
                }
                catch { /* 诊断不能影响是否放行战斗。 */ }
                if (observed.BarVisible)
                    SaveFightEndEvidence(finishDetectConfig, finishOperation.Id, probe.Reason, beforeCapture, ra);
                if (confirmed)
                {
                    finishDetectConfig.EndConfirmed = true;
                    finishOperation.Check();
                    Simulation.SendInput.SimulateAction(GIActions.Drop);
                    Logger.LogInformation("检测到编队加载进度条，识别到战斗结束");
                    // 取消正在进行的换队
                    Simulation.SendInput.SimulateAction(GIActions.OpenPartySetupScreen);
                    return true;
                }

                if (attempt + 1 < progressBarCheckCount)
                {
                    await Delay(100, ct);
                }
            }

            finishOperation.Check();
            Simulation.SendInput.SimulateAction(GIActions.Drop);
            Logger.LogInformation("未确认编队加载进度条，继续战斗：{Reason}，check={Check}", probe.Reason, finishOperation.Id);

            _lastFightFlagTime = DateTime.Now;
            return false;
        }
    }

    private static readonly AsyncLocal<DateTime> LastPassiveCameraFrame = new();

    internal static PartySetupFinishObservation ObservePartySetupBar(ImageRegion image, long frame)
    {
        // 使用捕获源签发的身份；frame参数仅保留旧调用兼容，不得续期旧图。
        ulong fingerprint = 14695981039346656037UL;
        var mat = image.SrcMat;
        var scale = mat.Width / 1920d;
        if (!mat.Empty() && mat.Channels() == 3)
        {
            for (var y = (int)(34 * scale); y < Math.Min(mat.Height, (int)(66 * scale)); y++)
            for (var x = (int)(736 * scale); x < Math.Min(mat.Width, (int)(1184 * scale)); x++)
            {
                var pixel = mat.At<Vec3b>(y, x);
                fingerprint = unchecked((fingerprint ^ pixel.Item0) * 1099511628211UL);
                fingerprint = unchecked((fingerprint ^ pixel.Item1) * 1099511628211UL);
                fingerprint = unchecked((fingerprint ^ pixel.Item2) * 1099511628211UL);
            }
        }
        return new(image.FrameStamp.Sequence, image.FrameStamp.CapturedAt, mat.Width, mat.Height,
            IsPartySetupProgressBarVisible(image), fingerprint) { Source = image.FrameStamp };
    }

    private static void SaveFightEndEvidence(TaskFightFinishDetectConfig config, string check, string reason,
        ImageRegion before, ImageRegion after)
    {
        try
        {
            if (!Logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug) || Interlocked.Increment(ref config.FinishEvidenceCount) > 2) return;
            var directory = Global.Absolute(@"log\screenshot");
            Directory.CreateDirectory(directory);
            foreach (var (name, image) in new[] { ("before", before), ("after", after) })
            {
                var scale = image.Width / 1920d;
                // 只保存顶部中间的判定现场，不包含右下 UID；不另抓可能已变化的截图。
                using var crop = image.DeriveCrop(600 * scale, 0, 720 * scale, 300 * scale);
                var path = Path.Combine(directory, $"fight-end-{config.FinishEvidenceId}-{check}-{after.Width}-{reason}-{name}.png");
                if (!Cv2.ImWrite(path, crop.SrcMat)) throw new IOException("无法保存战斗结束证据");
                Logger.LogDebug("FIGHT_END_EVIDENCE check={Check} reason={Reason} file={File}", check, reason, path);
            }
        }
        catch (Exception error)
        {
            try { Logger.LogDebug(error, "战斗结束证据保存失败，不改变判定结果"); }
            catch { /* 日志接收器失败同样不能改变结果。 */ }
        }
    }

    internal static void TraceFlowHost(TaskFightFinishDetectConfig config, Script.Flow.NativeCombatFlowRunner flow,
        bool requested, bool periodicDue, CombatBattleHost? host = null)
    {
        if (config.Diagnostics is not { } diagnostics) return;
        if (flow.IsAtomic)
        {
            diagnostics.ObserveAtomicStep();
            return; // 不为日志延长仍持有的 atomic 独占。
        }
        diagnostics.Write("FIGHT_HOST", config.FinishEvidenceId, () =>
        {
            var observation = AvatarRecognition.LatestPassiveObservation;
            var now = DateTime.UtcNow;
            var fresh = AutoFightSeek.TryCreatePassiveDecision(observation, now, out _, out _, out _);
            var age = observation.CapturedAtUtc == default ? -1 : (now - observation.CapturedAtUtc).TotalMilliseconds;
            return $"root={flow.IsAtRootBoundary} atomic=False pendingConfirmation={flow.HasPendingConfirmation} atomicSteps={diagnostics.AtomicSteps} requested={requested} periodicDue={periodicDue} " +
                $"freshTarget={fresh} ageMs={age:F0} healthBar={observation.HasNormalHealthBar} skipVisible={config.SkipFightEndCheckWhenEnemyVisible} rotate={config.RotateFindEnemyEnabled} mode={config.CombatTargetingMode} " +
                (host != null
                    ? $"hostState={host.State} hostReason={host.Reason} cameraRequests={host.CameraRequests} approachRequests={host.ApproachRequests} actualMotion={host.LastMotion}"
                    : $"seekCalls={diagnostics.SeekCalls} lastSeek={diagnostics.LastSeek} cameraPulse={diagnostics.LastCameraPulse} approachInput=False");
        });
    }

    private static bool? RunPassiveSeek(
        TaskFightFinishDetectConfig finishDetectConfig,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var observation = AvatarRecognition.LatestPassiveObservation;
        if (!AutoFightSeek.TryCreatePassiveDecision(observation, DateTime.UtcNow,
                out _, out var width, out _))
        {
            finishDetectConfig.Diagnostics?.RecordSeek("no-fresh-target");
            return null;
        }

        var outcome = "fresh-target-no-camera-input";
        var cameraPulse = 0;
        if (finishDetectConfig.RotateFindEnemyEnabled &&
            finishDetectConfig.CombatTargetingMode != CombatTargetingMode.ObserveOnly &&
            observation.CapturedAtUtc > LastPassiveCameraFrame.Value &&
            observation.Visual is { } visual)
        {
            LastPassiveCameraFrame.Value = observation.CapturedAtUtc;
            var pulse = observation.IndicatorDecision is { } indicator
                ? Math.Clamp(AutoFightSeek.GetIndicatorCameraOffset(indicator.Direction,
                    visual, width, observation.ImageHeight), -120, 120)
                : observation.HasNormalHealthBar
                    ? CombatContinuityPolicy.CameraPulse(visual.X + visual.Width / 2, width)
                    : 0;
            if (pulse != 0)
            {
                Simulation.SendInput.Mouse.MoveMouseBy(pulse, 0);
                cameraPulse = pulse;
                outcome = "camera-input-returned";
            }
            else outcome = "centered-no-camera-input";
        }
        finishDetectConfig.Diagnostics?.RecordSeek(outcome, cameraPulse);
        return false;
    }

    static bool IsYellow(int r, int g, int b)
    {
        //Logger.LogInformation($"IsYellow({r},{g},{b})");
        // 黄色范围：R高，G高，B低
        return (r >= 200 && r <= 255) &&
               (g >= 200 && g <= 255) &&
               (b >= 0 && b <= 100);
    }

    internal static bool IsPartySetupProgressBarVisible(ImageRegion captureRa)
    {
        var image = captureRa.SrcMat;
        if (image.Empty() || image.Channels() != 3 || image.Width < 640 || image.Width * 9 != image.Height * 16) return false;
        var scale = image.Width / 1920d;
        var y = (int)Math.Round(50 * scale);
        // 两个孤立颜色点也可能来自场景或特效；白色标记和黄色条身都必须连续。
        bool HasRun(int from, int to, Func<int, int, int, bool> matches)
        {
            from = (int)Math.Round(from * scale);
            to = (int)Math.Round(to * scale);
            var count = 0;
            for (var x = from; x <= to; x++)
            {
                var pixel = image.At<Vec3b>(y, x);
                if (matches(pixel.Item2, pixel.Item1, pixel.Item0)) count++;
            }
            return count >= Math.Ceiling((to - from + 1) * 0.8);
        }
        return HasRun(767, 769, IsWhite) && HasRun(786, 794, IsYellow);
    }

    internal static int FindNextGuardianSkillCommandIndex(
        IReadOnlyList<CombatCommand> commands,
        int currentIndex,
        string guardianName)
    {
        for (var index = currentIndex + 1; index < commands.Count; index++)
        {
            var command = commands[index];
            if (string.Equals(command.Name, guardianName, StringComparison.OrdinalIgnoreCase) &&
                command.Method == Method.Skill)
            {
                return index;
            }
        }

        return -1;
    }

    internal static int GetPaimonEndCheckDelayMilliseconds(int configuredDelayMs)
    {
        return Math.Max(0, configuredDelayMs);
    }

    static bool IsWhite(int r, int g, int b)
    {
        //Logger.LogInformation($"IsWhite({r},{g},{b})");
        // 白色范围：R高，G高，B低
        return (r >= 240 && r <= 255) &&
               (g >= 240 && g <= 255) &&
               (b >= 240 && b <= 255);
    }

    static double FindMax(double[] numbers)
    {
        if (numbers == null || numbers.Length == 0)
        {
            throw new ArgumentException("The array is empty or null.");
        }

        double max = numbers[0] > 10000 ? 0 : numbers[0];
        foreach (var num in numbers)
        {
            var cpnum = numbers[0] > 10000 ? 0 : num;
            max = Math.Max(max, num);
        }

        return max;
    }

    [Obsolete]
    private static Dictionary<string, double> ParseStringToDictionary(string input, double defaultValue = -1)
    {
        var dictionary = new Dictionary<string, double>();

        if (string.IsNullOrEmpty(input))
        {
            return dictionary; // 返回空字典
        }

        string[] pairs = input.Split(';', StringSplitOptions.RemoveEmptyEntries);

        foreach (var pair in pairs)
        {
            var parts = pair.Split(',', StringSplitOptions.TrimEntries);

            if (parts.Length > 0)
            {
                string name = parts[0];
                double value = defaultValue;

                if (parts.Length > 1 && double.TryParse(parts[1], out var parsedValue))
                {
                    value = parsedValue;
                }

                dictionary[name] = value;
            }
        }

        return dictionary;
    }


    // 无用
    // [Obsolete]
    // private bool HasFightFlagByGadget(ImageRegion imageRegion)
    // {
    //     // 小道具位置 1920-133,800,60,50
    //     var gadgetMat = imageRegion.DeriveCrop(AutoFightAssets.Get(imageRegion).GadgetRect).SrcMat;
    //     var list = ContoursHelper.FindSpecifyColorRects(gadgetMat, new Scalar(225, 220, 225), new Scalar(255, 255, 255));
    //     // 要大于 gadgetMat 的 1/2
    //     return list.Any(r => r.Width > gadgetMat.Width / 2 && r.Height > gadgetMat.Height / 2);
    // }
}
