using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
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

        internal static async Task TurnTowardIndicatorAsync(
            EnemySeekDecision decision,
            int imageWidth,
            int imageHeight,
            ILogger logger,
            CancellationToken ct)
        {
            if (decision.Action != AutoFightSeekAction.Approach || decision.Visual is not { } visual)
            {
                return;
            }

            var bearing = AutoFightSeek.GetIndicatorBearingDegrees(visual, imageWidth, imageHeight);
            var cameraSteps = AutoFightSeek.GetIndicatorCameraSteps(visual, imageWidth, imageHeight);
            var verticalCameraOffset = AutoFightSeek.GetIndicatorCameraVerticalOffset(visual, imageHeight);
            logger.LogInformation(
                "红色敌人方位小三角: 候选={SignalCount}，首次只选当前目标；位置=({X},{Y})，尺寸={Width}x{Height}，轮廓朝向角={Bearing:F1}°，方向={Direction}，分成 {CameraStepCount} 段完成总转向=({CameraOffset},{VerticalCameraOffset})",
                decision.SignalCount,
                visual.X,
                visual.Y,
                visual.Width,
                visual.Height,
                bearing,
                decision.Direction,
                cameraSteps.Count,
                cameraSteps.Sum(),
                verticalCameraOffset);

            if (cameraSteps.Count == 0 && verticalCameraOffset != 0)
            {
                Simulation.SendInput.Mouse.MoveMouseBy(0, verticalCameraOffset);
                await Task.Delay(180, ct);
            }

            for (var i = 0; i < cameraSteps.Count; i++)
            {
                Simulation.SendInput.Mouse.MoveMouseBy(
                    cameraSteps[i],
                    i == 0 ? verticalCameraOffset : 0);
                await Task.Delay(180, ct);
            }
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
                "检测到远距离敌人血条: 位置=({X},{Y})，尺寸={Width}x{Height}，转向=({CameraOffset},{VerticalCameraOffset})，短距接近",
                visual.X,
                visual.Y,
                visual.Width,
                visual.Height,
                cameraOffset,
                verticalCameraOffset);

            if (cameraOffset != 0 || verticalCameraOffset != 0)
            {
                Simulation.SendInput.Mouse.MoveMouseBy(cameraOffset, verticalCameraOffset);
                await Task.Delay(180, ct);
            }

            await MoveWithKeysAsync(ct, GIActions.MoveForward);
        }

        private static async Task MoveWithKeysAsync(CancellationToken ct, params GIActions[] actions)
        {
            var pressedActions = new List<GIActions>();
            try
            {
                foreach (var action in actions)
                {
                    Simulation.SendInput.SimulateAction(action, KeyType.KeyDown);
                    pressedActions.Add(action);
                }

                await Task.Delay(1000, ct);
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
                _pendingHorizontalCameraOffset += horizontalCameraOffset;
                _pendingVerticalCameraOffset += verticalCameraOffset;
                _preserveBudgetAcrossUnknownCameraMovement = false;
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
        // 6 个实拍轮廓在任意旋转后的 I1 距离保留少量抗锯齿余量；
        // 尺寸门另按 1920x1080 的真实 UI 尺寸过滤小型 HUD 鬼影。
        private const double DirectionIndicatorFeatureThreshold = 0.27;
        private const double DirectionIndicatorMinSolidity = 0.60;
        private const double DirectionIndicatorMaxSolidity = 0.90;
        private const double DirectionIndicatorMinHollowRatio = 0.002;
        private const double DirectionIndicatorMaxHollowRatio = 0.25;
        private const int HalfTurnMouseOffset = 1920;
        private const int MaxIndicatorCameraStep = 640;

        private static readonly string[] DirectionIndicatorTemplateNames =
        {
            "enemy_direction_indicator_left.png",
            "enemy_direction_indicator_variant_02.png",
            "enemy_direction_indicator_variant_03.png",
            "enemy_direction_indicator_variant_04.png",
            "enemy_direction_indicator_variant_05.png",
            "enemy_direction_indicator_variant_06.png"
        };

        private static readonly Lazy<IReadOnlyList<Point[]>> DirectionIndicatorTemplateContours =
            new(LoadDirectionIndicatorTemplateContours);

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
            var scale = imageHeight / 1080d;
            var minimumHeight = Math.Clamp(
                (int)Math.Round(4 * scale, MidpointRounding.AwayFromZero),
                2,
                4);
            var maximumHeight = Math.Max(
                minimumHeight,
                (int)Math.Round(14 * scale, MidpointRounding.AwayFromZero));
            var minimumWidth = Math.Max(
                Math.Max(12, (int)Math.Round(18 * scale, MidpointRounding.AwayFromZero)),
                visual.Height * 3);
            return visual.Height >= minimumHeight
                   && visual.Height <= maximumHeight
                   && visual.Width >= minimumWidth;
        }

        internal static bool IsDirectionIndicatorGeometry(EnemySeekVisual visual, int imageWidth, int imageHeight)
        {
            // 实拍箭头的红色轮廓约 30x24~36x31。现场误报集中在 15x15、17x17、
            // 18x21 等小红块；短边、长边和面积必须同时达到真实箭头尺度。
            if (visual.Width is > 56 || visual.Height is > 56
                || Math.Min(visual.Width, visual.Height) < 18
                || Math.Max(visual.Width, visual.Height) < 22
                || visual.Area < 160)
            {
                return false;
            }

            var fillRatio = visual.Area / (double)(visual.Width * visual.Height);
            var aspectRatio = visual.Width / (double)visual.Height;
            if (fillRatio is < 0.30 or > 0.78 || aspectRatio is < 0.65 or > 1.55)
            {
                return false;
            }

            var nearHorizontalEdge = visual.CenterX <= imageWidth * 0.2 || visual.CenterX >= imageWidth * 0.8;
            var nearVerticalEdge = visual.CenterY <= imageHeight * 0.4 || visual.CenterY >= imageHeight * 0.75;
            return nearHorizontalEdge || nearVerticalEdge;
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
            if (visual.IndicatorBearingDegrees.HasValue)
            {
                return visual.IndicatorBearingDegrees.Value;
            }

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
            var closeHeightThreshold = Math.Clamp(
                (int)Math.Round(imageHeight / 108d, MidpointRounding.AwayFromZero),
                8,
                12);
            return healthBar.Height < closeHeightThreshold;
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
                                            cameraHorizontalOffset);
            var verticalJumpIsValid = normalizedVerticalJump <= 0.22
                                      || IsCameraGuidedShift(
                                          observedVerticalShift,
                                          cameraVerticalOffset);
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
            int requestedCameraOffset)
        {
            return observedScreenShift != 0
                   && requestedCameraOffset != 0
                   && Math.Sign(observedScreenShift) == -Math.Sign(requestedCameraOffset);
        }

        internal static int GetVisibleEnemyCameraOffset(EnemySeekVisual healthBar, int imageWidth)
        {
            var delta = healthBar.CenterX - imageWidth / 2;
            var deadZone = Math.Max(80, imageWidth / 12);
            return Math.Abs(delta) <= deadZone
                ? 0
                : Math.Clamp(delta * 2, -960, 960);
        }

        internal static int GetVisibleEnemyCameraVerticalOffset(EnemySeekVisual healthBar, int imageHeight)
        {
            var targetY = imageHeight * 0.35;
            var delta = (int)Math.Round(healthBar.CenterY - targetY);
            return Math.Abs(delta) <= 80
                ? 0
                : Math.Clamp(delta, -160, 160);
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
            return inRightPartyHud || inBottomPlayerHud;
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
                    visual,
                    imageCrop.Width,
                    imageCrop.Height))
                .Where(visual => visual.HasValue)
                .Select(visual => visual!.Value)
                .ToList();

            if (indicatorOnly)
            {
                return SelectDirectionIndicatorDecision(
                    visuals,
                    imageCrop.Width,
                    imageCrop.Height,
                    indicatorRouteLocked: false);
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

            return VisibleHealthApproach.Evaluate(SelectSeekDecision(
                visuals,
                imageCrop.Width,
                imageCrop.Height,
                IsIndicatorRouteLocked,
                fixedTopState.tracked,
                fixedTopState.advanceCompleted,
                fixedTopState.exhausted),
                imageCrop.Width,
                imageCrop.Height);
        }

        internal static EnemySeekVisual? ClassifySeekVisual(
            Mat mask,
            EnemySeekVisual visual,
            int imageWidth,
            int imageHeight)
        {
            if (IsHealthBar(visual, imageHeight))
            {
                return IsPlayerHudHealthBar(visual, imageWidth, imageHeight)
                    ? null
                    : visual;
            }

            if (!IsDirectionIndicatorGeometry(visual, imageWidth, imageHeight))
            {
                return null;
            }

            var templates = DirectionIndicatorTemplateContours.Value;
            if (templates.Count == 0)
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
                    templates,
                    DirectionIndicatorFeatureThreshold))
            {
                return null;
            }

            var bearing = GetDirectionIndicatorOrientationDegrees(candidate);
            return bearing.HasValue
                ? visual with { IndicatorBearingDegrees = bearing.Value }
                : null;
        }

        internal static bool MatchesDirectionIndicatorFeature(
            Mat candidateMask,
            IReadOnlyCollection<Point[]> templateContours,
            double threshold)
        {
            var candidateContour = GetLargestExternalContour(candidateMask);
            var hollowRatio = GetDirectionIndicatorHollowRatio(candidateMask);
            if (candidateContour == null
                || candidateContour.Length < 3
                || !HasDirectionIndicatorConcavity(candidateContour)
                || hollowRatio is not (>= DirectionIndicatorMinHollowRatio and <= DirectionIndicatorMaxHollowRatio))
            {
                return false;
            }

            return templateContours.Any(template =>
                template.Length >= 3
                && Cv2.MatchShapes(
                    candidateContour,
                    template,
                    ShapeMatchModes.I1,
                    0) <= threshold);
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

        internal static double? GetDirectionIndicatorOrientationDegrees(Mat binaryMask)
        {
            var moments = Cv2.Moments(binaryMask, true);
            if (Math.Abs(moments.M00) < double.Epsilon)
            {
                return null;
            }

            // 主轴给出箭头的连续朝向，但只确定到 180°；沿主轴的三阶偏度用于区分
            // “箭头尖端”和“尾翼”，因此不需要穷举 360° 模板。
            var axisRadians = 0.5 * Math.Atan2(
                2 * moments.Mu11,
                moments.Mu20 - moments.Mu02);
            var axisX = Math.Cos(axisRadians);
            var axisY = Math.Sin(axisRadians);
            var skewAlongAxis =
                moments.Mu30 * axisX * axisX * axisX
                + 3 * moments.Mu21 * axisX * axisX * axisY
                + 3 * moments.Mu12 * axisX * axisY * axisY
                + moments.Mu03 * axisY * axisY * axisY;
            if (skewAlongAxis < 0)
            {
                axisX = -axisX;
                axisY = -axisY;
            }

            return Math.Atan2(axisX, -axisY) * 180d / Math.PI;
        }

        private static IReadOnlyList<Point[]> LoadDirectionIndicatorTemplateContours()
        {
            var result = new List<Point[]>();
            foreach (var fileName in DirectionIndicatorTemplateNames)
            {
                try
                {
                    using var template = GameTaskManager.LoadAssetImage(
                        "AutoFight",
                        fileName,
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
                        result.Add(contour);
                    }
                }
                catch
                {
                    // 单个样本损坏不应让整套寻敌失效；其余实拍样本继续提供旋转不变特征。
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
                LockIndicatorRoute();
                logger.LogInformation(
                    "首次箭头目标已锁定：候选 {SignalCount} 个，选取绝对转角最小的 {Bearing:F1}°；完成分段转身后执行固定 6 秒前进计划",
                    decision.SignalCount,
                    decision.Visual is { } visual
                        ? GetIndicatorBearingDegrees(visual, imageWidth, imageHeight)
                        : 0d);
                await MoveForwardTask.TurnTowardIndicatorAsync(
                    decision,
                    imageWidth,
                    imageHeight,
                    logger,
                    ct);
                currentDecision = CaptureSeekDecision(
                    bloodLower,
                    bloodHigher,
                    out currentImageWidth,
                    out currentImageHeight);
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

            var completedLockedSteps = 0;
            while (ShouldContinueLockedRouteSegment(currentDecision, completedLockedSteps))
            {
                await MoveForwardTask.AdvanceLockedRouteAsync(
                    GetLockedRouteVerticalStep(completedLockedSteps),
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
            var currentDecision = decision;
            var currentImageWidth = imageWidth;
            var currentImageHeight = imageHeight;
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
                        "血条候选跨帧跳变，停止本轮接近并恢复战斗结束检查：上一目标=({PreviousX},{PreviousY},{PreviousWidth}x{PreviousHeight})，当前=({CurrentX},{CurrentY},{CurrentWidth}x{CurrentHeight})",
                        previousVisual.X,
                        previousVisual.Y,
                        previousVisual.Width,
                        previousVisual.Height,
                        currentVisual.X,
                        currentVisual.Y,
                        currentVisual.Width,
                        currentVisual.Height);
                    return false;
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
                    if (guardianAvatar.TrySwitch(10))
                    {
                        guardianAvatar.ManualSkillCd = -1;
                        if (await AvatarSkillAsync(Logger, guardianAvatar, false, 1, ct, assumeActive: true))
                        {
                            var cd1 = guardianAvatar.AfterUseSkill();
                            if (cd1 > 0)
                            {
                                Logger.LogInformation("优先第 {text} 盾奶位 {GuardianAvatar} 战技Cd检测：{cd} 秒", guardianAvatarName,
                                    guardianAvatar.Name, cd1);
                                guardianAvatar.ManualSkillCd = -1;
                                return true;
                            }
                        }
            
                        guardianAvatar.UseSkill(guardianAvatarHold);
                        var imageAfterUseSkill = CaptureToRectArea();
                        var retry = 50;
                        try
                        {
                            while (!(await AvatarSkillAsync(Logger, guardianAvatar, false, 1, ct, imageAfterUseSkill, assumeActive: true)) && retry > 0)
                            {
                                Simulation.SendInput.SimulateAction(GIActions.ElementalSkill);
                                //防止在纳塔飞天或爬墙
                                Simulation.ReleaseAllKey();
                                if (retry % 3 == 0)
                                {
                                    Simulation.SendInput.SimulateAction(GIActions.NormalAttack);
                                    Simulation.SendInput.SimulateAction(GIActions.Drop);
                                }
                                var previousImage = imageAfterUseSkill;
                                imageAfterUseSkill = CaptureToRectArea();
                                previousImage.Dispose();
                                await Task.Delay(30, ct);
                                // Logger.LogInformation("优先第333 {t}", retry);
                                retry -= 1;
                            }
                        }
                        finally
                        {
                            imageAfterUseSkill.Dispose();
                        }
                        
                        if (retry > 0)
                        {
                            Logger.LogInformation("优先第 {text} 盾奶位 {GuardianAvatar} 释放战技：{t}",
                                guardianAvatarName, guardianAvatar.Name,"成功");
                            guardianAvatar.LastSkillTime = DateTime.UtcNow;
                            guardianAvatar.ManualSkillCd = -1;
                            return true;
                        }
                        
                        Logger.LogInformation("优先第 {text} 盾奶位 {GuardianAvatar} 释放战技：失败重试 {attempt} 次",
                            guardianAvatarName, guardianAvatar.Name, attempt + 1);
                        guardianAvatar.ManualSkillCd = 0;
                        guardianAvatar.UseSkill(guardianAvatarHold);
                        //防止在纳塔飞天或
                        Simulation.SendInput.SimulateAction(GIActions.NormalAttack);
                        Simulation.SendInput.SimulateAction(GIActions.Drop);
                    }
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
