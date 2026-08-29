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
        private readonly int totalItems;
        private readonly int visibleRows;
        private readonly int fastScrollRows;
        private readonly Size captureSize;
        private int scrolledRows;
        private int calibratedRows;
        private double averageInputsPerRow;
        private Vec3b? initialFlagColor;

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
            this.totalItems = @params.TotalItems;
            this.visibleRows = @params.VisibleRows;
            this.fastScrollRows = @params.FastScrollRows;
            this.captureSize = @params.CaptureSize;
        }

        internal async Task<bool> TryVerticalScollDown(Func<Mat, int, IEnumerable<Rect>> GetGridItems)
        {
            if (this.fastScroll)
            {
                return await TryVerticalScrollDownFast(GetGridItems);
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

        private async Task<bool> TryVerticalScrollDownFast(
            Func<Mat, int, IEnumerable<Rect>> getGridItems)
        {
            _ = getGridItems;
            if (this.totalItems <= 0 || this.visibleRows <= 0 || this.fastScrollRows <= 0)
            {
                throw new InvalidOperationException("圣遗物快速滚动缺少背包总数或可见行配置");
            }

            var totalRows = (int)Math.Ceiling(this.totalItems / (double)this.columns);
            var rowsToScroll = ArtifactRowScrollPlanner.RowsToScroll(
                totalRows,
                this.visibleRows,
                this.scrolledRows);
            if (rowsToScroll <= 0)
            {
                this.logger.LogInformation("YAS 圣遗物滚轮翻页到底");
                return false;
            }

            var timer = Stopwatch.StartNew();
            SystemControl.ActivateWindow();
            EnsureInitialFlagColor();
            var targetRow = this.scrolledRows + rowsToScroll;
            if (this.calibratedRows >= this.visibleRows)
            {
                await ScrollRowsFastAsync(rowsToScroll);
            }
            else
            {
                for (var row = 0; row < rowsToScroll; row++)
                {
                    var inputCount = await ScrollOneRowAsync(
                        row + 1,
                        rowsToScroll);
                    this.averageInputsPerRow =
                        (this.averageInputsPerRow * this.calibratedRows + inputCount) /
                        (this.calibratedRows + 1);
                    this.calibratedRows++;
                }
            }

            this.scrolledRows = targetRow;
            this.logger.LogInformation(
                "YAS 圣遗物滚轮已推进 {Rows} 行，累计 {ScrolledRows} 行，耗时 {ElapsedMilliseconds}ms",
                rowsToScroll,
                this.scrolledRows,
                timer.ElapsedMilliseconds);
            return true;
        }

        private void EnsureInitialFlagColor()
        {
            if (this.initialFlagColor.HasValue) return;

            var flagPosition = ArtifactGridLayout.ScrollFlagPosition(this.captureSize);
            using var initialCapture = TaskControl.CaptureToRectArea();
            this.initialFlagColor = ArtifactGridLayout.ReadBgr(
                initialCapture.SrcMat,
                flagPosition);
        }

        private async Task<int> ScrollOneRowAsync(
            int currentRow,
            int targetRows)
        {
            var flagPosition = ArtifactGridLayout.ScrollFlagPosition(this.captureSize);
            var detector = new ArtifactRowScrollDetector(
                this.initialFlagColor!.Value);

            for (var attempt = 1; attempt <= 25; attempt++)
            {
                this.input.Mouse.VerticalScroll(-1);
                await TaskControl.Delay(80, this.ct);
                using var capture = TaskControl.CaptureToRectArea();
                var color = ArtifactGridLayout.ReadBgr(capture.SrcMat, flagPosition);
                if (detector.Observe(color))
                {
                    this.logger.LogDebug(
                        "YAS 圣遗物滚轮第 {CurrentRow}/{TargetRows} 行对齐成功，共 {Attempts} 次滚轮输入",
                        currentRow,
                        targetRows,
                        attempt);
                    return attempt;
                }
            }

            throw new InvalidOperationException(
                $"YAS 圣遗物第 {currentRow}/{targetRows} 行滚动对齐超时");
        }

        private async Task ScrollRowsFastAsync(int rowsToScroll)
        {
            var inputCount = ArtifactRowScrollPlanner.EstimateInputCount(
                this.averageInputsPerRow,
                rowsToScroll);
            for (var inputIndex = 0; inputIndex < inputCount; inputIndex++)
            {
                this.input.Mouse.VerticalScroll(-1);
            }
            await TaskControl.Delay(80, this.ct);

            var flagPosition = ArtifactGridLayout.ScrollFlagPosition(this.captureSize);
            for (var attempt = 0; attempt <= 10; attempt++)
            {
                using var capture = TaskControl.CaptureToRectArea();
                var color = ArtifactGridLayout.ReadBgr(capture.SrcMat, flagPosition);
                if (ArtifactRowScrollDetector.IsNear(
                        this.initialFlagColor!.Value,
                        color))
                {
                    this.logger.LogDebug(
                        "YAS 圣遗物快速推进 {Rows} 行完成，预估输入 {InputCount} 次、补齐 {AlignInputs} 次",
                        rowsToScroll,
                        inputCount,
                        attempt);
                    return;
                }

                if (attempt == 10) break;
                this.input.Mouse.VerticalScroll(-1);
                await TaskControl.Delay(80, this.ct);
            }

            throw new InvalidOperationException(
                $"YAS 圣遗物快速推进 {rowsToScroll} 行后连续 10 次未能对齐");
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
