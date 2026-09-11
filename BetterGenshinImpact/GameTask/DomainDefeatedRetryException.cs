using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;

namespace BetterGenshinImpact.GameTask;

/// <summary>已观察到秘境败北；只交还所属秘境的有限退出/复苏流程，不代表恢复完成。</summary>
internal sealed class DomainDefeatedRetryException() : RetryException("检测到秘境内复苏界面，存在角色被击败，退出秘境后重试");
