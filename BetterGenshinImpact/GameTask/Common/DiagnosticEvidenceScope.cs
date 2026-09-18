using System;
using System.Collections.Generic;
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

internal sealed record DiagnosticEvidence(Guid RunId, int Sequence, string Request, string Phase,
    CaptureFrameStamp Source, string Detail, string? SourceRequest = null);

internal sealed class DiagnosticEvidenceScope : IAsyncDisposable
{
    private static readonly AsyncLocal<DiagnosticEvidenceScope?> Active = new();
    private readonly DiagnosticEvidenceScope? _previous;
    private readonly object _gate = new();
    private readonly Guid _runId = Guid.NewGuid();
    private readonly Channel<(DiagnosticEvidence Evidence, Mat Image)> _queue = Channel.CreateBounded<(DiagnosticEvidence, Mat)>(
        new BoundedChannelOptions(4) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Func<DiagnosticEvidence, Mat, Task> _sink;
    private readonly Task _writer;
    private readonly int _maxImages;
    private readonly long _maximumBytes;
    private readonly Dictionary<string, HashSet<string>> _phases = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Owner, string Request, string Phase),
        (CaptureFrameStamp Source, string Detail, ILogger Logger)> _frameRequests = new();
    private readonly Dictionary<(string Channel, string Request), (ImageRegion Frame, string Request, string Detail, long Bytes)> _before = new();
    private ILogger _logger = NullLogger.Instance;
    private int _accepted, _dropped, _writeFailures, _missingBefore;
    private long _bytes;
    private long _stagedBytes;
    private bool _closed;
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

