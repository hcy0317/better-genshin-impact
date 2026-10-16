using System;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

namespace BetterGenshinImpact.GameTask.AutoPathing;

// Only a route's preflight can issue this category. Generic combat/recognition failures
// must not be converted to skips. Keep the original failure without losing the route marker in V8.
internal sealed class PathingRequiredActorMissingException(string route, int? point,
    LegacyCombatFlowAdapter.MissingRequiredActorException original)
    : InvalidOperationException($"[BGI_PATH_REQUIRED_ACTOR_MISSING] 路线 {route} 第{point}点缺少必需角色，跳过本路线；尚未开始移动：{original.Message}")
{
    internal Exception OriginalFailure { get; } = original;
}
