using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using OpenCvSharp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;

namespace BetterGenshinImpact.GameTask.Common;

internal enum DiagnosticEvidencePriority { Routine, Warning, Error }

internal sealed record DiagnosticEvidence(Guid RunId, int Sequence, string Request, string Phase,
    CaptureFrameStamp Source, string Detail, string? SourceRequest = null, DiagnosticWindowFrame? Window = null,
    DiagnosticEvidencePriority Priority = DiagnosticEvidencePriority.Routine)
{
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.Now;
    public long ObservedTimestamp { get; init; } = Stopwatch.GetTimestamp();
    public long ObservedFrequency => Stopwatch.Frequency;
    public Guid? TaskInstanceId { get; init; } = TaskExecutionScope.DiagnosticId;
    public Guid? ScriptInstanceId { get; init; } = BetterGenshinImpact.Core.Script.ScriptAsyncLifetime.DiagnosticId;
    public string? UiRootId { get; init; } = Ui.UiOperation.Current?.RootId;
    public string? UiOperationId { get; init; } = Ui.UiOperation.Current?.Id;
    public IReadOnlyDictionary<string, string>? Fields { get; init; }
    public string IdentityStatus => TaskInstanceId == null ? "task-scope-unavailable" :
        ScriptInstanceId == null ? "task-bound:not-in-script-context" : "task-and-script-bound";
}

