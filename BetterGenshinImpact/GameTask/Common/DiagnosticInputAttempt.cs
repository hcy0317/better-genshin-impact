using System;
using System.Threading;
using Fischless.WindowsInput;

namespace BetterGenshinImpact.GameTask.Common;

internal enum DiagnosticInputStatus { NotSent, Sent, Unknown, Failed }

internal sealed record DiagnosticInputReceipt(Guid RequestId, long RequestedAt, long? StartedAt, long CompletedAt,
    long Frequency, int NativeRequested, int NativeSubmitted, DiagnosticInputStatus Status, string Reason)
{
    internal int TransportRequested { get; init; }
    internal int TransportAcknowledged { get; init; }
    internal string Describe() => $"requestId={RequestId:N} requestedAt={RequestedAt} startedAt={StartedAt?.ToString() ?? "none"} completedAt={CompletedAt} frequency={Frequency} nativeRequested={NativeRequested} nativeSubmitted={NativeSubmitted} transportRequested={TransportRequested} transportAcknowledged={TransportAcknowledged} status={Status} reason={Reason}";
}

/// <summary>只观察原生提交，不发送输入；异常文本不进入诊断字段。</summary>
internal sealed class DiagnosticInputAttempt : IDisposable
{
    private static readonly AsyncLocal<DiagnosticInputAttempt?> Active = new();
    private readonly DiagnosticInputAttempt? _previous;
    internal static DiagnosticInputAttempt? Current => Active.Value;
    private readonly TimeProvider _clock;
    private readonly InputDispatchCapture _capture;
    private readonly Guid _requestId = Guid.NewGuid();
    private readonly long _requestedAt;
    private long? _startedAt;
    private DiagnosticInputReceipt? _receipt;

    internal DiagnosticInputAttempt(TimeProvider clock)
    {
        _clock = clock;
        _previous = Active.Value;
        Active.Value = this;
        _requestedAt = clock.GetTimestamp();
        _capture = new InputDispatchCapture(() => _startedAt ??= clock.GetTimestamp());
    }

    internal DiagnosticInputReceipt Complete(bool applied, Exception? error = null)
    {
        if (_receipt != null) return _receipt;
        var status = _capture.Uncertain || _capture.Requested != _capture.Submitted ||
            _capture.TransportCalls != _capture.TransportAcknowledged || error != null && _capture.HasDispatch
            ? DiagnosticInputStatus.Unknown
            : error != null ? DiagnosticInputStatus.Failed
            : _capture.HasCompleteReceipt ? DiagnosticInputStatus.Sent
            : applied ? DiagnosticInputStatus.Unknown : DiagnosticInputStatus.NotSent;
        return _receipt = new(_requestId, _requestedAt, _startedAt, _clock.GetTimestamp(), _clock.TimestampFrequency,
            _capture.Requested, _capture.Submitted, status,
            error?.GetType().Name ?? (status == DiagnosticInputStatus.Unknown ? "native-delivery-unconfirmed" : "observed"))
        { TransportRequested = _capture.TransportCalls, TransportAcknowledged = _capture.TransportAcknowledged };
    }

    public void Dispose()
    {
        _capture.Dispose();
        if (ReferenceEquals(Active.Value, this)) Active.Value = _previous;
    }
}
