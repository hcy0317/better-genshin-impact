using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.GameTask.AutoFight.Assets;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Model;
using BetterGenshinImpact.GameTask.AutoPathing.Handler;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.AutoPathing.Model.Enum;
using BetterGenshinImpact.GameTask.AutoSkip;
using BetterGenshinImpact.GameTask.AutoTrackPath;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Job;
using BetterGenshinImpact.GameTask.Common.Map;
using BetterGenshinImpact.GameTask.Model.Area;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.Mvvm.Messaging.Messages;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.AutoPathing.Suspend;
using BetterGenshinImpact.GameTask.Common;
using Vanara.PInvoke;
using static BetterGenshinImpact.GameTask.Common.TaskControl;
using static BetterGenshinImpact.GameTask.SystemControl;
using ActionEnum = BetterGenshinImpact.GameTask.AutoPathing.Model.Enum.ActionEnum;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Common.Exceptions;
using BetterGenshinImpact.GameTask.Common.Map.Maps;
using BetterGenshinImpact.GameTask.Common.Map.Maps.Base;
using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using Fischless.GameCapture;
using BetterGenshinImpact.GameTask.Common.Ui;

namespace BetterGenshinImpact.GameTask.AutoPathing;

public partial class PathExecutor
{
    private readonly CameraRotateTask _rotateTask;
    private readonly TrapEscaper _trapEscaper;
    private readonly BlessingOfTheWelkinMoonTask _blessingOfTheWelkinMoonTask = new();
    private AutoSkipTrigger? _autoSkipTrigger;
    private long _arrivalReachedAt;
    public int SuccessFight = 0;
    //路径追踪完全走完所有路径结束的标识
    public bool SuccessEnd = false;
    private PathingPartyConfig? _partyConfig;
    private CancellationToken ct;
    private PathExecutorSuspend pathExecutorSuspend;
    private readonly PathMoveToIo _moveIo;
    private PathingMacroSession? _pathingMacro;

    public PathExecutor(CancellationToken ct) : this(ct, null) { }

    internal PathExecutor(CancellationToken ct, PathMoveToIo? io, PathingMacroSession? macro = null)
    {
        _trapEscaper = new(ct);
        _rotateTask = new(ct);
        this.ct = ct;
        _pathingMacro = macro;
        pathExecutorSuspend = new PathExecutorSuspend(this);
        _moveIo = io ?? new PathMoveToIo(native: true)
        {
            SwitchAvatar = async index => { await SwitchAvatar(index); },
            Locate = GetDirectPositionAndTime,
            LocateDirect = (screen, point) =>
            {
                var position = Navigation.GetPosition(screen, point.MapName, point.MapMatchMethod, point.MapLayerSelector);
                return Task.FromResult(new PathPosition(position, 0,
                    position != default && float.IsFinite(position.X) && float.IsFinite(position.Y)));
            },
            EndJudgment = EndJudgment,
            RotateUntil = (target, diff) => WaitUntilRotatedTo(target, diff),
            RotateStep = _rotateTask.RotateToApproach
        };
    }

    public PathingPartyConfig PartyConfig
    {
        get => _partyConfig ?? PathingPartyConfig.BuildDefault();
        set => _partyConfig = value;
    }

    /// <summary>
    /// 判断是否中止地图追踪的条件
    /// </summary>
    public Func<ImageRegion, bool>? EndAction { get; set; }

    private CombatScenes? _combatScenes;
    private PathMovementDiagnostics? _movementDiagnostics;
    // private readonly Dictionary<string, string> _actionAvatarIndexMap = new();

    private DateTime _elementalSkillLastUseTime = DateTime.MinValue;
    private DateTime _useGadgetLastUseTime = DateTime.MinValue;

    private const int RetryTimes = 2;
    private int _inTrap = 0;


    //记录当前相关点位数组
    public (int, List<WaypointForTrack>) CurWaypoints { get; set; }

    //记录当前点位
    public (int, WaypointForTrack) CurWaypoint { get; set; }

    //记录恢复点位数组
    private (int, List<WaypointForTrack>) RecordWaypoints { get; set; }

    //记录恢复点位
    private (int, WaypointForTrack) RecordWaypoint { get; set; }

    //跳过除走路径以外的操作
    private bool _skipOtherOperations = false;

    // 最近一次获取派遣奖励的时间
    private DateTime _lastGetExpeditionRewardsTime = DateTime.MinValue;

    private static RecognitionObject GetAutoSkipRecognitionObject(string objectName, ImageRegion region)
    {
        return RecognitionAssets.Get("AutoSkip", objectName, region.Width, region.Height);
    }


    //当到达恢复点位
    public void TryCloseSkipOtherOperations()
    {
        // Logger.LogWarning("判断是否跳过地图追踪:" + (CurWaypoint.Item1 < RecordWaypoint.Item1));
        if (RecordWaypoints == CurWaypoints && CurWaypoint.Item1 < RecordWaypoint.Item1)
        {
            return;
        }

        if (_skipOtherOperations)
        {
            Logger.LogWarning("已到达上次点位，地图追踪功能恢复");
        }

        _skipOtherOperations = false;
    }

    //记录点位，方便后面恢复
    public void StartSkipOtherOperations()
    {
        Logger.LogWarning("记录恢复点位，地图追踪将到达上次点位之前将跳过走路之外的操作");
        _skipOtherOperations = true;
        RecordWaypoints = CurWaypoints;
        RecordWaypoint = CurWaypoint;
    }

    public async Task Pathing(PathingTask task)
    {
        TaskExecutionScope.ThrowIfFailed();
        SuccessEnd = false;
        // SuspendableDictionary;
        const string sdKey = "PathExecutor";
        var sd = RunnerContext.Instance.SuspendableDictionary;
        sd.Remove(sdKey);

        RunnerContext.Instance.SuspendableDictionary.TryAdd(sdKey, pathExecutorSuspend);

        if (!task.Positions.Any())
        {
            Logger.LogWarning("没有路径点，寻路结束");
            return;
        }


        // 切换队伍
        if (!await SwitchPartyBefore(task))
        {
            return;
        }

        // 校验路径是否可以执行
        if (!await ValidateGameWithTask(task))
        {
            return;
        }

        InitializePathing(task);
        using var macroOwner = new PathingMacroSession(new NativePathingMacroIo(() =>
            $"route={task.FileName} segment={CurWaypoints.Item1} node={CurWaypoint.Item1}"));
        _pathingMacro = macroOwner;
        // 转换、按传送点分割路径
        var waypointsList = ConvertWaypointsForTrack(task.Positions, task);

        await Delay(100, ct);
        Navigation.WarmUp(task.Info.MapMatchMethod); // 提前加载地图特征点

        foreach (var waypoints in waypointsList) // 按传送点分割的路径
        {
            CurWaypoints = (waypointsList.FindIndex(wps => wps == waypoints), waypoints);
            var capturedRetryFailure = false;
            var endedEarly = await ExecuteSegmentWithRetriesAsync(async () =>
            {
                await ResolveAnomalies(); // 异常场景处理

                // 如果首个点是非TP点位，强制设置在这个点位附近优先做局部匹配
                if (waypoints[0].Type != WaypointType.Teleport.Code)
                {
                    Navigation.SetPrevPosition((float)waypoints[0].X, (float)waypoints[0].Y, waypoints[0].MapLayerSelector);
                }

                foreach (var waypoint in waypoints) // 一条路径
                {
                    CurWaypoint = (waypoints.FindIndex(wps => wps == waypoint), waypoint);
                    TryCloseSkipOtherOperations();
                    await RecoverWhenLowHp(waypoint); // 低血量恢复

                    if (waypoint.Type == WaypointType.Teleport.Code)
                    {
                        _pathingMacro.Release();
                        if (CurWaypoints.Item1 > 0)
                        {
                            var prevWaypoints = waypointsList[CurWaypoints.Item1 - 1];
                            var prevWaypoint = prevWaypoints[prevWaypoints.Count - 1];
                            if (prevWaypoint.Type == WaypointType.Teleport.Code
                                || prevWaypoint.Action == ActionEnum.Fight.Code
                                || prevWaypoint.Action == ActionEnum.NahidaCollect.Code
                                || prevWaypoint.Action == ActionEnum.PickAround.Code)
                            {
                                // No delay
                            }
                            else
                            {
                                await Delay(1000, ct);
                            }
                        }
                        await HandleTeleportWaypoint(waypoint);
                    }
                    else
                    {
                        await BeforeMoveToTarget(waypoint);
                        // Path不用走得很近，Target需要接近，但都需要先移动到对应位置
                        if (waypoint.Type == WaypointType.Orientation.Code)
                        {
                            _pathingMacro.Release();
                            // 方位点，只需要朝向
                            // 考虑到方位点大概率是作为执行action的最后一个点，所以放在此处处理，不和传送点一样单独处理
                            await FaceTo(waypoint);
                        }
                        else if (waypoint.Action != ActionEnum.UpDownGrabLeaf.Code)
                        {
                            await MoveTo(waypoint);
                        }

                        _arrivalReachedAt = Stopwatch.GetTimestamp();
                        await BeforeMoveCloseToTarget(waypoint);

                        if (IsTargetPoint(waypoint))
                        {
                            await MoveCloseTo(waypoint);
                        }

                        //skipOtherOperations如果重试，则跳过相关操作，
                        if ((!string.IsNullOrEmpty(waypoint.Action) && !_skipOtherOperations) ||
                            waypoint.Action == ActionEnum.CombatScript.Code)
                        {
                            //战斗前的节点记录，用于游泳检测回到战斗节点
                            AutoFightTask.FightWaypoint = waypoint.Action == ActionEnum.Fight.Code ? waypoint : null;

                            // 执行 action
                            await AfterMoveToTarget(waypoint);
                        }
                    }
                }

            }, exception =>
            {
                if (!capturedRetryFailure && exception is not CombatRecoveryCompletedException)
                {
                    capturedRetryFailure = true;
                    TaskFailureDiagnostics.CaptureScreenshotOnce(exception,
                        $"地图追踪分段 {CurWaypoints.Item1 + 1} 点位 {CurWaypoint.Item1 + 1} 原始失败，尚未重试：{exception.GetType().Name}");
                }
                StartSkipOtherOperations();
                Logger.LogWarning("地图追踪分段 {Segment} 点位 {Waypoint} 将重试：{Reason}",
                    CurWaypoints.Item1 + 1, CurWaypoint.Item1 + 1, exception.Message);
            }, () =>
            {
                _pathingMacro.Release();
                Simulation.SendInput.Keyboard.KeyUp(User32.VK.VK_W);
                Simulation.SendInput.Mouse.RightButtonUp();
                Simulation.SendInput.SimulateAction(GIActions.NormalAttack, KeyType.KeyUp);
            }, ct);
            if (endedEarly)
            {
                SuccessEnd = true;
                return;
            }
            if (waypoints == waypointsList.Last()) SuccessEnd = true;
        }
    }

