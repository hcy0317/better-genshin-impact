using Fischless.GameCapture;

namespace BetterGenshinImpact.GameTask.AutoFight;

// 几何已观察但不足以成为目标；只引导既有搜索镜头，不授权接近或进展。
internal enum UnconfirmedSearchHintKind { DirectionIndicator, ClippedHealthBar }
internal readonly record struct UnconfirmedSearchHint(CaptureFrameStamp Source, EnemySeekVisual Visual, int Width, int Height,
    UnconfirmedSearchHintKind Kind = UnconfirmedSearchHintKind.DirectionIndicator);
