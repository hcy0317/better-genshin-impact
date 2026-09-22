using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.AutoPathing.Model.Enum;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Vanara.PInvoke;
using static BetterGenshinImpact.GameTask.Common.TaskControl;
using BetterGenshinImpact.Core.Simulator.Extensions;

namespace BetterGenshinImpact.GameTask.AutoPathing;

public class TrapEscaper(CancellationToken ct)
{
    private readonly CameraRotateTask _rotateTask = new(ct);
    private static readonly Random _random = new Random();
    private int _lastActionIndex = 0;
    public static DateTime LastActionTime = DateTime.UtcNow;
    private static int _randomAngle = 0;

    private void IncreaseRandomAngle()
    {
        _randomAngle += _random.Next(30, 45);
    }

    private void ReduceRandomAngle()
    {
        _randomAngle += _random.Next(-45, -30);
    }

    public Task MoveTo(WaypointForTrack waypoint) => MoveTo(waypoint, null);

    internal async Task MoveTo(WaypointForTrack waypoint, PathRecoveryScope? scope)
    {
        if (scope == null) { await MoveToCore(waypoint, null); return; }
        scope.BeginMovement();
        try { await MoveToCore(waypoint, scope); }
        catch (RecoveryMovementExpired)
        {
            // Only the original inner movement window ended. The caller still has to observe arrival.
        }
        finally { scope.EndMovement(); }
    }

    private async Task MoveToCore(WaypointForTrack waypoint, PathRecoveryScope? scope)
    {
        var startTime = Now(scope);
        bool left = false;
        OpenCvSharp.Point2f position;
        using (var initialScreen = (scope?.Io.Capture() ?? CaptureToRectArea()))
        {
            position = (scope == null ? Navigation.GetPosition(initialScreen, waypoint.MapName, waypoint.MapMatchMethod, waypoint.MapLayerSelector) : (await (scope.Io.LocateDirect ?? scope.Io.Locate)(initialScreen, waypoint)).Point);
        }
        LastActionTime = Now(scope);
        scope?.BeginMovementIdleWindow();
        var targetOrientation = Navigation.GetTargetOrientation(waypoint, position);
        await _rotateTask.WaitUntilRotatedTo(targetOrientation, 5, 50, scope);

        // 按下w，一直走
        await Send(scope, GIActions.MoveForward, KeyType.KeyDown);
        try
        {
        while (!ct.IsCancellationRequested)
        {
            scope?.Check();
            var now = Now(scope);
            if ((now - LastActionTime).TotalSeconds > 5)
            {
                break;
            }
            if ((now - startTime).TotalSeconds > 25)
            {
                (scope?.Io.Logger ?? Logger).LogError("卡死脱困超时！");
                break;
            }

            using var screen = (scope?.Io.Capture() ?? CaptureToRectArea());
            position = (scope == null ? Navigation.GetPosition(screen, waypoint.MapName, waypoint.MapMatchMethod, waypoint.MapLayerSelector) : (await (scope.Io.LocateDirect ?? scope.Io.Locate)(screen, waypoint)).Point);

            // 旋转视角
            /* 这里的角度增加了一个randomAngle角度，用来在原角度不适用的情况下修改角度以适应复杂环境
               randomAngle会定期归零，不会任何程度上影响地图追踪的结果（指到达既设点位）
               randomAngle为类变量，会在需要修改角度的情况下进行更改，更改时会附带有重置计时器_lastActionTime的代码
               总体的自动避障逻辑为：
               0. 检测是否卡在障碍物上，如果是则执行大脱困
               1. 检测前面是否有障碍物，如果是则执行小脱困
               2. 重复0和1，角度会一直增加，达到“转一圈”的360度脱困效果，若成功脱困则将randomAngle归零
               */
            targetOrientation = Navigation.GetTargetOrientation(waypoint, position) + _randomAngle;

            //执行旋转
            await _rotateTask.WaitUntilRotatedTo(targetOrientation, 5, 50, scope);
            await Send(scope, GIActions.MoveForward, KeyType.KeyDown);
            //
            //这里是随机角度的归零逻辑，在脱困执行一秒后将randomAngle设为0以将实际角度重置为正面向点位的角度
            //其实就是在一段时间内进行角度的修改以实现自动避障
            if (_randomAngle != 0)
            {
                _randomAngle %= 360; //角度增加到360度时也会归零
                if ((Now(scope) - LastActionTime).TotalSeconds > 1.5)
                {
                    _randomAngle = 0;
                }
            }
            // 设置为非攀爬时误进入攀爬，自动脱离（小脱困）
            // 小脱困逻辑，在进入攀爬时，即后一帧会自动脱离，因此无需再执行脱困代码
            // 进入攀爬就代表前面有较高的物体（障碍物）阻挡，所以必须“旋转角度”以辅助绕过障碍物！！！

            // 先排除攀爬和飞行的情况
            if (waypoint.MoveMode != MoveModeEnum.Climb.Code &&
                waypoint.MoveMode != MoveModeEnum.Fly.Code)
                if (Bv.GetMotionStatus(screen) == MotionStatus.Climb)
                {
                    await Send(scope, GIActions.MoveForward, KeyType.KeyUp);
                    await Send(scope, GIActions.Drop);
                    await Wait(scope, 75, synchronous: true);
                    await Send(scope, GIActions.MoveBackward, KeyType.KeyDown);
                    await Wait(scope, 700, synchronous: true);
                    await Send(scope, GIActions.MoveBackward, KeyType.KeyUp);

                    LastActionTime = Now(scope);

                    //！！！！！！！！这里修改了randomAngle的值，用于在脱困后随机旋转角度！！！！！！！！
                    if (!left)
                    {
                        IncreaseRandomAngle();
                    }
                    else
                    {
                        ReduceRandomAngle();
                    }

                    continue;
                }

            await Wait(scope, 100);
        }

        scope?.Check();
        }
        finally
        {
            // Releases are not conditional on a live deadline or a Normal observation.
            await Send(scope, GIActions.MoveForward, KeyType.KeyUp);
        }
    }

