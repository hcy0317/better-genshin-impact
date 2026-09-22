using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.GameTask.Common.Map;
using BetterGenshinImpact.GameTask.Model.Area;
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.AutoPathing;

public class CameraRotateTask(CancellationToken ct)
{
    private double Dpi => TaskContext.Instance().DpiScale;

    /// <summary>
    /// 向目标角度旋转
    /// </summary>
    /// <param name="targetOrientation"></param>
    /// <param name="imageRegion"></param>
    /// <returns></returns>
    public float RotateToApproach(float targetOrientation, ImageRegion imageRegion)
        => RotateToApproach(targetOrientation, imageRegion, null);

    private float RotateToApproach(float targetOrientation, ImageRegion imageRegion, PathRecoveryScope? scope)
    {
        var cao = scope?.Io.CameraOrientation(imageRegion) ?? CameraOrientation.Compute(imageRegion.SrcMat);
        scope?.Check();
        var diff = (cao - targetOrientation + 180) % 360 - 180;
        diff += diff < -180 ? 360 : 0;
        if (diff == 0)
        {
            return diff;
        }

        // 平滑的旋转视角
        // todo dpi 和分辨率都会影响转动速度
        double controlRatio = 1;
        if (Math.Abs(diff) > 90)
        {
            controlRatio = 4;
        }
        else if (Math.Abs(diff) > 30)
        {
            controlRatio = 3;
        }
        else if (Math.Abs(diff) > 5)
        {
            controlRatio = 2;
        }

        var movement = (int)Math.Round(-controlRatio * diff * (scope?.Io.Dpi() ?? Dpi));
        scope?.Check();
        if (scope != null) scope.Io.MouseMove(movement, 0);
        else Simulation.SendInput.Mouse.MoveMouseBy(movement, 0);
        return diff;
    }

    /// <summary>
    /// 转动视角到目标角度
    /// </summary>
    /// <param name="targetOrientation">目标角度</param>
    /// <param name="maxDiff">最大误差</param>
    /// <param name="maxTryTimes">最大尝试次数（超时时间）</param>
    /// <returns></returns>
    public async Task<bool> WaitUntilRotatedTo(int targetOrientation, int maxDiff, int maxTryTimes = 50)
        => await WaitUntilRotatedTo(targetOrientation, maxDiff, maxTryTimes, null);

    internal async Task<bool> WaitUntilRotatedTo(int targetOrientation, int maxDiff, int maxTryTimes, PathRecoveryScope? scope)
    {
        bool isSuccessful = false;
        int count = 0;
        while (!ct.IsCancellationRequested)
        {
            scope?.Check();
            using var screen = scope?.Io.Capture() ?? CaptureToRectArea();
            scope?.Check();
            if (scope != null) await scope.BeforeInputAsync();
            if (Math.Abs(RotateToApproach(targetOrientation, screen, scope)) < maxDiff)
            {
                isSuccessful = true;
                break;
            }

            if (count >= maxTryTimes)
            {
                (scope?.Io.Logger ?? Logger).LogWarning("视角转动到目标角度超时，停止转动");
                break;
            }

            if (scope != null) await scope.DelayAsync(50);
            else await Delay(50, ct);
            count++;
        }
        return isSuccessful;
    }
}