internal sealed partial class DiagnosticEvidenceScope : IAsyncDisposable
{
    private static readonly AsyncLocal<DiagnosticEvidenceScope?> Active = new();
    private readonly DiagnosticEvidenceScope? _previous;
    private readonly object _gate = new();
    private readonly Guid _runId = Guid.NewGuid();
    private readonly LinkedList<(DiagnosticEvidence Evidence, Mat Image, long Bytes)> _queue = new();
    private readonly Channel<byte> _queueWake = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite, AllowSynchronousContinuations = false });
    private readonly int _queueCapacity;
    private readonly Func<DiagnosticEvidence, Mat, Task> _sink;
    private readonly DiagnosticEvidenceStorage? _storage;
    private readonly Task _writer;
    private readonly int? _maxImages;
    private readonly long? _maximumBytes;
    private readonly Dictionary<string, HashSet<string>> _phases = new(StringComparer.Ordinal);
    private readonly Queue<string> _phaseRequestOrder = new();
    private const int MaximumRememberedRequests = 1024; // 仅限去重元数据，不限制保存总量。
    private readonly Dictionary<(string Owner, string Request, string Phase),
        (CaptureFrameStamp Source, string Detail, ILogger Logger, IReadOnlyDictionary<string, string>? Fields)> _frameRequests = new();
    private readonly Dictionary<(string Channel, string Request), (ImageRegion Frame, string Request, string Detail, long Bytes)> _before = new();
    private ILogger _logger = NullLogger.Instance;
    private int _accepted, _dropped, _writeFailures, _missingBefore;
    private int _written, _duplicateSuppressed;
    private readonly Dictionary<string, int> _missingReasons = new(StringComparer.Ordinal);
    private long _bytes;
    private long _stagedBytes;
    private long _queuedBytes;
    private readonly long _maximumMemoryBytes;
    private bool _closed;
    private bool _accountingCompleted;
    private sealed record DrainSummary(Guid RunId, int Accepted, long Bytes, int Dropped, int MissingBefore,
        int WriteFailures, int Written, int DuplicateSuppressed, Dictionary<string, int> MissingReasons);
    private string? _retainedOwner;
    private ImageRegion? _retainedFrame;
    private long _retainedBytes;
    private string? _retainedEpisode;

    internal void EndFrameRequests(string owner, string request)
    {
        lock (_gate)
        {
            foreach (var key in _frameRequests.Keys.Where(key => key.Owner == owner && key.Request == request).ToArray())
                _frameRequests.Remove(key);
            if (_retainedOwner == owner && _retainedEpisode == request) ClearRetainedFrame();
        }
    }

    internal DiagnosticEvidenceScope(Func<DiagnosticEvidence, Mat, Task>? sink = null, int? maxImages = null,
        long? maximumBytes = null, int queueCapacity = 64, long maximumMemoryBytes = 256L * 1024 * 1024,
        int maxWindows = 256, int maxPendingWindows = 8)
    {
        if (maxImages.HasValue) ArgumentOutOfRangeException.ThrowIfLessThan(maxImages.Value, 1, nameof(maxImages));
        if (maximumBytes.HasValue) ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes.Value, 1, nameof(maximumBytes));
        ArgumentOutOfRangeException.ThrowIfLessThan(queueCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumMemoryBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxWindows, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPendingWindows, 1);
        _previous = Active.Value;
        if (_previous != null) throw new InvalidOperationException("已有证据作用域，子任务不能重建它");
        _maxImages = maxImages ?? 8192;
        _maximumBytes = maximumBytes;
        _maximumMemoryBytes = maximumMemoryBytes;
        _maxWindows = maxWindows;
        _maxPendingWindows = maxPendingWindows;
        _queueCapacity = queueCapacity;
        if (sink == null)
            _storage = new DiagnosticEvidenceStorage(Global.Absolute(Path.Combine("log", "evidence")), _runId,
                diagnostic: message => _logger.LogDebug("{Diagnostic}", message), onEvicted: IncidentEvicted);
        _sink = sink ?? WriteFileAsync;
        Active.Value = this;
        // 文件输出不得继承短命run/脚本/Input owner的AsyncLocal。
        if (ExecutionContext.IsFlowSuppressed()) _writer = Task.Run(WriteAsync);
        else { using (ExecutionContext.SuppressFlow()) _writer = Task.Run(WriteAsync); }
    }

    internal static DiagnosticEvidenceScope? Current => Active.Value;
    internal static DiagnosticEvidenceScope? CreateOwned() => Active.Value == null ? new() : null;
    private bool ImageLimitReached => _maxImages is { } limit && _accepted >= limit;
    private bool OrdinaryImageLimitReached => _maxImages is { } limit &&
        _accepted >= limit - Math.Min(64, limit / 4);
    private bool EvidenceImageLimitReached(DiagnosticEvidencePriority priority) => priority switch
    {
        DiagnosticEvidencePriority.Error => ImageLimitReached,
        DiagnosticEvidencePriority.Warning => _maxImages is { } limit && _accepted >= limit - Math.Min(16, limit / 8),
        _ => OrdinaryImageLimitReached
    };
    private int EvidenceQueueLimit(DiagnosticEvidencePriority priority) =>
        _queueCapacity - (priority < DiagnosticEvidencePriority.Error && _queueCapacity > 1 ? 1 : 0);
    private bool FitsByteLimit(long bytes, long replacing = 0, bool terminal = false) =>
        _maximumBytes is not { } limit || bytes <= limit - _bytes - _stagedBytes + replacing -
            (terminal ? 0 : Math.Min(32L * 1024 * 1024, limit / 4));
    private bool FitsMemory(long bytes, long replacing = 0, bool terminal = false) =>
        bytes <= _maximumMemoryBytes - _queuedBytes - _stagedBytes + replacing -
            (terminal ? 0 : Math.Min(32L * 1024 * 1024, _maximumMemoryBytes / 4));

    internal bool RequestFrame(string owner, string request, string phase, CaptureFrameStamp requestedSource,
        string detail, ILogger logger, IReadOnlyDictionary<string, string>? fields = null)
    {
        lock (_gate)
        {
            fields = SnapshotFields(fields);
            if (ReferenceEquals(_logger, NullLogger.Instance)) _logger = logger;
            if (phase == "terminal" && !_closed && _retainedOwner == owner && _retainedFrame is { } retained)
            {
                try
                {
                    if (retained.FrameStamp.SessionId != requestedSource.SessionId)
                    { MissingFrame(request, phase, "capture-source-changed", logger); return false; }
                    var saved = TryCaptureWithReason(retained, request, phase,
                        $"requestedSource={requestedSource.SessionId}/{requestedSource.Sequence}; latest-existing-frame; {detail}", out var reason, logger, fields: fields);
                    if (!saved) MissingFrame(request, phase, reason, logger);
                    return saved;
                }
                finally { ClearRetainedFrame(); }
            }
            var requestLimit = DiagnosticEvidenceStorage.IsTerminalPhase(phase) ? 4 : 3;
            if (_closed || !requestedSource.IsKnown || _frameRequests.Count >= requestLimit || ImageLimitReached)
            {
                MissingFrame(request, phase, _closed ? "scope-closed" : !requestedSource.IsKnown ? "source-unknown" :
                    ImageLimitReached ? "run-budget" : "request-queue-full", logger);
                return false;
            }
            if (_phases.TryGetValue(request, out var seen) && seen.Contains(phase))
            {
                MissingFrame(request, phase, "duplicate-phase", logger);
                return false;
            }
            _frameRequests.TryAdd((owner, request, phase), (requestedSource, detail, logger, fields));
            if (_retainedOwner != owner || _retainedEpisode != request)
            {
                ClearRetainedFrame();
                _retainedOwner = owner;
                _retainedEpisode = request;
            }
            return true;
        }
    }

    // 终态之后可能不再有生产帧；只消费仍排队的这一请求，不重复已有retained证据。
    internal void CapturePendingTerminal(string owner, string request, Func<ImageRegion?> capture)
    {
        (CaptureFrameStamp Source, string Detail, ILogger Logger, IReadOnlyDictionary<string, string>? Fields) pending;
        lock (_gate)
        {
            if (_closed || !_frameRequests.Remove((owner, request, "terminal"), out pending)) return;
        }
        try
        {
            using var frame = capture();
            if (frame == null || !frame.FrameStamp.IsKnown ||
                frame.FrameStamp.SessionId != pending.Source.SessionId || !frame.FrameStamp.IsAfter(pending.Source))
            {
                MissingFrame(request, "terminal", "terminal-source-unavailable-or-changed", pending.Logger);
                return;
            }
            if (!TryCaptureWithReason(frame, request, "terminal", "terminal-single-capture; " + pending.Detail,
                    out var reason, pending.Logger, fields: pending.Fields))
                MissingFrame(request, "terminal", reason, pending.Logger);
        }
        catch (Exception)
        {
            MissingFrame(request, "terminal", "terminal-capture-failed", pending.Logger);
        }
    }

    internal void CaptureRequestedFrames(string owner, ImageRegion frame)
    {
        ObserveExistingFrame(frame);
        lock (_gate)
        {
            // 仅在宿主已经请求搜索取证后保留一张已有帧，终态无需再等生产者。
            // 与技能暂存共用显式字节限制（如有）；最多每500ms复制一次，不增加截图/OCR/落盘频率。
            if (!_closed && !ImageLimitReached && _retainedOwner == owner && frame.FrameStamp.IsKnown &&
                (_retainedFrame == null || frame.FrameStamp.SessionId != _retainedFrame.FrameStamp.SessionId ||
                 frame.FrameStamp.CapturedTimestamp - _retainedFrame.FrameStamp.CapturedTimestamp >= frame.FrameStamp.TimestampFrequency / 2))
            {
                try
                {
                    var bytes = checked(frame.SrcMat.Total() * frame.SrcMat.ElemSize());
                    if (FitsByteLimit(bytes, _retainedBytes) && FitsMemory(bytes, _retainedBytes))
                    {
                        var episode = _retainedEpisode;
                        ClearRetainedFrame();
                        _retainedOwner = owner;
                        _retainedEpisode = episode;
                        var copy = new ImageRegion(frame.SrcMat.Clone(), 0, 0) { FrameStamp = frame.FrameStamp };
                        _retainedFrame = copy;
                        _retainedBytes = bytes;
                        _stagedBytes += bytes;
                    }
                }
                catch { /* 仍可尝试请求帧；保留失败不能影响生产感知。 */ }
            }
            foreach (var key in _frameRequests.Keys.Where(key => key.Owner == owner).ToArray())
            {
                var request = _frameRequests[key];
                if (!frame.FrameStamp.IsKnown || frame.FrameStamp.SessionId == request.Source.SessionId &&
                    !frame.FrameStamp.IsAfter(request.Source)) continue;
                _frameRequests.Remove(key);
                if (frame.FrameStamp.SessionId != request.Source.SessionId)
                {
                    MissingFrame(key.Request, key.Phase, "capture-source-changed", request.Logger);
                    continue;
                }
                var detail = $"requestedSource={request.Source.SessionId}/{request.Source.Sequence}; next-existing-frame; {request.Detail}";
                if (!TryCaptureWithReason(frame, key.Request, key.Phase, detail, out var reason, request.Logger, fields: request.Fields))
                    MissingFrame(key.Request, key.Phase, reason, request.Logger);
            }
        }
    }

    private void MissingFrame(string request, string phase, string reason, ILogger logger, int count = 1)
    {
        lock (_gate)
        {
            if (_accountingCompleted) return;
            if (ReferenceEquals(_logger, NullLogger.Instance)) _logger = logger;
            if (reason == "duplicate-phase") { _duplicateSuppressed++; return; }
            // 异常类型不是原因维度，避免任意文本让计数元数据无界增长。
            if (reason.StartsWith("capture-error:", StringComparison.Ordinal)) reason = "capture-error";
            _dropped += count;
            CountReason("capture:" + reason, count);
            IncidentMissing(request, phase, "capture:" + reason, count);
        }
        try { logger.LogDebug("EVIDENCE_CAPTURE_MISSING run={Run} request={Request} phase={Phase} reason={Reason}",
            _runId, request, phase, reason); } catch { }
    }

    // 调用者持有_gate；写盘失败与采集缺失分桶，accepted不冒充已落盘。
    private void CountReason(string reason, int count = 1) => _missingReasons[reason] = _missingReasons.GetValueOrDefault(reason) + count;

    private void ClearRetainedFrame()
    {
        _retainedFrame?.Dispose();
        _retainedFrame = null;
        _retainedOwner = null;
        _retainedEpisode = null;
        _stagedBytes -= _retainedBytes;
        _retainedBytes = 0;
    }

    internal bool TryCapture(ImageRegion frame, string request, string phase, string detail, ILogger? logger = null,
        string? sourceRequest = null, DiagnosticEvidencePriority priority = DiagnosticEvidencePriority.Routine,
        IReadOnlyDictionary<string, string>? fields = null)
    {
        var captured = TryCaptureWithReason(frame, request, phase, detail, out var reason, logger, sourceRequest,
            priority: priority, fields: SnapshotFields(fields));
        if (!captured) MissingFrame(request, phase, reason, logger ?? _logger);
        return captured;
    }

    private bool TryCaptureWithReason(ImageRegion frame, string request, string phase, string detail, out string reason,
        ILogger? logger = null, string? sourceRequest = null, DiagnosticWindowFrame? window = null,
        DiagnosticEvidencePriority priority = DiagnosticEvidencePriority.Routine,
        IReadOnlyDictionary<string, string>? fields = null)
    {
        reason = "accepted";
        Mat? owned = null;
        try
        {
            lock (_gate)
            {
                if (_closed) { reason = "scope-closed"; return false; }
                var terminal = DiagnosticEvidenceStorage.IsTerminalPhase(phase) || priority >= DiagnosticEvidencePriority.Warning ||
                    window != null && phase != "selection-before-submit";
                if (DiagnosticEvidenceStorage.IsTerminalPhase(phase)) priority = DiagnosticEvidencePriority.Error;
                else if (terminal && priority == DiagnosticEvidencePriority.Routine) priority = DiagnosticEvidencePriority.Warning;
                if (_storage?.BudgetExhausted == true && !terminal) { reason = "run-disk-budget"; return false; }
                if (!frame.FrameStamp.IsKnown) { reason = "source-unknown"; return false; }
                if (frame.SrcMat.Empty()) { reason = "empty-frame"; return false; }
                if (logger != null && ReferenceEquals(_logger, NullLogger.Instance)) _logger = logger;
                request = request.Length > 256 ? request[..256] : request;
                phase = phase.Length > 64 ? phase[..64] : phase;
                _phases.TryGetValue(request, out var seen);
                if (window == null && seen != null && seen.Contains(phase))
                { reason = "duplicate-phase"; return false; }
                var bytes = checked(frame.SrcMat.Total() * frame.SrcMat.ElemSize());
                if (EvidenceImageLimitReached(priority) || !FitsByteLimit(bytes, terminal: terminal))
                { reason = "run-image-or-byte-budget"; return false; }
                if (terminal) ReclaimQueuedLowerPriority(bytes, priority);
                if (!FitsMemory(bytes, terminal: terminal)) { reason = "memory-budget"; return false; }
                if (_queue.Count >= EvidenceQueueLimit(priority))
                { reason = "writer-queue-full"; return false; }
                owned = frame.SrcMat.Clone();
                var evidence = new DiagnosticEvidence(_runId, _accepted + 1, request, phase, frame.FrameStamp,
                    detail.Length > 2048 ? detail[..2048] : detail, sourceRequest, window, priority) { Fields = fields };
                if (window != null && _incidents.TryGetValue(window.WindowId, out var incident))
                    evidence = evidence with { TaskInstanceId = incident.TaskInstanceId, ScriptInstanceId = incident.ScriptInstanceId,
                        UiRootId = incident.UiRootId, UiOperationId = incident.UiOperationId };
                _queue.AddLast((evidence, owned, bytes));
                _queueWake.Writer.TryWrite(0);
                owned = null; // 从这里起只有writer拥有并释放图像。
                _accepted++;
                _bytes += bytes;
                _queuedBytes += bytes;
                if (window != null) return true;
                if (!_phases.TryGetValue(request, out seen))
                {
                    if (_phaseRequestOrder.Count >= MaximumRememberedRequests)
                        _phases.Remove(_phaseRequestOrder.Dequeue());
                    _phaseRequestOrder.Enqueue(request);
                    _phases[request] = seen = new(StringComparer.Ordinal);
                }
                seen.Add(phase);
                return true;
            }
        }
        catch (Exception error) { reason = "capture-error:" + error.GetType().Name; return false; }
        finally { owned?.Dispose(); }
    }

    private static (string Channel, string Request) BeforeKey(string channel, string request) =>
        (channel, channel == "path" ? "" : request);

    internal bool RememberBefore(ImageRegion frame, string channel, string request, string detail)
    {
        ImageRegion? owned = null;
        try
        {
            lock (_gate)
            {
                if (_closed || !frame.FrameStamp.IsKnown)
                { MissingFrame(request, "before-" + channel, _closed ? "scope-closed" : "source-unknown", _logger); return false; }
                var key = BeforeKey(channel, request);
                if (ImageLimitReached || !_before.ContainsKey(key) && _before.Count >= 2)
                { MissingFrame(request, "before-" + channel, ImageLimitReached ? "run-budget" : "before-slots-full", _logger); return false; }
                var bytes = checked(frame.SrcMat.Total() * frame.SrcMat.ElemSize());
                if (!FitsByteLimit(bytes, _before.GetValueOrDefault(key).Bytes))
                { MissingFrame(request, "before-" + channel, "run-byte-budget", _logger); return false; }
                if (!FitsMemory(bytes, _before.GetValueOrDefault(key).Bytes))
                { MissingFrame(request, "before-" + channel, "memory-budget", _logger); return false; }
                RemoveBefore(key);
                owned = new ImageRegion(frame.SrcMat.Clone(), 0, 0) { FrameStamp = frame.FrameStamp };
                _before[key] = (owned, request, detail, bytes);
                _stagedBytes += bytes;
                owned = null;
                return true;
            }
        }
        catch { MissingFrame(request, "before-" + channel, "capture-error", _logger); return false; }
        finally { owned?.Dispose(); }
    }

    internal void CaptureFault(ImageRegion frame, string channel, string request, string phase, string detail, ILogger? logger = null,
        bool allowPreviousRequest = false)
    {
        lock (_gate)
        {
            if (_closed) return;
            var normalizedRequest = request.Length > 256 ? request[..256] : request;
            var normalizedPhase = phase.Length > 64 ? phase[..64] : phase;
            if (_phases.TryGetValue(normalizedRequest, out var seen) && seen.Contains(normalizedPhase))
            {
                _duplicateSuppressed++;
                return;
            }
            var key = BeforeKey(channel, request);
            if (_before.TryGetValue(key, out var before) && (before.Request == request || allowPreviousRequest))
            {
                if (!TryCaptureWithReason(before.Frame, request, "before-" + channel, before.Detail, out var reason, logger, before.Request,
                        priority: DiagnosticEvidencePriority.Warning))
                {
                    if (reason != "duplicate-phase") _missingBefore++;
                    MissingFrame(request, "before-" + channel, reason, logger ?? _logger);
                }
                RemoveBefore(key);
            }
            else
            {
                _missingBefore++;
                MissingFrame(request, "before-" + channel, "before-unavailable", logger ?? _logger);
            }
            TryCapture(frame, request, phase, detail, logger, priority: DiagnosticEvidencePriority.Warning);
        }
    }

    internal void ForgetBefore(string channel, string? request = null)
    {
        lock (_gate)
        {
            foreach (var key in _before.Keys.Where(key => key.Channel == channel &&
                         (request == null || _before[key].Request == request)).ToArray()) RemoveBefore(key);
        }
    }

    internal void AppendBeforeDetail(string channel, string request, string detail)
    {
        lock (_gate)
        {
            if (_closed || !_before.TryGetValue(BeforeKey(channel, request), out var before) || before.Request != request) return;
            var combined = before.Detail + "; " + detail;
            _before[BeforeKey(channel, request)] = (before.Frame, before.Request,
                combined.Length > 2048 ? combined[..2048] : combined, before.Bytes);
        }
    }

    private void RemoveBefore((string Channel, string Request) key)
    {
        if (!_before.Remove(key, out var before)) return;
        _stagedBytes -= before.Bytes;
        before.Frame.Dispose();
    }

    private async Task WriteAsync()
    {
        try { _storage?.Initialize(); }
        catch (Exception error) { try { _logger.LogWarning(error, "EVIDENCE_STORAGE_UNAVAILABLE run={Run}", _runId); } catch { } }
        await foreach (var _ in _queueWake.Reader.ReadAllAsync().ConfigureAwait(false))
        while (TryTakeQueued(out var item))
        {
            try { await _sink(item.Evidence, item.Image).ConfigureAwait(false); Interlocked.Increment(ref _written); IncidentWritten(item.Evidence); }
            catch (Exception error)
            {
                lock (_gate)
                {
                    _writeFailures++;
                    var reason = error is DiagnosticEvidenceBudgetException ? "write:run-disk-budget" : "write:failed";
                    CountReason(reason);
                    IncidentMissing(item.Evidence.Request, item.Evidence.Phase, reason, 1);
                }
                try { _logger.LogWarning(error, "EVIDENCE_WRITE_FAILED run={Run} sequence={Sequence}", _runId, item.Evidence.Sequence); } catch { }
            }
            finally { item.Image.Dispose(); lock (_gate) _queuedBytes -= item.Bytes; }
        }
        DrainSummary summary;
        object[] incidents;
        lock (_gate)
        {
            _accountingCompleted = true;
            summary = new(_runId, _accepted, _bytes, _dropped, _missingBefore, _writeFailures, _written,
                _duplicateSuppressed, new(_missingReasons, StringComparer.Ordinal));
            incidents = IncidentSummaries();
        }
        try
        {
            if (_storage != null)
                await _storage.CompleteAsync(new { summary.RunId, accepted = summary.Accepted, written = summary.Written,
                    dropped = summary.Dropped, missingBefore = summary.MissingBefore, writeFailures = summary.WriteFailures,
                    duplicateSuppressed = summary.DuplicateSuppressed, missingReasons = summary.MissingReasons,
                    incidents, rejectedIncidents = _rejectedIncidents.Values, rejectedIdentityOverflow = _rejectedIdentityOverflow,
                    identities = _identities, identityDrops = _identityDrops }).ConfigureAwait(false);
        }
        catch (Exception error) { try { _logger.LogWarning(error, "EVIDENCE_SUMMARY_FAILED run={Run}", _runId); } catch { } }
        finally { _storage?.Dispose(); }
        LogIncidentSummaries(incidents);
        try { _logger.LogDebug("EVIDENCE_DRAINED run={Run} accepted={Accepted} bytes={Bytes} dropped={Dropped} missingBefore={MissingBefore} writeFailures={Failures} imageLimit={ImageLimit} byteLimit={ByteLimit} written={Written} duplicateSuppressed={DuplicateSuppressed} missingReasons={MissingReasons}",
            summary.RunId, summary.Accepted, summary.Bytes, summary.Dropped, summary.MissingBefore, summary.WriteFailures,
            _maxImages?.ToString() ?? "unlimited", _maximumBytes?.ToString() ?? "unlimited", summary.Written, summary.DuplicateSuppressed,
            JsonConvert.SerializeObject(summary.MissingReasons)); } catch { }
    }

    private async Task WriteFileAsync(DiagnosticEvidence evidence, Mat image)
    {
        var directory = Global.Absolute(Path.Combine("log", "evidence", _runId.ToString("N")));
        var name = Path.Combine(directory, $"evidence-{evidence.Sequence:D4}");
        await _storage!.WriteAsync(evidence, image).ConfigureAwait(false);
        try { _logger.LogDebug("EVIDENCE_SAVED run={Run} request={Request} phase={Phase} source={Session}/{Sequence} metadata={File} imageReference=ImageFile",
            _runId, evidence.Request, evidence.Phase, evidence.Source.SessionId, evidence.Source.Sequence, name + ".json"); } catch { }
    }

    public ValueTask DisposeAsync()
    {
        if (ReferenceEquals(Active.Value, this)) Active.Value = _previous;
        lock (_gate)
        {
            if (!_closed)
            {
                FlushStormTails();
                _closed = true;
                foreach (var pending in _frameRequests)
                    MissingFrame(pending.Key.Request, pending.Key.Phase, "no-next-frame-before-run-end", pending.Value.Logger);
                _frameRequests.Clear();
                CloseWindows("no-next-frame-before-run-end");
                ClearHistory();
                ClearRetainedFrame();
                foreach (var before in _before.Values) before.Frame.Dispose();
                _before.Clear();
                _phases.Clear();
                _phaseRequestOrder.Clear();
                _stagedBytes = 0;
                _queueWake.Writer.TryComplete();
            }
        }
        return new(_writer);
    }

    private bool TryTakeQueued(out (DiagnosticEvidence Evidence, Mat Image, long Bytes) item)
    {
        lock (_gate)
        {
            if (_queue.First == null) { item = default; return false; }
            item = _queue.First.Value;
            _queue.RemoveFirst();
            return true;
        }
    }

    private void ReclaimQueuedLowerPriority(long incomingBytes, DiagnosticEvidencePriority incomingPriority)
    {
        // 先低等级，再同等级最旧；Error不能被Warning替换，writer已持有的Mat不能回收。
        for (var priority = DiagnosticEvidencePriority.Routine; priority < incomingPriority; priority++)
        {
            var node = _queue.First;
            while (node != null && (!FitsMemory(incomingBytes, terminal: true) || _queue.Count >= EvidenceQueueLimit(incomingPriority)))
            {
                var next = node.Next;
                var item = node.Value;
                if (item.Evidence.Priority == priority)
                {
                    _queue.Remove(node);
                    _queuedBytes -= item.Bytes;
                    item.Image.Dispose();
                    MissingFrame(item.Evidence.Request, item.Evidence.Phase,
                        priority == DiagnosticEvidencePriority.Routine ? "routine-reclaimed-for-incident" : "warning-reclaimed-for-error", _logger);
                }
                node = next;
            }
        }
    }
}
