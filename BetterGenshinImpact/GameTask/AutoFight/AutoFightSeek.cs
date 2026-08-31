using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.Core.Config;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;
using static BetterGenshinImpact.GameTask.Common.TaskControl;
using OpenCvSharp;
using BetterGenshinImpact.Core.Recognition.OpenCv;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using System;
using BetterGenshinImpact.GameTask.AutoFight.Assets;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.GameTask;

namespace BetterGenshinImpact.GameTask.AutoFight
{
    public static  class MoveForwardTask
    {

        public static async Task ExecuteAsync(Scalar scalarLower, Scalar scalarHigher, ILogger logger, CancellationToken ct)
        {
            await MoveForwardAsync(scalarLower, scalarHigher, logger, ct);
        }

        public static async Task<bool?> MoveForwardAsync(Scalar scalarLower, Scalar scalarHigher, ILogger logger, CancellationToken ct)
        {
            _ = scalarLower;
            _ = scalarHigher;
            return await AutoFightSeek.DetectAndApproachEnemyAsync(logger, ct);
        }

        internal static async Task<(int horizontal, int vertical)> TurnTowardIndicatorAsync(
            EnemySeekDecision decision,
            int imageWidth,
            int imageHeight,
            ILogger logger,
            CancellationToken ct)
        {
            if (decision.Action != AutoFightSeekAction.Approach || decision.Visual is not { } visual)
            {
                return (0, 0);
            }

            var bearing = AutoFightSeek.GetIndicatorBearingDegrees(visual, imageWidth, imageHeight);
            var cameraOffset = AutoFightSeek.GetIndicatorCameraOffset(
                decision.Direction,
                visual,
                imageWidth,
                imageHeight);
            var verticalCameraOffset = AutoFightSeek.GetIndicatorCameraVerticalOffset(visual, imageHeight);
            logger.LogInformation(
                "红色敌人方位小三角反馈转向: 候选={SignalCount}，位置=({X},{Y})，尺寸={Width}x{Height}，采用方位角={Bearing:F1}°，模板方位角={TemplateBearing:F1}°，方向={Direction}，本步转向=({CameraOffset},{VerticalCameraOffset})",
                decision.SignalCount,
                visual.X,
                visual.Y,
                visual.Width,
                visual.Height,
                bearing,
                visual.IndicatorBearingDegrees,
                decision.Direction,
                cameraOffset,
                verticalCameraOffset);

            if (cameraOffset != 0 || verticalCameraOffset != 0)
            {
                Simulation.SendInput.Mouse.MoveMouseBy(cameraOffset, verticalCameraOffset);
                await Task.Delay(180, ct);
            }

            return (cameraOffset, verticalCameraOffset);
        }

        internal static async Task AdvanceLockedRouteAsync(
            int verticalCameraOffset,
            int completedSteps,
            int signalCount,
            ILogger logger,
            CancellationToken ct)
        {
            if (verticalCameraOffset != 0)
            {
                Simulation.SendInput.Mouse.MoveMouseBy(0, verticalCameraOffset);
                await Task.Delay(80, ct);
            }

            logger.LogInformation(
                "锁定路线前进第 {Step}/6 秒：本帧记录到其他箭头 {SignalCount} 个但不改目标，血条仍为唯一抢占条件，俯仰修正={VerticalOffset}",
                completedSteps + 1,
                signalCount,
                verticalCameraOffset);
            await MoveWithKeysAsync(ct, GIActions.MoveForward);
        }

        internal static async Task AdvanceFixedTopHealthOnceAsync(
            ILogger logger,
            CancellationToken ct)
        {
            var nextAdvance = AutoFightSeek.GetFixedTopHealthAdvanceCount() + 1;
            logger.LogInformation(
                nextAdvance == 1
                    ? "顶部固定精英血条首次出现：沿当前已确定方向前进 1 秒，确定输出点后停止本阶段接近"
                    : "顶部固定精英血条连续 10 秒未掉血：执行第 {Advance}/6 次 1 秒位置微调，然后重新观察输出进展",
                nextAdvance);
            await MoveWithKeysAsync(ct, GIActions.MoveForward);
        }

        internal static async Task ApproachVisibleEnemyAsync(
            EnemySeekDecision decision,
            int imageWidth,
            int imageHeight,
            ILogger logger,
            CancellationToken ct)
        {
            if (decision.Action != AutoFightSeekAction.ApproachVisibleEnemy || decision.Visual is not { } visual)
            {
                return;
            }

            var cameraOffset = AutoFightSeek.GetVisibleEnemyCameraOffset(visual, imageWidth);
            var verticalCameraOffset = AutoFightSeek.GetVisibleEnemyCameraVerticalOffset(visual, imageHeight);
            logger.LogInformation(
                "检测到远距离敌人血条: 位置=({X},{Y})，尺寸={Width}x{Height}，仅高度低于 {CloseHeight}px 才接近，转向=({CameraOffset},{VerticalCameraOffset})",
                visual.X,
                visual.Y,
                visual.Width,
                visual.Height,
                AutoFightSeek.GetVisibleEnemyCloseHeightThreshold(imageHeight),
                cameraOffset,
                verticalCameraOffset);

            if (cameraOffset != 0 || verticalCameraOffset != 0)
            {
                Simulation.SendInput.Mouse.MoveMouseBy(cameraOffset, verticalCameraOffset);
                await Task.Delay(180, ct);
            }

            if (cameraOffset != 0)
            {
                logger.LogInformation(
                    "血条仍有横向误差，本轮只转向不前进，下一帧重新定位");
                return;
            }

            var approachDuration = AutoFightSeek.GetVisibleEnemyApproachDurationMilliseconds(
                visual,
                imageHeight);
            logger.LogInformation(
                "血条横向已对准，执行 {Duration}ms 短脉冲接近后重新截图",
                approachDuration);
            await MoveWithKeysAsync(
                ct,
                approachDuration,
                GIActions.MoveForward);
        }

        private static Task MoveWithKeysAsync(
            CancellationToken ct,
            params GIActions[] actions)
        {
            return MoveWithKeysAsync(ct, 1000, actions);
        }

        private static async Task MoveWithKeysAsync(
            CancellationToken ct,
            int durationMilliseconds,
            params GIActions[] actions)
        {
            var pressedActions = new List<GIActions>();
            try
            {
                foreach (var action in actions)
                {
                    Simulation.SendInput.SimulateAction(action, KeyType.KeyDown);
                    pressedActions.Add(action);
                }

                await Task.Delay(durationMilliseconds, ct);
            }
            finally
            {
                foreach (var action in pressedActions)
                {
                    Simulation.SendInput.SimulateAction(action, KeyType.KeyUp);
                }
            }
        }
    }

    internal enum AutoFightSeekAction
    {
        Scan,
        Approach,
        ContinueLockedRoute,
        ApproachVisibleEnemy,
        ApproachFixedTopHealthTarget,
        KeepFighting
    }

    internal enum EnemyIndicatorDirection
    {
        None,
        Forward,
        Left,
        Right,
        Behind
    }

    internal readonly record struct EnemySeekVisual(
        int X,
        int Y,
        int Width,
        int Height,
        int Area,
        double? IndicatorBearingDegrees = null)
    {
        internal int CenterX => X + Width / 2;
        internal int CenterY => Y + Height / 2;
    }

    internal readonly record struct EnemySeekDecision(
        AutoFightSeekAction Action,
        EnemyIndicatorDirection Direction,
        EnemySeekVisual? Visual = null,
        int SignalCount = 0);

    internal readonly record struct DirectionIndicatorTemplateSpec(
        string FileName,
        double BearingDegrees);

    internal sealed record DirectionIndicatorTemplateFeature(
        Point[] Contour,
        Mat Mask,
        double BearingDegrees);

    internal sealed class VisibleHealthApproachPolicy
    {
        private readonly object _gate = new();
        private readonly int _maxApproachSteps;
        private readonly int _missingFramesBeforeReset;
        private int _approachSteps;
        private int _missingFrames;
        private EnemySeekVisual? _trackedVisual;
        private int _pendingHorizontalCameraOffset;
        private int _pendingVerticalCameraOffset;
        private bool _preserveBudgetAcrossUnknownCameraMovement;

