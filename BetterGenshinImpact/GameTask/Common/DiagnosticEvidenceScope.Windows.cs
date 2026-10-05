using System;
using System.Collections.Generic;
using System.Linq;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask.Common;

internal sealed record DiagnosticWindowFrame(Guid WindowId, CaptureFrameStamp Anchor, int RelativeIndex,
    double RelativeSeconds = 0, double BeforeSeconds = 10, double AfterSeconds = 5, string Occurrence = "first");

internal sealed partial class DiagnosticEvidenceScope
{
    private sealed record HistoryFrame(CaptureFrameStamp Source, ImageRegion? Image, long Bytes, string? Missing)
    {
        // 环缓存和末态候选共享不可变像素；仅最后一个所有者释放并扣减预算。
        internal int References = 1;
    }
    private sealed class PendingWindow(string request, string phase, string detail, CaptureFrameStamp anchor, ILogger logger,
        IReadOnlyDictionary<string, string>? fields, Guid? incidentId = null, string occurrence = "first")
    {
        internal readonly Guid Id = incidentId ?? Guid.NewGuid();
        internal readonly string Occurrence = occurrence;
        internal readonly string Request = request, Phase = phase, Detail = detail;
        internal readonly CaptureFrameStamp Anchor = anchor;
        internal readonly ILogger Logger = logger;
        internal readonly IReadOnlyDictionary<string, string>? Fields = fields;
        internal int Next = 1;
        internal double LastAfterSeconds;
    }

    private readonly Queue<HistoryFrame> _history = new();
    private readonly List<PendingWindow> _windows = [];
    private readonly HashSet<(string Request, string Phase)> _windowEvents = [];
    private readonly Dictionary<(string Request, string Phase), Guid> _eventIncidents = new();
    private sealed class StormTail(PendingWindow window)
    {
        internal readonly PendingWindow Window = window;
        internal readonly List<HistoryFrame> Frames = [];
        internal double LastAfterSeconds;
        internal bool Complete;
    }
    private readonly Dictionary<Guid, StormTail> _stormTails = new();
    private readonly int _maxWindows, _maxPendingWindows;
    private CaptureFrameStamp _lastWindowSource;
    private HistoryFrame? _latestHistory;
    private CaptureFrameStamp _lastHistorySample;
    internal int HistoryCloneCount { get; private set; }
    private const double WindowBeforeSeconds = 10;
    private const double WindowAfterSeconds = 5;

    private static double SecondsBetween(CaptureFrameStamp later, CaptureFrameStamp earlier) =>
        (later.CapturedTimestamp - earlier.CapturedTimestamp) / (double)earlier.TimestampFrequency;