    internal sealed class EndConditionSatisfiedException() : Exception("达成结束条件，结束地图追踪");

    /// <summary>正常完成返回 false；仅显式结束条件返回 true；失败或取消始终向调用方传播。</summary>
    internal static async Task<bool> ExecuteSegmentWithRetriesAsync(Func<Task> execute,
        Action<Exception> onRetry, Action releaseInput, CancellationToken ct)
    {
        for (var attempt = 0; attempt < RetryTimes; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await execute();
                ct.ThrowIfCancellationRequested();
                TaskExecutionScope.ThrowIfFailed();
                return false;
            }
            catch (EndConditionSatisfiedException)
            {
                ct.ThrowIfCancellationRequested();
                return true;
            }
            catch (HandledException exception)
            {
                throw new InvalidOperationException("地图追踪未完整完成：" + exception.Message, exception);
            }
            catch (RetryException exception)
            {
                ct.ThrowIfCancellationRequested();
                if (attempt == RetryTimes - 1)
                    throw new InvalidOperationException($"地图追踪重试 {RetryTimes} 次仍未完成；停止当前路线：{exception.Message}", exception);
                onRetry(exception);
            }
            catch (RetryNoCountException exception)
            {
                ct.ThrowIfCancellationRequested();
                attempt--;
                onRetry(exception);
            }
            finally { releaseInput(); }
        }
        throw new InvalidOperationException("地图追踪未完整完成");
    }

    private bool IsTargetPoint(WaypointForTrack waypoint)
    {
        // 方位点不需要接近
        if (waypoint.Type == WaypointType.Orientation.Code || waypoint.Action == ActionEnum.UpDownGrabLeaf.Code)
        {
            return false;
        }


        var action = ActionEnum.GetEnumByCode(waypoint.Action);
        if (action is not null && action.UseWaypointTypeEnum != ActionUseWaypointTypeEnum.Custom)
        {
            // 强制点位类型的 action，以 action 为准
            return action.UseWaypointTypeEnum == ActionUseWaypointTypeEnum.Target;
        }

        // 其余情况和没有action的情况以点位类型为准
        return waypoint.Type == WaypointType.Target.Code;
    }

    private async Task<bool> SwitchPartyBefore(PathingTask task)
    {
        using var ra = CaptureToRectArea();

        // 切换队伍前判断是否全队死亡 // 可能队伍切换失败导致的死亡
        if (Bv.ClickIfInReviveModal(ra))
        {
            var returnedToMainUi = await Bv.WaitForMainUi(ct);
            if (!Bv.IsReviveRecoveryConfirmed(clicked: true, returnedToMainUi))
            {
                throw new RetryException("已点击全队复苏，但未确认返回主界面");
            }

            Logger.LogInformation("已确认全队复苏并返回主界面");
            await Delay(4000, ct);
            // 血量肯定不满，直接去七天神像回血
            await TpStatueOfTheSeven();
            throw new RetryException("全队复苏并回血后重试路线");
        }

        if (PartyConfig.SkipPartySwitch)
        {
            return true;
        }

        var pRaList = ra.FindMulti(RecognitionAssets.Get("AutoFight", "P", ra)); // 判断是否联机
        if (pRaList.Count > 0)
        {
            Logger.LogInformation("处于联机状态下，不切换队伍");
        }
        else
        {
            if (PartyConfig is { Enabled: false })
            {
                // 调度器未配置的情况下，根据地图追踪条件配置切换队伍
                var partyName = FilterPartyNameByConditionConfig(task);
                if (!await SwitchParty(partyName))
                {
                    Logger.LogError("切换队伍失败，无法执行此路径！请检查地图追踪设置！");
                    return false;
                }
            }
            else if (!string.IsNullOrEmpty(PartyConfig.PartyName))
            {
                if (!await SwitchParty(PartyConfig.PartyName))
                {
                    Logger.LogError("切换队伍失败，无法执行此路径！请检查配置组中的地图追踪配置！");
                    return false;
                }
            }
        }

        return true;
    }

    private void InitializePathing(PathingTask task)
    {
        LogScreenResolution();
        InitHurryOnConfig();
        WeakReferenceMessenger.Default.Send(new PropertyChangedMessage<object>(this,
            "UpdateCurrentPathing", new object(), task));
    }

    private void LogScreenResolution()
    {
        var gameScreenSize = SystemControl.GetGameScreenRect(TaskContext.Instance().GameHandle);
        if (gameScreenSize.Width * 9 != gameScreenSize.Height * 16)
        {
            Logger.LogError("游戏窗口分辨率不是 16:9 ！当前分辨率为 {Width}x{Height} , 非 16:9 分辨率的游戏无法正常使用地图追踪功能！",
                gameScreenSize.Width, gameScreenSize.Height);
            throw new Exception("游戏窗口分辨率不是 16:9 ！无法使用地图追踪功能！");
        }

        if (gameScreenSize.Width < 1920 || gameScreenSize.Height < 1080)
        {
            Logger.LogError("游戏窗口分辨率小于 1920x1080 ！当前分辨率为 {Width}x{Height} , 小于 1920x1080 的分辨率的游戏地图追踪的效果非常差！",
                gameScreenSize.Width, gameScreenSize.Height);
            throw new Exception("游戏窗口分辨率小于 1920x1080 ！无法使用地图追踪功能！");
        }
    }

    /// <summary>
    /// 切换队伍
    /// </summary>
    /// <param name="partyName"></param>
    /// <returns></returns>
    private async Task<bool> SwitchParty(string? partyName)
    {
        bool success = true;
        if (!string.IsNullOrEmpty(partyName))
        {
            if (RunnerContext.Instance.PartyName == partyName)
            {
                return success;
            }

            bool forceTp = PartyConfig.IsVisitStatueBeforeSwitchParty;

            if (forceTp) // 强制传送模式
            {
                await new TpTask(ct).TpToStatueOfTheSeven(); // fix typos
                success = await new SwitchPartyTask().Start(partyName, ct);
            }
            else // 优先原地切换模式
            {
                try
                {
                    success = await new SwitchPartyTask().Start(partyName, ct);
                }
                catch (PartySetupFailedException)
                {
                    await new TpTask(ct).TpToStatueOfTheSeven();
                    success = await new SwitchPartyTask().Start(partyName, ct);
                }
            }

            if (success)
            {
                RunnerContext.Instance.PartyName = partyName;
                RunnerContext.Instance.ClearCombatScenes();
            }
        }

        return success;
    }


    private static string? FilterPartyNameByConditionConfig(PathingTask task)
    {
        var pathingConditionConfig = TaskContext.Instance().Config.PathingConditionConfig;
        var materialName = task.GetMaterialName();
        var specialActions = task.Positions
            .Select(p => p.Action)
            .Where(action => !string.IsNullOrEmpty(action))
            .Distinct()
            .ToList();
        var partyName = pathingConditionConfig.FilterPartyName(materialName, specialActions);
        return partyName;
    }

    /// <summary>
    /// 校验
    /// </summary>
    /// <param name="task"></param>
    /// <returns></returns>
    private async Task<bool> ValidateGameWithTask(PathingTask task)
    {
        _combatScenes = await RunnerContext.Instance.GetCombatScenes(ct);
        if (_combatScenes == null)
        {
            return false;
        }
        CombatScriptHandler.ValidateRouteRequirements(task.Positions, task.Info.Name, _combatScenes.GetAvatars().Select(avatar => avatar.Name));

        // 没有强制配置的情况下，使用地图追踪内的条件配置
        // 必须放在这里，因为要通过队伍识别来得到最终结果
        var pathingConditionConfig = TaskContext.Instance().Config.PathingConditionConfig;
        var skipPartySwitch = PartyConfig.SkipPartySwitch;
        if (PartyConfig is { Enabled: false })
        {
            PartyConfig = pathingConditionConfig.BuildPartyConfigByCondition(_combatScenes);
            PartyConfig.SkipPartySwitch = skipPartySwitch;
        }

        // 校验角色是否存在
        if (task.HasAction(ActionEnum.NahidaCollect.Code))
        {
            var avatar = _combatScenes.SelectAvatar("纳西妲");
            if (avatar == null)
            {
                Logger.LogError("此路径存在纳西妲收集动作，队伍中没有纳西妲角色，无法执行此路径！");
                return false;
            }

            // _actionAvatarIndexMap.Add("nahida_collect", avatar.Index.ToString());
        }

        // 把所有需要切换的角色编号记录下来
        Dictionary<string, ElementalType> map = new()
        {
            { ActionEnum.HydroCollect.Code, ElementalType.Hydro },
            { ActionEnum.ElectroCollect.Code, ElementalType.Electro },
            { ActionEnum.AnemoCollect.Code, ElementalType.Anemo },
            { ActionEnum.PyroCollect.Code, ElementalType.Pyro }
        };

        foreach (var (action, el) in map)
        {
            if (!ValidateElementalActionAvatarIndex(task, action, el, _combatScenes))
            {
                return false;
            }
        }

        return true;
    }

    private bool ValidateElementalActionAvatarIndex(PathingTask task, string action, ElementalType el,
        CombatScenes combatScenes)
    {
        if (task.HasAction(action))
        {
            foreach (var avatar in combatScenes.GetAvatars())
            {
                if (ElementalCollectAvatarConfigs.Get(avatar.Name, el) != null)
                {
                    return true;
                }
            }

            Logger.LogError("此路径存在 {El}元素采集 动作，队伍中没有对应元素角色:{Names}，无法执行此路径！", el.ToChinese(),
                string.Join(",", ElementalCollectAvatarConfigs.GetAvatarNameList(el)));
            return false;
        }
        else
        {
            return true;
        }
    }

    private List<List<WaypointForTrack>> ConvertWaypointsForTrack(List<Waypoint> positions, PathingTask task)
    {
        var validationDiagnostics = RouteLayerSelectorResolver.ValidateTask(task);
        if (validationDiagnostics.Count > 0)
        {
            throw new InvalidOperationException($"地图追踪任务图层选择器配置无效：{string.Join("; ", validationDiagnostics)}");
        }

        // 把 X Y 转换为 MatX MatY
        var allList = positions.Select(waypoint =>
        {
            var effectiveSelector = RouteLayerSelectorResolver.ResolveEffectiveSelector(task.Info, waypoint, out var diagnostics);
            foreach (var diagnostic in diagnostics)
            {
                Logger.LogWarning("地图追踪任务 {TaskName} 图层选择器诊断：{Diagnostic}", task.Info.Name, diagnostic);
            }

            WaypointForTrack wft = new WaypointForTrack(waypoint, new RouteMapContext(task.Info.MapName, task.Info.MapMatchMethod, effectiveSelector));
            wft.Misidentification=waypoint.PointExtParams.Misidentification;
            wft.MonsterTag = waypoint.PointExtParams.MonsterTag;
            wft.EnableMonsterLootSplit = waypoint.PointExtParams.EnableMonsterLootSplit;
            wft.PathingTaskFileName = task.FileName;
            wft.PathingTaskFullPath = task.FullPath;
            return wft;
        }).ToList();

        // 按照WaypointType.Teleport.Code切割数组
        var result = new List<List<WaypointForTrack>>();
        var tempList = new List<WaypointForTrack>();
        foreach (var waypoint in allList)
        {
            if (waypoint.Type == WaypointType.Teleport.Code)
            {
                if (tempList.Count > 0)
                {
                    result.Add(tempList);
                    tempList = new List<WaypointForTrack>();
                }
            }

            tempList.Add(waypoint);
        }

        result.Add(tempList);

        return result;
    }

    /// <summary>
    /// 尝试队伍回血，如果单人回血，由于记录检查时是哪位残血，则当作行走位处理。
    /// </summary>
    private async Task<bool> TryPartyHealing()
    {
        if (_combatScenes is null) return false;
        foreach (var avatar in _combatScenes.GetAvatars())
        {
            if (avatar.Name == "白术")
            {
                if (avatar.TrySwitch())
                {
                    //1命白术能两次
                    Simulation.SendInput.SimulateAction(GIActions.ElementalSkill);
                    await Delay(800, ct);
                    Simulation.SendInput.SimulateAction(GIActions.ElementalSkill);
                    await Delay(800, ct);
                    await SwitchAvatar(PartyConfig.MainAvatarIndex);
                    await Delay(4000, ct);
                    return true;
                }

                break;
            }
            else if (avatar.Name == "希格雯")
            {
                if (avatar.TrySwitch())
                {
                    Simulation.SendInput.SimulateAction(GIActions.ElementalSkill);
                    await Delay(11000, ct);
                    await SwitchAvatar(PartyConfig.MainAvatarIndex);
                    return true;
                }

                break;
            }
            else if (avatar.Name == "珊瑚宫心海")
            {
                if (avatar.TrySwitch())
                {
                    Simulation.SendInput.SimulateAction(GIActions.ElementalSkill);
                    await Delay(500, ct);
                    //尝试Q全队回血
                    Simulation.SendInput.SimulateAction(GIActions.ElementalBurst);
                    //单人血只给行走位加血
                    await SwitchAvatar(PartyConfig.MainAvatarIndex);
                    await Delay(5000, ct);
                    return true;
                }
            }
        }


        return false;
    }

    private async Task RecoverWhenLowHp(WaypointForTrack waypoint)
    {
        var timing = PartyConfig.RecoverTiming;
        if (timing == RecoverTiming.Never)
        {
            return;
        }

        if (timing == RecoverTiming.OnlyTeleport && waypoint.Type != WaypointType.Teleport.Code)
        {
            return;
        }

        using var region = CaptureToRectArea();
        if (IsKnownPathTransformation(region)) return;
        if (Bv.CurrentAvatarIsLowHp(region))
        {
            _pathingMacro?.Release();
            CaptureLowHpRecoveryEvidence(region,
                $"route={waypoint.PathingTaskFileName} segment={CurWaypoints.Item1 + 1} node={waypoint.Id} type={waypoint.Type} move={waypoint.MoveMode} action={waypoint.Action} skipOtherOperations={_skipOtherOperations}; before any healing or teleport", Logger);
            if (await TryPartyHealing())
            {
                var fence = new Fischless.GameCapture.CaptureFrameFence(region.FrameStamp, TimeProvider.System.GetTimestamp());
                var healed = await HealingObservation.WaitAsync(fence, () =>
                {
                    using var fresh = CaptureToRectArea();
                    return new(fresh.FrameStamp, Bv.IsInMainUi(fresh), Bv.CurrentAvatarIsLowHp(fresh));
                }, ms => Delay(ms, ct), ct);
                if (healed) return;
            }
            Logger.LogInformation("当前角色血量过低，去七天神像恢复");
            await TpStatueOfTheSeven();
            throw new RetryException("回血完成后重试路线");
        }
        else if (ReleaseMacroBeforeRevive(region) && Bv.ClickIfInReviveModal(region))
        {
            var returnedToMainUi = await Bv.WaitForMainUi(ct);
            if (!Bv.IsReviveRecoveryConfirmed(clicked: true, returnedToMainUi))
            {
                throw new RetryException("已点击全队复苏，但未确认返回主界面");
            }

            Logger.LogInformation("已确认全队复苏并返回主界面");
            await Delay(4000, ct);
            // 血量肯定不满，直接去七天神像回血
            await TpStatueOfTheSeven();
            throw new RetryException("回血完成后重试路线");
        }
    }

    internal static void CaptureLowHpRecoveryEvidence(ImageRegion frame, string detail, ILogger logger)
    {
        try
        {
            var source = frame.FrameStamp;
            DiagnosticEvidenceScope.Current?.TryCapture(frame, $"path-low-hp:{source.SessionId}/{source.Sequence}",
                "before-recovery", detail, logger);
            logger.LogDebug("PATH_LOW_HP_RECOVERY source={Session}/{Sequence} {Detail}", source.SessionId, source.Sequence, detail);
        }
        catch { /* 取证借用当前帧，不影响恢复、重试或帧所有权。 */ }
    }

    private async Task TpStatueOfTheSeven()
    {
        // tp 到七天神像回血
        var tpTask = new TpTask(ct);
        await RunnerContext.Instance.StopAutoPickRunTask(async () => await tpTask.TpToStatueOfTheSeven(), 5);
        Logger.LogInformation("血量恢复完成。【设置】-【七天神像设置】可以修改回血相关配置。");
    }

    /// <summary>
    /// 尝试自动领取派遣奖励，
    /// </summary>
    /// <returns>是否可以领取派遣奖励</returns>
    private async Task<bool> TryGetExpeditionRewardsDispatch(TpTask? tpTask = null)
    {
        if (tpTask == null)
        {
            tpTask = new TpTask(ct);
        }
        
        // 最小5分钟间隔
        if ( _combatScenes?.CurrentMultiGameStatus?.IsInMultiGame == true || (DateTime.UtcNow - _lastGetExpeditionRewardsTime).TotalMinutes < 5)
        {
            return false;
        }

        //打开大地图操作
        await tpTask.OpenBigMapUi();
        bool changeBigMap = false;
        string adventurersGuildCountry =
            TaskContext.Instance().Config.OtherConfig.AutoFetchDispatchAdventurersGuildCountry;
        if (!RunnerContext.Instance.isAutoFetchDispatch && adventurersGuildCountry != "无" && !string.IsNullOrEmpty(adventurersGuildCountry))
        {
            using var ra1 = CaptureToRectArea();
            var textRect = new Rect(60, 20, 160, 260);
            using var textMat = new Mat(ra1.SrcMat, textRect);
            string text = OcrFactory.Paddle.Ocr(textMat);
            if (text.Contains("探索派遣奖励"))
            {
                changeBigMap = true;
                Logger.LogInformation("开始自动领取派遣任务！");
                try
                {
                    RunnerContext.Instance.isAutoFetchDispatch = true;
                    await RunnerContext.Instance.StopAutoPickRunTask(
                        async () => await new GoToAdventurersGuildTask().Start(adventurersGuildCountry, ct, null, true),
                        5);
                    Logger.LogInformation("自动领取派遣结束，回归原任务！");
                }
                catch (Exception e)
                {
                    Logger.LogInformation("未知原因，发生异常，尝试继续执行任务！");
                }
                finally
                {
                    RunnerContext.Instance.isAutoFetchDispatch = false;
                    _lastGetExpeditionRewardsTime = DateTime.UtcNow; // 无论成功与否都更新时间
                }
            }
        }

        return changeBigMap;
    }

    private async Task HandleTeleportWaypoint(WaypointForTrack waypoint)
    {
        var forceTp = waypoint.Action == ActionEnum.ForceTp.Code;
        TpTask tpTask = new TpTask(ct);
        await TryGetExpeditionRewardsDispatch(tpTask);
        var (tpX, tpY) = await tpTask.Tp(waypoint.GameX, waypoint.GameY, waypoint.MapContext, forceTp);
        var (tprX, tprY) = MapManager.GetMap(waypoint.MapContext)
            .ConvertGenshinMapCoordinatesToImageCoordinates(new Point2f((float)tpX, (float)tpY));
        Navigation.SetPrevPosition(tprX, tprY, waypoint.MapLayerSelector); // 通过上一个位置直接进行局部特征匹配
        await Delay(500, ct); // 多等一会
    }

    public async Task FaceTo(WaypointForTrack waypoint)
    {
        Point2f position;
        using (var screen = CaptureToRectArea())
        {
            position = await GetPosition(screen, waypoint);
        }
        var targetOrientation = Navigation.GetTargetOrientation(waypoint, position);
        Logger.LogDebug("朝向点，位置({x2},{y2})", $"{waypoint.GameX:F1}", $"{waypoint.GameY:F1}");
        await WaitUntilRotatedTo(targetOrientation, 2);
        await Delay(500, ct);
    }

    public DateTime moveToStartTime;

    public async Task MoveTo(WaypointForTrack waypoint)
    {
        try
        {
        // 已证实变身时只能导航，不切回不存在的人形活动位。
        var transformed = false;
        if (_pathingMacro != null)
        {
            using var scene = _moveIo.Capture();
            transformed = IsKnownPathTransformation(scene);
        }
        if (!transformed)
        {
            _pathingMacro?.Release();
            await _moveIo.SwitchAvatar(PartyConfig.MainAvatarIndex);
        }
        // 切人完成时刻：切人后有约1秒CD，期间无法切换到其他角色（用于生存位）
        var switchAvatarTime = _moveIo.Clock.GetUtcNow().UtcDateTime;

        Point2f position;
        int additionalTimeInMs;
        using (var initialScreen = _moveIo.Capture())
        {
            var located = await _moveIo.Locate(initialScreen, waypoint);
            position = located.Point;
            additionalTimeInMs = located.AdditionalTimeInMs;
            if (_pathingMacro?.HasTail == true)
                _pathingMacro.AdoptNavigation(new(IsKnownPathTransformation(initialScreen)
                    ? PathingMacroScene.Transformed : _moveIo.CombatHud(initialScreen) ? PathingMacroScene.World : PathingMacroScene.Unknown,
                    initialScreen.FrameStamp), located.IsDirect && position != default && float.IsFinite(position.X) && float.IsFinite(position.Y), ct);
        }
        var targetOrientation = Navigation.GetTargetOrientation(waypoint, position);
        _moveIo.Logger.LogDebug("粗略接近途经点，位置({x2},{y2})", $"{waypoint.GameX:F1}", $"{waypoint.GameY:F1}");
        await _moveIo.RotateUntil(targetOrientation, 5);
        moveToStartTime = _moveIo.Clock.GetUtcNow().UtcDateTime;
        var progressHeartbeat = new PathProgressHeartbeat(moveToStartTime, TimeSpan.FromSeconds(15));
        var movementWatchdog = new PathMovementWatchdog(
            () => moveToStartTime,
            TimeSpan.FromSeconds(60));
        var lastPositionRecord = _moveIo.Clock.GetUtcNow().UtcDateTime;
        var fastMode = false;
        var prevPositions = new List<Point2f>();
        var fastModeColdTime = DateTime.MinValue;
        var prevNotTooFarPosition = position;
        int num = 0, distanceTooFarRetryCount = 0, consecutiveRotationCountBeyondAngle = 0;
        // 连续偏角>5°持续状态的起始时间（配合帧数下限使用，替代原纯帧计数）
        DateTime beyondAngleStartTime = DateTime.MinValue;
        var hurryOnState = new HurryOnState();
        var flightObserved = false;
        var plainFlightTransit = waypoint.Type == WaypointType.Path.Code && string.IsNullOrWhiteSpace(waypoint.Action);
        var climbWindow = new PathClimbProgressWindow();
        CaptureFrameStamp lastMoveFrame = default;

        // 按下w，一直走
        SendPathForward(KeyType.KeyDown);
        // 赶路帧间隔：始终使用配置值（5-150 钳制），不依赖是否配置赶路角色（空选也可用）
        var hurryFrameInterval = Math.Clamp(PartyConfig.HurryOnFrameInterval, 5, 150);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            _pathingMacro?.CheckNavigation(ct);
            num++;
            if ((_moveIo.Clock.GetUtcNow().UtcDateTime - moveToStartTime).TotalSeconds > 240)
            {
                _moveIo.Logger.LogWarning("执行超时，放弃此次追踪");
                throw new RetryException("路径点执行超时，放弃整条路径");
            }

            using var screen = _moveIo.Capture();
            transformed = IsKnownPathTransformation(screen);

            _moveIo.EndJudgment(screen);

            // position = await GetPosition(screen, waypoint);
             var located = await _moveIo.Locate(screen, waypoint);
             position = located.Point;
             additionalTimeInMs = located.AdditionalTimeInMs;
             ct.ThrowIfCancellationRequested();
             if (_moveIo.Clock.GetUtcNow().UtcDateTime >= moveToStartTime.AddSeconds(240))
                 throw new RetryException("路径点执行超时，放弃整条路径");
             if (additionalTimeInMs>0)
             {
                 additionalTimeInMs = additionalTimeInMs + 1000;//当做起步补偿
            }
            var distance = Navigation.GetDistance(waypoint, position);
            var progressObservedAt = _moveIo.Clock.GetUtcNow().UtcDateTime;
            if (waypoint.MoveMode == MoveModeEnum.Climb.Code && progressObservedAt >= moveToStartTime.AddSeconds(60))
                throw new RetryException("攀爬途经点超过60秒仍未完成，重试当前路线分段");
            var observation = waypoint.MoveMode == MoveModeEnum.Fly.Code || waypoint.MoveMode == MoveModeEnum.Climb.Code
                ? ReadMoveObservation(screen, located, lastMoveFrame) : default;
            // Recognition is synchronous and may itself consume the remaining node budget.
            ct.ThrowIfCancellationRequested();
            progressObservedAt = _moveIo.Clock.GetUtcNow().UtcDateTime;
            if (progressObservedAt >= moveToStartTime.AddSeconds(240))
                throw new RetryException("路径点执行超时，放弃整条路径");
            if (waypoint.MoveMode == MoveModeEnum.Climb.Code && progressObservedAt >= moveToStartTime.AddSeconds(60))
                throw new RetryException("攀爬途经点超过60秒仍未完成，重试当前路线分段");
            lastMoveFrame = screen.FrameStamp;
            if (waypoint.MoveMode == MoveModeEnum.Fly.Code && observation.Valid && observation.Motion == MotionStatus.Fly)
                flightObserved = true;
            Debug.WriteLine($"接近目标点中，距离为{distance}");
            if (distance < 4 && (waypoint.MoveMode != MoveModeEnum.Fly.Code ||
                observation.Valid && (flightObserved || plainFlightTransit)))
            {
                _moveIo.Logger.LogDebug("到达路径点附近");
                if (waypoint.MoveMode == MoveModeEnum.Fly.Code)
                    _moveIo.Logger.LogDebug("PATH_FLY_ARRIVAL node={Node} type={Type} action={Action} distance={Distance:F2} direct={Direct} source={Session}/{Sequence} observedFlight={Flight} plainTransit={PlainTransit}",
                        waypoint.Id, waypoint.Type, waypoint.Action, distance, located.IsDirect,
                        observation.Stamp.SessionId, observation.Stamp.Sequence, flightObserved, plainFlightTransit);
                break;
            }

            if (distance < 4 && waypoint.MoveMode == MoveModeEnum.Fly.Code)
            {
                SendPathForward(KeyType.KeyUp);
                await AdvanceFlyAsync(observation.Valid && observation.Motion == MotionStatus.Normal, waypoint, observation.Stamp);
                continue;
            }
            if (!_moveIo.IsDown(GIActions.MoveForward))
                SendPathForward(KeyType.KeyDown);

            if (movementWatchdog.ShouldAbort(
                    waypoint.MoveMode == MoveModeEnum.Climb.Code,
                    progressObservedAt))
            {
                _moveIo.Logger.LogWarning(
                    "攀爬途经点等待超时：耗时={ElapsedSeconds:F1}s，当前位置=({CurrentX:F1},{CurrentY:F1})，目标位置=({TargetX:F1},{TargetY:F1})，剩余距离={Distance:F1}；保留路线并重试当前分段",
                    (progressObservedAt - moveToStartTime).TotalSeconds,
                    position.X,
                    position.Y,
                    waypoint.GameX,
                    waypoint.GameY,
                    distance);
                throw new RetryException("攀爬途经点超过60秒仍未完成，重试当前路线分段");
            }

            if (progressHeartbeat.ShouldReport(progressObservedAt))
            {
                _moveIo.Logger.LogDebug(
                    "途经点仍在接近中：耗时={ElapsedSeconds:F1}s，移动模式={MoveMode}，当前位置=({CurrentX:F1},{CurrentY:F1})，目标位置=({TargetX:F1},{TargetY:F1})，剩余距离={Distance:F1}，帧数={FrameCount}",
                    (progressObservedAt - moveToStartTime).TotalSeconds,
                    waypoint.MoveMode,
                    position.X,
                    position.Y,
                    waypoint.GameX,
                    waypoint.GameY,
                    distance,
                    num);
            }

            if (distance > 500)
            {
                if (pathExecutorSuspend.CheckAndResetSuspendPoint())
                {
                    throw new RetryNoCountException("可能暂停导致路径过远，重试一次此路线！");
                }
                else
                {
                    distanceTooFarRetryCount++;
                    if (distanceTooFarRetryCount > 50)
                    {
                        if (position == new Point2f())
                        {
                            throw new HandledException("重试多次后，当前点位无法被识别，放弃此路径！");
                        }
                        else
                        {
                            _moveIo.Logger.LogWarning($"距离过远（{position.X},{position.Y}）->（{waypoint.X},{waypoint.Y}）={distance}，重试多次后仍然失败，放弃此路径点！");
                            throw new HandledException("目标距离过远，可能是当前点位无法识别，放弃此路径！");
                        }
                    }
                    else
                    {
                        // 取余减少日志输出频率
                        if (distanceTooFarRetryCount % 5 == 0)
                        {
                            _moveIo.Logger.LogWarning($"距离过远（{position.X},{position.Y}）->（{waypoint.X},{waypoint.Y}）={distance}，重试");
                        }
                        // 取余减少判断频率
                        if (distanceTooFarRetryCount % 10 == 0)
                        {
                            await ResolveAnomalies(screen);
                            _moveIo.Logger.LogInformation($"重置到上次正确识别的坐标 ({prevNotTooFarPosition.X},{prevNotTooFarPosition.Y})");
                            Navigation.SetPrevPosition(prevNotTooFarPosition.X, prevNotTooFarPosition.Y, waypoint.MapLayerSelector);
                            // 淡入淡出特效
                            await _moveIo.Delay(500, ct);
                        }
                        await _moveIo.Delay(50, ct);
                        continue;
                    }
                }
            } else
            {
                prevNotTooFarPosition = position;
            }

            // 非攀爬状态下，检测是否卡死（脱困触发器）
            if (waypoint.MoveMode != MoveModeEnum.Climb.Code)
            {
                if ((_moveIo.Clock.GetUtcNow().UtcDateTime - lastPositionRecord).TotalMilliseconds > 1000 + additionalTimeInMs)
                {
                    lastPositionRecord = _moveIo.Clock.GetUtcNow().UtcDateTime;
                    prevPositions.Add(position);
                    if (prevPositions.Count > 8)
                    {
                        var delta = prevPositions[^1] - prevPositions[^8];
                        if (Math.Abs(delta.X) + Math.Abs(delta.Y) < 3)
                        {
                            _inTrap++;
                            if (_inTrap > 2)
                            {
                                throw new RetryException("此路线出现3次卡死，重试一次路线或放弃此路线！");
                            }

                            _moveIo.Logger.LogWarning("疑似卡死，尝试脱离...");

                            //调用脱困代码，由TrapEscaper接管移动
                            _pathingMacro?.Release();
                            Helpers.ApplicationHostBootstrapGuard.EnsureAllowed();
                            await _trapEscaper.RotateAndMove();
                            await _trapEscaper.MoveTo(waypoint);
                            SendPathForward(KeyType.KeyDown);
                            _moveIo.Logger.LogInformation("卡死脱离结束");
                            continue;
                        }
                    }
                }
            }

            if (waypoint.MoveMode == MoveModeEnum.Climb.Code &&
                climbWindow.Observe(observation, progressObservedAt, additionalTimeInMs))
            {
                try { await RecoverNormalClimbAsync(waypoint, observation.Stamp); }
                finally { climbWindow.Clear(); }
                continue;
            }

            // 旋转视角
            targetOrientation = Navigation.GetTargetOrientation(waypoint, position);
            //执行旋转
            var diff = _moveIo.RotateStep(targetOrientation, screen);
            // 进入MoveTo超过2秒且至少5帧后才启用旋转纠正（绕过起步阶段的抖动）
            if ((_moveIo.Clock.GetUtcNow().UtcDateTime - moveToStartTime).TotalSeconds > 2 && num >= 5)
            {
                if (Math.Abs(diff) > 5)
                {
                    consecutiveRotationCountBeyondAngle++;
                    if (beyondAngleStartTime == DateTime.MinValue)
                    {
                        beyondAngleStartTime = _moveIo.Clock.GetUtcNow().UtcDateTime;
                    }
                }
                else
                {
                    consecutiveRotationCountBeyondAngle = 0;
                    beyondAngleStartTime = DateTime.MinValue;
                }

                // 连续偏角>5°持续超过2秒（且至少3帧）时，说明边走边转不动，松W站定转向
                if (consecutiveRotationCountBeyondAngle >= 3
                    && (_moveIo.Clock.GetUtcNow().UtcDateTime - beyondAngleStartTime).TotalSeconds > 2)
                {
                    // 松W键，站定好转向，转完重新按下W继续走
                    SendPathForward(KeyType.KeyUp);
                    await _moveIo.RotateUntil(targetOrientation, 2);
                    SendPathForward(KeyType.KeyDown);
                }
            }

            // 赶路逻辑（使用角色技能加速赶路）
            if (!transformed) _pathingMacro?.Release();
            var hurryOnResult = !transformed && await (_moveIo.HurryOn?.Invoke(diff, waypoint, distance, screen, num)
                ?? TryHurryOnAsync(diff, waypoint, distance, screen, num, hurryOnState));
            if (hurryOnResult)
            {
                // continue 会跳过底部 await _moveIo.Delay(...)，
                // 导致 async state machine 的 MoveNext() 永不返回，调用栈逐轮叠加直到溢出。
                // 在此处显式等待以展开栈。
                await _moveIo.Delay(hurryFrameInterval, ct);
                continue;
            }

            // 根据指定方式进行移动
            if (waypoint.MoveMode == MoveModeEnum.Fly.Code)
            {
                var isFlying = _moveIo.Motion(screen) == MotionStatus.Fly;
                await AdvanceFlyAsync(!isFlying);
                continue;
            }

            if (waypoint.MoveMode == MoveModeEnum.Jump.Code)
            {
                _moveIo.Send(GIActions.Jump, KeyType.KeyPress);
                await _moveIo.Delay(200, ct);
                continue;
            }

            // 只有设置为run才会一直疾跑
            if (waypoint.MoveMode == MoveModeEnum.Run.Code)
            {
                if (distance > 20 != fastMode) // 距离大于20时可以使用疾跑/自由泳
                {
                    if (fastMode)
                    {
                        _moveIo.Send(GIActions.SprintMouse, KeyType.KeyUp);
                    }
                    else
                    {
                        _moveIo.Send(GIActions.SprintMouse, KeyType.KeyDown);
                    }

                    fastMode = !fastMode;
                }
            }
            else if (waypoint.MoveMode == MoveModeEnum.Dash.Code)
            {
                if (distance > 20) // 距离大于25时可以使用疾跑
                {
                    if (Math.Abs((fastModeColdTime - _moveIo.Clock.GetUtcNow().UtcDateTime).TotalMilliseconds) > 1000) //冷却一会
                    {
                        fastModeColdTime = _moveIo.Clock.GetUtcNow().UtcDateTime;
                        _moveIo.Send(GIActions.SprintMouse, KeyType.KeyPress);
                    }
                }
            }
            else if (waypoint.MoveMode != MoveModeEnum.Climb.Code) //否则自动短疾跑
            {
                // 使用 E 技能
                if (!transformed && distance > 10 && !string.IsNullOrEmpty(PartyConfig.GuardianAvatarIndex) &&
                    double.TryParse(PartyConfig.GuardianElementalSkillSecondInterval, out var s))
                {
                    if (s < 1)
                    {
                        _moveIo.Logger.LogWarning("元素战技冷却时间设置太短，不执行！");
                        return;
                    }

                    var ms = s * 1000;
                    if ((_moveIo.Clock.GetUtcNow().UtcDateTime - _elementalSkillLastUseTime).TotalMilliseconds > ms)
                    {
                        // 可能刚切过人在冷却时间内
                        if ((_moveIo.Clock.GetUtcNow().UtcDateTime - switchAvatarTime).TotalSeconds < 1 &&
                            (!string.IsNullOrEmpty(PartyConfig.MainAvatarIndex) &&
                             PartyConfig.GuardianAvatarIndex != PartyConfig.MainAvatarIndex))
                        {
                            await _moveIo.Delay(800, ct); // 总共1s
                        }

                        await UseElementalSkill();
                        _elementalSkillLastUseTime = _moveIo.Clock.GetUtcNow().UtcDateTime;
                    }
                }

                // 自动疾跑
                if (distance > 20 && PartyConfig.AutoRunEnabled)
                {
                    if (Math.Abs((fastModeColdTime - _moveIo.Clock.GetUtcNow().UtcDateTime).TotalMilliseconds) > 2500) //冷却时间2.5s，回复体力用
                    {
                        fastModeColdTime = _moveIo.Clock.GetUtcNow().UtcDateTime;
                        _moveIo.Send(GIActions.SprintMouse, KeyType.KeyPress);
                    }
                }
            }

            // 使用小道具
            if (PartyConfig.UseGadgetIntervalMs > 0)
            {
                if ((_moveIo.Clock.GetUtcNow().UtcDateTime - _useGadgetLastUseTime).TotalMilliseconds > PartyConfig.UseGadgetIntervalMs)
                {
                    _moveIo.Send(GIActions.QuickUseGadget, KeyType.KeyPress);
                    _useGadgetLastUseTime = _moveIo.Clock.GetUtcNow().UtcDateTime;
                }
            }

            await _moveIo.Delay(hurryFrameInterval, ct);
        }

        }
        finally
        {
            // Cleanup is permitted after cancellation/deadline; never leave this movement holding W.
            SendPathForward(KeyType.KeyUp);
        }
    }

    private PathMoveObservation ReadMoveObservation(ImageRegion screen, PathPosition position, CaptureFrameStamp previous)
    {
        var stamp = screen.FrameStamp;
        var sourceUsable = position.IsDirect && float.IsFinite(position.Point.X) && float.IsFinite(position.Point.Y)
            && position.Point != default && stamp.IsFresh(_moveIo.Clock, TimeSpan.FromSeconds(2))
            && _moveIo.CombatHud(screen);
        var motion = sourceUsable ? _moveIo.Motion(screen) : MotionStatus.Normal;
        // Keep the producer's timestamp: HUD/motion work must not refresh an aging source.
        sourceUsable = sourceUsable && stamp.IsFresh(_moveIo.Clock, TimeSpan.FromSeconds(2));
        var valid = sourceUsable && (!previous.IsKnown || stamp.IsAfter(previous));
        return new(stamp, position.Point, valid, motion, sourceUsable);
    }

    private async Task RecoverNormalClimbAsync(WaypointForTrack waypoint, CaptureFrameStamp previous)
    {
        async Task<PathMoveObservation> Observe()
        {
            using var screen = _moveIo.Capture();
            var position = await (_moveIo.LocateDirect ?? _moveIo.Locate)(screen, waypoint);
            var observation = ReadMoveObservation(screen, position, previous);
            previous = screen.FrameStamp;
            return observation;
        }
        ct.ThrowIfCancellationRequested();
        var remaining = moveToStartTime.AddSeconds(60) - _moveIo.Clock.GetUtcNow().UtcDateTime;
        if (remaining <= TimeSpan.Zero) throw new RetryException("攀爬途经点超过60秒仍未完成，重试当前路线分段");
        try
        {
            await UiOperation.RunAsync("path-climb-recovery", remaining, ct, async operation =>
            {
                using var scope = new PathRecoveryScope(_moveIo, operation, ct, previous, Observe);
                var confirmed = await scope.ObserveAsync();
                if (Navigation.GetDistance(waypoint, confirmed.Position) < 4) return true;
                if (++_inTrap > 2) throw new RetryException("此路线出现3次卡死，重试一次路线或放弃此路线！");
                await _trapEscaper.RotateAndMove(scope);
                await _trapEscaper.MoveTo(waypoint, scope);
                return true;
            }, clock: _moveIo.Clock);
        }
        catch (RecoveryObservationChanged) { /* Fresh Climb/Fly/unknown returns to this node, never means arrival. */ }
        catch (TimeoutException) { throw new RetryException("攀爬途经点超过60秒仍未完成，重试当前路线分段"); }
    }

    private async Task AdvanceFlyAsync(bool jump, WaypointForTrack? nearPoint = null, CaptureFrameStamp previous = default)
    {
        var near = nearPoint != null;
        async Task<bool> Step(UiOperation? operation)
        {
            if (jump)
            {
                operation?.Check();
                if (near)
                {
                    _moveIo.CheckInput();
                    operation!.Check();
                    while (true)
                    {
                        using var fresh = _moveIo.Capture();
                        var position = await (_moveIo.LocateDirect ?? _moveIo.Locate)(fresh, nearPoint!);
                        operation.Check();
                        var observed = ReadMoveObservation(fresh, position, previous);
                        operation.Check();
                        if (observed.SourceUsable && observed.Stamp == previous && observed.Motion == MotionStatus.Normal)
                        {
                            // Non-blocking capture may return the same producer frame; wait, never sign a replacement.
                            await _moveIo.Delay(50, ct);
                            operation.Check();
                            continue;
                        }
                        jump = observed.Valid && observed.Motion == MotionStatus.Normal && Navigation.GetDistance(nearPoint!, position.Point) < 4;
                        break;
                    }
                }
                operation?.Check();
                ct.ThrowIfCancellationRequested();
                if (jump)
                {
                    _moveIo.Send(GIActions.Jump, KeyType.KeyPress);
                    await _moveIo.Delay(200, ct);
                    operation?.Check();
                }
            }
            await _moveIo.Delay(100, ct);
            operation?.Check();
            return true;
        }
        if (!near) { await Step(null); return; }
        var remaining = moveToStartTime.AddSeconds(240) - _moveIo.Clock.GetUtcNow().UtcDateTime;
        if (remaining <= TimeSpan.Zero) throw new RetryException("路径点执行超时，放弃整条路径");
        try { await UiOperation.RunAsync("path-near-flight", remaining, ct, op => Step(op), clock: _moveIo.Clock); }
        catch (TimeoutException) { throw new RetryException("路径点执行超时，放弃整条路径"); }
    }

    private async Task UseElementalSkill()
    {
        if (string.IsNullOrEmpty(PartyConfig.GuardianAvatarIndex))
        {
            return;
        }

        await Delay(200, ct);

        // 切人
        Logger.LogInformation("切换盾、回血角色，使用元素战技");
        var avatar = await SwitchAvatar(PartyConfig.GuardianAvatarIndex, true);
        if (avatar == null)
        {
            return;
        }

        // 钟离往身后放柱子
        if (avatar.Name == "钟离")
        {
            Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyUp);
            await Delay(50, ct);
            Simulation.SendInput.SimulateAction(GIActions.MoveBackward);
            await Delay(200, ct);
        }

        avatar.UseSkill(PartyConfig.GuardianElementalSkillLongPress);

        // 钟离往身后放柱子 后继续走路
        if (avatar.Name == "钟离")
        {
            Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyDown);
        }
    }

    private async Task MoveCloseTo(WaypointForTrack waypoint)
    {
        Point2f position;
        int targetOrientation;
        Logger.LogDebug("精确接近目标点，位置({x2},{y2})", $"{waypoint.GameX:F1}", $"{waypoint.GameY:F1}");

        var stepsTaken = 0;
        var rotationPolicy = new PreciseApproachRotationPolicy(maxConsecutiveFailures: 2);
        while (!ct.IsCancellationRequested)
        {
            stepsTaken++;
            if (stepsTaken > 25)
            {
                throw new RetryException("精确接近目标点超时，重试当前路线分段");
            }

            using var screen = CaptureToRectArea();

            EndJudgment(screen);

            position = await GetPosition(screen, waypoint);
            var distance = Navigation.GetDistance(waypoint, position);
            if (distance < 2)
            {
                Logger.LogDebug("已到达路径点");
                break;
            }

            targetOrientation = Navigation.GetTargetOrientation(waypoint, position);
            var rotated = await WaitUntilRotatedTo(targetOrientation, 2, maxTryTimes: 20);
            var rotationFailed = rotationPolicy.Observe(rotated);
            if (stepsTaken == 1 || stepsTaken % 5 == 0 || !rotated)
                Logger.LogDebug("PATH_APPROACH segment={Segment} waypoint={Waypoint} step={Step}/25 targetImage=({TargetX:F1},{TargetY:F1}) currentImage=({X:F1},{Y:F1}) distance={Distance:F2} targetAngle={Angle} rotated={Rotated} consecutiveFailures={Failures}",
                    CurWaypoints.Item1 + 1, CurWaypoint.Item1 + 1, stepsTaken, waypoint.X, waypoint.Y,
                    position.X, position.Y, distance, targetOrientation, rotated, rotationPolicy.ConsecutiveFailures);
            if (rotationFailed)
            {
                Logger.LogWarning(
                    "精确接近连续 {Failures} 次无法完成视角转向，停止小碎步接近，避免在错误方向空转",
                    rotationPolicy.ConsecutiveFailures);
                throw new RetryException("精确接近连续转向失败，重试当前路线分段");
            }
            if (!rotated)
            {
                continue;
            }
            // 小碎步接近
            Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyDown);
            Thread.Sleep(60);
            Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyUp);
            // Simulation.SendInput.Keyboard.KeyDown(User32.VK.VK_W).Sleep(60).KeyUp(User32.VK.VK_W);
            await Delay(20, ct);
        }

        _arrivalReachedAt = Stopwatch.GetTimestamp();
        await SettleArrivalAsync(waypoint.Action,
            () => Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyUp),
            (milliseconds, token) => Delay(milliseconds, token), ct);
    }

    internal static async Task SettleArrivalAsync(string? action, Action releaseMovement,
        Func<int, CancellationToken, Task> delay, CancellationToken token)
    {
        releaseMovement();
        token.ThrowIfCancellationRequested();
        if (action != ActionEnum.Fight.Code) await delay(1000, token);
    }

    internal static Task DispatchArrivalActionAsync(IActionHandler handler, WaypointForTrack? waypoint,
        object? config, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return handler.RunAsync(token, waypoint, config);
    }

    private async Task BeforeMoveCloseToTarget(WaypointForTrack waypoint)
    {
        if (waypoint.MoveMode == MoveModeEnum.Fly.Code && waypoint.Action == ActionEnum.StopFlying.Code)
        {
            await ActionFactory.GetBeforeHandler(ActionEnum.StopFlying.Code).RunAsync(ct, waypoint);
        }
    }

    private async Task BeforeMoveToTarget(WaypointForTrack waypoint)
    {
        if (waypoint.Action == ActionEnum.UpDownGrabLeaf.Code)
        {
            _pathingMacro?.Release();
            Simulation.SendInput.Mouse.MiddleButtonClick();
            await Delay(300, ct);
            using var screen = CaptureToRectArea();
            var position = await GetPosition(screen, waypoint);
            var targetOrientation = Navigation.GetTargetOrientation(waypoint, position);
            await WaitUntilRotatedTo(targetOrientation, 10);
            var handler = ActionFactory.GetBeforeHandler(waypoint.Action);
            await handler.RunAsync(ct, waypoint);
        }
        else if (waypoint.Action == ActionEnum.LogOutput.Code)
        {
            Logger.LogInformation(waypoint.LogInfo);
        }
    }

    private async Task AfterMoveToTarget(WaypointForTrack waypoint)
    {
        if (waypoint.Action == ActionEnum.NahidaCollect.Code
            || waypoint.Action == ActionEnum.PickAround.Code
            || waypoint.Action == ActionEnum.Fight.Code
            || waypoint.Action == ActionEnum.HydroCollect.Code
            || waypoint.Action == ActionEnum.ElectroCollect.Code
            || waypoint.Action == ActionEnum.AnemoCollect.Code
            || waypoint.Action == ActionEnum.PyroCollect.Code
            || waypoint.Action == ActionEnum.CombatScript.Code
            || waypoint.Action == ActionEnum.Mining.Code
            || waypoint.Action == ActionEnum.LinneaMining.Code
            || waypoint.Action == ActionEnum.Fishing.Code
            || waypoint.Action == ActionEnum.ExitAndRelogin.Code
            || waypoint.Action == ActionEnum.EnterAndExitWonderland.Code
            || waypoint.Action == ActionEnum.SetTime.Code
            || waypoint.Action == ActionEnum.UseGadget.Code
            || waypoint.Action == ActionEnum.PickUpCollect.Code)
        {
            if (waypoint.Action != ActionEnum.CombatScript.Code) _pathingMacro?.Release();
            var handler = waypoint.Action == ActionEnum.CombatScript.Code && _pathingMacro != null
                ? new CombatScriptHandler(_pathingMacro) : ActionFactory.GetAfterHandler(waypoint.Action);
            Logger.LogDebug("PATH_ACTION_HANDOFF action={Action} sinceArrivalMs={Milliseconds:F1} fixedSettleMs={Settle}",
                waypoint.Action, _arrivalReachedAt == 0 ? -1 : Stopwatch.GetElapsedTime(_arrivalReachedAt).TotalMilliseconds,
                waypoint.Action == ActionEnum.Fight.Code ? 0 : IsTargetPoint(waypoint) ? 1000 : 0);
            //,PartyConfig
            await DispatchArrivalActionAsync(handler, waypoint, PartyConfig, ct);
            //统计结束战斗的次数
            if (waypoint.Action == ActionEnum.Fight.Code)
            {
                SuccessFight++;
            }
            if (_pathingMacro != null) await _pathingMacro.WaitAsync(1000, ct);
            else await Delay(1000, ct);
        }
    }

    private async Task<Avatar?> SwitchAvatar(string index, bool needSkill = false)
    {
        _pathingMacro?.Release();
        if (string.IsNullOrEmpty(index))
        {
            return null;
        }

        var avatar = _combatScenes?.SelectAvatar(int.Parse(index));
        if (avatar == null) return null;
        if (needSkill && !avatar.IsSkillReady())
        {
            Logger.LogInformation("角色{Name}技能未冷却，跳过。", avatar.Name);
            return null;
        }

        var success = avatar.TrySwitch(5);//多切换一次，否则如果切人纠正要等下一个循环
        if (success)
        {
            await Delay(100, ct);
            return avatar;
        }

        Logger.LogInformation("尝试切换角色{Name}失败！", avatar.Name);
        return null;
    }
    
    /// <summary>
    /// 根据时间在两个点之间插值。
    /// </summary>
    /// <param name="startPoint">起点坐标</param>
    /// <param name="endPoint">终点坐标</param>
    /// <param name="startTime">起始时间</param>
    /// <param name="midTime">中间时间</param>
    /// <param name="endTime">结束时间</param>
    /// <returns>中间点坐标</returns>
    public static Point2f InterpolatePointByTime(
        Point2f startPoint,
        Point2f endPoint,
        DateTime startTime,
        DateTime midTime,
        DateTime endTime)
    {
        // 计算时间差
        double totalMillis = (endTime - startTime).TotalMilliseconds;
        double midMillis = (midTime - startTime).TotalMilliseconds;

        // 防止除以0
        if (totalMillis == 0)
            return startPoint;

        // 计算比例
        float t = (float)(midMillis / totalMillis);
        if (t>1.0f)
        {
            t = 1.0f;
        }
        // 插值计算
        float x = startPoint.X + (endPoint.X - startPoint.X) * t;
        float y = startPoint.Y + (endPoint.Y - startPoint.Y) * t;

        return new Point2f(x, y);
    }
    
    private  Point2f prePosition;
    private  DateTime preTime;
    //自动构造点位的最大时间
    private int maxAutoPositionTime=10000; 
    private async Task WaitForCloseMap(int maxAttempts, int delayMs)
    {
        await Delay(delayMs, ct);
        for (var i = 0; i < maxAttempts; i++)
        {
            using var capture = CaptureToRectArea();
            if (Bv.IsInMainUi(capture))
            {
                return;
            }

            await Delay(delayMs, ct);
        }
        
    }

    private async Task<Point2f> GetPosition(ImageRegion imageRegion, WaypointForTrack waypoint)
    {
        return (await GetPositionAndTime(imageRegion, waypoint)).point;
    }
    //
    public bool GetPositionAndTimeSuspendFlag = false;
    private async Task<(Point2f point,int additionalTimeInMs)> GetPositionAndTime(ImageRegion imageRegion, WaypointForTrack waypoint)
    {
        var result = await GetDirectPositionAndTime(imageRegion, waypoint);
        return (result.Point, result.AdditionalTimeInMs);
    }

    private async Task<PathPosition> GetDirectPositionAndTime(ImageRegion imageRegion, WaypointForTrack waypoint)
    {
        // 复用此次导航帧；诊断不取新截图、不更新角色共享状态、不把提示当作运动许可。
        (_movementDiagnostics ??= new(Logger)).Observe($"{CurWaypoints.Item1 + 1}/{CurWaypoint.Item1 + 1}", () =>
        {
            if (!imageRegion.FrameStamp.IsFresh(TimeProvider.System, TimeSpan.FromSeconds(2)))
                return new(imageRegion.FrameStamp, waypoint.MoveMode, waypoint.Action, null, null, null, null);
            var hud = Bv.IsCombatHud(imageRegion);
            int? active = null;
            if (hud && _combatScenes != null)
            {
                var index = PartyAvatarSideIndexHelper.GetAvatarIndexIsActiveWithContext(imageRegion,
                    _combatScenes.GetAvatarIndexRectSnapshot(), new AvatarActiveCheckContext());
                if (index > 0) active = index;
            }
            return new(imageRegion.FrameStamp, waypoint.MoveMode, waypoint.Action, hud, active,
                hud ? Bv.CurrentAvatarIsLowHp(imageRegion, imageRegion.Height / 1080d) : null,
                Bv.GetMotionStatus(imageRegion).ToString());
        }, imageRegion, waypoint.PathingTaskFileName);
        var position = Navigation.GetPosition(imageRegion, waypoint.MapName, waypoint.MapMatchMethod, waypoint.MapLayerSelector);
        var isDirect = float.IsFinite(position.X) && float.IsFinite(position.Y) && position != default;
        int time = 0;
        if (position == new Point2f())
        {
            if (!Bv.IsInMainUi(imageRegion))
            {
                Logger.LogDebug("小地图位置定位失败，且当前不是主界面，进入异常处理");
                await ResolveAnomalies(imageRegion);
                isDirect = false;
            }
        }

        var distance = Navigation.GetDistance(waypoint, position);
        //中途暂停过，地图未识别到
        if (position is {X:0,Y:0} && GetPositionAndTimeSuspendFlag)
        {
            GetPositionAndTimeSuspendFlag = false;
            throw new RetryNoCountException("可能暂停导致路径过远，重试一次此路线！");
        }
        //何时处理   pathTooFar  路径过远  unrecognized 未识别
        if ((position is {X:0,Y:0} && waypoint.Misidentification.Type.Contains("unrecognized")) || (distance>500 && waypoint.Misidentification.Type.Contains("pathTooFar")))
        {
            if (waypoint.Misidentification.HandlingMode == "previousDetectedPoint")
            {
                if (prePosition != default)
                {
                    position = prePosition;
                    isDirect = false;
                    Logger.LogInformation(@$"未识别到具体路径，取上次点位");
                }
            }else if (waypoint.Misidentification.HandlingMode == "mapRecognition"){
                isDirect = false;
                //大地图识别坐标
                DateTime start = DateTime.Now;
                TpTask tpTask = new TpTask(ct);
                await tpTask.OpenBigMapUi(mapName: waypoint.MapName);
                try
                {
                    position = MapManager.GetMap(waypoint.MapContext).ConvertGenshinMapCoordinatesToImageCoordinates(tpTask.GetPositionFromBigMap(waypoint.MapContext));
                }
                catch (Exception e)
                {
                    Logger.LogInformation(@$"地图中心点识别失败！");
                }
               
                Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_ESCAPE);
                //Bv.IsInMainUi(imageRegion);
                await WaitForCloseMap(10,200);
                DateTime end = DateTime.Now;
                time=(int)(end - start).TotalMilliseconds;
                Logger.LogInformation(@$"未识别到具体路径，打开地图计算中心点({position.X},{position.Y})");
            }
            
            /*if (prePosition!=default)
            {*/
                //position = InterpolatePointByTime(prePosition,new Point2f((float)waypoint.GameX,(float)waypoint.GameY),preTime,DateTime.Now,preTime.AddMilliseconds(maxAutoPositionTime));
                //Logger.LogInformation(@$"未识别到具体路径，预测其路径为（{position.X},{position.Y}）,开始结束点位为：（{prePosition.X},{prePosition.Y}）（{waypoint.GameX},{waypoint.GameY}）");
                //Point2f GetBigMapCenterPoint(string mapName)

               // Logger.LogInformation(@$"未识别到具体路径，打开地图计算中心点({position.X},{position.Y})");
                //position =prePosition;
           // }

        }
        else
        {
            prePosition = position;
            preTime = DateTime.Now;
        }

        //Logger.LogDebug("识别到路径："+position.X+","+position.Y);
        return new(position, time, isDirect);
    }

    private async Task<bool> WaitUntilRotatedTo(int targetOrientation, int maxDiff, int maxTryTimes = 50)
    {
        if (await _rotateTask.WaitUntilRotatedTo(targetOrientation, maxDiff, maxTryTimes))
        {
            return true;
        }
        await ResolveAnomalies();
        return await _rotateTask.WaitUntilRotatedTo(targetOrientation, maxDiff, maxTryTimes);
    }

    /**
     * 处理各种异常场景
     * 需要保证耗时不能太高
     */
    private async Task ResolveAnomalies(ImageRegion? imageRegion = null)
    {
        _pathingMacro?.Release();
        if (_moveIo.RecoverUi != null) { await _moveIo.RecoverUi(imageRegion, ct); return; }
        using var ownedImageRegion = imageRegion == null ? CaptureToRectArea() : null;
        imageRegion ??= ownedImageRegion!;

        // 一些异常界面处理
        var cookRa = imageRegion.Find(GetAutoSkipRecognitionObject("Cook", imageRegion));
        var closeRa = imageRegion.Find(GetAutoSkipRecognitionObject("PageCloseMain", imageRegion));
        var closeRa2 = imageRegion.Find(ElementRecognition.Get("PageCloseWhite", imageRegion));
        var closeRa3 = imageRegion.Find(GetAutoSkipRecognitionObject("PageClose", imageRegion));
        if (cookRa.IsExist() || closeRa.IsExist() || closeRa2.IsExist() || closeRa3.IsExist())
        {
            // 排除大地图
            if (Bv.IsInBigMapUi(imageRegion))
            {
                return;
            }

            Logger.LogInformation("检测到其他界面，使用ESC关闭界面");
            Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_ESCAPE);
            await Delay(1000, ct); // 等待界面关闭
        }


        // 处理月卡
        await _blessingOfTheWelkinMoonTask.Start(ct);

        if (PartyConfig.AutoSkipEnabled)
        {
            // 判断是否进入剧情
            await AutoSkip();
        }
    }

    private async Task AutoSkip()
    {
        using var ra = CaptureToRectArea();
        var disabledUiButtonRa = ra.Find(GetAutoSkipRecognitionObject("DisabledUiButton", ra));
        if (disabledUiButtonRa.IsExist())
        {
            Logger.LogWarning("进入剧情，自动点击剧情直到结束");

            if (_autoSkipTrigger == null)
            {
                _autoSkipTrigger = new AutoSkipTrigger(new AutoSkipConfig
                {
                    Enabled = true,
                    QuicklySkipConversationsEnabled = true, // 快速点击过剧情
                    ClosePopupPagedEnabled = true,
                    ClickChatOption = "优先选择最后一个选项",
                });
                _autoSkipTrigger.Init();
            }

            int noDisabledUiButtonTimes = 0;

            while (true)
            {
                using var captureContent = new CaptureContent(CaptureToRectArea());
                var currentCapture = captureContent.CaptureRectArea;
                disabledUiButtonRa = currentCapture.Find(GetAutoSkipRecognitionObject("DisabledUiButton", currentCapture));
                if (disabledUiButtonRa.IsExist())
                {
                    _autoSkipTrigger.OnCapture(captureContent);
                    noDisabledUiButtonTimes = 0;
                }
                else
                {
                    noDisabledUiButtonTimes++;
                    if (noDisabledUiButtonTimes > 10)
                    {
                        Logger.LogInformation("自动剧情结束");
                        break;
                    }
                }

                await Delay(210, ct);
            }
        }
    }

    private void EndJudgment(ImageRegion ra)
    {
        if (EndAction != null && EndAction(ra))
        {
            throw new EndConditionSatisfiedException();
        }
    }
}
