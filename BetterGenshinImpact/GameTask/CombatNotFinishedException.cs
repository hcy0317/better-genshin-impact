using System;

namespace BetterGenshinImpact.GameTask;

/// <summary>即使被 JavaScript 重新包装，宿主仍能识别“战斗未确认结束”。</summary>
internal sealed class CombatNotFinishedException(string reason, Exception? inner = null)
    : InvalidOperationException($"[{ErrorCode}] 战斗未确认结束，停止当前任务且不执行下一路线或传送：{reason}", inner)
{
    internal const string ErrorCode = "BGI_COMBAT_UNCONFIRMED";
}