    // 唯一生产入口：借用已有帧，绝不截图/OCR/等待writer；未知与重复帧不能填满窗口。
    internal void ObserveExistingFrame(ImageRegion frame)
    {
        lock (_gate)
        {
            if (_closed || !frame.FrameStamp.IsKnown) return;
            var source = frame.FrameStamp;
            if (_lastWindowSource.IsKnown && (source.SessionId != _lastWindowSource.SessionId ||
                                             source.TimestampFrequency != _lastWindowSource.TimestampFrequency))
            {
                // 同一时钟上迟到的旧来源帧不能把已经切换的窗口再切回去。
                if (source.TimestampFrequency == _lastWindowSource.TimestampFrequency &&
                    source.CapturedTimestamp < _lastWindowSource.CapturedTimestamp) return;
                CloseWindows("capture-source-changed");
                ClearHistory();
            }
            else if (_lastWindowSource.IsKnown && !source.IsAfter(_lastWindowSource)) return;
            _lastWindowSource = source;
            foreach (var tail in _stormTails.Values)
            {
                if (tail.Complete || !source.IsAfter(tail.Window.Anchor)) continue;
                var elapsed = SecondsBetween(source, tail.Window.Anchor);
                if (elapsed > WindowAfterSeconds) { tail.Complete = true; continue; }
                if (elapsed - tail.LastAfterSeconds < 1 && elapsed != WindowAfterSeconds) continue;
                var copy = CopyHistory(frame);
                tail.Frames.Add(copy);
                tail.LastAfterSeconds = elapsed;
                tail.Complete = elapsed == WindowAfterSeconds;
            }
            // 后窗口已齐或已越界即交给writer，不把旧故障像素钉到整轮一条龙结束。
            // 否则若干重复故障即可耗尽暂存内存和pending槽，后续终态也无法取得锚点。
            FlushStormTails(completedOnly: true);
            // 先交付后帧，再放入历史；窗口保存的Mat与调用方和环缓存各自独占。
            foreach (var window in _windows.ToArray())
            {
                if (!source.IsAfter(window.Anchor)) continue;
                var elapsed = SecondsBetween(source, window.Anchor);
                if (elapsed > WindowAfterSeconds)
                {
                    if (window.LastAfterSeconds < WindowAfterSeconds)
                        MissingFrame(window.Request, window.Phase, "window-after-boundary-unavailable", window.Logger);
                    _windows.Remove(window);
                    continue;
                }
                if (elapsed - window.LastAfterSeconds >= 1 || elapsed == WindowAfterSeconds)
                {
                    SaveWindowFrame(window, frame, window.Next++);
                    window.LastAfterSeconds = elapsed;
                }
                if (elapsed == WindowAfterSeconds) _windows.Remove(window);
            }
            while (_history.TryPeek(out var oldest) && SecondsBetween(source, oldest.Source) > WindowBeforeSeconds)
                ReleaseHistory(_history.Dequeue());
            if (_latestHistory != null) { ReleaseHistory(_latestHistory); _latestHistory = null; }
            // 给writer、输入前帧和当前帧留余量；高分辨率时降低采样密度，不增加内存上限。
            long bytes;
            try { bytes = checked(frame.SrcMat.Total() * frame.SrcMat.ElemSize()); }
            catch { _latestHistory = new(source, null, 0, "capture-error"); return; }
            var historySlots = Math.Clamp(_maximumMemoryBytes / 3 / Math.Max(1, bytes), 2, 11);
            var sampleSeconds = WindowBeforeSeconds / (historySlots - 1);
            if (!_lastHistorySample.IsKnown || SecondsBetween(source, _lastHistorySample) >= sampleSeconds)
            {
                _history.Enqueue(CopyHistory(frame));
                _lastHistorySample = source;
            }
            else _latestHistory = new(source, null, 0, "anchor-not-sampled:source-pixels-not-retained");
        }
    }

    // 故障调用方仍持有原图时，按需强制保留准确锚点；常态帧不再逐帧Clone。
    internal bool RequestWindowFromFrame(string request, string phase, ImageRegion anchor, string detail, ILogger? logger = null,
        IReadOnlyDictionary<string, string>? fields = null, string? changeKey = null)
    {
        lock (_gate)
        {
            ObserveExistingFrame(anchor);
            if (!_closed && anchor.FrameStamp == _lastWindowSource &&
                !_history.Any(item => item.Source == anchor.FrameStamp && item.Image != null))
            {
                if (_latestHistory != null) ReleaseHistory(_latestHistory);
                _latestHistory = CopyHistory(anchor);
            }
            return RequestWindow(request, phase, anchor.FrameStamp, detail, logger, fields, changeKey);
        }
    }

