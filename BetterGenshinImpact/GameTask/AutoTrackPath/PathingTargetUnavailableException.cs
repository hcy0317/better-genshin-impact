using System;
using System.Linq;
using BetterGenshinImpact.GameTask.Common.Exceptions;

namespace BetterGenshinImpact.GameTask.AutoTrackPath;

/// <summary>跨脚本边界只标注已确认的目标不可用类别，不发布路线成功。</summary>
internal sealed class PathingTargetUnavailableException(Exception original)
    : InvalidOperationException(ErrorCode + " " + original.Message)
{
    internal const string ErrorCode = "[BGI_PATH_TARGET_UNAVAILABLE]";
    // ClearScript会读取GetBaseException；原异常保留为诊断属性，标记必须留在最内层消息。
    internal Exception OriginalFailure { get; } = original;

    internal static bool IsUnavailableTarget(Exception error)
    {
        if (TaskFailureRecoveryPolicy.IsTerminalFailure(error)) return false;
        if (error is PathingTargetUnavailableException or TpPointNotActivate or TeleportSelectionMismatchException)
            return error.InnerException == null || IsUnavailableTarget(error.InnerException);
        if (error is AggregateException aggregate)
            return aggregate.InnerExceptions.Count > 0 && aggregate.InnerExceptions.All(IsUnavailableTarget);
        return error.InnerException != null && IsUnavailableTarget(error.InnerException);
    }
}