    public Task RotateAndMove() => RotateAndMove(null);

    internal async Task RotateAndMove(PathRecoveryScope? scope)
    {
        IncreaseRandomAngle();
        // 脱离攀爬状态
        await Send(scope, GIActions.MoveForward, KeyType.KeyUp);
        await Send(scope, GIActions.Drop);
        await Wait(scope, 75);
        if (scope != null) await scope.ObserveAsync();
        var attacked = scope == null && LandingAttackGuard.TryAttack(() =>
        {
            using var screen = (scope?.Io.Capture() ?? CaptureToRectArea());
            return scope?.Io.Motion(screen) ?? Bv.GetMotionStatus(screen);
        }, () => Simulation.SendInput.SimulateAction(GIActions.NormalAttack), ct);
        (scope?.Io.Logger ?? Logger).LogDebug(attacked
            ? "脱困：确认飞行，执行一次下落攻击"
            : "脱困：未确认飞行，不发送普攻");
        await Wait(scope, 500);

        TimeSpan timeSinceLastAction = Now(scope) - LastActionTime;

        if (timeSinceLastAction.TotalSeconds >= 10)
        {
            _lastActionIndex = 0;
        }
        else
        {
            _lastActionIndex++;
        }

        var difference = _lastActionIndex * 1000;

        switch (_lastActionIndex % 3)
        {
            case 0:
                // 向后移动
                await MoveBackward(1000 + difference, scope);
                break;

            case 1:
                // 向左移动
                await MoveLeft(700 + difference, scope);
                break;

            case 2:
                // 向右移动
                await MoveRight(700 + difference, scope);
                break;
        }

        LastActionTime = Now(scope);
    }

    private async Task MoveBackward(int delay, PathRecoveryScope? scope)
    {
        try
        {
        await Send(scope, GIActions.MoveBackward, KeyType.KeyDown);
        await Wait(scope, 500, synchronous: true);
        await Send(scope, GIActions.Jump);
        await Wait(scope, delay, synchronous: true);
        }
        finally { await Send(scope, GIActions.MoveBackward, KeyType.KeyUp); }
    }

    private async Task MoveLeft(int delay, PathRecoveryScope? scope)
    {
        try
        {
        await Send(scope, GIActions.MoveLeft, KeyType.KeyDown);
        await Wait(scope, 300, synchronous: true);
        await Send(scope, GIActions.Jump);
        await Wait(scope, delay, synchronous: true);
        }
        finally { await Send(scope, GIActions.MoveLeft, KeyType.KeyUp); }
        await Send(scope, GIActions.Drop);
    }

    private async Task MoveRight(int delay, PathRecoveryScope? scope)
    {
        try
        {
        await Send(scope, GIActions.MoveRight, KeyType.KeyDown);
        await Wait(scope, 300, synchronous: true);
        await Send(scope, GIActions.Jump);
        await Wait(scope, delay, synchronous: true);
        }
        finally { await Send(scope, GIActions.MoveRight, KeyType.KeyUp); }
        await Send(scope, GIActions.Drop);
    }
    private static DateTime Now(PathRecoveryScope? scope) => scope?.Io.Clock.GetUtcNow().UtcDateTime ?? DateTime.UtcNow;

    private static Task Send(PathRecoveryScope? scope, GIActions action, KeyType type = KeyType.KeyPress)
    {
        if (scope != null) return scope.SendAsync(action, type);
        Simulation.SendInput.SimulateAction(action, type);
        return Task.CompletedTask;
    }

    private Task Wait(PathRecoveryScope? scope, int milliseconds, bool synchronous = false)
    {
        if (scope != null) return scope.DelayAsync(milliseconds);
        if (synchronous) { Sleep(milliseconds); return Task.CompletedTask; }
        return Delay(milliseconds, ct);
    }
}