    internal bool RequestWindow(string request, string phase, CaptureFrameStamp anchor, string detail, ILogger? logger = null,
        IReadOnlyDictionary<string, string>? fields = null, string? changeKey = null)
    {
        lock (_gate)
        {
            if (_accountingCompleted) return false;
            logger ??= _logger;
            request = request.Length > 256 ? request[..256] : request;
            phase = phase.Length > 64 ? phase[..64] : phase;
            detail = detail.Length > 2048 ? detail[..2048] : detail;
            changeKey ??= detail;
            changeKey = changeKey.Length > 512 ? changeKey[..512] : changeKey;
            var snapshot = SnapshotFields(fields);
            if (_eventIncidents.TryGetValue((request, phase), out var existing))
            {
                _duplicateSuppressed++;
                RefreshIncident(_incidents[existing], anchor, detail, changeKey, logger, snapshot);
                return false;
            }
            var terminal = DiagnosticEvidenceStorage.IsTerminalPhase(phase);
            var windowLimit = _maxWindows - (!terminal && _maxWindows > 1 ? 1 : 0);
            var pendingLimit = _maxPendingWindows - (!terminal && _maxPendingWindows > 1 ? 1 : 0);
            var rejection = _closed ? "scope-closed" : !anchor.IsKnown ? "source-unknown" :
                _windowEvents.Count >= windowLimit ? "window-budget" : _windows.Count >= pendingLimit ? "window-queue-full" : null;
            if (rejection != null)
            {
                _missingBefore++;
                MissingFrame(request, phase, rejection, logger);
                return false;
            }
            var pivot = _latestHistory?.Source == anchor ? _latestHistory : _history.FirstOrDefault(item => item.Source == anchor);
            if (pivot == null)
            {
                // 不用相邻样本冒充已经淘汰的真实锚点。
                _missingBefore++;
                MissingFrame(request, phase, "window-anchor-unavailable", logger);
                return false;
            }
            // 分组字段各自有界，避免长Detail截断把后面的关键证据一起丢掉。
            var window = new PendingWindow(request, phase, detail, anchor, logger, snapshot);
            _windowEvents.Add((request, phase));
            _eventIncidents.Add((request, phase), window.Id);
            _incidents.Add(window.Id, new(window.Id, request, phase, anchor) { LastDetail = changeKey });
            if (ReferenceEquals(_logger, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)) _logger = logger;
            var before = _history.Where(item => item.Source.SessionId == anchor.SessionId &&
                item.Source.TimestampFrequency == anchor.TimestampFrequency && anchor.IsAfter(item.Source) &&
                SecondsBetween(anchor, item.Source) <= WindowBeforeSeconds).ToArray();
            if (before.Length == 0 || SecondsBetween(anchor, before[0].Source) < WindowBeforeSeconds)
            {
                _missingBefore++;
                MissingFrame(request, phase, "window-before-unavailable", logger);
            }
            // 所有故障的触发帧先于历史入队，不能让历史填满配额后丢掉决策原图。
            SaveHistory(window, pivot, 0);
            for (var i = 0; i < before.Length; i++) SaveHistory(window, before[i], i - before.Length);
            // 触发可能稍晚于锚点：只补入环内确实已存在的后帧，不重标来源。
            foreach (var item in _history.Where(item => item.Source.IsAfter(anchor) &&
                         SecondsBetween(item.Source, anchor) <= WindowAfterSeconds))
            {
                var elapsed = SecondsBetween(item.Source, anchor);
                if (elapsed - window.LastAfterSeconds < 1 && elapsed != WindowAfterSeconds) continue;
                SaveHistory(window, item, window.Next++);
                window.LastAfterSeconds = elapsed;
            }
            if (window.LastAfterSeconds < WindowAfterSeconds)
                if (SecondsBetween(_lastWindowSource, anchor) > WindowAfterSeconds)
                    MissingFrame(request, phase, "window-after-boundary-unavailable", logger);
                else _windows.Add(window);
            return true;
        }
    }

