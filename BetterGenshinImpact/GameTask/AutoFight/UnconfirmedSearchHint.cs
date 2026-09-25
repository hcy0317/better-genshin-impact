using Fischless.GameCapture;

namespace BetterGenshinImpact.GameTask.AutoFight;

// 已过形状门禁、但尖端朝向未确认；只能引导既有搜索镜头，绝不是行动目标。
internal readonly record struct UnconfirmedSearchHint(CaptureFrameStamp Source, EnemySeekVisual Visual, int Width, int Height);
