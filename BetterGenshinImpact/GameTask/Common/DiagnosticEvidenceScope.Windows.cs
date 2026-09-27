using System;
using System.Collections.Generic;
using System.Linq;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask.Common;

internal sealed record DiagnosticWindowFrame(Guid WindowId, CaptureFrameStamp Anchor, int RelativeIndex);

internal sealed partial class DiagnosticEvidenceScope
{
    private sealed record HistoryFrame(CaptureFrameStamp Source, ImageRegion? Image, long Bytes, string? Missing);
    private sealed class PendingWindow(string request, string phase, string detail, CaptureFrameStamp anchor, ILogger logger)
    {
        internal readonly Guid Id = Guid.NewGuid();
        internal readonly string Request = request, Phase = phase, Detail = detail;
        internal readonly CaptureFrameStamp Anchor = anchor;
        internal readonly ILogger Logger = logger;
        internal int Next = 1;
    }

    private readonly Queue<HistoryFrame> _history = new();
    private readonly List<PendingWindow> _windows = [];
    private readonly HashSet<(string Request, string Phase)> _windowEvents = [];
    private readonly int _maxWindows, _maxPendingWindows;
    private CaptureFrameStamp _lastWindowSource;

    // 唯一生产入口：借用已有帧，绝不截图/OCR/等待writer；未知与重复帧不能填满窗口。
    internal void ObserveExistingFrame(ImageRegion frame)
    {
        lock (_gate)
        {
            if (_closed || !frame.FrameStamp.IsKnown) return;
            var source = frame.FrameStamp;
            if (_lastWindowSource.IsKnown && source.SessionId != _lastWindowSource.SessionId)
            {
                // 同一时钟上迟到的旧来源帧不能把已经切换的窗口再切回去。
                if (source.TimestampFrequency == _lastWindowSource.TimestampFrequency &&
                    source.CapturedTimestamp < _lastWindowSource.CapturedTimestamp) return;
                CloseWindows("capture-source-changed");
                ClearHistory();
            }
            else if (_lastWindowSource.IsKnown && !source.IsAfter(_lastWindowSource)) return;
            _lastWindowSource = source;
            // 先交付后帧，再放入历史；窗口保存的Mat与调用方和环缓存各自独占。
            foreach (var window in _windows.ToArray())
            {
                if (!source.IsAfter(window.Anchor)) continue;
                SaveWindowFrame(window, frame, window.Next++);
                if (window.Next > 10) _windows.Remove(window);
            }
            while (_history.Count >= 11) ReleaseHistory(_history.Dequeue());
            ImageRegion? owned = null;
            long bytes = 0;
            string? reason = null;
            try
            {
                bytes = checked(frame.SrcMat.Total() * frame.SrcMat.ElemSize());
                if (frame.SrcMat.Empty()) reason = "empty-frame";
                else if (!FitsMemory(bytes)) reason = "memory-budget";
                else owned = new ImageRegion(frame.SrcMat.Clone(), 0, 0) { FrameStamp = source };
            }
            catch { reason = "capture-error"; }
            if (owned != null) _stagedBytes += bytes;
            _history.Enqueue(new(source, owned, owned == null ? 0 : bytes, reason));
        }
    }

    internal bool RequestWindow(string request, string phase, CaptureFrameStamp anchor, string detail, ILogger? logger = null)
    {
        lock (_gate)
        {
            if (_accountingCompleted) return false;
            logger ??= _logger;
            request = request.Length > 256 ? request[..256] : request;
            phase = phase.Length > 64 ? phase[..64] : phase;
            detail = detail.Length > 2048 ? detail[..2048] : detail;
            if (_windowEvents.Contains((request, phase))) { _duplicateSuppressed++; return false; }
            var terminal = DiagnosticEvidenceStorage.IsTerminalPhase(phase);
            var windowLimit = _maxWindows - (!terminal && _maxWindows > 1 ? 1 : 0);
            var pendingLimit = _maxPendingWindows - (!terminal && _maxPendingWindows > 1 ? 1 : 0);
            var rejection = _closed ? "scope-closed" : !anchor.IsKnown ? "source-unknown" :
                _windowEvents.Count >= windowLimit ? "window-budget" : _windows.Count >= pendingLimit ? "window-queue-full" : null;
            if (rejection != null)
            {
                _missingBefore += 10;
                MissingFrame(request, phase, rejection, logger, 21);
                return false;
            }
            _windowEvents.Add((request, phase));
            var pivot = _history.FirstOrDefault(item => item.Source == anchor);
            if (pivot == null)
            {
                // 锚点已不在有界历史时，无法证明剩余帧紧邻该锚点，不能伪造RelativeIndex。
                _missingBefore += 10;
                MissingFrame(request, phase, "window-anchor-unavailable", logger, 21);
                return false;
            }
            var window = new PendingWindow(request, phase, detail, anchor, logger);
            var before = _history.Where(item => item.Source.SessionId == anchor.SessionId && anchor.IsAfter(item.Source)).TakeLast(10).ToArray();
            var missing = 10 - before.Length;
            if (missing > 0)
            {
                _missingBefore += missing;
                MissingFrame(request, phase, "window-before-unavailable", logger, missing);
            }
            if (terminal) SaveHistory(window, pivot, 0);
            for (var i = 0; i < before.Length; i++) SaveHistory(window, before[i], i - before.Length);
            if (!terminal) SaveHistory(window, pivot, 0);
            // 触发可能稍晚于锚点：只补入环内确实已存在的后帧，不重标来源。
            foreach (var item in _history.Where(item => item.Source.IsAfter(anchor)).Take(10)) SaveHistory(window, item, window.Next++);
            if (window.Next <= 10)
            {
                if (_lastWindowSource.IsKnown && _lastWindowSource.SessionId != anchor.SessionId)
                    MissingFrame(request, phase, "capture-source-changed", logger, 11 - window.Next);
                else _windows.Add(window);
            }
            return true;
        }
    }

    internal bool RequestLatestWindow(string request, string phase, string detail, ILogger? logger = null)
    {
        lock (_gate) return RequestWindow(request, phase, _lastWindowSource, detail, logger);
    }

    private void SaveHistory(PendingWindow window, HistoryFrame item, int index)
    {
        if (item.Image != null) SaveWindowFrame(window, item.Image, index);
        else
        {
            if (index < 0) _missingBefore++;
            MissingFrame(window.Request, window.Phase, item.Missing ?? "window-frame-unavailable", window.Logger);
        }
    }

    private void SaveWindowFrame(PendingWindow window, ImageRegion frame, int index)
    {
        if (TryCaptureWithReason(frame, window.Request, window.Phase, window.Detail, out var reason,
                window.Logger, window: new(window.Id, window.Anchor, index))) return;
        if (index < 0) _missingBefore++;
        MissingFrame(window.Request, window.Phase, reason, window.Logger);
    }

    private void CloseWindows(string reason)
    {
        foreach (var window in _windows)
            MissingFrame(window.Request, window.Phase, reason, window.Logger, 11 - window.Next);
        _windows.Clear();
    }

    private void ReleaseHistory(HistoryFrame item)
    {
        item.Image?.Dispose();
        _stagedBytes -= item.Bytes;
    }

    private void ClearHistory()
    {
        while (_history.TryDequeue(out var item)) ReleaseHistory(item);
    }
}
