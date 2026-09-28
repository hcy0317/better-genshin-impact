using System;

namespace BetterGenshinImpact.GameTask.Common.Ui;

/// <summary>
/// 界面空转的兜底探测：观察既不命中目标、又没有任何已识别界面可依（<see cref="UiSnapshot.CanEscape"/> 为假）时，
/// 超过宽限期后允许发送一次 Escape。该状态真实出现在"世界已渲染但 HUD 与菜单均未出现"的中间态，
/// 此时状态机没有任何可执行动作，只能空转到预算耗尽。
/// </summary>
internal sealed class UiStallEscapeProbe
{
    private readonly TimeSpan _grace;
    private readonly int _maxProbes;
    private readonly TimeProvider _clock;
    private DateTimeOffset? _stalledSince;
    private int _probes;

    internal UiStallEscapeProbe(TimeSpan grace, int maxProbes, TimeProvider? clock = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(grace, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxProbes, 1);
        _grace = grace;
        _maxProbes = maxProbes;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>连续两次探测之间至少间隔一个宽限期；整次恢复最多探测 <c>maxProbes</c> 次。</summary>
    internal bool ShouldProbe(UiSnapshot observed)
    {
        if (_probes >= _maxProbes)
        {
            // 次数用尽后不再重置宽限计时：本实例只服务一次恢复边界。
            return false;
        }

        if (!IsUnrecognizedStall(observed))
        {
            _stalledSince = null;
            return false;
        }

        var now = _clock.GetUtcNow();
        _stalledSince ??= now;
        if (now - _stalledSince.Value < _grace)
        {
            return false;
        }

        _stalledSince = now;
        _probes++;
        return true;
    }

    /// <summary>
    /// 是否处于"没有任何已识别界面"的空转状态：证据可用、未命中任何目标，
    /// 且既没有可执行动作（<see cref="UiSnapshot.CanEscape"/> 为假），也没有复苏、对话、确认框、
    /// 奖励遮罩、秘境提示等需要保持输入禁忌的证据。
    /// </summary>
    internal static bool IsUnrecognizedStall(UiSnapshot observed) =>
        observed.HasUsableEvidence && !observed.MainReady && !observed.CanEscape &&
        !observed.FullPartyDefeat && !observed.Reward.IsCandidate && !observed.BlackConfirm &&
        !observed.Talk && !observed.Prompt && !observed.Revive && !observed.InDomain &&
        !observed.DomainExit.Visible && !observed.DomainTip.IsCandidate;
}