    internal bool RequestLatestWindow(string request, string phase, string detail, ILogger? logger = null,
        IReadOnlyDictionary<string, string>? fields = null, string? changeKey = null)
    {
        lock (_gate) return RequestWindow(request, phase, _lastWindowSource, detail, logger, fields, changeKey);
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
                window.Logger, window: new(window.Id, window.Anchor, index, SecondsBetween(frame.FrameStamp, window.Anchor), Occurrence: window.Occurrence), fields: window.Fields))
        {
            _incidents[window.Id].Queued++;
            return;
        }
        if (index < 0) _missingBefore++;
        MissingFrame(window.Request, window.Phase, reason, window.Logger);
    }

    private void CloseWindows(string reason)
    {
        FlushStormTails();
        foreach (var window in _windows)
            MissingFrame(window.Request, window.Phase, reason, window.Logger);
        _windows.Clear();
    }

    private static IReadOnlyDictionary<string, string>? SnapshotFields(IReadOnlyDictionary<string, string>? fields)
    {
        if (fields == null) return null;
        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in fields.Take(16))
            snapshot[item.Key.Length > 64 ? item.Key[..64] : item.Key] = item.Value.Length > 512 ? item.Value[..512] : item.Value;
        return snapshot;
    }

    private void RefreshIncident(Incident incident, CaptureFrameStamp anchor, string detail, string changeKey, ILogger logger,
        IReadOnlyDictionary<string, string>? fields)
    {
        if (incident.Occurrences < int.MaxValue) incident.Occurrences++;
        if (_closed || !anchor.IsKnown || anchor.SessionId != incident.LastSource.SessionId || !anchor.IsAfter(incident.LastSource))
        {
            MissingFrame(incident.Request, incident.Phase, "repeat-source-not-new", logger);
            return;
        }
        var changed = changeKey != incident.LastDetail;
        incident.LastSource = anchor;
        incident.LastDetail = changeKey;
        if (changed && incident.Changes < int.MaxValue) incident.Changes++;
        var pivot = _latestHistory?.Source == anchor ? _latestHistory : _history.FirstOrDefault(item => item.Source == anchor);
        if (pivot?.Image == null)
        { MissingFrame(incident.Request, incident.Phase, "repeat-anchor-unavailable", logger); return; }
        var tail = new PendingWindow(incident.Request, incident.Phase, detail, anchor, logger, fields, incident.Id, "last");
        if (_stormTails.ContainsKey(incident.Id) || _stormTails.Count < _maxPendingWindows)
        {
            if (_stormTails.Remove(incident.Id, out var old))
                foreach (var item in old.Frames) ReleaseHistory(item);
            var candidate = new StormTail(tail);
            var retained = _history.Where(item => item.Source.SessionId == anchor.SessionId &&
                item.Source.TimestampFrequency == anchor.TimestampFrequency && anchor.IsAfter(item.Source) &&
                SecondsBetween(anchor, item.Source) <= WindowBeforeSeconds).Append(pivot);
            foreach (var item in retained) { item.References++; candidate.Frames.Add(item); }
            _stormTails[incident.Id] = candidate;
        }
        else MissingFrame(incident.Request, incident.Phase, "repeat-tail-budget", logger);
        if (!changed) return;
        // 变化窗口每incident至多8次；更长风暴仍滚动保留末态并逐事件报告不足。
        if (incident.Changes > 8 || _windows.Count >= _maxPendingWindows)
        { MissingFrame(incident.Request, incident.Phase, "change-window-budget", logger); return; }
        var change = new PendingWindow(incident.Request, incident.Phase, detail, anchor, logger, fields, incident.Id, "change");
        SaveHistory(change, pivot, 0);
        var before = _history.Where(item => item.Source.SessionId == anchor.SessionId && anchor.IsAfter(item.Source) &&
            SecondsBetween(anchor, item.Source) <= WindowBeforeSeconds).ToArray();
        for (var i = 0; i < before.Length; i++) SaveHistory(change, before[i], i - before.Length);
        if (before.Length == 0 || SecondsBetween(anchor, before[0].Source) < WindowBeforeSeconds)
            MissingFrame(incident.Request, incident.Phase, "change-before-unavailable", logger);
        _windows.Add(change);
    }

    private void FlushStormTails(bool completedOnly = false)
    {
        foreach (var entry in _stormTails.ToArray())
        {
            var tail = entry.Value;
            if (completedOnly && !tail.Complete) continue;
            _stormTails.Remove(entry.Key);
            var window = tail.Window;
            var nearby = tail.Frames.Where(item => item.Source != window.Anchor).ToArray();
            // 逐张交付并释放暂存引用，避免先复制整组、最后才释放造成瞬时双倍占用。
            // 锚点仍是真实重复触发帧，不用当前帧冒充。
            foreach (var item in tail.Frames.OrderBy(item => item.Source == window.Anchor ? 0 : 1))
            {
                try
                {
                    SaveHistory(window, item, item.Source == window.Anchor ? 0 :
                        item.Source.IsAfter(window.Anchor) ? window.Next++ : -1);
                }
                finally { ReleaseHistory(item); }
            }
            if (!nearby.Any(item => SecondsBetween(item.Source, window.Anchor) <= -WindowBeforeSeconds))
                MissingFrame(window.Request, window.Phase, "last-before-unavailable", window.Logger);
            if (!nearby.Any(item => SecondsBetween(item.Source, window.Anchor) >= WindowAfterSeconds))
                MissingFrame(window.Request, window.Phase, "last-after-unavailable", window.Logger);
        }
    }

    private void ReleaseHistory(HistoryFrame item)
    {
        if (--item.References != 0) return;
        item.Image?.Dispose();
        _stagedBytes -= item.Bytes;
    }

    private HistoryFrame CopyHistory(ImageRegion frame)
    {
        try
        {
            var bytes = checked(frame.SrcMat.Total() * frame.SrcMat.ElemSize());
            if (frame.SrcMat.Empty()) return new(frame.FrameStamp, null, 0, "empty-frame");
            if (!FitsMemory(bytes)) return new(frame.FrameStamp, null, 0, "memory-budget");
            var owned = new ImageRegion(frame.SrcMat.Clone(), 0, 0) { FrameStamp = frame.FrameStamp };
            HistoryCloneCount++;
            _stagedBytes += bytes;
            return new(frame.FrameStamp, owned, bytes, null);
        }
        catch { return new(frame.FrameStamp, null, 0, "capture-error"); }
    }

    private void ClearHistory()
    {
        while (_history.TryDequeue(out var item)) ReleaseHistory(item);
        if (_latestHistory != null) { ReleaseHistory(_latestHistory); _latestHistory = null; }
        _lastHistorySample = default;
    }
}
