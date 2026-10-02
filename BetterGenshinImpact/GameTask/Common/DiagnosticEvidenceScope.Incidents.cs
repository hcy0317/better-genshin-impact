using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Fischless.GameCapture;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace BetterGenshinImpact.GameTask.Common;

internal sealed partial class DiagnosticEvidenceScope
{
    // 每个已准入窗口最多一条；不持有图像，run结束后仍可解释缺帧与写入失败。
    private sealed class Incident(Guid id, string request, string phase, CaptureFrameStamp anchor)
    {
        internal readonly Guid Id = id;
        internal readonly string Request = request, Phase = phase;
        internal readonly CaptureFrameStamp Anchor = anchor;
        internal readonly DateTimeOffset ObservedAt = DateTimeOffset.Now;
        internal readonly long ObservedTimestamp = Stopwatch.GetTimestamp();
        internal readonly Guid? TaskInstanceId = TaskExecutionScope.DiagnosticId;
        internal readonly Guid? ScriptInstanceId = BetterGenshinImpact.Core.Script.ScriptAsyncLifetime.DiagnosticId;
        internal readonly string? UiRootId = Ui.UiOperation.Current?.RootId;
        internal readonly string? UiOperationId = Ui.UiOperation.Current?.Id;
        internal readonly Dictionary<string, int> Missing = new(StringComparer.Ordinal);
        internal int Queued, Written, Evicted;
        internal int Occurrences = 1, Changes;
        internal CaptureFrameStamp LastSource = anchor;
        internal string LastDetail = "";
        internal double? FirstWrittenSeconds, LastWrittenSeconds;
    }

    private readonly Dictionary<Guid, Incident> _incidents = new();
    private readonly Dictionary<int, (Guid IncidentId, double Seconds)> _writtenIncidentFrames = new();
    private sealed record RejectedIncident(string Request, string Phase, Dictionary<string, int> MissingReasons);
    private readonly Dictionary<(string Request, string Phase), RejectedIncident> _rejectedIncidents = new();
    private int _rejectedIdentityOverflow;
    private sealed record RunIdentity(string Kind, string Id, string Source, string Fingerprint);
    private readonly HashSet<RunIdentity> _identities = new();
    private int _identityDrops;

    internal void RegisterIdentity(string kind, string id, string source, string? fingerprint)
    {
        static string Bound(string value) => value.Length > 512 ? value[..512] : value;
        lock (_gate)
        {
            if (_closed) return;
            // 同名路线/同路线不同selector不是同一个身份；完整variant参与有界去重。
            // 绝对路径只保留稳定摘要，避免用户目录进入日志和证据包。
            var safeSource = Path.IsPathRooted(source)
                ? "path-sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    source.Replace('/', '\\').ToUpperInvariant()))) : Bound(source);
            var identity = new RunIdentity(Bound(kind), Bound(id), safeSource,
                Bound(fingerprint ?? "unknown:not-exposed-by-caller"));
            if (_identities.Contains(identity)) return;
            if (_identities.Count >= 128) { _identityDrops++; return; }
            _identities.Add(identity);
        }
    }

    private object[] IncidentSummaries() => _incidents.Values.Select(item => (object)new
    {
        IncidentId = item.Id, RunId = _runId, item.Request, item.Phase, item.Anchor,
        item.TaskInstanceId, item.ScriptInstanceId, item.UiRootId, item.UiOperationId,
        item.ObservedAt, item.ObservedTimestamp, ObservedFrequency = Stopwatch.Frequency,
        BeforeSeconds = WindowBeforeSeconds, AfterSeconds = WindowAfterSeconds,
        item.Occurrences, item.Changes, item.LastSource,
        QueuedFrames = item.Queued, WrittenFrames = item.Written,
        EvictedFrames = item.Evicted, RetainedFrames = item.Written - item.Evicted,
        RetainedStartSeconds = _writtenIncidentFrames.Values.Where(frame => frame.IncidentId == item.Id).Select(frame => (double?)frame.Seconds).Min(),
        RetainedEndSeconds = _writtenIncidentFrames.Values.Where(frame => frame.IncidentId == item.Id).Select(frame => (double?)frame.Seconds).Max(),
        item.FirstWrittenSeconds, item.LastWrittenSeconds, MissingReasons = new Dictionary<string, int>(item.Missing),
        Sampling = "existing-source-sparse-not-video"
    }).ToArray();

    private void IncidentMissing(string request, string phase, string reason, int count)
    {
        var found = false;
        foreach (var item in _incidents.Values)
            if (item.Request == request && item.Phase == phase)
            {
                found = true;
                item.Missing[reason] = item.Missing.GetValueOrDefault(reason) + count;
            }
        if (found) return;
        request = request.Length > 256 ? request[..256] : request;
        phase = phase.Length > 64 ? phase[..64] : phase;
        if (!_rejectedIncidents.TryGetValue((request, phase), out var rejected))
        {
            if (_rejectedIncidents.Count >= 128) { _rejectedIdentityOverflow++; return; }
            _rejectedIncidents[(request, phase)] = rejected = new(request, phase, new(StringComparer.Ordinal));
        }
        rejected.MissingReasons[reason] = rejected.MissingReasons.GetValueOrDefault(reason) + count;
    }

    private void IncidentWritten(DiagnosticEvidence evidence)
    {
        lock (_gate)
        {
            if (evidence.Window is not { } frame || !_incidents.TryGetValue(frame.WindowId, out var item)) return;
            item.Written++;
            var seconds = SecondsBetween(evidence.Source, item.Anchor);
            _writtenIncidentFrames[evidence.Sequence] = (item.Id, seconds);
            item.FirstWrittenSeconds = Math.Min(item.FirstWrittenSeconds ?? seconds, seconds);
            item.LastWrittenSeconds = Math.Max(item.LastWrittenSeconds ?? seconds, seconds);
        }
    }

    private void IncidentEvicted(int sequence, string reason)
    {
        lock (_gate)
        {
            if (!_writtenIncidentFrames.Remove(sequence, out var frame) || !_incidents.TryGetValue(frame.IncidentId, out var item)) return;
            item.Evicted++;
            item.Missing["retention:" + reason] = item.Missing.GetValueOrDefault("retention:" + reason) + 1;
        }
    }

    private void LogIncidentSummaries(object[] summaries)
    {
        try { _logger.LogDebug("EVIDENCE_RUN_IDENTITIES run={Run} identities={Identities} omitted={Omitted}",
            _runId, JsonConvert.SerializeObject(_identities), _identityDrops); } catch { }
        foreach (var item in summaries)
            try { _logger.LogDebug("EVIDENCE_WINDOW_SUMMARY {Summary}", JsonConvert.SerializeObject(item)); } catch { }
        try { _logger.LogDebug("EVIDENCE_REJECTED_INCIDENTS entries={Entries} overflow={Overflow}",
            JsonConvert.SerializeObject(_rejectedIncidents.Values), _rejectedIdentityOverflow); } catch { }
    }
}
