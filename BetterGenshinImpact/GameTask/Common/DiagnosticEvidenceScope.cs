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
    private readonly Dictionary<(string Channel, string Request), (ImageRegion Frame, string Request, string Detail, long Bytes)> _before = new();
    private ILogger _logger = NullLogger.Instance;
    private int _accepted, _dropped, _writeFailures, _missingBefore;
    private long _bytes;
    private long _stagedBytes;
    private bool _closed;

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

    internal bool TryCapture(ImageRegion frame, string request, string phase, string detail, ILogger? logger = null,
        string? sourceRequest = null)
    {
        Mat? owned = null;
        try
        {
            lock (_gate)
            {
                if (_closed || !frame.FrameStamp.IsKnown || frame.SrcMat.Empty()) return false;
                if (logger != null && ReferenceEquals(_logger, NullLogger.Instance)) _logger = logger;
                request = request.Length > 256 ? request[..256] : request;
                phase = phase.Length > 64 ? phase[..64] : phase;
                if (_phases.TryGetValue(request, out var seen) && (seen.Contains(phase) || seen.Count >= 3)) return false;
                var bytes = checked(frame.SrcMat.Total() * frame.SrcMat.ElemSize());
                if (_accepted >= _maxImages || bytes > _maximumBytes - _bytes - _stagedBytes) { _dropped++; return false; }
                owned = frame.SrcMat.Clone();
                var evidence = new DiagnosticEvidence(_runId, _accepted + 1, request, phase, frame.FrameStamp,
                    detail.Length > 2048 ? detail[..2048] : detail, sourceRequest);
                if (!_queue.Writer.TryWrite((evidence, owned))) { _dropped++; return false; }
                owned = null; // 从这里起只有writer拥有并释放图像。
                _accepted++;
                _bytes += bytes;
                (_phases.GetValueOrDefault(request) ?? (_phases[request] = new(StringComparer.Ordinal))).Add(phase);
                return true;
            }
        }
        catch { return false; /* 取证不能改变业务结果或发送额外输入。 */ }
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
                foreach (var before in _before.Values) before.Frame.Dispose();
                _before.Clear();
                _stagedBytes = 0;
                _queue.Writer.TryComplete();
            }
        }
        return new(_writer);
    }
}