        internal VisibleHealthApproachPolicy(int maxApproachSteps, int missingFramesBeforeReset)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxApproachSteps);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(missingFramesBeforeReset);
            _maxApproachSteps = maxApproachSteps;
            _missingFramesBeforeReset = missingFramesBeforeReset;
        }

        internal EnemySeekDecision Evaluate(
            EnemySeekDecision decision,
            int imageWidth,
            int imageHeight)
        {
            lock (_gate)
            {
                if (decision.Action == AutoFightSeekAction.ApproachVisibleEnemy)
                {
                    if (decision.Visual is { } currentVisual)
                    {
                        var targetIsConsistent = _trackedVisual is not { } trackedVisual
                                                 || AutoFightSeek.IsVisibleHealthTargetConsistent(
                                trackedVisual,
                                currentVisual,
                                imageWidth,
                                imageHeight,
                                _pendingHorizontalCameraOffset,
                                _pendingVerticalCameraOffset);
                        if (!targetIsConsistent
                            && !_preserveBudgetAcrossUnknownCameraMovement)
                        {
                            _approachSteps = 0;
                        }
                        _trackedVisual = currentVisual;
                    }
                    ClearPendingCameraMovement();
                    _preserveBudgetAcrossUnknownCameraMovement = false;
                    _missingFrames = 0;
                    return _approachSteps >= _maxApproachSteps
                        ? new EnemySeekDecision(AutoFightSeekAction.Scan, EnemyIndicatorDirection.None)
                        : decision;
                }

                if (decision.Action == AutoFightSeekAction.KeepFighting && decision.Visual.HasValue)
                {
                    _approachSteps = 0;
                    _missingFrames = 0;
                    _trackedVisual = decision.Visual;
                    ClearPendingCameraMovement();
                    _preserveBudgetAcrossUnknownCameraMovement = false;
                    return decision;
                }

                _missingFrames++;
                if (_missingFrames >= _missingFramesBeforeReset)
                {
                    _approachSteps = 0;
                    _missingFrames = 0;
                    _trackedVisual = null;
                    ClearPendingCameraMovement();
                    _preserveBudgetAcrossUnknownCameraMovement = false;
                }
                return decision;
            }
        }

        internal void RecordApproachStep(
            int horizontalCameraOffset,
            int verticalCameraOffset)
        {
            lock (_gate)
            {
                if (_approachSteps < _maxApproachSteps)
                {
                    _approachSteps++;
                }
                _pendingHorizontalCameraOffset = horizontalCameraOffset;
                _pendingVerticalCameraOffset = verticalCameraOffset;
                _preserveBudgetAcrossUnknownCameraMovement = false;
                _missingFrames = 0;
            }
        }

        internal void RecordCameraMovement(
            int horizontalCameraOffset,
            int verticalCameraOffset)
        {
            lock (_gate)
            {
                if (!_trackedVisual.HasValue)
                {
                    return;
                }
                if (horizontalCameraOffset == 0 && verticalCameraOffset == 0)
                {
                    return;
                }
                _pendingHorizontalCameraOffset += horizontalCameraOffset;
                _pendingVerticalCameraOffset += verticalCameraOffset;
                _missingFrames = 0;
            }
        }

        internal void PreserveBudgetAcrossUnknownCameraMovement()
        {
            lock (_gate)
            {
                if (!_trackedVisual.HasValue)
                {
                    return;
                }
                ClearPendingCameraMovement();
                _preserveBudgetAcrossUnknownCameraMovement = true;
            }
        }

        internal void Reset()
        {
            lock (_gate)
            {
                _approachSteps = 0;
                _missingFrames = 0;
                _trackedVisual = null;
                ClearPendingCameraMovement();
                _preserveBudgetAcrossUnknownCameraMovement = false;
            }
        }

        private void ClearPendingCameraMovement()
        {
            _pendingHorizontalCameraOffset = 0;
            _pendingVerticalCameraOffset = 0;
        }
    }

    public class AutoFightSeek
    {
        public static int RotationCount = 0;

        private static int _indicatorRouteLocked;
        private static int _fixedTopHealthTracked;
        private static int _fixedTopHealthAdvanceCompleted;
        private static int _fixedTopHealthMissingFrames;
        private static int _fixedTopHealthBaselineWidth;
        private static int _fixedTopHealthLowestWidth;
        private static long _fixedTopHealthLastProgressTicks;
        private static int _fixedTopHealthAdvanceCount;
        private static int _seekSelectionScreenshotSequence;

        private static bool IsIndicatorRouteLocked => Volatile.Read(ref _indicatorRouteLocked) == 1;

        internal static void ResetSeekState()
        {
            RotationCount = 0;
            Interlocked.Exchange(ref _indicatorRouteLocked, 0);
            Interlocked.Exchange(ref _fixedTopHealthTracked, 0);
            Interlocked.Exchange(ref _fixedTopHealthAdvanceCompleted, 0);
            Interlocked.Exchange(ref _fixedTopHealthMissingFrames, 0);
            Interlocked.Exchange(ref _fixedTopHealthBaselineWidth, 0);
            Interlocked.Exchange(ref _fixedTopHealthLowestWidth, 0);
            Interlocked.Exchange(ref _fixedTopHealthLastProgressTicks, 0);
            Interlocked.Exchange(ref _fixedTopHealthAdvanceCount, 0);
            VisibleHealthApproach.Reset();
        }

        private static void LockIndicatorRoute()
        {
            Interlocked.Exchange(ref _indicatorRouteLocked, 1);
        }

        private static void UnlockIndicatorRoute()
        {
            Interlocked.Exchange(ref _indicatorRouteLocked, 0);
        }

        private const int VerticalSeekScaleNumerator = 4;
        private const int VerticalSeekScaleDenominator = 10;
        private const int VerticalSeekMaxTargetOffset = 3200;
        private const int MaxVerticalMouseStep = 240;
        private const int SeekViewportHeight = 900;
        private const int LockedRouteForwardSeconds = 6;
        private const int MaxVisibleEnemyApproachSteps = 4;
        private const int VisibleHealthResetMissingFrames = 3;
        private const int FixedTopHealthResetMissingFrames = 3;
        private const int FixedTopHealthProgressMinPixels = 8;
        private const int FixedTopHealthNoProgressSeconds = 10;
        private const int FixedTopHealthMaxAdvanceCount = 6;
        private const int IndicatorReacquireDelayMilliseconds = 120;
        private const int MaxIndicatorTurnFeedbackSteps = 8;
        private const int VisibleEnemyCloseHealthBarHeightAt1080 = 6;
        // 16 个从本机实战截图裁出的完整菱形箭头按 22.5° 覆盖一周；只接受强轮廓匹配，
        // 再叠加粉红色占比、屏幕环带和跨帧复核，避免红叶与 HUD 图标误报。
        private const double DirectionIndicatorFeatureThreshold = 0.14;
        private const double DirectionIndicatorOrientationMinScore = 0.55;
        private const double DirectionIndicatorMaxScreenBearingDelta = 35;
        private const double DirectionIndicatorMinSolidity = 0.50;
        private const double DirectionIndicatorMaxSolidity = 0.92;
        private const double DirectionIndicatorMinPinkRedShare = 0.70;
        private const int HalfTurnMouseOffset = 1920;
        private const int MaxIndicatorCameraStep = 320;
        private const int MaxVisibleEnemyCameraStep = 320;

        private static readonly DirectionIndicatorTemplateSpec[] DirectionIndicatorTemplateSpecs =
        {
            new("enemy_direction_indicator_live_bearing_000.png", 0),
            new("enemy_direction_indicator_live_bearing_022_5.png", 22.5),
            new("enemy_direction_indicator_live_bearing_045.png", 45),
            new("enemy_direction_indicator_live_bearing_067_5.png", 67.5),
            new("enemy_direction_indicator_live_bearing_090.png", 90),
            new("enemy_direction_indicator_live_bearing_112_5.png", 112.5),
            new("enemy_direction_indicator_live_bearing_135.png", 135),
            new("enemy_direction_indicator_live_bearing_157_5.png", 157.5),
            new("enemy_direction_indicator_live_bearing_180.png", 180),
            new("enemy_direction_indicator_live_bearing_202_5.png", -157.5),
            new("enemy_direction_indicator_live_bearing_225.png", -135),
            new("enemy_direction_indicator_live_bearing_247_5.png", -112.5),
            new("enemy_direction_indicator_live_bearing_270.png", -90),
            new("enemy_direction_indicator_live_bearing_292_5.png", -67.5),
            new("enemy_direction_indicator_live_bearing_315.png", -45),
            new("enemy_direction_indicator_live_bearing_337_5.png", -22.5)
        };

        private static readonly Lazy<IReadOnlyList<DirectionIndicatorTemplateFeature>> DirectionIndicatorTemplates =
            new(LoadDirectionIndicatorTemplates);

        private static readonly int[] VerticalSeekWave = { 0, -1, 0, 1 };
        private static readonly int[] VerticalSeekTrackCenters = { -2, 0, 2 };
        private static readonly int[] LockedRouteVerticalSteps = { 0, -120, 240, -240, 120, 0 };
        private static readonly VisibleHealthApproachPolicy VisibleHealthApproach =
            new(MaxVisibleEnemyApproachSteps, VisibleHealthResetMissingFrames);
        
        private static readonly Dictionary<int, int> RotaryFactorMapping = new Dictionary<int, int> //旋转因子映射表
        {
            { 1, 100 }, { 2, 90 }, { 3, 80}, { 4, 70 }, { 5, 60}, { 6,45 },
            { 7, 30 }, { 8, 15 }, { 9, 6 }, { 10, 1 }, { 11,-10 }, { 12,-50 }, { 13, -60 }
        };
        
        public static async Task<bool?> SeekAndFightAsync(ILogger logger, int detectDelayTime,int delayTime,CancellationToken ct,bool isEndCheck = false,int rotaryFactor = 6)
        {
            Scalar bloodLower = new Scalar(255, 90, 90);
            
            var adjustedX = RotaryFactorMapping[rotaryFactor];
            var adjustedDivisor = rotaryFactor<=12 ? 2 : 1.3;
            
            // Logger.LogInformation("开始寻找敌人 {Text} ...",adjustedX);
            
            int retryCount = isEndCheck? 1 : 0;
            var currentVerticalTarget = 0;

            var initialDecision = CaptureSeekDecision(
                bloodLower,
                null,
                out var initialImageWidth,
                out var initialImageHeight);
            if (await HandleDetectedEnemyAsync(
                    initialDecision,
                    initialImageWidth,
                    initialImageHeight,
                    bloodLower,
                    null,
                    logger,
                    ct))
            {
                return false;
            }

            if (ShouldRecenterCameraBeforeSeek())
            {
                await ResetSeekCameraPitchAsync(logger, ct);
            }

            currentVerticalTarget = GetSeekCameraVerticalTargetOffset(SeekViewportHeight, RotationCount, retryCount);
            logger.LogDebug("寻敌进入波浪轨迹: y={Y}, rotation={RotationCount}, retry={RetryCount}",
                currentVerticalTarget, RotationCount, retryCount);
            await MoveSeekCameraVerticallyAsync(currentVerticalTarget, ct);

            while (retryCount < 25+(int)(adjustedX / 5))
            {
                var decision = CaptureSeekDecision(bloodLower, null, out var imageWidth, out var imageHeight);
                if (await HandleDetectedEnemyAsync(
                        decision,
                        imageWidth,
                        imageHeight,
                        bloodLower,
                        null,
                        logger,
                        ct))
                {
                    return false;
                }

                if (retryCount == 0)
                {
                    await Delay(delayTime,ct);
                    // Logger.LogInformation("打开编队界面检查战斗是否结束，延时{detectDelayTime}毫秒检查", detectDelayTime);
                    Logger.LogInformation("打开编队界面检查战斗是否结束");
                    Simulation.SendInput.SimulateAction(GIActions.OpenPartySetupScreen);
                    await Delay(detectDelayTime, ct);
                    using var ra3 = CaptureToRectArea();
                    var b33 = ra3.SrcMat.At<Vec3b>(50, 790); // 进度条颜色
                    var whiteTile3 = ra3.SrcMat.At<Vec3b>(50, 768); // 白块
                    Simulation.SendInput.SimulateAction(GIActions.Drop);
                
                    if (IsWhite(whiteTile3.Item2, whiteTile3.Item1, whiteTile3.Item0) &&
                        IsYellow(b33.Item2, b33.Item1, b33.Item0))
                    {
                        logger.LogInformation("识别到战斗结束-s");
                        Simulation.SendInput.SimulateAction(GIActions.OpenPartySetupScreen);
                        return true;
                    }
                }

                var offset = GetSeekCameraOffset(imageWidth, imageHeight, RotationCount, retryCount);
                currentVerticalTarget = GetSeekCameraVerticalTargetOffset(imageHeight, RotationCount, retryCount + 1);
                logger.LogDebug("寻敌调整视角: x={X}, y={Y}, rotation={RotationCount}, retry={RetryCount}",
                    offset.x, offset.y, RotationCount, retryCount);
                await MoveSeekCameraAsync(offset, ct);

                await Task.Delay(50+(int)(adjustedX/adjustedDivisor),ct);

                var decisionAfterMove = CaptureSeekDecision(
                    bloodLower,
                    null,
                    out var movedImageWidth,
                    out var movedImageHeight);
                if (await HandleDetectedEnemyAsync(
                        decisionAfterMove,
                        movedImageWidth,
                        movedImageHeight,
                        bloodLower,
                        null,
                        logger,
                        ct))
                {
                    return false;
                }

                retryCount++;
            }
            
            await ReturnSeekCameraPitchToCenterAsync(logger, currentVerticalTarget, ct);
            logger.LogInformation("寻找敌人：{Text}", "无");
            return null;
        }

        internal static async Task<bool> DetectAndApproachEnemyAsync(
            ILogger logger,
            CancellationToken ct)
        {
            var bloodLower = new Scalar(255, 90, 90);
            var decision = CaptureSeekDecision(
                bloodLower,
                null,
                out var imageWidth,
                out var imageHeight);
            return await HandleDetectedEnemyAsync(
                decision,
                imageWidth,
                imageHeight,
                bloodLower,
                null,
                logger,
                ct);
        }

        internal static EnemySeekDecision SelectSeekDecision(
            IReadOnlyCollection<EnemySeekVisual> visuals,
            int imageWidth,
            int imageHeight,
            bool indicatorRouteLocked = false,
            bool fixedTopHealthTracked = false,
            bool fixedTopHealthAdvanceCompleted = false,
            bool fixedTopHealthExhausted = false)
        {
            // 顶部固定精英血条是整场唯一可靠的目标状态；同一帧可能混有技能栏、伤害特效等
            // 短红条，必须先于普通悬浮血条选择，否则专用接近路线永远拿不到控制权。
            var fixedTopHealthBar = visuals
                .Where(visual => IsHealthBar(visual, imageHeight))
                .Where(visual => IsFixedTopEliteHealthBar(visual, imageWidth, imageHeight))
                .OrderBy(visual => Math.Abs(visual.CenterX - imageWidth / 2))
                .ThenByDescending(visual => visual.Width)
                .FirstOrDefault();
            if (fixedTopHealthBar != default || fixedTopHealthTracked)
            {
                if (fixedTopHealthExhausted)
                {
                    return new EnemySeekDecision(AutoFightSeekAction.Scan, EnemyIndicatorDirection.None);
                }
                return new EnemySeekDecision(
                    fixedTopHealthAdvanceCompleted
                        ? AutoFightSeekAction.KeepFighting
                        : AutoFightSeekAction.ApproachFixedTopHealthTarget,
                    EnemyIndicatorDirection.None,
                    fixedTopHealthBar == default ? null : fixedTopHealthBar,
                    1);
            }

            var floatingHealthBar = visuals
                .Where(visual => IsHealthBar(visual, imageHeight))
                .Where(visual => !IsFixedTopEliteHealthBar(visual, imageWidth, imageHeight))
                .OrderByDescending(visual => visual.Height)
                .ThenByDescending(visual => visual.Area)
                .ThenByDescending(visual => visual.Width)
                .ThenBy(visual => Math.Abs(visual.CenterX - imageWidth / 2))
                .FirstOrDefault();
            if (floatingHealthBar != default)
            {
                return ShouldApproachVisibleEnemy(floatingHealthBar, imageWidth, imageHeight)
                    ? new EnemySeekDecision(
                        AutoFightSeekAction.ApproachVisibleEnemy,
                        GetVisibleEnemyDirection(floatingHealthBar, imageWidth),
                        floatingHealthBar,
                        1)
                    : new EnemySeekDecision(
                        AutoFightSeekAction.KeepFighting,
                        EnemyIndicatorDirection.None,
                        floatingHealthBar,
                        1);
            }

            return SelectDirectionIndicatorDecision(
                visuals,
                imageWidth,
                imageHeight,
                indicatorRouteLocked);
        }

        private static EnemySeekDecision SelectDirectionIndicatorDecision(
            IReadOnlyCollection<EnemySeekVisual> visuals,
            int imageWidth,
            int imageHeight,
            bool indicatorRouteLocked)
        {
            var indicators = visuals
                .Where(visual => IsDirectionIndicatorGeometry(visual, imageWidth, imageHeight))
                .OrderBy(visual => Math.Abs(GetIndicatorBearingDegrees(
                    visual,
                    imageWidth,
                    imageHeight)))
                .ThenByDescending(visual => visual.Area)
                .ToList();

            if (indicatorRouteLocked)
            {
                return new EnemySeekDecision(
                    AutoFightSeekAction.ContinueLockedRoute,
                    EnemyIndicatorDirection.None,
                    null,
                    indicators.Count);
            }

            if (indicators.Count == 0)
            {
                return new EnemySeekDecision(AutoFightSeekAction.Scan, EnemyIndicatorDirection.None);
            }

            var indicator = indicators[0];
            return new EnemySeekDecision(
                AutoFightSeekAction.Approach,
                GetIndicatorDirection(indicator, imageWidth, imageHeight),
                indicator,
                indicators.Count);
        }

        internal static int GetIndicatorCameraOffset(
            EnemyIndicatorDirection direction,
            EnemySeekVisual visual,
            int imageWidth,
            int imageHeight = SeekViewportHeight)
        {
            var bearing = GetIndicatorBearingDegrees(visual, imageWidth, imageHeight);
            if (Math.Abs(bearing) < 8)
            {
                return 0;
            }

            return Math.Clamp(
                (int)Math.Round(bearing / 180d * HalfTurnMouseOffset),
                -MaxIndicatorCameraStep,
                MaxIndicatorCameraStep);
        }

        internal static IReadOnlyList<int> GetIndicatorCameraSteps(
            EnemySeekVisual visual,
            int imageWidth,
            int imageHeight)
        {
            var bearing = GetIndicatorBearingDegrees(visual, imageWidth, imageHeight);
            if (Math.Abs(bearing) < 8)
            {
                return Array.Empty<int>();
            }

            var remaining = (int)Math.Round(bearing / 180d * HalfTurnMouseOffset);
            var steps = new List<int>();
            while (remaining != 0)
            {
                var step = Math.Clamp(remaining, -MaxIndicatorCameraStep, MaxIndicatorCameraStep);
                steps.Add(step);
                remaining -= step;
            }

            return steps;
        }

        private static bool IsHealthBar(EnemySeekVisual visual, int imageHeight)
        {
            if (visual.IndicatorBearingDegrees.HasValue)
            {
                return false;
            }

            var scale = imageHeight / 1080d;
            var minimumHeight = Math.Clamp(
                (int)Math.Round(4 * scale, MidpointRounding.AwayFromZero),
                2,
                4);
            var maximumHeight = Math.Max(
                minimumHeight,
                (int)Math.Round(32 * scale, MidpointRounding.AwayFromZero));
            var thickHealthBarThreshold = Math.Max(
                minimumHeight,
                (int)Math.Round(15 * scale, MidpointRounding.AwayFromZero));
            var minimumWidth = visual.Height >= thickHealthBarThreshold
                ? Math.Max(12, (int)Math.Round(18 * scale, MidpointRounding.AwayFromZero))
                : Math.Max(
                    Math.Max(16, (int)Math.Round(24 * scale, MidpointRounding.AwayFromZero)),
                    visual.Height * 3);
            var thickFillRatio = visual.Area / (double)Math.Max(1, visual.Width * visual.Height);
            return visual.Height >= minimumHeight
                   && visual.Height <= maximumHeight
                   && visual.Width >= minimumWidth
                   && (visual.Height < thickHealthBarThreshold || thickFillRatio >= 0.70);
        }

        internal static bool IsDirectionIndicatorGeometry(EnemySeekVisual visual, int imageWidth, int imageHeight)
        {
            // 实拍箭头的红色轮廓约 30x24~36x31。现场误报集中在 15x15、17x17、
            // 18x21 等小红块；短边、长边和面积必须同时达到真实箭头尺度。
            if (visual.Width is > 46 || visual.Height is > 46
                || Math.Min(visual.Width, visual.Height) < 18
                || Math.Max(visual.Width, visual.Height) < 22
                || visual.Area < 160)
            {
                return false;
            }

            var fillRatio = visual.Area / (double)(visual.Width * visual.Height);
            var aspectRatio = visual.Width / (double)visual.Height;
            if (fillRatio is < 0.30 or > 0.78 || aspectRatio is < 0.57 or > 1.75)
            {
                return false;
            }

            if (IsDirectionIndicatorHudNoise(visual, imageWidth, imageHeight))
            {
                return false;
            }

            var horizontalRadius = Math.Max(1d, imageWidth * 0.26);
            var verticalRadius = Math.Max(1d, imageHeight * 0.39);
            var normalizedX = (visual.CenterX - imageWidth / 2d) / horizontalRadius;
            var normalizedY = (visual.CenterY - imageHeight / 2d) / verticalRadius;
            return normalizedX * normalizedX + normalizedY * normalizedY >= 0.60;
        }

        internal static bool IsDirectionIndicatorHudNoise(
            EnemySeekVisual visual,
            int imageWidth,
            int imageHeight)
        {
            var inTopLeftHud = visual.CenterX <= imageWidth * 0.18
                               && visual.CenterY <= imageHeight * 0.38;
            var inTopRightHud = visual.CenterX >= imageWidth * 0.78
                                && visual.CenterY <= imageHeight * 0.28;
            var inFarRightHud = visual.CenterX >= imageWidth * 0.88;
            var inBottomHud = visual.CenterY >= imageHeight * 0.92;
            var inBottomLeftHud = visual.CenterX <= imageWidth * 0.22
                                  && visual.CenterY >= imageHeight * 0.75;
            return inTopLeftHud
                   || inTopRightHud
                   || inFarRightHud
                   || inBottomHud
                   || inBottomLeftHud;
        }

        private static EnemyIndicatorDirection GetIndicatorDirection(
            EnemySeekVisual visual,
            int imageWidth,
            int imageHeight)
        {
            var bearing = GetIndicatorBearingDegrees(visual, imageWidth, imageHeight);
            if (bearing is <= -20 and >= -160)
            {
                return EnemyIndicatorDirection.Left;
            }

            if (bearing is >= 20 and <= 160)
            {
                return EnemyIndicatorDirection.Right;
            }

            return Math.Abs(bearing) > 160
                ? EnemyIndicatorDirection.Behind
                : EnemyIndicatorDirection.Forward;
        }

        internal static bool IsFixedTopEliteHealthBar(
            EnemySeekVisual visual,
            int imageWidth,
            int imageHeight)
        {
            return IsHealthBar(visual, imageHeight)
                   && visual.Width >= 60
                   && visual.CenterY <= imageHeight * 0.18
                   && Math.Abs(visual.CenterX - imageWidth / 2d) <= imageWidth * 0.25;
        }

        internal static (bool tracked, bool advanceCompleted, bool exhausted)
            ObserveFixedTopHealthPresence(EnemySeekVisual? healthBar, DateTime? now = null)
        {
            var nowTicks = (now ?? DateTime.UtcNow).Ticks;
            if (healthBar is { } visibleHealthBar)
            {
                Interlocked.Exchange(ref _fixedTopHealthMissingFrames, 0);
                if (Interlocked.Exchange(ref _fixedTopHealthTracked, 1) == 0)
                {
                    Interlocked.Exchange(ref _fixedTopHealthBaselineWidth, visibleHealthBar.Width);
                    Interlocked.Exchange(ref _fixedTopHealthLowestWidth, visibleHealthBar.Width);
                    Interlocked.Exchange(ref _fixedTopHealthLastProgressTicks, nowTicks);
                }
                else
                {
                    var lowestWidth = Volatile.Read(ref _fixedTopHealthLowestWidth);
                    if (visibleHealthBar.Width <= lowestWidth - FixedTopHealthProgressMinPixels)
                    {
                        Interlocked.Exchange(ref _fixedTopHealthLowestWidth, visibleHealthBar.Width);
                        Interlocked.Exchange(ref _fixedTopHealthLastProgressTicks, nowTicks);
                    }
                }

                var lastProgressTicks = Volatile.Read(ref _fixedTopHealthLastProgressTicks);
                var noProgress = lastProgressTicks > 0
                                 && nowTicks - lastProgressTicks
                                 >= TimeSpan.FromSeconds(FixedTopHealthNoProgressSeconds).Ticks;
                if (noProgress
                    && Volatile.Read(ref _fixedTopHealthAdvanceCompleted) == 1
                    && Volatile.Read(ref _fixedTopHealthAdvanceCount) < FixedTopHealthMaxAdvanceCount)
                {
                    Interlocked.Exchange(ref _fixedTopHealthAdvanceCompleted, 0);
                }
            }
            else if (Volatile.Read(ref _fixedTopHealthTracked) == 1)
            {
                var missingFrames = Interlocked.Increment(ref _fixedTopHealthMissingFrames);
                if (missingFrames >= FixedTopHealthResetMissingFrames)
                {
                    Interlocked.Exchange(ref _fixedTopHealthTracked, 0);
                    Interlocked.Exchange(ref _fixedTopHealthAdvanceCompleted, 0);
                    Interlocked.Exchange(ref _fixedTopHealthMissingFrames, 0);
                    Interlocked.Exchange(ref _fixedTopHealthBaselineWidth, 0);
                    Interlocked.Exchange(ref _fixedTopHealthLowestWidth, 0);
                    Interlocked.Exchange(ref _fixedTopHealthLastProgressTicks, 0);
                    Interlocked.Exchange(ref _fixedTopHealthAdvanceCount, 0);
                }
            }

            var tracked = Volatile.Read(ref _fixedTopHealthTracked) == 1;
            var advanceCompleted = Volatile.Read(ref _fixedTopHealthAdvanceCompleted) == 1;
            var lastProgress = Volatile.Read(ref _fixedTopHealthLastProgressTicks);
            var exhausted = tracked
                            && advanceCompleted
                            && Volatile.Read(ref _fixedTopHealthAdvanceCount) >= FixedTopHealthMaxAdvanceCount
                            && lastProgress > 0
                            && nowTicks - lastProgress
                            >= TimeSpan.FromSeconds(FixedTopHealthNoProgressSeconds).Ticks;
            return (
                tracked,
                advanceCompleted,
                exhausted);
        }

        internal static void MarkFixedTopHealthAdvanceCompleted(DateTime? now = null)
        {
            Interlocked.Exchange(ref _fixedTopHealthAdvanceCompleted, 1);
            Interlocked.Increment(ref _fixedTopHealthAdvanceCount);
            Interlocked.Exchange(
                ref _fixedTopHealthLastProgressTicks,
                (now ?? DateTime.UtcNow).Ticks);
        }

        internal static int GetFixedTopHealthAdvanceCount()
        {
            return Volatile.Read(ref _fixedTopHealthAdvanceCount);
        }

        internal static double GetIndicatorBearingDegrees(
            EnemySeekVisual visual,
            int imageWidth,
            int imageHeight)
        {
            if (visual.IndicatorBearingDegrees is { } templateBearing)
            {
                return NormalizeBearingDegrees(templateBearing);
            }

            return GetIndicatorScreenBearingDegrees(visual, imageWidth, imageHeight);
        }

        internal static double GetIndicatorScreenBearingDegrees(
            EnemySeekVisual visual,
            int imageWidth,
            int imageHeight)
        {
            var deltaX = visual.CenterX - imageWidth / 2d;
            var deltaY = visual.CenterY - imageHeight / 2d;
            return Math.Atan2(deltaX, -deltaY) * 180d / Math.PI;
        }

        internal static bool AreDirectionIndicatorsStable(
            EnemySeekVisual first,
            EnemySeekVisual second,
            int imageWidth,
            int imageHeight)
        {
            var centerDistance = Math.Sqrt(
                Math.Pow(first.CenterX - second.CenterX, 2)
                + Math.Pow(first.CenterY - second.CenterY, 2));
            var firstBearing = GetIndicatorBearingDegrees(first, imageWidth, imageHeight);
            var secondBearing = GetIndicatorBearingDegrees(second, imageWidth, imageHeight);
            var bearingDelta = Math.Abs((firstBearing - secondBearing + 540d) % 360d - 180d);
            var widthRatio = Math.Abs(first.Width - second.Width) / (double)Math.Max(first.Width, second.Width);
            var heightRatio = Math.Abs(first.Height - second.Height) / (double)Math.Max(first.Height, second.Height);
            return centerDistance <= 48
                   && bearingDelta <= 25
                   && widthRatio <= 0.35
                   && heightRatio <= 0.35;
        }

        internal static bool IsIndicatorFeedbackContinuous(
            EnemySeekVisual previous,
            EnemySeekVisual current,
            int cameraHorizontalOffset,
            int imageWidth,
            int imageHeight)
        {
            var previousBearing = GetIndicatorBearingDegrees(
                previous, imageWidth, imageHeight);
            var currentBearing = GetIndicatorBearingDegrees(
                current, imageWidth, imageHeight);
            var expectedBearing = NormalizeBearingDegrees(
                previousBearing
                - cameraHorizontalOffset / (double)HalfTurnMouseOffset * 180d);
            var expectedError = CircularBearingDeltaDegrees(
                expectedBearing,
                currentBearing);
            var widthRatio = Math.Abs(previous.Width - current.Width)
                             / (double)Math.Max(previous.Width, current.Width);
            var heightRatio = Math.Abs(previous.Height - current.Height)
                              / (double)Math.Max(previous.Height, current.Height);
            var stillConverging = Math.Abs(currentBearing)
                                  <= Math.Abs(previousBearing) + 12;
            return expectedError <= 25
                   && widthRatio <= 0.35
                   && heightRatio <= 0.35
                   && stillConverging;
        }

        private static double NormalizeBearingDegrees(double bearing)
        {
            return (bearing + 540d) % 360d - 180d;
        }

        private static double CircularBearingDeltaDegrees(double left, double right)
        {
            return Math.Abs(NormalizeBearingDegrees(left - right));
        }

        internal static int GetNextRotationCount(int currentRotationCount, bool? seekResult)
        {
            return seekResult == null ? currentRotationCount + 1 : 0;
        }

        internal static int GetIndicatorCameraVerticalOffset(EnemySeekVisual visual, int imageHeight)
        {
            if (visual.IndicatorBearingDegrees.HasValue)
            {
                return 0;
            }

            var center = imageHeight / 2;
            var deadZone = Math.Max(60, imageHeight / 6);
            var delta = visual.CenterY - center;
            return Math.Abs(delta) <= deadZone
                ? 0
                : Math.Clamp(delta * 2, -MaxVerticalMouseStep, MaxVerticalMouseStep);
        }

        internal static bool ShouldApproachVisibleEnemy(
            EnemySeekVisual healthBar,
            int imageWidth,
            int imageHeight)
        {
            // 红色填充宽度同时受剩余血量影响，不能作为距离尺；
            // 普通悬浮血条随目标接近而变厚，使用高度判断是否已进入有效战斗距离。
            var closeHeightThreshold = GetVisibleEnemyCloseHeightThreshold(imageHeight);
            return healthBar.Height < closeHeightThreshold;
        }

        internal static int GetVisibleEnemyCloseHeightThreshold(int imageHeight)
        {
            return Math.Clamp(
                (int)Math.Round(
                    imageHeight / 1080d * VisibleEnemyCloseHealthBarHeightAt1080,
                    MidpointRounding.AwayFromZero),
                2,
                16);
        }

        internal static bool IsVisibleHealthTargetConsistent(
            EnemySeekVisual previous,
            EnemySeekVisual current,
            int imageWidth,
            int imageHeight,
            int cameraHorizontalOffset = 0,
            int cameraVerticalOffset = 0)
        {
            if (imageWidth <= 0 || imageHeight <= 0)
            {
                return false;
            }

            var normalizedHorizontalJump = Math.Abs(current.CenterX - previous.CenterX) / (double)imageWidth;
            var normalizedVerticalJump = Math.Abs(current.CenterY - previous.CenterY) / (double)imageHeight;
            var observedHorizontalShift = current.CenterX - previous.CenterX;
            var observedVerticalShift = current.CenterY - previous.CenterY;
            var horizontalJumpIsValid = normalizedHorizontalJump <= 0.22
                                        || IsCameraGuidedShift(
                                            observedHorizontalShift,
                                            cameraHorizontalOffset,
                                            imageWidth);
            var verticalJumpIsValid = normalizedVerticalJump <= 0.22
                                      || IsCameraGuidedShift(
                                          observedVerticalShift,
                                          cameraVerticalOffset,
                                          imageHeight);
            if (!horizontalJumpIsValid || !verticalJumpIsValid)
            {
                return false;
            }

            var previousHorizontalError = Math.Abs(previous.CenterX - imageWidth / 2d) / imageWidth;
            var currentHorizontalError = Math.Abs(current.CenterX - imageWidth / 2d) / imageWidth;
            var previousVerticalError = Math.Abs(previous.CenterY - imageHeight / 2d) / imageHeight;
            var currentVerticalError = Math.Abs(current.CenterY - imageHeight / 2d) / imageHeight;
            var keepsConverging = currentHorizontalError <= previousHorizontalError + 0.03
                                  && currentVerticalError <= previousVerticalError + 0.08;
            var heightRatio = current.Height / (double)Math.Max(1, previous.Height);
            return keepsConverging && heightRatio is >= 0.45 and <= 2.25;
        }

        private static bool IsCameraGuidedShift(
            int observedScreenShift,
            int requestedCameraOffset,
            int imageExtent)
        {
            if (observedScreenShift == 0
                || requestedCameraOffset == 0
                || Math.Sign(observedScreenShift) != -Math.Sign(requestedCameraOffset))
            {
                return false;
            }

            var maximumExpectedShift = Math.Abs(requestedCameraOffset) * 1.5
                                       + imageExtent * 0.05;
            return Math.Abs(observedScreenShift) <= maximumExpectedShift;
        }

        internal static int GetVisibleEnemyCameraOffset(EnemySeekVisual healthBar, int imageWidth)
        {
            var delta = healthBar.CenterX - imageWidth / 2;
            var deadZone = Math.Max(80, imageWidth / 12);
            return Math.Abs(delta) <= deadZone
                ? 0
                : Math.Clamp(delta, -MaxVisibleEnemyCameraStep, MaxVisibleEnemyCameraStep);
        }

        internal static int GetVisibleEnemyCameraVerticalOffset(EnemySeekVisual healthBar, int imageHeight)
        {
            var targetY = imageHeight * 0.35;
            var delta = (int)Math.Round(healthBar.CenterY - targetY);
            return Math.Abs(delta) <= 80
                ? 0
                : Math.Clamp(delta, -160, 160);
        }

        internal static int GetVisibleEnemyApproachDurationMilliseconds(
            EnemySeekVisual healthBar,
            int imageHeight)
        {
            var closeHeightThreshold = GetVisibleEnemyCloseHeightThreshold(imageHeight);
            var distanceRatio = Math.Clamp(
                (closeHeightThreshold - healthBar.Height)
                / (double)Math.Max(1, closeHeightThreshold),
                0,
                1);
            return Math.Clamp(
                (int)Math.Round(250 + distanceRatio * 250),
                250,
                500);
        }

        private static EnemyIndicatorDirection GetVisibleEnemyDirection(
            EnemySeekVisual healthBar,
            int imageWidth)
        {
            var delta = healthBar.CenterX - imageWidth / 2d;
            var deadZone = imageWidth * 0.1;
            if (delta < -deadZone)
            {
                return EnemyIndicatorDirection.Left;
            }

            return delta > deadZone
                ? EnemyIndicatorDirection.Right
                : EnemyIndicatorDirection.Forward;
        }

        internal static Rect GetSeekDetectionRegion(int imageWidth, int imageHeight)
        {
            return new Rect(0, 0, imageWidth, imageHeight);
        }

        internal static bool IsPlayerHudHealthBar(
            EnemySeekVisual visual,
            int imageWidth,
            int imageHeight)
        {
            var inRightPartyHud = visual.CenterX >= imageWidth * 0.82
                                  && visual.CenterY >= imageHeight * 0.12;
            var inBottomPlayerHud = visual.CenterY >= imageHeight * 0.90;
            var inTopLeftHud = visual.CenterX <= imageWidth * 0.22
                               && visual.CenterY <= imageHeight * 0.18;
            var inTopRightHud = visual.CenterX >= imageWidth * 0.70
                                && visual.CenterY <= imageHeight * 0.12;
            var inVeryTopHud = visual.CenterY <= imageHeight * 0.03;
            return inRightPartyHud
                   || inBottomPlayerHud
                   || inTopLeftHud
                   || inTopRightHud
                   || inVeryTopHud;
        }

        internal static bool ShouldContinueLockedRouteSegment(EnemySeekDecision decision, int completedSteps)
        {
            return decision.Action == AutoFightSeekAction.ContinueLockedRoute
                   && completedSteps < LockedRouteForwardSeconds;
        }

        internal static bool ShouldContinueVisibleEnemyApproach(EnemySeekDecision decision, int completedSteps)
        {
            return decision.Action == AutoFightSeekAction.ApproachVisibleEnemy
                   && completedSteps < MaxVisibleEnemyApproachSteps;
        }

        internal static int GetLockedRouteVerticalStep(int completedSteps)
        {
            return LockedRouteVerticalSteps[Math.Max(0, completedSteps) % LockedRouteVerticalSteps.Length];
        }

        internal static Mat CreateSeekColorMask(Mat source, Scalar bloodLower, Scalar? bloodHigher)
        {
            using var legacyMask = bloodHigher.HasValue
                ? OpenCvCommonHelper.Threshold(source, bloodLower, bloodHigher.Value)
                : OpenCvCommonHelper.Threshold(source, bloodLower);
            using var hsv = new Mat();
            using var lowHueRed = new Mat();
            using var highHueRed = new Mat();
            Cv2.CvtColor(source, hsv, ColorConversionCodes.BGR2HSV);
            Cv2.InRange(hsv, new Scalar(0, 100, 140), new Scalar(12, 255, 255), lowHueRed);
            Cv2.InRange(hsv, new Scalar(165, 100, 140), new Scalar(180, 255, 255), highHueRed);

            using var redIndicatorMask = new Mat();
            Cv2.BitwiseOr(lowHueRed, highHueRed, redIndicatorMask);
            var combined = new Mat();
            Cv2.BitwiseOr(legacyMask, redIndicatorMask, combined);
            return combined;
        }

        internal static EnemySeekDecision CaptureSeekDecision(
            Scalar bloodLower,
            Scalar? bloodHigher,
            out int imageWidth,
            out int imageHeight,
            bool indicatorOnly = false)
        {
            using var image = CaptureToRectArea();
            var detectionRegion = GetSeekDetectionRegion(image.Width, image.Height);
            using var imageCrop = image.DeriveCrop(
                detectionRegion.X,
                detectionRegion.Y,
                detectionRegion.Width,
                detectionRegion.Height);
            using var mask = CreateSeekColorMask(imageCrop.SrcMat, bloodLower, bloodHigher);
            using var labels = new Mat();
            using var stats = new Mat();
            using var centroids = new Mat();

            imageWidth = imageCrop.Width;
            imageHeight = imageCrop.Height;
            var numLabels = Cv2.ConnectedComponentsWithStats(
                mask,
                labels,
                stats,
                centroids,
                connectivity: PixelConnectivity.Connectivity4,
                ltype: MatType.CV_32S);

            if (numLabels <= 1)
            {
                if (indicatorOnly)
                {
                    return SelectDirectionIndicatorDecision(
                        Array.Empty<EnemySeekVisual>(),
                        imageCrop.Width,
                        imageCrop.Height,
                        indicatorRouteLocked: false);
                }
                var fixedTopStateWithoutRedMask = ObserveFixedTopHealthPresence(null);
                return VisibleHealthApproach.Evaluate(SelectSeekDecision(
                    Array.Empty<EnemySeekVisual>(),
                    imageCrop.Width,
                    imageCrop.Height,
                    IsIndicatorRouteLocked,
                    fixedTopStateWithoutRedMask.tracked,
                    fixedTopStateWithoutRedMask.advanceCompleted,
                    fixedTopStateWithoutRedMask.exhausted),
                    imageCrop.Width,
                    imageCrop.Height);
            }

            var visuals = new List<EnemySeekVisual>(numLabels - 1);
            for (var i = 1; i < numLabels; i++)
            {
                using var row = stats.Row(i);
                if (row.GetArray(out int[] statsArray) && statsArray.Length >= 5)
                {
                    visuals.Add(new EnemySeekVisual(
                        statsArray[0],
                        statsArray[1],
                        statsArray[2],
                        statsArray[3],
                        statsArray[4]));
                }
            }

            visuals = visuals
                .Select(visual => ClassifySeekVisual(
                    mask,
                    imageCrop.SrcMat,
                    visual,
                    imageCrop.Width,
                    imageCrop.Height))
                .Where(visual => visual.HasValue)
                .Select(visual => visual!.Value)
                .ToList();

            if (indicatorOnly)
            {
                var indicatorDecision = SelectDirectionIndicatorDecision(
                    visuals,
                    imageCrop.Width,
                    imageCrop.Height,
                    indicatorRouteLocked: false);
                SaveSeekSelectionScreenshot(imageCrop.SrcMat, visuals, indicatorDecision);
                return indicatorDecision;
            }

            var fixedTopHealthBar = visuals
                .Where(visual => IsHealthBar(visual, imageCrop.Height))
                .Where(visual => IsFixedTopEliteHealthBar(
                    visual,
                    imageCrop.Width,
                    imageCrop.Height))
                .OrderBy(visual => Math.Abs(visual.CenterX - imageCrop.Width / 2))
                .ThenByDescending(visual => visual.Width)
                .Select(visual => (EnemySeekVisual?)visual)
                .FirstOrDefault();
            var fixedTopState = ObserveFixedTopHealthPresence(fixedTopHealthBar);

            var decision = VisibleHealthApproach.Evaluate(SelectSeekDecision(
                visuals,
                imageCrop.Width,
                imageCrop.Height,
                IsIndicatorRouteLocked,
                fixedTopState.tracked,
                fixedTopState.advanceCompleted,
                fixedTopState.exhausted),
                imageCrop.Width,
                imageCrop.Height);
            SaveSeekSelectionScreenshot(imageCrop.SrcMat, visuals, decision);
            return decision;
        }

        private static void SaveSeekSelectionScreenshot(
            Mat source,
            IReadOnlyCollection<EnemySeekVisual> visuals,
            EnemySeekDecision decision)
        {
            if (decision.Action is not (AutoFightSeekAction.Approach
                or AutoFightSeekAction.ApproachVisibleEnemy
                or AutoFightSeekAction.ApproachFixedTopHealthTarget)
                || decision.Visual is not { } selected)
            {
                return;
            }

            try
            {
                var directory = Global.Absolute(@"log\screenshot\auto-fight-seek");
                Directory.CreateDirectory(directory);
                var sequence = Interlocked.Increment(ref _seekSelectionScreenshotSequence);
                var path = Path.Combine(
                    directory,
                    $"selection-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{sequence:D6}-{decision.Action}.jpg");

                using var annotated = source.Clone();
                var frameCenter = new Point(annotated.Width / 2, annotated.Height / 2);
                Cv2.Line(
                    annotated,
                    new Point(frameCenter.X - 16, frameCenter.Y),
                    new Point(frameCenter.X + 16, frameCenter.Y),
                    new Scalar(255, 255, 0),
                    2);
                Cv2.Line(
                    annotated,
                    new Point(frameCenter.X, frameCenter.Y - 16),
                    new Point(frameCenter.X, frameCenter.Y + 16),
                    new Scalar(255, 255, 0),
                    2);

                foreach (var visual in visuals)
                {
                    var isSelected = visual.Equals(selected);
                    var isHealthBar = IsHealthBar(visual, annotated.Height);
                    var classificationColor = isHealthBar
                        ? new Scalar(255, 128, 0)
                        : new Scalar(255, 0, 255);
                    var label = isHealthBar
                        ? "health"
                        : visual.IndicatorBearingDegrees is { } templateBearing
                            ? $"arrow {templateBearing:F1}"
                            : "arrow";
                    Cv2.Rectangle(
                        annotated,
                        new Rect(visual.X, visual.Y, visual.Width, visual.Height),
                        classificationColor,
                        2);
                    Cv2.PutText(
                        annotated,
                        label,
                        new Point(
                            visual.X,
                            Math.Max(14, visual.Y - 4)),
                        HersheyFonts.HersheySimplex,
                        0.45,
                        classificationColor,
                        1);
                    if (isSelected)
                    {
                        Cv2.Rectangle(
                            annotated,
                            new Rect(visual.X, visual.Y, visual.Width, visual.Height),
                            new Scalar(0, 255, 0),
                            3);
                    }
                }

                Cv2.Line(
                    annotated,
                    frameCenter,
                    new Point(selected.CenterX, selected.CenterY),
                    new Scalar(0, 255, 0),
                    2);
                Cv2.PutText(
                    annotated,
                    $"{decision.Action} bearing={GetIndicatorBearingDegrees(selected, annotated.Width, annotated.Height):F1}",
                    new Point(16, 36),
                    HersheyFonts.HersheySimplex,
                    0.8,
                    new Scalar(0, 255, 0),
                    2);

                if (Cv2.ImWrite(path, annotated))
                {
                    Logger.LogDebug("寻敌执行选择前截图: {Path}", path);
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "保存寻敌执行选择前截图失败");
            }
        }

        internal static EnemySeekVisual? ClassifySeekVisual(
            Mat mask,
            EnemySeekVisual visual,
            int imageWidth,
            int imageHeight)
        {
            return ClassifySeekVisual(
                mask,
                source: null,
                visual,
                imageWidth,
                imageHeight);
        }

        internal static EnemySeekVisual? ClassifySeekVisual(
            Mat mask,
            Mat? source,
            EnemySeekVisual visual,
            int imageWidth,
            int imageHeight)
        {
            if (IsHealthBar(visual, imageHeight))
            {
                if (!IsPlayerHudHealthBar(visual, imageWidth, imageHeight)
                    && MatchesHealthBarFeature(mask, source, visual))
                {
                    return visual;
                }
            }

            if (!IsDirectionIndicatorGeometry(visual, imageWidth, imageHeight))
            {
                return null;
            }

            var templates = DirectionIndicatorTemplates.Value;
            if (templates.Count == 0)
            {
                return null;
            }

            if (source != null
                && !HasDirectionIndicatorPinkRedShare(source, visual))
            {
                return null;
            }

            using var candidate = new Mat(mask, new Rect(
                visual.X,
                visual.Y,
                visual.Width,
                visual.Height)).Clone();
            if (!MatchesDirectionIndicatorFeature(
                    candidate,
                    templates.Select(template => template.Contour).ToArray(),
                    DirectionIndicatorFeatureThreshold))
            {
                return null;
            }

            var bearingMatch = MatchDirectionIndicatorTemplateBearing(
                candidate,
                templates);
            if (bearingMatch.HasValue
                && CircularBearingDeltaDegrees(
                    bearingMatch.Value.bearing,
                    GetIndicatorScreenBearingDegrees(visual, imageWidth, imageHeight))
                > DirectionIndicatorMaxScreenBearingDelta)
            {
                return null;
            }

            return bearingMatch.HasValue
                ? visual with { IndicatorBearingDegrees = bearingMatch.Value.bearing }
                : null;
        }

        internal static bool MatchesHealthBarFeature(
            Mat binaryMask,
            EnemySeekVisual visual)
        {
            return MatchesHealthBarFeature(binaryMask, source: null, visual);
        }

        internal static bool MatchesHealthBarFeature(
            Mat binaryMask,
            Mat? source,
            EnemySeekVisual visual)
        {
            var rect = new Rect(visual.X, visual.Y, visual.Width, visual.Height);
            if (rect.X < 0 || rect.Y < 0
                || rect.Right > binaryMask.Width
                || rect.Bottom > binaryMask.Height)
            {
                return false;
            }

            using var candidate = new Mat(binaryMask, rect);
            var longestRun = 0;
            for (var y = 0; y < candidate.Height; y++)
            {
                var currentRun = 0;
                for (var x = 0; x < candidate.Width; x++)
                {
                    if (candidate.At<byte>(y, x) != 0)
                    {
                        currentRun++;
                        longestRun = Math.Max(longestRun, currentRun);
                    }
                    else
                    {
                        currentRun = 0;
                    }
                }
            }

            var foregroundPixels = Cv2.CountNonZero(candidate);
            var fillRatio = foregroundPixels / (double)(candidate.Width * candidate.Height);
            if (longestRun < Math.Ceiling(candidate.Width * 0.60)
                || fillRatio < 0.70)
            {
                return false;
            }

            if (source == null)
            {
                return true;
            }

            if (rect.Right > source.Width || rect.Bottom > source.Height)
            {
                return false;
            }

            using var colorCandidate = new Mat(source, rect);
            using var salmonMask = new Mat();
            Cv2.InRange(
                colorCandidate,
                new Scalar(50, 50, 180),
                new Scalar(150, 150, 255),
                salmonMask);
            var channels = Cv2.Split(colorCandidate);
            try
            {
                using var blueGreenDifference = new Mat();
                using var similarBlueGreen = new Mat();
                Cv2.Absdiff(channels[0], channels[1], blueGreenDifference);
                Cv2.Threshold(
                    blueGreenDifference,
                    similarBlueGreen,
                    45,
                    255,
                    ThresholdTypes.BinaryInv);
                Cv2.BitwiseAnd(salmonMask, similarBlueGreen, salmonMask);
                Cv2.BitwiseAnd(salmonMask, candidate, salmonMask);
            }
            finally
            {
                foreach (var channel in channels)
                {
                    channel.Dispose();
                }
            }

            return Cv2.CountNonZero(salmonMask) / (double)foregroundPixels >= 0.40;
        }

        internal static bool HasDirectionIndicatorPinkRedShare(
            Mat source,
            EnemySeekVisual visual)
        {
            var rect = new Rect(visual.X, visual.Y, visual.Width, visual.Height);
            if (rect.X < 0 || rect.Y < 0
                || rect.Right > source.Width
                || rect.Bottom > source.Height)
            {
                return false;
            }

            using var candidate = new Mat(source, rect);
            using var hsv = new Mat();
            using var lowHueRed = new Mat();
            using var highHueRed = new Mat();
            using var allRed = new Mat();
            Cv2.CvtColor(candidate, hsv, ColorConversionCodes.BGR2HSV);
            Cv2.InRange(hsv, new Scalar(0, 100, 140), new Scalar(12, 255, 255), lowHueRed);
            Cv2.InRange(hsv, new Scalar(165, 100, 140), new Scalar(180, 255, 255), highHueRed);
            Cv2.BitwiseOr(lowHueRed, highHueRed, allRed);
            var redPixels = Cv2.CountNonZero(allRed);
            if (redPixels == 0)
            {
                return false;
            }

            var pinkRedShare = Cv2.CountNonZero(highHueRed) / (double)redPixels;
            return pinkRedShare >= DirectionIndicatorMinPinkRedShare;
        }

        internal static bool MatchesDirectionIndicatorFeature(
            Mat candidateMask,
            IReadOnlyCollection<Point[]> templateContours,
            double threshold)
        {
            var candidateContour = GetLargestExternalContour(candidateMask);
            if (candidateContour == null
                || candidateContour.Length < 3
                || !HasDirectionIndicatorConcavity(candidateContour))
            {
                return false;
            }

            var closestTemplateDistance = templateContours
                .Where(template => template.Length >= 3)
                .Select(template => Cv2.MatchShapes(
                    candidateContour,
                    template,
                    ShapeMatchModes.I1,
                    0))
                .DefaultIfEmpty(double.MaxValue)
                .Min();
            return closestTemplateDistance <= threshold;
        }

        internal static bool HasDirectionIndicatorConcavity(Point[] contour)
        {
            if (contour.Length < 3)
            {
                return false;
            }

            var contourArea = Math.Abs(Cv2.ContourArea(contour));
            var hull = Cv2.ConvexHull(contour);
            var hullArea = Math.Abs(Cv2.ContourArea(hull));
            if (contourArea <= 0 || hullArea <= 0)
            {
                return false;
            }

            var solidity = contourArea / hullArea;
            return solidity is >= DirectionIndicatorMinSolidity and <= DirectionIndicatorMaxSolidity;
        }

        internal static double? GetDirectionIndicatorHollowRatio(Mat binaryMask)
        {
            using var working = binaryMask.Clone();
            Cv2.FindContours(
                working,
                out Point[][] contours,
                out HierarchyIndex[] hierarchy,
                RetrievalModes.Tree,
                ContourApproximationModes.ApproxSimple);
            if (contours.Length == 0 || hierarchy.Length != contours.Length)
            {
                return null;
            }

            var externalIndex = Enumerable.Range(0, contours.Length)
                .Where(index => hierarchy[index].Parent < 0)
                .OrderByDescending(index => Math.Abs(Cv2.ContourArea(contours[index])))
                .FirstOrDefault(-1);
            if (externalIndex < 0)
            {
                return null;
            }

            var outerArea = Math.Abs(Cv2.ContourArea(contours[externalIndex]));
            if (outerArea <= 0)
            {
                return null;
            }

            var hollowArea = Enumerable.Range(0, contours.Length)
                .Where(index => hierarchy[index].Parent == externalIndex)
                .Sum(index => Math.Abs(Cv2.ContourArea(contours[index])));
            return hollowArea >= 1.5 ? hollowArea / outerArea : null;
        }

        internal static Point[]? GetLargestExternalContour(Mat binaryMask)
        {
            using var working = binaryMask.Clone();
            Cv2.FindContours(
                working,
                out Point[][] contours,
                out _,
                RetrievalModes.External,
                ContourApproximationModes.ApproxSimple);
            return contours
                .OrderByDescending(contour => Cv2.ContourArea(contour))
                .FirstOrDefault();
        }

        internal static (double bearing, double score)? MatchDirectionIndicatorTemplateBearing(
            Mat candidateMask)
        {
            return MatchDirectionIndicatorTemplateBearing(
                candidateMask,
                DirectionIndicatorTemplates.Value);
        }

        private static (double bearing, double score)? MatchDirectionIndicatorTemplateBearing(
            Mat candidateMask,
            IReadOnlyCollection<DirectionIndicatorTemplateFeature> templates)
        {
            var bestBearing = 0d;
            var bestScore = double.NegativeInfinity;
            foreach (var template in templates)
            {
                using var resized = new Mat();
                using var score = new Mat();
                Cv2.Resize(
                    candidateMask,
                    resized,
                    template.Mask.Size(),
                    interpolation: InterpolationFlags.Nearest);
                Cv2.MatchTemplate(
                    resized,
                    template.Mask,
                    score,
                    TemplateMatchModes.CCoeffNormed);
                var currentScore = score.At<float>(0, 0);
                if (currentScore > bestScore)
                {
                    bestScore = currentScore;
                    bestBearing = template.BearingDegrees;
                }
            }

            return bestScore >= DirectionIndicatorOrientationMinScore
                ? (NormalizeBearingDegrees(bestBearing), bestScore)
                : null;
        }

        internal static int GetDirectionIndicatorTemplateCount()
        {
            return DirectionIndicatorTemplates.Value.Count;
        }

        internal static IReadOnlyList<Point[]> GetDirectionIndicatorTemplateContours()
        {
            return DirectionIndicatorTemplates.Value
                .Select(template => template.Contour)
                .ToArray();
        }

        private static IReadOnlyList<DirectionIndicatorTemplateFeature> LoadDirectionIndicatorTemplates()
        {
            var result = new List<DirectionIndicatorTemplateFeature>();
            foreach (var spec in DirectionIndicatorTemplateSpecs)
            {
                try
                {
                    using var template = GameTaskManager.LoadAssetImage(
                        "AutoFight",
                        spec.FileName,
                        1920,
                        1080,
                        ImreadModes.Unchanged);
                    using var mask = new Mat();
                    if (template.Channels() == 4)
                    {
                        Cv2.ExtractChannel(template, mask, 3);
                    }
                    else
                    {
                        using var colorMask = CreateSeekColorMask(
                            template,
                            new Scalar(255, 90, 90),
                            null);
                        colorMask.CopyTo(mask);
                    }

                    var contour = GetLargestExternalContour(mask);
                    if (contour is { Length: >= 3 })
                    {
                        result.Add(new DirectionIndicatorTemplateFeature(
                            contour,
                            mask.Clone(),
                            spec.BearingDegrees));
                    }
                }
                catch
                {
                    // 单个样本损坏不应让整套寻敌失效；其余实拍方位模板继续提供匹配。
                }
            }

            return result.AsReadOnly();
        }

        private static async Task<bool> HandleDetectedEnemyAsync(
            EnemySeekDecision decision,
            int imageWidth,
            int imageHeight,
            Scalar bloodLower,
            Scalar? bloodHigher,
            ILogger logger,
            CancellationToken ct)
        {
            if (decision.Action == AutoFightSeekAction.Scan)
            {
                return false;
            }

            if (decision.Action == AutoFightSeekAction.Approach)
            {
                var confirmation = await ConfirmDirectionIndicatorAsync(
                    decision,
                    imageWidth,
                    imageHeight,
                    bloodLower,
                    bloodHigher,
                    logger,
                    ct);
                if (!confirmation.HasValue)
                {
                    return false;
                }
                decision = confirmation.Value.decision;
                imageWidth = confirmation.Value.imageWidth;
                imageHeight = confirmation.Value.imageHeight;
            }

            if (decision.Action == AutoFightSeekAction.KeepFighting)
            {
                UnlockIndicatorRoute();
                return true;
            }

            if (decision.Action == AutoFightSeekAction.ApproachFixedTopHealthTarget)
            {
                LockIndicatorRoute();
                return await HandleFixedTopHealthApproachAsync(
                    decision,
                    imageWidth,
                    imageHeight,
                    bloodLower,
                    bloodHigher,
                    logger,
                    ct);
            }

            if (decision.Action == AutoFightSeekAction.ApproachVisibleEnemy)
            {
                UnlockIndicatorRoute();
                return await HandleVisibleEnemyApproachAsync(
                    decision,
                    imageWidth,
                    imageHeight,
                    bloodLower,
                    bloodHigher,
                    logger,
                    ct);
            }

            var currentDecision = decision;
            var currentImageWidth = imageWidth;
            var currentImageHeight = imageHeight;
            if (decision.Action == AutoFightSeekAction.Approach)
            {
                logger.LogInformation(
                    "首次箭头目标已锁定：候选 {SignalCount} 个，初始屏幕方位角 {Bearing:F1}°；采用逐步转向并在每步后重新截图校正",
                    decision.SignalCount,
                    decision.Visual is { } visual
                        ? GetIndicatorBearingDegrees(visual, imageWidth, imageHeight)
                        : 0d);
                var feedbackResult = await TurnTowardIndicatorWithFeedbackAsync(
                    decision,
                    imageWidth,
                    imageHeight,
                    bloodLower,
                    bloodHigher,
                    logger,
                    ct);
                currentDecision = feedbackResult.decision;
                currentImageWidth = feedbackResult.imageWidth;
                currentImageHeight = feedbackResult.imageHeight;
                if (currentDecision.Action == AutoFightSeekAction.ContinueLockedRoute)
                {
                    LockIndicatorRoute();
                }
                else
                {
                    UnlockIndicatorRoute();
                }
            }

            if (currentDecision.Action == AutoFightSeekAction.KeepFighting)
            {
                UnlockIndicatorRoute();
                logger.LogInformation("完成分段转身后血条已进入近距离战斗视野，取消 6 秒前进计划");
                return true;
            }

            if (currentDecision.Action == AutoFightSeekAction.ApproachFixedTopHealthTarget)
            {
                logger.LogInformation(
                    "完成箭头分段转身后检测到顶部固定精英血条；沿当前方向再前进 1 秒确定输出点");
                return await HandleFixedTopHealthApproachAsync(
                    currentDecision,
                    currentImageWidth,
                    currentImageHeight,
                    bloodLower,
                    bloodHigher,
                    logger,
                    ct);
            }

            if (currentDecision.Action == AutoFightSeekAction.ApproachVisibleEnemy)
            {
                UnlockIndicatorRoute();
                logger.LogInformation("完成分段转身后立即检测到血条，血条优先并取消 6 秒前进计划");
                return await HandleVisibleEnemyApproachAsync(
                    currentDecision,
                    currentImageWidth,
                    currentImageHeight,
                    bloodLower,
                    bloodHigher,
                    logger,
                    ct);
            }

            if (currentDecision.Action == AutoFightSeekAction.Scan)
            {
                UnlockIndicatorRoute();
                logger.LogInformation(
                    "箭头反馈转向未形成可靠锁定，本轮返回扫描且不执行盲目前进");
                return false;
            }

            var completedLockedSteps = 0;
            while (ShouldContinueLockedRouteSegment(currentDecision, completedLockedSteps))
            {
                var lockedRouteVerticalOffset = GetLockedRouteVerticalStep(completedLockedSteps);
                VisibleHealthApproach.RecordCameraMovement(0, lockedRouteVerticalOffset);
                await MoveForwardTask.AdvanceLockedRouteAsync(
                    lockedRouteVerticalOffset,
                    completedLockedSteps,
                    currentDecision.SignalCount,
                    logger,
                    ct);
                completedLockedSteps++;

                await Task.Delay(IndicatorReacquireDelayMilliseconds, ct);
                currentDecision = CaptureSeekDecision(
                    bloodLower,
                    bloodHigher,
                    out currentImageWidth,
                    out currentImageHeight);

                if (currentDecision.Action == AutoFightSeekAction.KeepFighting)
                {
                    UnlockIndicatorRoute();
                    logger.LogInformation(
                        "锁定路线前进 {Steps} 秒后血条进入近距离战斗视野，提前结束路线计划",
                        completedLockedSteps);
                    return true;
                }

                if (currentDecision.Action == AutoFightSeekAction.ApproachVisibleEnemy)
                {
                    UnlockIndicatorRoute();
                    logger.LogInformation(
                        "锁定路线前进 {Steps} 秒后检测到血条，血条优先并提前切换到接近逻辑",
                        completedLockedSteps);
                    return await HandleVisibleEnemyApproachAsync(
                        currentDecision,
                        currentImageWidth,
                        currentImageHeight,
                        bloodLower,
                        bloodHigher,
                        logger,
                        ct);
                }

                if (currentDecision.Action == AutoFightSeekAction.ApproachFixedTopHealthTarget)
                {
                    logger.LogInformation(
                        "锁定路线前进 {Steps} 秒后检测到顶部固定精英血条；沿当前方向再前进 1 秒确定输出点",
                        completedLockedSteps);
                    return await HandleFixedTopHealthApproachAsync(
                        currentDecision,
                        currentImageWidth,
                        currentImageHeight,
                        bloodLower,
                        bloodHigher,
                        logger,
                        ct);
                }
            }

            UnlockIndicatorRoute();
            logger.LogInformation(
                "锁定路线已完成 6 秒前进且仍无血条；解除目标锁定，下一轮将重新选择绝对转角最小的箭头（本帧记录 {SignalCount} 个）",
                currentDecision.SignalCount);
            return true;
        }

        private static async Task<(EnemySeekDecision decision, int imageWidth, int imageHeight)>
            TurnTowardIndicatorWithFeedbackAsync(
                EnemySeekDecision initialDecision,
                int imageWidth,
                int imageHeight,
                Scalar bloodLower,
                Scalar? bloodHigher,
                ILogger logger,
                CancellationToken ct)
        {
            var currentDecision = initialDecision;
            var currentImageWidth = imageWidth;
            var currentImageHeight = imageHeight;
            var indicatorAligned = false;

            for (var step = 0;
                 step < MaxIndicatorTurnFeedbackSteps
                 && currentDecision.Action == AutoFightSeekAction.Approach;
                 step++)
            {
                if (currentDecision.Visual is not { } visual)
                {
                    break;
                }

                var bearing = GetIndicatorBearingDegrees(
                    visual,
                    currentImageWidth,
                    currentImageHeight);
                if (Math.Abs(bearing) < 8)
                {
                    logger.LogInformation(
                        "箭头逐步转向已进入正前方死区：{Bearing:F1}°，停止继续横向转镜",
                        bearing);
                    indicatorAligned = true;
                    break;
                }

                var previousVisual = visual;
                var movement = await MoveForwardTask.TurnTowardIndicatorAsync(
                    currentDecision,
                    currentImageWidth,
                    currentImageHeight,
                    logger,
                    ct);
                VisibleHealthApproach.RecordCameraMovement(
                    movement.horizontal,
                    movement.vertical);

                await Task.Delay(IndicatorReacquireDelayMilliseconds, ct);
                var observedDecision = CaptureSeekDecision(
                    bloodLower,
                    bloodHigher,
                    out var observedImageWidth,
                    out var observedImageHeight);
                currentDecision = observedDecision;
                currentImageWidth = observedImageWidth;
                currentImageHeight = observedImageHeight;
                if (observedDecision.Action != AutoFightSeekAction.Approach)
                {
                    break;
                }

                var confirmation = await ConfirmDirectionIndicatorAsync(
                    observedDecision,
                    observedImageWidth,
                    observedImageHeight,
                    bloodLower,
                    bloodHigher,
                    logger,
                    ct);
                if (!confirmation.HasValue)
                {
                    logger.LogWarning(
                        "箭头逐步转向后复核失败，解除目标并回到扫描，不执行锁定路线前进");
                    currentDecision = new EnemySeekDecision(
                        AutoFightSeekAction.Scan,
                        EnemyIndicatorDirection.None);
                    break;
                }

                if (confirmation.Value.decision.Visual is not { } confirmedVisual
                    || !IsIndicatorFeedbackContinuous(
                        previousVisual,
                        confirmedVisual,
                        movement.horizontal,
                        currentImageWidth,
                        currentImageHeight))
                {
                    logger.LogWarning(
                        "箭头逐步转向检测到目标不连续，解除目标并回到扫描，避免切换到其他箭头");
                    currentDecision = new EnemySeekDecision(
                        AutoFightSeekAction.Scan,
                        EnemyIndicatorDirection.None);
                    break;
                }

                currentDecision = confirmation.Value.decision;
                currentImageWidth = confirmation.Value.imageWidth;
                currentImageHeight = confirmation.Value.imageHeight;
            }

            if (indicatorAligned)
            {
                currentDecision = new EnemySeekDecision(
                    AutoFightSeekAction.ContinueLockedRoute,
                    EnemyIndicatorDirection.None,
                    null,
                    currentDecision.SignalCount);
            }
            else if (currentDecision.Action == AutoFightSeekAction.Approach)
            {
                logger.LogWarning(
                    "箭头逐步转向达到 {Steps} 步上限仍未对准，解除目标并回到扫描",
                    MaxIndicatorTurnFeedbackSteps);
                currentDecision = new EnemySeekDecision(
                    AutoFightSeekAction.Scan,
                    EnemyIndicatorDirection.None);
            }

            return (currentDecision, currentImageWidth, currentImageHeight);
        }

        private static async Task<(EnemySeekDecision decision, int imageWidth, int imageHeight)?>
            ConfirmDirectionIndicatorAsync(
                EnemySeekDecision initialDecision,
                int imageWidth,
                int imageHeight,
                Scalar bloodLower,
                Scalar? bloodHigher,
                ILogger logger,
                CancellationToken ct)
        {
            if (initialDecision.Visual is not { } initialVisual)
            {
                return null;
            }

            await Task.Delay(120, ct);
            var confirmedDecision = CaptureSeekDecision(
                bloodLower,
                bloodHigher,
                out var confirmedImageWidth,
                out var confirmedImageHeight,
                indicatorOnly: true);
            if (confirmedDecision.Action != AutoFightSeekAction.Approach
                || confirmedDecision.Visual is not { } confirmedVisual
                || !AreDirectionIndicatorsStable(
                    initialVisual,
                    confirmedVisual,
                    imageWidth,
                    imageHeight))
            {
                logger.LogDebug(
                    "箭头候选未通过 120ms 静止复核，按 HUD 鬼影丢弃：位置=({X},{Y})，尺寸={Width}x{Height}",
                    initialVisual.X,
                    initialVisual.Y,
                    initialVisual.Width,
                    initialVisual.Height);
                return null;
            }

            return (confirmedDecision, confirmedImageWidth, confirmedImageHeight);
        }

        private static async Task<bool> HandleVisibleEnemyApproachAsync(
            EnemySeekDecision decision,
            int imageWidth,
            int imageHeight,
            Scalar bloodLower,
            Scalar? bloodHigher,
            ILogger logger,
            CancellationToken ct)
        {
            if (decision.Visual is not { } initialVisual)
            {
                return false;
            }

            await Task.Delay(IndicatorReacquireDelayMilliseconds, ct);
            var currentDecision = CaptureSeekDecision(
                bloodLower,
                bloodHigher,
                out var currentImageWidth,
                out var currentImageHeight);
            if (currentDecision.Action == AutoFightSeekAction.KeepFighting)
            {
                return true;
            }

            if (currentDecision.Action != AutoFightSeekAction.ApproachVisibleEnemy
                || currentDecision.Visual is not { } confirmedVisual
                || !IsVisibleHealthTargetConsistent(
                    initialVisual,
                    confirmedVisual,
                    imageWidth,
                    imageHeight,
                    cameraHorizontalOffset: 0,
                    cameraVerticalOffset: 0))
            {
                logger.LogDebug(
                    "血条候选未通过 {Delay}ms 静止复核，按瞬时特效或场景横线丢弃：位置=({X},{Y})，尺寸={Width}x{Height}",
                    IndicatorReacquireDelayMilliseconds,
                    initialVisual.X,
                    initialVisual.Y,
                    initialVisual.Width,
                    initialVisual.Height);
                return false;
            }

            var completedSteps = 0;
            while (ShouldContinueVisibleEnemyApproach(currentDecision, completedSteps))
            {
                var previousDecision = currentDecision;
                var horizontalCameraOffset = previousDecision.Visual is { } guidedVisual
                    ? GetVisibleEnemyCameraOffset(guidedVisual, currentImageWidth)
                    : 0;
                var verticalCameraOffset = previousDecision.Visual is { } guidedVerticalVisual
                    ? GetVisibleEnemyCameraVerticalOffset(guidedVerticalVisual, currentImageHeight)
                    : 0;
                await MoveForwardTask.ApproachVisibleEnemyAsync(
                    currentDecision,
                    currentImageWidth,
                    currentImageHeight,
                    logger,
                    ct);
                VisibleHealthApproach.RecordApproachStep(
                    horizontalCameraOffset,
                    verticalCameraOffset);
                completedSteps++;
                await Task.Delay(IndicatorReacquireDelayMilliseconds, ct);
                currentDecision = CaptureSeekDecision(
                    bloodLower,
                    bloodHigher,
                    out currentImageWidth,
                    out currentImageHeight);

                if (currentDecision.Action == AutoFightSeekAction.ApproachVisibleEnemy
                    && previousDecision.Visual is { } previousVisual
                    && currentDecision.Visual is { } currentVisual
                    && !IsVisibleHealthTargetConsistent(
                        previousVisual,
                        currentVisual,
                        currentImageWidth,
                        currentImageHeight,
                        horizontalCameraOffset,
                        verticalCameraOffset))
                {
                    logger.LogWarning(
                        "血条候选跨帧跳变，停止追踪旧目标但保留当前已检测到敌人的结果：上一目标=({PreviousX},{PreviousY},{PreviousWidth}x{PreviousHeight})，当前=({CurrentX},{CurrentY},{CurrentWidth}x{CurrentHeight})",
                        previousVisual.X,
                        previousVisual.Y,
                        previousVisual.Width,
                        previousVisual.Height,
                        currentVisual.X,
                        currentVisual.Y,
                        currentVisual.Width,
                        currentVisual.Height);
                    return true;
                }

                if (currentDecision.Action == AutoFightSeekAction.KeepFighting)
                {
                    logger.LogInformation("血条接近 {Steps} 秒后敌人已进入近距离战斗视野", completedSteps);
                    return true;
                }
            }

            logger.LogInformation(
                "血条优先接近已执行 {Steps} 秒但未确认进入近距离；恢复战斗结束检查",
                completedSteps);
            return false;
        }

        private static async Task<bool> HandleFixedTopHealthApproachAsync(
            EnemySeekDecision decision,
            int imageWidth,
            int imageHeight,
            Scalar bloodLower,
            Scalar? bloodHigher,
            ILogger logger,
            CancellationToken ct)
        {
            _ = decision;
            _ = imageWidth;
            _ = imageHeight;
            _ = bloodLower;
            _ = bloodHigher;
            await MoveForwardTask.AdvanceFixedTopHealthOnceAsync(logger, ct);
            MarkFixedTopHealthAdvanceCompleted();
            UnlockIndicatorRoute();
            return true;
        }

        internal static (int x, int y) GetSeekCameraOffset(int imageWidth, int imageHeight, int rotationCount, int retryCount)
        {
            var horizontalStep = Math.Max(80, imageWidth / 6);
            var currentVerticalTarget = GetSeekCameraVerticalTargetOffset(imageHeight, rotationCount, retryCount);
            var nextVerticalTarget = GetSeekCameraVerticalTargetOffset(imageHeight, rotationCount, retryCount + 1);
            return (horizontalStep, nextVerticalTarget - currentVerticalTarget);
        }

        internal static int GetSeekCameraVerticalTargetOffset(int imageHeight, int rotationCount, int retryCount)
        {
            var safeRotationCount = Math.Max(0, rotationCount);
            var trackCenter = VerticalSeekTrackCenters[safeRotationCount % VerticalSeekTrackCenters.Length];
            var phaseOffset = safeRotationCount / VerticalSeekTrackCenters.Length * 2;
            var waveBand = VerticalSeekWave[(Math.Max(0, retryCount) + phaseOffset) % VerticalSeekWave.Length];
            var verticalBand = trackCenter + waveBand;
            return Math.Clamp(
                imageHeight * verticalBand * VerticalSeekScaleNumerator / VerticalSeekScaleDenominator,
                -VerticalSeekMaxTargetOffset,
                VerticalSeekMaxTargetOffset);
        }

        private static async Task MoveSeekCameraAsync((int x, int y) offset, CancellationToken ct)
        {
            await MoveSeekCameraVerticallyAsync(offset.y, ct);

            VisibleHealthApproach.RecordCameraMovement(offset.x, 0);
            Simulation.SendInput.Mouse.MoveMouseBy(offset.x, 0);
        }

        private static async Task MoveSeekCameraVerticallyAsync(int offsetY, CancellationToken ct)
        {
            VisibleHealthApproach.RecordCameraMovement(0, offsetY);
            var remaining = offsetY;
            while (remaining != 0)
            {
                var step = Math.Clamp(remaining, -MaxVerticalMouseStep, MaxVerticalMouseStep);
                Simulation.SendInput.Mouse.MoveMouseBy(0, step);
                remaining -= step;
                await Task.Delay(45, ct);
            }
        }

        private static async Task ReturnSeekCameraPitchToCenterAsync(ILogger logger, int currentVerticalTarget, CancellationToken ct)
        {
            if (currentVerticalTarget == 0)
            {
                return;
            }

            logger.LogDebug("寻敌结束回正视角俯仰: y={Y}", -currentVerticalTarget);
            await MoveSeekCameraVerticallyAsync(-currentVerticalTarget, ct);
        }

        private static async Task ResetSeekCameraPitchAsync(ILogger logger, CancellationToken ct)
        {
            logger.LogDebug("寻敌重置视角俯仰");
            VisibleHealthApproach.PreserveBudgetAcrossUnknownCameraMovement();
            Simulation.SendInput.Mouse.MiddleButtonClick();
            await Task.Delay(500, ct);
        }

        internal static bool ShouldRecenterCameraBeforeSeek()
        {
            return true;
        }

        internal static bool ShouldResetCameraBeforeSeek(int rotationCount, int retryCount)
        {
            return retryCount == 0 && rotationCount > 0 && rotationCount % 3 == 0;
        }
        
        private static bool IsYellow(int r, int g, int b)
        {
            //Logger.LogInformation($"IsYellow({r},{g},{b})");
            // 黄色范围：R高，G高，B低
            return (r >= 200 && r <= 255) &&
                   (g >= 200 && g <= 255) &&
                   (b >= 0 && b <= 100);
        }

        private static bool IsWhite(int r, int g, int b)
        {
            //Logger.LogInformation($"IsWhite({r},{g},{b})");
            // 白色范围：R高，G高，B低
            return (r >= 240 && r <= 255) &&
                   (g >= 240 && g <= 255) &&
                   (b >= 240 && b <= 255);
        }
    }

    public class AutoFightSkill
    {
        public static async Task<bool> EnsureGuardianSkill(Avatar guardianAvatar, CombatCommand command, string lastFightName,
            string guardianAvatarName, bool guardianAvatarHold, int retryCount, CancellationToken ct,bool guardianCombatSkip = false,
            bool burstEnabled = false)
        {
            int attempt = 0;
            var maxAttempts = GuardianSkillSwitchPolicy.NormalizeAttemptCount(retryCount);

            if (guardianAvatar.IsSkillReady())
            {
                while (attempt < maxAttempts)
                {
                    if (await TryUseGuardianSkillOnceAsync(
                            guardianAvatar,
                            guardianAvatarHold,
                            guardianAvatarName,
                            ct))
                    {
                        Logger.LogInformation(
                            "优先第 {Position} 盾奶位 {GuardianAvatar} 已确认切换成功且战技进入冷却",
                            guardianAvatarName,
                            guardianAvatar.Name);
                        return true;
                    }

                    Logger.LogWarning(
                        "优先第 {Position} 盾奶位 {GuardianAvatar} 未确认战技释放，第 {Attempt}/{MaxAttempts} 次失败",
                        guardianAvatarName,
                        guardianAvatar.Name,
                        attempt + 1,
                        maxAttempts);
                    Simulation.ReleaseAllKey();
                    Simulation.SendInput.SimulateAction(GIActions.NormalAttack);
                    Simulation.SendInput.SimulateAction(GIActions.Drop);
                    await Task.Delay(250, ct);
                    attempt++;
                }
            }
            else if (burstEnabled)
            {
                using var image = CaptureToRectArea();
                if (!guardianAvatar.IsActive(image))
                {
                    var skillArea = AutoFightAssets.Get(image).AvatarQRectListMap[guardianAvatar.Index - 1];//Q技能区域
                    // 首先对图像进行预处理，转为灰度图
                    using var grayImage = image.DeriveCrop(skillArea).SrcMat.CvtColor(ColorConversionCodes.BGR2GRAY);
                
                    //调试用
                    // grayImage.SaveImage("D:\\Images\\grayImage.png");
                    // Cv2.ImShow("灰度图像", grayImage);
                    
                    // 计算图像的平均亮度
                    var meanBrightness = Cv2.Mean(grayImage);
                    var avgBrightness = meanBrightness.Val0;
                    // 根据平均亮度动态调整Canny边缘检测的阈值
                    var threshold1 = avgBrightness * 0.9;
                    var threshold2 = avgBrightness * 2;
                
                    // Logger.LogInformation("角色{i} 平均亮度 {avgBrightness}", i, avgBrightness);
                
                    Cv2.Canny(grayImage, grayImage, threshold1: (float)threshold1, threshold2: (float)threshold2); // 边缘检测
                    
                    // 使用霍夫变换检测圆形
                    var circles = Cv2.HoughCircles(grayImage, HoughModes.Gradient, dp: 1.2, minDist: 20,
                        param1: 70, param2: 30, minRadius: 25, maxRadius: 34);

                    if (circles.Length > 0)
                    {
                        Logger.LogInformation("优先第 {text} 盾奶位 {GuardianAvatar} 元素爆发状态：{attempt}，尝试释放",
                            guardianAvatarName, guardianAvatar.Name, "就绪");
                        
                        if (guardianAvatar.TrySwitch(8))
                        {
                            Simulation.SendInput.SimulateAction(GIActions.ElementalBurst);
                            Sleep(500, ct);
                            Simulation.ReleaseAllKey();
                        
                            //普攻一下，防止在纳塔飞天
                            Simulation.SendInput.SimulateAction(GIActions.NormalAttack);
                            using (var imageAfterBurst = CaptureToRectArea())
                            {
                                if (await AvatarSkillAsync(Logger, guardianAvatar, true, 1, ct, assumeActive: true)
                                    || !Bv.IsInMainUi(imageAfterBurst)) //Q技能CD（冷却检测）或者不在主界面（大招动画播放中）
                                {
                                    guardianAvatar.IsBurstReady = false;
                                }
                                else
                                {
                                    Sleep(500, ct);
                                    Simulation.SendInput.SimulateAction(GIActions.NormalAttack);//普攻一下，防止在纳塔飞天
                                    Simulation.SendInput.SimulateAction(GIActions.ElementalBurst);//尝试再放一次,不检查
                                    guardianAvatar.IsBurstReady = true;
                                }
                                Logger.LogInformation("优先第 {guardianAvatarName} 盾奶位 {GuardianAvatar} 释放元素爆发：{text}",
                                    guardianAvatarName, guardianAvatar.Name, !guardianAvatar.IsBurstReady ? "成功" : "失败");
                            }
                        }
                    }
                }
            }

            return GuardianSkillSwitchPolicy.CanReuseConfirmedCooldown(
                guardianAvatar.IsSkillReady(),
                guardianAvatar.HasConfirmedSkillCooldown);
        }

        private static async Task<bool> TryUseGuardianSkillOnceAsync(
            Avatar guardianAvatar,
            bool hold,
            string guardianAvatarName,
            CancellationToken ct)
        {
            if (!guardianAvatar.TrySwitch(10))
            {
                Logger.LogWarning(
                    "优先第 {Position} 盾奶位 {GuardianAvatar} 未确认切换成功，不发送战技按键",
                    guardianAvatarName,
                    guardianAvatar.Name);
                return false;
            }

            using (var activeCapture = CaptureToRectArea())
            {
                if (!guardianAvatar.IsActive(activeCapture))
                {
                    Logger.LogWarning(
                        "盾奶位 {GuardianAvatar} 切换后二次身份确认失败，不发送战技按键",
                        guardianAvatar.Name);
                    return false;
                }

                var baselineCd = guardianAvatar.ReadSkillCurrentCd(activeCapture);
                var baselineCooldownVisible = baselineCd > 0
                    || await AvatarSkillAsync(
                        Logger,
                        guardianAvatar,
                        false,
                        1,
                        ct,
                        activeCapture,
                        assumeActive: true);
                if (baselineCooldownVisible)
                {
                    Logger.LogWarning(
                        "盾奶位 {GuardianAvatar} 动作前已显示战技冷却，但缺少本轮就绪到冷却转换证据，暂不提交成功",
                        guardianAvatar.Name);
                    return false;
                }
            }

            if (hold)
            {
                Simulation.SendInput.SimulateAction(GIActions.ElementalSkill, KeyType.Hold);
            }
            else
            {
                Simulation.SendInput.SimulateAction(GIActions.ElementalSkill);
            }
            await Task.Delay(250, ct);

            const int cooldownConfirmationAttempts = 8;
            for (var checkAttempt = 0;
                 checkAttempt < cooldownConfirmationAttempts;
                 checkAttempt++)
            {
                ct.ThrowIfCancellationRequested();
                using var capture = CaptureToRectArea();
                if (!guardianAvatar.IsActive(capture))
                {
                    Logger.LogWarning(
                        "盾奶位 {GuardianAvatar} 释放战技确认期间失去出战身份，停止本次确认",
                        guardianAvatar.Name);
                    return false;
                }

                var detectedCd = guardianAvatar.ReadSkillCurrentCd(capture);
                var cooldownVisible = detectedCd > 0
                    || await AvatarSkillAsync(
                        Logger,
                        guardianAvatar,
                        false,
                        1,
                        ct,
                        capture,
                        assumeActive: true);
                if (GuardianSkillSwitchPolicy.IsSkillCastConfirmed(
                        baselineCooldownVisible: false,
                        cooldownVisibleAfterInput: cooldownVisible,
                        guardianStillActive: true))
                {
                    guardianAvatar.ConfirmSkillUsed(detectedCd);
                    if (detectedCd > 0)
                    {
                        Logger.LogInformation(
                            "优先第 {Position} 盾奶位 {GuardianAvatar} 战技Cd检测：{Cd:F1} 秒",
                            guardianAvatarName,
                            guardianAvatar.Name,
                            detectedCd);
                    }
                    return true;
                }

                if (checkAttempt + 1 < cooldownConfirmationAttempts)
                {
                    await Task.Delay(120, ct);
                }
            }

            return false;
        }
        
        //新方法，备用，非OCR识别，判断色块进行，速度更快
        //检测技能图标中释放含有白色色块，检测前进行角色切换的确认，skills：false为E技能，true为Q技能
        /// <summary>
        /// 检测角色技能冷却状态
        /// </summary>
        /// <param name="logger">日志记录器</param>
        /// <param name="guardianAvatar">角色对象</param>
        /// <param name="skills">技能类型，false为E技能，true为Q技能</param>
        /// <param name="retryCount">重试次数</param>
        /// <param name="ct">取消令牌</param>
        /// <param name="image">图像对象</param>
        /// <param name="needLog">是否需要日志输出</param>
        /// <param name="isResetCd">是否重置技能冷却状态</param>
        /// <returns>技能是否就绪</returns>
        public static async Task<bool> AvatarSkillAsync(ILogger logger, Avatar guardianAvatar, bool skills , int retryCount, 
            CancellationToken ct,ImageRegion? image = null,bool needLog = false, bool isResetCd = false,
            bool assumeActive = false)
        {
            if (assumeActive || guardianAvatar.TrySwitch())
            {
                Scalar bloodLower = new Scalar(255, 255, 255);
                int attempt = 0;
                while (attempt < retryCount)
                {
                    using var ownedImage = image == null ? CaptureToRectArea() : null;
                    var image2 = image ?? ownedImage!;

                    // var image2 = CaptureToRectArea();

                    var skillAra = !skills
                        ? new Rect(image2.Width * 1688 / 1920, image2.Height * 988 / 1080,
                            image2.Width * 22 / 1920, image2.Height * 12 / 1080) //E技能区域
                        
                        : new Rect(image2.Width * 1809 / 1920, image2.Height * 968 / 1080,
                            image2.Width * 30 / 1920, image2.Height * 15 / 1080); //Q技能区域
                    
                    using var skillRegion = image2.DeriveCrop(skillAra);
                    using var mask2 = OpenCvCommonHelper.Threshold(
                        skillRegion.SrcMat,
                        bloodLower,
                        bloodLower
                    );

                    using var labels2 = new Mat();
                    using var stats2 = new Mat();
                    using var centroids2 = new Mat();

                    int numLabels2 = Cv2.ConnectedComponentsWithStats(mask2, labels2, stats2, centroids2,
                        connectivity: PixelConnectivity.Connectivity4, ltype: MatType.CV_32S);

                    if (needLog) Logger.LogInformation("技能状态：{guardianAvatar.Name} - {skills} 状态 {text}", 
                        guardianAvatar.Name, skills?"Q技能":"E技能", numLabels2 > 1?"冷却中":"就绪");
                    
                    // Logger.LogInformation("技能状态：{numLabels2} 数量", numLabels2);
                    if (numLabels2 > 2)
                    {
                        if (!isResetCd)
                        {
                            return true;
                        }
                        if (skills)
                        {
                            guardianAvatar.IsBurstReady = true;
                        }
                        else
                        {
                            guardianAvatar.ManualSkillCd = 0;
                        }
                        
                        return true;
                    }
                    
                    attempt++;
                   if (retryCount > 1) await Task.Delay(100, ct);
                }
            }

            if (!isResetCd)
            {
                return false;
            }

            if (skills)
            {
                guardianAvatar.IsBurstReady = false;
            }
            else
            {
                guardianAvatar.AfterUseSkill();
            }

            return false;

        }
        
        /// <summary>
        /// 全队Q检测函数，备用，后续可用于自动EQ开发
        /// 不再推荐使用原因，可以参考使用 BetterGenshinImpact.GameTask.AutoFight.Model.Avatar.IsBurstReadyByClassify 方法，识别速度更快，效果更好
        /// </summary>
        /// <param name="image"></param>
        /// <param name="useEqList"></param>
        /// <param name="avatarCurrent"></param>
        /// <returns></returns>
        [Obsolete]
        public static Task<List<int>> AvatarQSkillAsync(ImageRegion? image = null, List<int>? useEqList = null,int? avatarCurrent = null)
        {
            var ownImage = image == null;
            image ??= CaptureToRectArea();
            try
            {
                image.SrcMat.ConvertTo(image.SrcMat, MatType.CV_8UC3, alpha: 2, beta: -200); // 增加亮度和对比度
                var useMedicine = new List<int>();
                var eqList = useEqList ?? new List<int> { 1, 2, 3, 4 };

                foreach (var i in eqList)
                {
                    var skillArea = i != avatarCurrent ? AutoFightAssets.Get(image).AvatarQRectListMap[i - 1]: new Rect(1762, 915, 114, 111);

                    using var grayImage = image.DeriveCrop(skillArea).SrcMat.CvtColor(ColorConversionCodes.BGR2GRAY);

                    var meanBrightness = Cv2.Mean(grayImage);
                    var avgBrightness = meanBrightness.Val0;
                    var threshold1 = avgBrightness * 0.9;
                    var threshold2 = avgBrightness * 2;

                    Cv2.Canny(grayImage, grayImage, threshold1: (float)threshold1, threshold2: (float)threshold2);

                    var circles = Cv2.HoughCircles(grayImage, HoughModes.Gradient, dp: 1.2, minDist: 20,
                        param1: 90, param2:i != avatarCurrent ? 25 : 35, minRadius: i != avatarCurrent ? 25 : 50, maxRadius:i != avatarCurrent ? 34 : 60);

                    if (circles.Length > 0)
                    {
                        useMedicine.Add(i);
                    }
                }

                if (useMedicine.Count > 0)
                {
                    return Task.FromResult(useMedicine);
                }

                return Task.FromResult(new List<int>());
            }
            finally
            {
                if (ownImage) image.Dispose();
            }
        }
    }

}
