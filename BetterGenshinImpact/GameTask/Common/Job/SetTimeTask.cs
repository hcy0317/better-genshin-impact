using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.GameTask.AutoSkip.Assets;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.View.Drawable;
using BetterGenshinImpact.GameTask.Common.Ui;
using Fischless.WindowsInput;
using Microsoft.Extensions.Logging;
using Vanara.PInvoke;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.Common.Job;

public class SetTimeTask
{
    // 圆心坐标
    private const double CenterX = 1441;
    private const double CenterY = 501.6;

    private readonly ReturnMainUiTask _returnMainUiTask = new();
    private readonly SetTimeFlowIo? _io;
    public SetTimeTask() { }
    internal SetTimeTask(SetTimeFlowIo io) => _io = io;

    public async Task Start(int hour, int minute, CancellationToken ct, bool skipTimeAdjustmentAnimation = false)
    {
        try
        {
            await DoOnce(hour, minute, ct, skipTimeAdjustmentAnimation);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            try { (_io?.Logger ?? Logger).LogError(e, "设置时间异常，保留失败：{Msg}", e.Message); } catch { }
            throw;
        }
        finally
        {
            if (_io == null) { try { VisionContext.Instance().DrawContent.ClearAll(); } catch { } }
        }
    }

    public async Task DoOnce(int hour, int minute, CancellationToken ct, bool skipTimeAdjustmentAnimation = false)
    {
        await SetTimeFlow.ExecuteAsync(hour, minute, skipTimeAdjustmentAnimation, _io ?? CreateNativeIo(), ct);
    }

    private SetTimeFlowIo CreateNativeIo() => new()
    {
        Logger = Logger,
        ReturnMain = token => _returnMainUiTask.Start(token),
        Open = async token =>
        {
            UiEscapeInput.Run(() => { token.ThrowIfCancellationRequested(); UiOperation.Current?.Check(); },
                () => Simulation.SendInput.Keyboard.KeyDown(User32.VK.VK_ESCAPE),
                () => Simulation.SendInput.Keyboard.KeyUp(User32.VK.VK_ESCAPE), Thread.Sleep);
            await Delay(800, token);
            await MouseClick(50, 700, 900, token);
        },
        Observe = () =>
        {
            using var frame = CaptureToRectArea();
            var observation = TimeSettingUiReader.Read(frame);
            return Bv.IsInPromptDialog(frame) ? observation with { Visible = false } : observation;
        },
        SetDial = (h, m, token) => SetTime(h, m, 30, 150, 300, 50, token),
        Confirm = (admission, token) => MouseClick(1500, 1000, 0, token, admission),
        SkipAnimation = token => MouseClick(200, 200, 100, token),
        Delay = (ms, token) => TaskControl.Delay(ms, token)
    };

    double[] GetPosition(double r, double index)
    {
        double angle = index * Math.PI / 720;
        return [CenterX + r * Math.Cos(angle), CenterY + r * Math.Sin(angle)];
    }

    async Task MouseClick(double x, double y, int stepDuration, CancellationToken ct, Action? admission = null)
    {
        GameCaptureRegion.GameRegion1080PPosMove(x, y);
        await Delay(50, ct);
        await HoldMouseAsync(() => Delay(50, ct), ct, admission);
        await Delay(stepDuration, ct);
    }

    async Task MouseClickAndMove(double x1, double y1, double x2, double y2, int stepDuration, CancellationToken ct)
    {
        GameCaptureRegion.GameRegion1080PPosMove(x1, y1);
        await Delay(50, ct);
        await HoldMouseAsync(async () =>
        {
            await Delay(50, ct);
            GameCaptureRegion.GameRegion1080PPosMove(x2, y2);
            await Delay(50, ct);
        }, ct);
        await Delay(stepDuration, ct);
    }

    private static async Task HoldMouseAsync(Func<Task> body, CancellationToken ct, Action? admission = null)
        => await HoldMouseAsync(body, () => Simulation.SendInput.Mouse.LeftButtonDown(),
            () => Simulation.SendInput.Mouse.LeftButtonUp(), ct, admission);

    internal static async Task HoldMouseAsync(Func<Task> body, Action down, Action up, CancellationToken ct, Action? admission = null)
    {
        var entered = false;
        Exception? failure = null;
        try
        {
            using (var capture = new InputDispatchCapture(() =>
                { ct.ThrowIfCancellationRequested(); UiOperation.Current?.Check(); admission?.Invoke(); entered = true; }))
            {
                down();
                if (capture.NativeCalls == 0 || capture.Requested <= 0 || capture.Submitted != capture.Requested || capture.Uncertain)
                    throw new InvalidOperationException("调时按下缺少完整原生回执");
            }
            await body();
        }
        catch (Exception error) { failure = error; }
        finally
        {
            if (entered)
            {
                try
                {
                    using var capture = new InputDispatchCapture();
                    up();
                    if (capture.NativeCalls == 0 || capture.Requested <= 0 || capture.Submitted != capture.Requested || capture.Uncertain)
                        throw new InvalidOperationException("调时松键缺少完整原生回执");
                }
                catch (Exception cleanup) { failure = failure == null ? cleanup : new AggregateException(failure, cleanup); }
            }
        }
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    async Task SetTime(int hour, int minute, int r1, int r2, int r3, int stepDuration, CancellationToken ct)
    {
        int end = (hour + 6) * 60 + minute - 20;
        int n = 3;
        for (int i = -n + 1; i < 1; i++)
        {
            double[] position = GetPosition(r1, end + i * 1440.0 / n);
            await MouseClick(position[0], position[1], stepDuration, ct);
        }

        double[] position1 = GetPosition(r2, end + 5);
        double[] position2 = GetPosition(r3, end + 20 + 0.5);
        await MouseClickAndMove(position1[0], position1[1], position2[0], position2[1], stepDuration, ct);
    }
}
