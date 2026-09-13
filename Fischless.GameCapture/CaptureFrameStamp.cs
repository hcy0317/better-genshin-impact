namespace Fischless.GameCapture;

/// <summary>捕获源签发的身份；复制/裁剪帧必须保留此值，不能刷新采集时间。</summary>
public readonly record struct CaptureFrameStamp(
    Guid SessionId, long Sequence, long CapturedTimestamp, long TimestampFrequency, DateTimeOffset CapturedAt)
{
    public bool IsKnown => SessionId != Guid.Empty && Sequence > 0 && CapturedTimestamp >= 0 && TimestampFrequency > 0;

    public bool IsFresh(TimeProvider clock, TimeSpan maximumAge)
    {
        if (!IsKnown || maximumAge < TimeSpan.Zero || clock.TimestampFrequency != TimestampFrequency) return false;
        var now = clock.GetTimestamp();
        return now >= CapturedTimestamp &&
            (now - CapturedTimestamp) / (double)TimestampFrequency <= maximumAge.TotalSeconds;
    }

    public bool IsAfter(CaptureFrameStamp earlier) => IsKnown && earlier.IsKnown &&
        SessionId == earlier.SessionId && Sequence > earlier.Sequence && CapturedTimestamp >= earlier.CapturedTimestamp;
}

/// <summary>输入结束后的因果栅栏；排除队列中晚到但在输入前取得的帧。</summary>
public readonly record struct CaptureFrameFence(CaptureFrameStamp Before, long InputCompletedTimestamp)
{
    public bool Accepts(CaptureFrameStamp candidate) => candidate.IsAfter(Before) &&
        candidate.TimestampFrequency == Before.TimestampFrequency &&
        InputCompletedTimestamp >= Before.CapturedTimestamp &&
        candidate.CapturedTimestamp > InputCompletedTimestamp;
}

/// <summary>由实际捕获生产者调用；消费者不拥有签发新帧身份的权限。</summary>
public sealed class CaptureFrameSource(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private Guid _session = Guid.NewGuid();
    private long _sequence;

    public CaptureFrameStamp Next(long? capturedTimestamp = null)
    {
        lock (_gate)
        {
            var now = _clock.GetTimestamp();
            var captured = capturedTimestamp ?? now;
            if (captured < 0 || captured > now) throw new ArgumentOutOfRangeException(nameof(capturedTimestamp));
            return new(_session, ++_sequence, captured, _clock.TimestampFrequency,
                _clock.GetUtcNow() - _clock.GetElapsedTime(captured, now));
        }
    }

    public void Restart()
    {
        lock (_gate)
        {
            _session = Guid.NewGuid();
            _sequence = 0;
        }
    }
}
