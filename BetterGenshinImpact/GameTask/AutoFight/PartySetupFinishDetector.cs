using System;

namespace BetterGenshinImpact.GameTask.AutoFight;

internal readonly record struct PartySetupFinishObservation(long FrameId, DateTimeOffset CapturedAt,
    int Width, int Height, bool BarVisible, ulong Fingerprint);

/// <summary>一次打开编队请求内的结束证据；重复截图、背景颜色和单帧闪烁均不能完成确认。</summary>
internal sealed class PartySetupFinishDetector(PartySetupFinishObservation before, DateTimeOffset requestedAt)
{
    private long _lastFrame = before.FrameId;
    private DateTimeOffset _lastTime = before.CapturedAt;
    private ulong _lastFingerprint = before.Fingerprint;
    private DateTimeOffset? _firstCandidateAt;
    private bool _sawClear = !before.BarVisible;
    public string Reason { get; private set; } = "awaiting-post-input-frame";

    public bool Observe(PartySetupFinishObservation sample)
    {
        if (sample.FrameId <= _lastFrame || sample.CapturedAt <= _lastTime || sample.CapturedAt <= requestedAt)
        {
            Reason = "stale-frame";
            return false;
        }
        _lastFrame = sample.FrameId;
        _lastTime = sample.CapturedAt;
        if (sample.Width < 640 || sample.Width * 9 != sample.Height * 16
            || sample.Width != before.Width || sample.Height != before.Height)
        {
            _firstCandidateAt = null;
            Reason = "unsupported-or-changed-size";
            return false;
        }
        if (!sample.BarVisible)
        {
            _sawClear = true;
            _firstCandidateAt = null;
            _lastFingerprint = sample.Fingerprint;
            Reason = "no-bar";
            return false;
        }
        if (!_sawClear)
        {
            Reason = "pre-existing-candidate";
            return false;
        }
        if (sample.Fingerprint == _lastFingerprint)
        {
            Reason = "repeated-image";
            return false;
        }
        _lastFingerprint = sample.Fingerprint;
        if (_firstCandidateAt == null)
        {
            _firstCandidateAt = sample.CapturedAt;
            Reason = "first-candidate";
            return false;
        }
        if (sample.CapturedAt - _firstCandidateAt < TimeSpan.FromMilliseconds(80))
        {
            Reason = "awaiting-stable-candidate";
            return false;
        }
        Reason = "confirmed-post-input-bar";
        return true;
    }
}
