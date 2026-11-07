using System;
using System.Collections.Generic;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask.Common;

internal sealed partial class DiagnosticEvidenceScope
{
    // At most two exact observation slots, charged to the existing memory budget.
    private readonly Dictionary<string, (string Request, ImageRegion Frame, long Bytes)> _exactFrames = new();

    internal bool RememberExactSource(string owner, string request, CaptureFrameStamp expected)
    {
        lock (_gate)
        {
            if (_closed || !expected.IsKnown) return false;
            if (_exactFrames.TryGetValue(owner, out var existing) && existing.Request == request && existing.Frame.FrameStamp == expected) return true;
            var found = _latestHistory?.Source == expected ? _latestHistory : System.Linq.Enumerable.FirstOrDefault(_history, item => item.Source == expected);
            if (found?.Image == null || found.Image.FrameStamp != expected)
            { ForgetExactFrame(owner, request); MissingFrame(request, "combat-last-progress", "exact-progress-source-not-in-history", _logger); return false; }
            RememberExactFrame(owner, request, found.Image);
            if (_exactFrames.TryGetValue(owner, out existing) && existing.Request == request && existing.Frame.FrameStamp == expected) return true;
            ForgetExactFrame(owner, request);
            return false;
        }
    }

    internal void RememberExactFrame(string owner, string request, ImageRegion frame)
    {
        ImageRegion? copy = null;
        try
        {
            lock (_gate)
            {
                if (_closed || !frame.FrameStamp.IsKnown || frame.SrcMat.Empty()) return;
                if (!_exactFrames.ContainsKey(owner) && _exactFrames.Count + _reservations.Count >= 2)
                { MissingFrame(request, "last-observation", "exact-slots-full", _logger); return; }
                var previous = _exactFrames.GetValueOrDefault(owner);
                var bytes = checked(frame.SrcMat.Total() * frame.SrcMat.ElemSize());
                if (!FitsByteLimit(bytes, previous.Bytes) || !FitsMemory(bytes, previous.Bytes))
                { MissingFrame(request, "last-observation", "exact-memory-or-byte-budget", _logger); return; }
                copy = new ImageRegion(frame.SrcMat.Clone(), 0, 0) { FrameStamp = frame.FrameStamp };
                if (_exactFrames.Remove(owner)) { _stagedBytes -= previous.Bytes; previous.Frame.Dispose(); }
                _exactFrames[owner] = (request, copy, bytes);
                _stagedBytes += bytes;
                copy = null;
            }
        }
        catch { MissingFrame(request, "last-observation", "exact-retention-failed", _logger); }
        finally { copy?.Dispose(); }
    }

    internal bool CaptureExactWindow(string owner, string request, CaptureFrameStamp expected,
        string phase, string detail, ILogger? logger = null, IReadOnlyDictionary<string, string>? fields = null)
    {
        lock (_gate)
        {
            if (!_exactFrames.TryGetValue(owner, out var stored) || stored.Request != request || stored.Frame.FrameStamp != expected)
            { MissingFrame(request, phase, "exact-last-source-not-retained", logger ?? _logger); return false; }
            // A later target capture may have advanced sparse history beyond this decision frame.
            // Save these exact pixels independently, then attach any available surrounding window.
            var captured = TryCapture(stored.Frame, request, phase, detail, logger,
                priority: DiagnosticEvidencePriority.Error, fields: fields);
            RequestWindowFromFrame(request, phase, stored.Frame, detail, logger, fields);
            return captured;
        }
    }

    internal void ForgetExactFrame(string owner, string request)
    {
        lock (_gate)
        {
            if (_exactFrames.TryGetValue(owner, out var stored) && stored.Request == request)
            { _exactFrames.Remove(owner); _stagedBytes -= stored.Bytes; stored.Frame.Dispose(); }
        }
    }
}
