using System;

namespace BetterGenshinImpact.GameTask.AutoTrackPath;

// 只说明本轮目标定位/可见性未确认，不推断游戏中的激活状态。
internal sealed class TeleportTargetLocalizationException(string message) : Exception(message);
