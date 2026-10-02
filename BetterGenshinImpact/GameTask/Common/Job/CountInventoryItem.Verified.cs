using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.GameTask.Model.GameUI;
using Fischless.GameCapture;
using Fischless.WindowsInput;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.Common.Job;

internal partial class CountInventoryItem
{
    private static InventoryScrollObservation ReadPreciousPage(ImageRegion frame, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var tab = frame.Find(ElementRecognition.Get("BagPreciousItemChecked", frame));
        if (!tab.IsExist() || !frame.FrameStamp.IsFresh(TimeProvider.System, TimeSpan.FromSeconds(2)))
            throw new InvalidOperationException("贵重道具分类页或截图来源未确认，停止库存扫描");
        return PreciousInventoryScrollReader.Read(frame.SrcMat);
    }

    private async Task ResetPreciousInventoryToTopAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var frame = TaskControl.CaptureToRectArea();
            var scroll = ReadPreciousPage(frame, ct);
            if (!scroll.Known || scroll.AtTop) return;
            GameCaptureRegion.GameRegion1080PPosMove(1289, (int)Math.Round((scroll.Top + scroll.Bottom) / 2));
            ct.ThrowIfCancellationRequested();
            using var dispatch = new InputDispatchCapture();
            try
            {
                input.Mouse.LeftButtonDown();
                await TaskControl.Delay(60, ct);
                GameCaptureRegion.GameRegion1080PPosMove(1289, 123);
                await TaskControl.Delay(80, ct);
            }
            finally { if (dispatch.HasDispatch) input.Mouse.LeftButtonUp(); }
            await TaskControl.Delay(300, ct);
        }
        // 后续首个扫描帧仍必须证明顶部；拖动次数耗尽不能产生完成证明。
    }

    internal static bool HasCompleteInventoryLayout(IReadOnlyCollection<Rect> rects, Rect roi, int columns, bool atBottom)
    {
        if (rects.Count == 0 || rects.Any(rect => rect.Width <= 0 || rect.Height <= 0 || rect.X < 0 || rect.Y < 0 ||
            rect.Right > roi.Width || rect.Bottom > roi.Height)) return false;
        var cells = GridCell.ClusterToCells(rects, Math.Max(2, (int)(roi.Height * .025))).ToArray();
        var rows = cells.GroupBy(cell => cell.RowNum).OrderBy(group => group.Key).ToArray();
        var firstHeight = rows[0].Average(cell => cell.Rect.Height);
        var lastHeight = rows[^1].Average(cell => cell.Rect.Height);
        if (rows[0].Min(cell => cell.Rect.Top) > firstHeight * .35 ||
            roi.Height - rows[^1].Max(cell => cell.Rect.Bottom) > lastHeight * (atBottom ? .35 : 1.1))
            return false;
        for (var row = 0; row < rows.Length; row++)
        {
            var ordered = rows[row].OrderBy(cell => cell.ColNum).ToArray();
            // A missing trailing cell is not proof of an empty slot, even on the last page.
            if (ordered.Length != columns ||
                !ordered.Select(cell => cell.ColNum).SequenceEqual(Enumerable.Range(0, ordered.Length)) ||
                ordered[0].Rect.Left > roi.Width / (double)columns * .35) return false;
            if (row == 0) continue;
            var previousY = rows[row - 1].Average(cell => cell.Rect.Y);
            var currentY = rows[row].Average(cell => cell.Rect.Y);
            var height = rows[row - 1].Average(cell => cell.Rect.Height);
            if (currentY - previousY < height * .9 || currentY - previousY > height * 1.5) return false;
        }
        return true;
    }

    private sealed class InventoryPageEvidenceObserver(InventoryScanEvidence evidence, GridParams parameters, CancellationToken ct)
    {
        private readonly List<string?> _names = [];
        private CaptureFrameStamp _source;
        private InventoryScrollObservation _scroll;
        private bool _pageReady, _layoutComplete;
        private int _expectedSlots;

        internal void Attach(GridScreen screen)
        {
            screen.OnPageCaptured += frame =>
            {
                _scroll = ReadPreciousPage(frame, ct);
                _source = frame.FrameStamp;
                _names.Clear();
                _pageReady = false;
            };
            screen.OnAfterTurnToNewPage += page =>
            {
                var rects = page.Item2.Select(item => item.Item1).ToArray();
                _expectedSlots = rects.Length;
                _layoutComplete = HasCompleteInventoryLayout(rects, parameters.Roi, parameters.Columns, _scroll.AtBottom);
                _pageReady = true;
            };
            screen.OnBeforeScroll += () =>
            {
                FinishPage();
                using var frame = TaskControl.CaptureToRectArea();
                var current = ReadPreciousPage(frame, ct);
                if (_scroll.Known && current.Known && (Math.Abs(current.Top - _scroll.Top) > 2 || Math.Abs(current.Bottom - _scroll.Bottom) > 2))
                    throw new InvalidOperationException("计数期间背包页面发生移动，不能沿用旧页覆盖证明");
            };
        }

        internal void RecordItem(string? name) => _names.Add(name);

        internal void FinishPage()
        {
            if (!_pageReady) return;
            evidence.ObservePage(_source, _scroll, _names, _layoutComplete && _expectedSlots == _names.Count);
            _names.Clear();
            _pageReady = false;
        }
    }
}
