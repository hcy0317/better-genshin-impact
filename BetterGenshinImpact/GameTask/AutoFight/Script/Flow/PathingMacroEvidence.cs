using System;
using System.Collections.Generic;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Model.Area;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

internal sealed class PathingMacroEvidence(string context, string commands)
{
    private readonly string _request = "pathing-macro:" + Guid.NewGuid().ToString("N");
    private readonly Queue<string> _receipts = new();
    private readonly Dictionary<string, string> _mapping = new();
    private string? _firstReceipt;
    private long _inputCount, _observations;
    private string _lastPhase = "not-observed";
    private PathingMacroObservation _last;

    internal void Mapping(string logical, string physical)
    {
        try { if (_mapping.Count < 16 || _mapping.ContainsKey(logical)) _mapping[logical] = physical; }
        catch { }
    }

    internal void Input(PathingMacroInput input, CombatBattleHostInputResult receipt)
    {
        try
        {
            var value = $"ordinal={++_inputCount} kind={input.Kind} physical={input.Key} delta={input.X},{input.Y} status={receipt.Status} " +
                $"native={receipt.NativeSubmitted}/{receipt.NativeRequested} transport={receipt.TransportAcknowledged}/{receipt.TransportRequested} " +
                $"started={receipt.StartedTimestamp} completed={receipt.CompletedTimestamp} observableAfter={receipt.ObservableAfterTimestamp} error={receipt.Error?.GetType().Name}";
            _firstReceipt ??= value;
            if (_receipts.Count == 6) _receipts.Dequeue();
            _receipts.Enqueue(value);
        }
        catch { }
    }

    private Dictionary<string, string> Fields()
    {
        var fields = new Dictionary<string, string>
        {
            ["macro:state"] = $"phase={_lastPhase} scene={_last.Scene} canFire={_last.CanFire} observations={_observations} inputCount={_inputCount}",
            ["macro:lastSource"] = $"{_last.Source.SessionId}/{_last.Source.Sequence} capturedTimestamp={_last.Source.CapturedTimestamp} frequency={_last.Source.TimestampFrequency}",
            ["macro:mapping"] = string.Join(",", System.Linq.Enumerable.Select(_mapping, pair => pair.Key + "->" + pair.Value)),
            ["input:first"] = _firstReceipt ?? "not-submitted"
        };
        var index = 0;
        foreach (var receipt in _receipts) fields["input:tail:" + index++] = receipt;
        foreach (var key in System.Linq.Enumerable.ToArray(fields.Keys)) fields[key] = Bounded(fields[key]);
        return fields;
    }

    private static string Bounded(string value) => value.Length > 512 ? value[..512] : value;

    internal void Failed(Exception error, ILogger? logger = null)
    {
        try
        {
            var fields = Fields();
            fields["macro:failure"] = Bounded(error.GetType().Name + ":" + error.Message);
            logger?.LogWarning("PATH_RAW_END {Context} request={Request} phase={Phase} scene={Scene} observations={Observations} inputs={Inputs} error={Error} evidence={Evidence}",
                context, _request, _lastPhase, _last.Scene, _observations, _inputCount, error.GetType().Name,
                Newtonsoft.Json.JsonConvert.SerializeObject(fields));
            DiagnosticEvidenceScope.Current?.RequestLatestWindow(_request, "macro-failed",
                $"{context}; phase={_lastPhase}; lastObservedSource={_last.Source.SessionId}/{_last.Source.Sequence}; " +
                "latest retained existing frame, not a new capture or proof that it is the exact final decision frame", logger, fields);
        }
        catch { }
    }

    internal void Capture(ImageRegion frame, string phase, PathingMacroObservation observation, ILogger? logger = null)
    {
        try
        {
            _last = observation;
            _lastPhase = phase;
            _observations++;
            DiagnosticEvidenceScope.Current?.ObserveExistingFrame(frame);
            // 只保存既有边界观察；同一阶段首次unknown与首次已识别各一帧，不逐帧输出。
            if (phase is not ("entry" or "post" or "scene-transition" or "before-fire" or
                "before-cannon-handshake" or "cannon-handshake-complete" or "cannon-turn-complete")) return;
            var evidencePhase = observation.Scene == PathingMacroScene.Unknown ? phase + "-unknown" : phase;
            DiagnosticEvidenceScope.Current?.TryCapture(frame, _request, evidencePhase,
                $"{context}; scene={observation.Scene}; canFire={observation.CanFire}; phase observation only, not completion proof; commands={commands}",
                logger, fields: Fields());
        }
        catch { /* 诊断不能影响原宏的准入、期限和结果。 */ }
    }
}
