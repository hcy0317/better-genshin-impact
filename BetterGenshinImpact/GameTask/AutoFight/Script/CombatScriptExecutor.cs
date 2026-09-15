using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.AutoFight.Script;

public static class CombatScriptExecutor
{
    /// <summary>
    /// 执行简易战斗策略脚本。
    /// </summary>
    /// <param name="combatScript">已解析的策略</param>
    /// <param name="ct">取消令牌</param>
    /// <param name="logger">日志</param>
    /// <param name="combatScenes">
    /// 可选。传了则使用现成的 CombatScenes（由调用方管理生命周期）；
    /// 不传则在内部通过截图自动创建并管理生命周期。
    /// </param>
    public static async Task<CombatExecutionResult> ExecuteAsync(
        CombatScript combatScript,
        CancellationToken ct,
        ILogger logger,
        CombatScenes? combatScenes = null,
        CombatScriptExecutionMode mode = CombatScriptExecutionMode.RequiredSequence)
    {
        var ownsScenes = false;
        try
        {
            if (combatScenes == null)
            {
                using var capture = CaptureToRectArea();
                combatScenes = new CombatScenes();
                ownsScenes = true;
                combatScenes.InitializeTeam(capture);
                if (!combatScenes.CheckTeamInitialized())
                {
                    throw new InvalidOperationException("队伍识别未初始化成功，简易策略未执行");
                }
            }

            // 仅在内部创建 CombatScenes 时设置 Avatar.Ct，外部传入时由调用方管理
            if (ownsScenes)
            {
                combatScenes.BeforeTask(ct);
            }

            try
            {
                using var flow = Flow.NativeCombatFlowRunner.Create(combatScript.CombatCommands, combatScenes, loop: false, mode);
                var result = await flow.RunRoundAsync(ct);
                var outcome = FromFlowResult(result);
                outcome.EnsureCanContinue();
                return outcome;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (GuardianCoverageException)
            {
                BetterGenshinImpact.Core.Simulator.Simulation.ReleaseAllKey();
                throw;
            }
            catch (RetryException e)
            {
                logger.LogWarning("简易策略未完成，交回调用方处理重试：{Msg}", e.Message);
                throw;
            }
            catch (Exception e)
            {
                logger.LogError(e, "执行简易策略脚本时发生错误！");
                throw;
            }
        }
        finally
        {
            if (ownsScenes)
            {
                SafeDispose(combatScenes!, logger);
            }
        }
    }

    internal static CombatExecutionResult FromFlowResult(Flow.CombatFlowResult result) => result switch
    {
        Flow.CombatFlowResult.Succeeded or Flow.CombatFlowResult.SatisfiedExisting => new(CombatExecutionKind.Completed, result.ToString()),
        Flow.CombatFlowResult.Skipped => new(CombatExecutionKind.Skipped, "FLOW_OPTIONAL_SKIPPED"),
        Flow.CombatFlowResult.Failed => new(CombatExecutionKind.Failed, "FLOW_REQUIREMENT_FAILED"),
        _ => new(CombatExecutionKind.Deferred, "FLOW_NOT_COMPLETED:" + result)
    };

    /// <summary>旧脚本真实顺序执行边界；execute是游戏输入/观测端口，不参与角色筛选或结果汇总。</summary>
    internal static Task<CombatExecutionResult> ExecuteLegacyAsync(CombatScript script, IEnumerable<string> party,
        Func<CombatCommand, CombatCommand?, CancellationToken, CombatExecutionResult> execute,
        CombatScriptExecutionMode mode, ILogger logger, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var available = party.ToHashSet(StringComparer.Ordinal);
        var named = script.CombatCommands.Where(c => c.Name != CombatScriptParser.CurrentAvatarName && !c.Method.IsFlowControl)
            .Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        CombatExecutionResult Fail(string reason) => new(CombatExecutionKind.Failed, reason);
        if (script.CombatCommands.Count == 0) return Task.FromResult(Fail("EMPTY_FRAGMENT"));
        if (available.Count == 0) return Task.FromResult(Fail("PARTY_NOT_INITIALIZED"));
        if (named.Count > 0 && !named.Overlaps(available)) return Task.FromResult(Fail("NO_APPLICABLE_ACTOR"));
        if (script.HasFlowCommands) throw new InvalidOperationException("增强策略必须由完整流程校验，不能套用旧模板过滤");
        if (mode == CombatScriptExecutionMode.RequiredSequence && !named.IsSubsetOf(available))
            return Task.FromResult(Fail("REQUIRED_ACTOR_MISSING:" + string.Join(",", named.Except(available))));
        var anyCompleted = false;
        CombatCommand? previous = null;
        foreach (var command in script.CombatCommands)
        {
            ct.ThrowIfCancellationRequested();
            if (command.Name != CombatScriptParser.CurrentAvatarName && !available.Contains(command.Name))
            {
                logger.LogDebug("旧路线模板跳过不在队的角色 {Actor}", command.Name);
                continue;
            }
            var result = execute(command, previous, ct);
            ct.ThrowIfCancellationRequested();
            if (!result.CanContinue) return Task.FromResult(result);
            anyCompleted |= result.Kind == CombatExecutionKind.Completed;
            if (result.Kind == CombatExecutionKind.Completed) previous = command;
        }
        return Task.FromResult(new CombatExecutionResult(anyCompleted ? CombatExecutionKind.Completed : CombatExecutionKind.Skipped,
            anyCompleted ? "FRAGMENT_COMPLETED" : "ALL_OPTIONAL_ACTIONS_SKIPPED"));
    }

    /// <summary>
    /// 安全释放 CombatScenes，异常仅记录不抛出，
    /// 避免释放异常覆盖 try 块中的原始异常。
    /// </summary>
    private static void SafeDispose(CombatScenes combatScenes, ILogger logger)
    {
        try
        {
            combatScenes.Dispose();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "释放战斗场景资源时发生异常");
        }
    }
}
