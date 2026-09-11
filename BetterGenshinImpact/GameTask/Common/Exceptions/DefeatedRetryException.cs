using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;

namespace BetterGenshinImpact.GameTask.Common.Exceptions;

/// <summary>已观察到败北，交还所属路线/秘境的有限重试；不代表战斗成功。</summary>
internal sealed class DefeatedRetryException(string message) : RetryException(message);
