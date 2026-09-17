using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

namespace BetterGenshinImpact.GameTask.AutoPathing.Handler;

/// <summary>路线动作的共同边界：只替换游戏I/O，选角、预算、输入和结果仍使用同一执行器。</summary>
internal static class NativeActionHandler
{
    internal static async Task<INativeCombatIo> ResolveAsync(INativeCombatIo? io, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (io != null) return io;
        var scenes = await RunnerContext.Instance.GetCombatScenes(ct)
            ?? throw new InvalidOperationException("队伍识别未初始化，路线动作尚未执行");
        return new NativeCombatIo(scenes);
    }

    internal static Task ExecuteAsync(INativeCombatIo io, string script, CancellationToken ct) =>
        ExecuteAsync(io, CombatScriptParser.ParseContext(script, validate: false).CombatCommands, ct);

    internal static async Task ExecuteAsync(INativeCombatIo io, IReadOnlyList<CombatCommand> commands, CancellationToken ct, Action? observe = null)
    {
        ct.ThrowIfCancellationRequested();
        using var runner = NativeCombatFlowRunner.Create(commands, io, loop: false);
        CombatFlowStep step;
        do
        {
            step = await runner.StepAsync(ct);
            ct.ThrowIfCancellationRequested();
            observe?.Invoke();
        } while (!step.RoundCompleted);
        var outcome = CombatScriptExecutor.FromFlowResult(step.Result);
        ct.ThrowIfCancellationRequested();
        if (outcome.Kind != CombatExecutionKind.Completed)
            throw new InvalidOperationException($"[BGI_COMBAT_FRAGMENT_INCOMPLETE] 路线动作未完成：{outcome.Kind}/{outcome.Reason}");
    }

    internal static IReadOnlyList<CombatCommand> WithConfirmedSkill(string actor, IReadOnlyList<CombatCommand> commands, bool hold, Action? observe = null)
    {
        static bool E(CombatCommand command) => command.Args?.FirstOrDefault() is "E" or "VK_E";
        var start = commands.ToList().FindIndex(command => command.Method == Method.KeyDown && E(command));
        var end = commands.ToList().FindIndex(start + 1, command => command.Method == Method.KeyUp && E(command));
        if (start < 0 || end <= start) throw new InvalidOperationException("采集动作缺少匹配的E按住/松开，禁止执行");
        var result = commands.Take(start).Select(command => new CombatCommand(command)).ToList();
        result.Add(new CombatCommand(actor, hold ? "e(hold,wait)" : "e(wait)")
        { NativeSkillSequence = commands.Skip(start).Take(end - start + 1).Select(command => new CombatCommand(command)).ToArray(), NativeSkillObserver = observe });
        result.AddRange(commands.Skip(end + 1).Select(command => new CombatCommand(command)));
        return result;
    }

    internal static async Task WithSelectedActorAsync(INativeCombatIo io, string actor, Func<CancellationToken, Task> operation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var runner = NativeCombatFlowRunner.Create(CombatScriptParser.ParseContext(actor + " wait(0)").CombatCommands, io, loop: false);
        var result = CombatScriptExecutor.FromFlowResult(await runner.RunRoundAsync(ct));
        if (result.Kind != CombatExecutionKind.Completed)
            throw new InvalidOperationException($"[BGI_COMBAT_FRAGMENT_INCOMPLETE] 专用操作选角未完成：{actor}/{result.Kind}");
        // 不销毁选角的输入Session再裸调用；整个专用操作借用同一个owner。
        await runner.RunHostOperationAsync(token => new ValueTask(operation(token)), ct);
        ct.ThrowIfCancellationRequested();
    }
}