    internal DiagnosticEvidenceScope(Func<DiagnosticEvidence, Mat, Task>? sink = null, int maxImages = 16,
        long maximumBytes = 128L * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxImages, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);
        _previous = Active.Value;
        if (_previous != null) throw new InvalidOperationException("已有证据预算，子任务不能重建并刷新它");
        _maxImages = maxImages;
        _maximumBytes = maximumBytes;
        _sink = sink ?? WriteFileAsync;
        Active.Value = this;
        // 文件输出不得继承短命run/脚本/Input owner的AsyncLocal。
        if (ExecutionContext.IsFlowSuppressed()) _writer = Task.Run(WriteAsync);
        else { using (ExecutionContext.SuppressFlow()) _writer = Task.Run(WriteAsync); }
    }

    internal static DiagnosticEvidenceScope? Current => Active.Value;
    internal static DiagnosticEvidenceScope? CreateOwned() => Active.Value == null ? new() : null;

    internal bool RequestFrame(string owner, string request, string phase, CaptureFrameStamp requestedSource,
        string detail, ILogger logger)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_logger, NullLogger.Instance)) _logger = logger;
            if (phase == "terminal" && !_closed && _retainedOwner == owner && _retainedFrame is { } retained)
            {
                try
                {
                    if (retained.FrameStamp.SessionId != requestedSource.SessionId)
                    { MissingFrame(request, phase, "capture-source-changed", logger); return false; }
                    var saved = TryCaptureWithReason(retained, request, phase,
                        $"requestedSource={requestedSource.SessionId}/{requestedSource.Sequence}; latest-existing-frame; {detail}", out var reason, logger);
                    if (!saved) MissingFrame(request, phase, reason, logger);
                    return saved;
                }
                finally { ClearRetainedFrame(); }
            }
            if (_closed || !requestedSource.IsKnown || _frameRequests.Count >= 4 || _accepted >= _maxImages)
            {
                MissingFrame(request, phase, _closed ? "scope-closed" : !requestedSource.IsKnown ? "source-unknown" :
                    _accepted >= _maxImages ? "run-budget" : "request-queue-full", logger);
                return false;
            }
            if (_phases.TryGetValue(request, out var seen) && (seen.Contains(phase) || seen.Count >= 3))
            {
                MissingFrame(request, phase, "duplicate-or-request-budget", logger);
                return false;
            }
            _frameRequests.TryAdd((owner, request, phase), (requestedSource, detail, logger));
            if (_retainedOwner != owner || _retainedEpisode != request)
            {
                ClearRetainedFrame();
                _retainedOwner = owner;
                _retainedEpisode = request;
            }
            return true;
        }
    }

    internal void CaptureRequestedFrames(string owner, ImageRegion frame)
    {
        lock (_gate)
        {
            // 仅在宿主已经请求搜索取证后保留一张已有帧，终态无需再等生产者。
            // 与技能暂存共用原字节预算；最多每500ms复制一次，不增加截图/OCR/落盘频率。
            if (!_closed && _accepted < _maxImages && _retainedOwner == owner && frame.FrameStamp.IsKnown &&
                (_retainedFrame == null || frame.FrameStamp.SessionId != _retainedFrame.FrameStamp.SessionId ||
                 frame.FrameStamp.CapturedTimestamp - _retainedFrame.FrameStamp.CapturedTimestamp >= frame.FrameStamp.TimestampFrequency / 2))
            {
                try
                {
                    var bytes = checked(frame.SrcMat.Total() * frame.SrcMat.ElemSize());
                    if (bytes <= _maximumBytes - _bytes - _stagedBytes + _retainedBytes)
                    {
                        var copy = new ImageRegion(frame.SrcMat.Clone(), 0, 0) { FrameStamp = frame.FrameStamp };
                        var episode = _retainedEpisode;
                        ClearRetainedFrame();
                        _retainedOwner = owner;
                        _retainedEpisode = episode;
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
                if (!TryCaptureWithReason(frame, key.Request, key.Phase, detail, out var reason, request.Logger))
                    MissingFrame(key.Request, key.Phase, reason, request.Logger);
            }
        }
    }

    private void MissingFrame(string request, string phase, string reason, ILogger logger)
    {
        _dropped++;
        try { logger.LogDebug("EVIDENCE_CAPTURE_MISSING run={Run} request={Request} phase={Phase} reason={Reason}",
            _runId, request, phase, reason); } catch { }
    }

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
        string? sourceRequest = null)
        => TryCaptureWithReason(frame, request, phase, detail, out _, logger, sourceRequest);

    private bool TryCaptureWithReason(ImageRegion frame, string request, string phase, string detail, out string reason,
        ILogger? logger = null, string? sourceRequest = null)
    {
        reason = "accepted";
        Mat? owned = null;
        try
        {
            lock (_gate)
            {
                if (_closed) { reason = "scope-closed"; return false; }
                if (!frame.FrameStamp.IsKnown) { reason = "source-unknown"; return false; }
                if (frame.SrcMat.Empty()) { reason = "empty-frame"; return false; }
                if (logger != null && ReferenceEquals(_logger, NullLogger.Instance)) _logger = logger;
                request = request.Length > 256 ? request[..256] : request;
                phase = phase.Length > 64 ? phase[..64] : phase;
                if (_phases.TryGetValue(request, out var seen) && (seen.Contains(phase) || seen.Count >= 3))
                { reason = "duplicate-or-request-budget"; return false; }
                var bytes = checked(frame.SrcMat.Total() * frame.SrcMat.ElemSize());
                if (_accepted >= _maxImages || bytes > _maximumBytes - _bytes - _stagedBytes)
                { _dropped++; reason = "run-image-or-byte-budget"; return false; }
                owned = frame.SrcMat.Clone();
                var evidence = new DiagnosticEvidence(_runId, _accepted + 1, request, phase, frame.FrameStamp,
                    detail.Length > 2048 ? detail[..2048] : detail, sourceRequest);
                if (!_queue.Writer.TryWrite((evidence, owned))) { _dropped++; reason = "writer-queue-full"; return false; }
                owned = null; // 从这里起只有writer拥有并释放图像。
                _accepted++;
                _bytes += bytes;
                (_phases.GetValueOrDefault(request) ?? (_phases[request] = new(StringComparer.Ordinal))).Add(phase);
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
                if (_closed || !frame.FrameStamp.IsKnown) return false;
                var key = BeforeKey(channel, request);
                if (_accepted >= _maxImages || !_before.ContainsKey(key) && _before.Count >= 2)
                { _dropped++; return false; }
                var bytes = checked(frame.SrcMat.Total() * frame.SrcMat.ElemSize());
                if (bytes > _maximumBytes - _bytes - _stagedBytes + _before.GetValueOrDefault(key).Bytes)
                { _dropped++; return false; }
                owned = new ImageRegion(frame.SrcMat.Clone(), 0, 0) { FrameStamp = frame.FrameStamp };
                RemoveBefore(key);
                _before[key] = (owned, request, detail, bytes);
                _stagedBytes += bytes;
                owned = null;
                return true;
            }
        }
        catch { return false; /* 保留输入/导航既有结果。 */ }
        finally { owned?.Dispose(); }
    }

    internal void CaptureFault(ImageRegion frame, string channel, string request, string phase, string detail, ILogger? logger = null,
        bool allowPreviousRequest = false)
    {
        lock (_gate)
        {
            if (_closed) return;
            var key = BeforeKey(channel, request);
            if (_before.TryGetValue(key, out var before) && (before.Request == request || allowPreviousRequest))
            {
                TryCapture(before.Frame, request, "before-" + channel, before.Detail, logger, before.Request);
                RemoveBefore(key);
            }
            else _missingBefore++;
            TryCapture(frame, request, phase, detail, logger);
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

    private void RemoveBefore((string Channel, string Request) key)
    {
        if (!_before.Remove(key, out var before)) return;
        _stagedBytes -= before.Bytes;
        before.Frame.Dispose();
    }

    private async Task WriteAsync()
    {
        await foreach (var item in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try { await _sink(item.Evidence, item.Image).ConfigureAwait(false); }
            catch (Exception error)
            {
                Interlocked.Increment(ref _writeFailures);
                try { _logger.LogWarning(error, "EVIDENCE_WRITE_FAILED run={Run} sequence={Sequence}", _runId, item.Evidence.Sequence); } catch { }
            }
            finally { item.Image.Dispose(); }
        }
        try { _logger.LogDebug("EVIDENCE_DRAINED run={Run} accepted={Accepted} bytes={Bytes} dropped={Dropped} missingBefore={MissingBefore} writeFailures={Failures}",
            _runId, _accepted, _bytes, _dropped, _missingBefore, _writeFailures); } catch { }
    }

    private async Task WriteFileAsync(DiagnosticEvidence evidence, Mat image)
    {
        var directory = Global.Absolute(Path.Combine("log", "evidence", _runId.ToString("N")));
        Directory.CreateDirectory(directory);
        var name = Path.Combine(directory, $"evidence-{evidence.Sequence:D4}");
        image.SaveImage(name + ".png");
        await File.WriteAllTextAsync(name + ".json", JsonConvert.SerializeObject(evidence, Formatting.Indented)).ConfigureAwait(false);
        try { _logger.LogDebug("EVIDENCE_SAVED run={Run} request={Request} phase={Phase} source={Session}/{Sequence} file={File}",
            _runId, evidence.Request, evidence.Phase, evidence.Source.SessionId, evidence.Source.Sequence, name); } catch { }
    }

    public ValueTask DisposeAsync()
    {
        if (ReferenceEquals(Active.Value, this)) Active.Value = _previous;
        lock (_gate)
        {
            if (!_closed)
            {
                _closed = true;
                foreach (var pending in _frameRequests)
                    MissingFrame(pending.Key.Request, pending.Key.Phase, "no-next-frame-before-run-end", pending.Value.Logger);
                _frameRequests.Clear();
                ClearRetainedFrame();
                foreach (var before in _before.Values) before.Frame.Dispose();
                _before.Clear();
                _stagedBytes = 0;
                _queue.Writer.TryComplete();
            }
        }
        return new(_writer);
    }
}
