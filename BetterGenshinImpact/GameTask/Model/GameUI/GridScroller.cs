using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.WindowsInput;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.GameTask.Model.GameUI
{
    /// <summary>
    /// Grid界面垂直滚动服务类
    /// </summary>
    public class GridScroller
    {
        private readonly Rect roi;
        private readonly CancellationToken ct;
        private readonly ILogger logger;
        private readonly InputSimulator input = Simulation.SendInput;
        private readonly int columns;
        private readonly int s1Round;
        private readonly int roundMilliseconds;
        private readonly int s2Round;
        private readonly double s3Scale;
        private readonly bool fastScroll;
        private readonly Size captureSize;
        private bool isCalibrated;
        private double pixelsPerInput;
        private double residualPixels;

        internal GridScroller(GridParams @params, ILogger logger, InputSimulator input, CancellationToken ct)
        {
            this.roi = @params.Roi;
            this.ct = ct;
            this.logger = logger;
            this.input = input;
            this.columns = @params.Columns;
            this.s1Round = @params.S1Round;
            this.roundMilliseconds = @params.RoundMilliseconds;
            this.s2Round = @params.S2Round;
            this.s3Scale = @params.S3Scale;
            this.fastScroll = @params.FastScroll;
            this.captureSize = @params.CaptureSize;
        }

        internal async Task<bool> TryVerticalScollDown(Func<Mat, int, IEnumerable<Rect>> GetGridItems)
        {
            if (this.fastScroll)
            {
                throw new InvalidOperationException(
                    "圣遗物快速滚动必须由 YasPaginationCursor 提供行数。");
            }

            using var ra = TaskControl.CaptureToRectArea();
            using ImageRegion prevGrid = ra.DeriveCrop(roi);

            for (int i = 0; i < this.s1Round; i++)
            {
                this.input.Mouse.VerticalScroll(-2);
                await TaskControl.Delay(this.roundMilliseconds, this.ct);
            }
            await TaskControl.Delay(300, this.ct);
            using var ra2 = TaskControl.CaptureToRectArea();
            using ImageRegion scrolledGrid = ra2.DeriveCrop(this.roi);

            bool isScrolling = IsScrolling(prevGrid.CacheGreyMat, scrolledGrid.CacheGreyMat, out Point2d shift, logger: this.logger);

            if (isScrolling)
            {
                for (int i = 0; i < this.s2Round; i++)    // 再滚动差不多（最多行数-1）行
                {
                    input.Mouse.VerticalScroll(-2);
                    await TaskControl.Delay(this.roundMilliseconds, ct);
                }

                DateTimeOffset rollingEndTime = DateTime.Now.AddSeconds(2);
                while (DateTime.Now < rollingEndTime)
                {
                    await TaskControl.Delay(60, ct);
                    using var ra4 = TaskControl.CaptureToRectArea();
                    using ImageRegion grid2 = ra4.DeriveCrop(this.roi);
                    var gridItems2 = GetGridItems(grid2.SrcMat, this.columns).ToList();
                    if (gridItems2.Count == 0)
                    {
                        this.logger.LogDebug("滚动过程中暂未检测到网格项，等待下一帧");
                        continue;
                    }

                    if (gridItems2.Min(i => i.Y) > (ra4.Width * this.s3Scale))  // 最后精细滚动，保证完整地显示最多行
                    {
                        input.Mouse.VerticalScroll(-1);
                    }
                    else
                    {
                        break;
                    }
                }
                using var ra3 = TaskControl.CaptureToRectArea();
                using ImageRegion grid3 = ra3.DeriveCrop(this.roi);
                grid3.MoveTo(grid3.Width, grid3.Height);
                await TaskControl.Delay(300, ct);
                return true;
            }
            else
            {
                await TaskControl.Delay(300, ct);
                this.logger.LogInformation("滚动到底部了");
                return false;
            }
        }

        internal async Task<YasScrollReceipt> ScrollRowsFastAsync(
            int rowsToScroll,
            Func<Mat, int, IEnumerable<Rect>> getGridItems)
        {
            if (rowsToScroll <= 0)
                throw new ArgumentOutOfRangeException(nameof(rowsToScroll));
            if (!this.fastScroll)
                throw new InvalidOperationException("当前网格未启用 YAS 快速滚动。");

            var timer = Stopwatch.StartNew();
            SystemControl.ActivateWindow();
            if (!this.isCalibrated)
            {
                this.pixelsPerInput = await MeasureScrollPixelsPerInputAsync();
                this.isCalibrated = true;
            }
            var plans = YasPixelScrollPlanner.CreateChunkedPlans(
                ArtifactGridLayout.RowPitchPixels(this.captureSize),
                this.pixelsPerInput,
                this.residualPixels,
                rowsToScroll,
                YasPixelScrollPlanner.ArtifactRowsPerSegment);
            var totalInputs = 0;
            for (var row = 0; row < plans.Count; row++)
            {
                var segment = plans[row];
                await SendScrollInputsAsync(
                    segment.InputCount,
                    direction: -1,
                    YasPixelScrollPlanner.FastInputIntervalMilliseconds);
                totalInputs += segment.InputCount;
                await TaskControl.Delay(
                    YasPixelScrollPlanner.ArtifactSegmentCooldownMilliseconds,
                    this.ct);
                this.residualPixels = segment.ResidualPixels;
                this.logger.LogDebug(
                    "YAS 圣遗物短推进 {Row}/{Rows}：{Inputs} 次输入，残差 {Residual:F2}px",
                    row + 1,
                    rowsToScroll,
                    segment.InputCount,
                    this.residualPixels);
            }
            if (!await WaitForPageSettleAsync())
            {
                throw new InvalidOperationException(
                    $"YAS 圣遗物 {rowsToScroll} 次短推进完成后未连续稳定");
            }
            this.residualPixels = await CorrectPageOffsetAsync(
                getGridItems,
                this.residualPixels);
            this.logger.LogInformation(
                "YAS 圣遗物滚动执行器已按 {Segments} 次短推进确认 {Rows} 行：{Inputs} 次输入，残差 {Residual:F2}px，耗时 {ElapsedMilliseconds}ms",
                plans.Count,
                rowsToScroll,
                totalInputs,
                this.residualPixels,
                timer.ElapsedMilliseconds);
            return new YasScrollReceipt(
                rowsToScroll,
                rowsToScroll,
                Settled: true,
                PhysicallyVerified: true);
        }

        private async Task<double> MeasureScrollPixelsPerInputAsync()
        {
            var rulerRect = ArtifactGridLayout.RulerRect(this.captureSize);
            using var baselineCapture = TaskControl.CaptureToRectArea();
            var baseline = YasPixelScrollPlanner.ReadRuler(
                baselineCapture.SrcMat,
                rulerRect);
            var inputCount = 0;
            try
            {
                for (inputCount = 1;
                     inputCount <= YasPixelScrollPlanner.CalibrationInputLimit;
                     inputCount++)
                {
                    await SendScrollInputsAsync(
                        1,
                        direction: -1,
                        YasPixelScrollPlanner.FirstPageInputIntervalMilliseconds);
                    await TaskControl.Delay(
                        YasPixelScrollPlanner.CalibrationSettleDelayMilliseconds,
                        this.ct);
                    using var capture = TaskControl.CaptureToRectArea();
                    var shifted = YasPixelScrollPlanner.ReadRuler(
                        capture.SrcMat,
                        rulerRect);
                    var pixelShift = YasPixelScrollPlanner.FindRulerShift(
                        baseline,
                        shifted,
                        baseline.Count - 8);
                    if (pixelShift <= 0) continue;

                    var pixelsPerInput = pixelShift / (double)inputCount;
                    this.logger.LogInformation(
                        "YAS 圣遗物 ruler 标定 {Inputs} 次输入移动 {Pixels}px，每次 {PixelsPerInput:F2}px",
                        inputCount,
                        pixelShift,
                        pixelsPerInput);
                    return pixelsPerInput;
                }
                throw new InvalidOperationException(
                    "YAS 圣遗物 ruler 在5次输入内未能标定滚动速度");
            }
            finally
            {
                var inputsToUndo = Math.Min(
                    inputCount,
                    YasPixelScrollPlanner.CalibrationInputLimit);
                if (inputsToUndo > 0)
                {
                    await SendScrollInputsAsync(
                        inputsToUndo,
                        direction: 1,
                        YasPixelScrollPlanner.FirstPageInputIntervalMilliseconds,
                        CancellationToken.None);
                    await TaskControl.Delay(
                        YasPixelScrollPlanner.CalibrationSettleDelayMilliseconds,
                        CancellationToken.None);
                }
            }
        }

        private async Task<bool> WaitForPageSettleAsync()
        {
            await TaskControl.Delay(
                YasPixelScrollPlanner.CalibrationSettleDelayMilliseconds,
                this.ct);
            var rulerRect = ArtifactGridLayout.RulerRect(this.captureSize);
            using var baselineCapture = TaskControl.CaptureToRectArea();
            var previous = YasPixelScrollPlanner.ReadRuler(
                baselineCapture.SrcMat,
                rulerRect);
            var settleTracker = new YasScrollSettleTracker();
            for (var sample = 0;
                 sample < YasPixelScrollPlanner.MaximumPageSettleSamples;
                 sample++)
            {
                await TaskControl.Delay(
                    YasPixelScrollPlanner.PageSettleSampleIntervalMilliseconds,
                    this.ct);
                using var capture = TaskControl.CaptureToRectArea();
                var current = YasPixelScrollPlanner.ReadRuler(
                    capture.SrcMat,
                    rulerRect);
                if (settleTracker.Observe(
                        YasPixelScrollPlanner.IsRulerStable(previous, current)))
                {
                    return true;
                }
                previous = current;
            }
            return false;
        }

        private async Task<double> CorrectPageOffsetAsync(
            Func<Mat, int, IEnumerable<Rect>> getGridItems,
            double theoreticalResidual)
        {
            using var capture = TaskControl.CaptureToRectArea();
            using var grid = new Mat(capture.SrcMat, this.roi);
            var offset = ArtifactGridAlignmentPlanner.VerticalOffsetPixels(
                getGridItems(grid, this.columns),
                this.captureSize,
                this.roi,
                this.columns);
            if (!offset.HasValue)
                throw new InvalidOperationException(
                    "YAS 圣遗物滚动后无法测得网格偏移，拒绝提交分页回执。");

            var correctionThreshold = Math.Max(4, this.pixelsPerInput / 2d);
            if (Math.Abs(offset.Value) <= correctionThreshold)
            {
                return offset.Value;
            }

            var correction = ArtifactGridAlignmentPlanner.CorrectionInputs(
                offset.Value,
                this.pixelsPerInput);
            correction = Math.Clamp(correction, -2, 2);
            if (correction == 0) return offset.Value;
            await SendScrollInputsAsync(
                Math.Abs(correction),
                correction > 0 ? -1 : 1,
                YasPixelScrollPlanner.FastInputIntervalMilliseconds);
            if (!await WaitForPageSettleAsync())
                throw new InvalidOperationException(
                    "YAS 圣遗物页末补偿后未稳定，拒绝提交分页回执。");

            using var correctedCapture = TaskControl.CaptureToRectArea();
            using var correctedGrid = new Mat(correctedCapture.SrcMat, this.roi);
            var correctedOffset = ArtifactGridAlignmentPlanner.VerticalOffsetPixels(
                getGridItems(correctedGrid, this.columns),
                this.captureSize,
                this.roi,
                this.columns);
            if (!correctedOffset.HasValue ||
                Math.Abs(correctedOffset.Value) > correctionThreshold)
                throw new InvalidOperationException(
                    $"YAS 圣遗物页末补偿后仍未对齐：{correctedOffset?.ToString("F1") ?? "unknown"}px。");
            this.logger.LogWarning(
                "YAS 圣遗物页末实际偏移 {Offset:F1}px，补偿 {Inputs} 次输入，修正后 {CorrectedOffset:F1}px",
                offset.Value,
                correction,
                correctedOffset.Value);
            return correctedOffset.Value;
        }

        private async Task SendScrollInputsAsync(
            int inputCount,
            int direction,
            int intervalMilliseconds,
            CancellationToken? cancellationToken = null)
        {
            var effectiveCancellationToken = cancellationToken ?? this.ct;
            using var pacer = new YasScrollInputPacer();
            for (var inputIndex = 0; inputIndex < inputCount; inputIndex++)
            {
                this.input.Mouse.VerticalScroll(direction);
                if (intervalMilliseconds > 0)
                {
                    await pacer.DelayAsync(
                        intervalMilliseconds,
                        effectiveCancellationToken);
                }
            }
        }

        internal static bool HasVerticalMovement(bool phaseDetected, Point2d shift)
        {
            return phaseDetected || Math.Abs(shift.Y) > 1;
        }

        /// <summary>
        /// 判断是否还能继续滚动，如果到底了则只能滚动一丝并很快地回弹
        /// </summary>
        /// <param name="prevGray">先前的灰度图</param>
        /// <param name="nextGray">尝试滚动并等待可能的回弹后的灰度图</param>
        /// <param name="shift">估计的位移</param>
        /// <param name="lowerThreshold">低于下限则可能不存在平移</param>
        /// <param name="upperThreshold">上限用于抵消微小的其他差异，比如高亮图标的呼吸闪烁</param>
        /// <param name="logger"></param>
        /// <returns></returns>
        public static bool IsScrolling(Mat prevGray, Mat nextGray, out Point2d shift, double lowerThreshold = 0.5, double upperThreshold = 0.95, ILogger? logger = null)
        {
            using Mat prev = new Mat();
            prevGray.ConvertTo(prev, MatType.CV_32FC1);
            using Mat next = new Mat();
            nextGray.ConvertTo(next, MatType.CV_32FC1);

            using Mat window = new Mat();
            shift = Cv2.PhaseCorrelate(prev, next, window, out double response);
            // 相位相关性
            //logger?.LogInformation($"response={response:F3}, shift=({shift.X:F2}, {shift.Y:F2})");
            return response > lowerThreshold && response < upperThreshold;
        }
    }
}
