using System;
using System.Collections.Generic;
using System.Linq;
using Fischless.GameCapture;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.GameLoading;

internal readonly record struct GameLoadingAgreementText(string Text, Rect Bounds);

/// <summary>仅处理官方明确协议更新；消费一次同意尝试，等待因果新帧确认离开弹窗。</summary>
internal sealed class GameLoadingAgreement
{
    internal static readonly TimeSpan MaximumFrameAge = TimeSpan.FromSeconds(2);
    private CaptureFrameStamp _previous;
    private Rect _previousButton;
    private bool _consumed;
    private CaptureFrameFence _inputFence;
    private object? _previousRuntime, _consumedRuntime;
    private IntPtr _previousWindow, _consumedWindow;
    private CaptureFrameStamp _successorPrevious;
    private int _successorKind;

    internal bool AwaitingDismissal => _consumed;

    internal bool Handle(IReadOnlyList<GameLoadingAgreementText> text, CaptureFrameStamp source, Size size,
        TimeProvider clock, Action<Rect> click, Action<int> delay) =>
        HandleAttempt(text, source, size, clock, bounds => { click(bounds); return true; }, delay);

    // false仅表示明确没有进入目标Click；进入/异常/不明结果必须消费一次。
    internal bool HandleAttempt(IReadOnlyList<GameLoadingAgreementText> text, CaptureFrameStamp source, Size size,
        TimeProvider clock, Func<Rect, bool> click, Action<int> delay, bool ageConfirmationVisible = false,
        object? targetRuntime = null, IntPtr targetWindow = default)
    {
        var joined = Normalize(string.Concat(text.Select(region => region.Text)));
        var recognized = TryFind(text, joined, size, out var button);
        if (_consumed)
            return WaitForSuccessor(text, joined, recognized, source, clock, ageConfirmationVisible, targetRuntime, targetWindow);
        if (!recognized)
        {
            _previous = default;
            return joined.Contains("用户协议") && joined.Contains("更新");
        }
        if (!source.IsFresh(clock, MaximumFrameAge))
        {
            _previous = default;
            return true;
        }
        var corroborated = source.IsAfter(_previous) && source.CapturedTimestamp > _previous.CapturedTimestamp &&
            source.TimestampFrequency == _previous.TimestampFrequency && SameButton(button, _previousButton) &&
            ReferenceEquals(targetRuntime, _previousRuntime) && targetWindow == _previousWindow;
        _previous = source;
        _previousButton = button;
        _previousRuntime = targetRuntime;
        _previousWindow = targetWindow;
        if (!corroborated) return true;

        // 在派发前消费；异常/不明结果也不能导致下一轮重复点击同意。
        _consumed = true;
        _consumedRuntime = targetRuntime;
        _consumedWindow = targetWindow;
        var noInput = false;
        try
        {
            if (!click(button))
            {
                noInput = true;
                _consumed = false;
                _previous = default;
                _inputFence = default;
                _consumedRuntime = null;
                _consumedWindow = default;
                ResetSuccessor();
            }
        }
        finally
        {
            if (!noInput)
            {
                _inputFence = new CaptureFrameFence(source, clock.GetTimestamp());
                delay(80);
            }
        }
        return true;
    }

    private bool WaitForSuccessor(IReadOnlyList<GameLoadingAgreementText> text, string joined, bool agreementVisible,
        CaptureFrameStamp source, TimeProvider clock, bool ageConfirmationVisible, object? runtime, IntPtr window)
    {
        // GameRuntime是一次绑定的sealed产物，Window/Input均get-only；句柄再防同runtime下的目标变化。
        var sameTarget = _consumedRuntime != null && ReferenceEquals(runtime, _consumedRuntime) && window == _consumedWindow;
        if (!source.IsFresh(clock, MaximumFrameAge) || source.TimestampFrequency != _inputFence.Before.TimestampFrequency ||
            source.CapturedTimestamp <= _inputFence.InputCompletedTimestamp ||
            clock.GetElapsedTime(_inputFence.InputCompletedTimestamp, source.CapturedTimestamp) < TimeSpan.FromMilliseconds(60) ||
            (_consumedRuntime != null && !sameTarget))
        { ResetSuccessor(); return true; }

        var kind = 0;
        if (!agreementVisible && !joined.Contains("用户协议") && !joined.Contains("隐私政策") && !HasHumanPrompt(joined))
        {
            if (text.Any(region => Normalize(region.Text) is "点击进入" or "点击进入游戏" or "进入游戏")) kind = 1;
            else if (ageConfirmationVisible && (joined.Contains("适龄") || joined.Contains("监护"))) kind = 2;
        }
        if (kind == 0) { ResetSuccessor(); return true; }
        // 原代仍用原始输入fence，不把foreign source当作通过。
        if (_inputFence.Accepts(source)) { ResetSuccessor(); return false; }
        if (source.SessionId == _inputFence.Before.SessionId || !sameTarget)
        { ResetSuccessor(); return true; }

        // WGC resize仅能在同一已绑定目标上换代；consumed与原始fence永久保留。
        var confirmed = source.IsAfter(_successorPrevious) && source.CapturedTimestamp > _successorPrevious.CapturedTimestamp &&
            source.TimestampFrequency == _successorPrevious.TimestampFrequency && kind == _successorKind;
        _successorPrevious = source;
        _successorKind = kind;
        return !confirmed;
    }

    private void ResetSuccessor()
    { _successorPrevious = default; _successorKind = 0; }

    private static bool TryFind(IReadOnlyList<GameLoadingAgreementText> text, string joined, Size size, out Rect button)
    {
        button = default;
        if (!joined.Contains("用户协议和隐私政策更新提示") || !joined.Contains("法律约束力") || HasHumanPrompt(joined))
            return false;
        var agree = text.Where(region => Normalize(region.Text) == "同意").ToArray();
        var refuse = text.Where(region => Normalize(region.Text) == "拒绝").ToArray();
        if (agree.Length != 1 || refuse.Length != 1) return false;
        var right = agree[0].Bounds;
        var left = refuse[0].Bounds;
        if (!Inside(right, size) || !Inside(left, size) || right.X <= left.Right ||
            Math.Abs(right.Y + right.Height / 2d - left.Y - left.Height / 2d) > Math.Max(right.Height, left.Height) ||
            right.Y < size.Height / 2d) return false;
        button = right;
        return true;
    }

    private static bool HasHumanPrompt(string text) => text.Contains("验证码") || text.Contains("登录") ||
        text.Contains("二维码") || text.Contains("扫码") || text.Contains("密码");
    private static bool Inside(Rect rect, Size size) => rect.Width > 0 && rect.Height > 0 && rect.X >= 0 && rect.Y >= 0 &&
        rect.Right <= size.Width && rect.Bottom <= size.Height;
    private static bool SameButton(Rect left, Rect right) => right.Width > 0 &&
        Math.Abs(left.X - right.X) <= 2 && Math.Abs(left.Y - right.Y) <= 2 &&
        Math.Abs(left.Width - right.Width) <= 2 && Math.Abs(left.Height - right.Height) <= 2;
    private static string Normalize(string text) => new(text.Where(character => !char.IsWhiteSpace(character) && !char.IsPunctuation(character)).ToArray());
}
