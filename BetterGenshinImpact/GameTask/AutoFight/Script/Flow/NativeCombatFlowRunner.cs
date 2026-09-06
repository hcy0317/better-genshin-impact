using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoFight.SkillData;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Model.Area;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

internal sealed class CombatFlowRecoveryException(string message)
    : BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception.RetryException(message);

/// <summary>宿主拥有结束检测；此适配器只驱动一份本场流程，不启动后台输入循环。</summary>
internal sealed class NativeCombatFlowRunner : IDisposable
{
    private static readonly CombatInputCoordinator InputCoordinator = new();
    private readonly ICombatFlowGame _game;
    private readonly CombatFlowExecution? _execution;
    private readonly JsonCombatFlowExecution? _jsonExecution;
    private IDisposable? _exclusive;
    private bool _disposed;
    public CombatFlowContext Context => _execution?.Context ?? _jsonExecution!.Context;
    public CombatFlowStatistics RuntimeStatistics => _execution?.RuntimeStatistics ?? _jsonExecution!.RuntimeStatistics;
    public bool IsAtomic => _execution?.IsAtomic ?? _jsonExecution!.IsAtomic;
    public bool IsAtRootBoundary => _execution?.IsAtRootBoundary ?? _jsonExecution!.IsAtRootBoundary;
    public bool TakeFinishCheckRequest() => _execution?.TakeFinishCheckRequest() ?? _jsonExecution!.TakeFinishCheckRequest();

    private NativeCombatFlowRunner(CombatFlowProgram program, CombatScenes scenes)
    {
        _game = new NativeGame(scenes);
        _execution = new(program, _game);
        foreach (var diagnostic in program.Diagnostics) Logger.LogWarning("战斗策略数据：{Diagnostic}", diagnostic);
    }

    private NativeCombatFlowRunner(JsonCombatStrategy strategy, ICombatFlowGame game,
        SkillCatalogSnapshot? database, TimeProvider? clock)
    {
        _game = game;
        _jsonExecution = new(strategy, game, database, clock);
        if (game is NativeGame)
            foreach (var diagnostic in _jsonExecution.Diagnostics) Logger.LogWarning("战斗策略数据：{Diagnostic}", diagnostic);
    }

    internal static NativeCombatFlowRunner? Create(JsonCombatStrategy strategy, ICombatFlowGame game,
        SkillCatalogSnapshot? database = null, TimeProvider? clock = null) =>
        JsonCombatFlowExecution.RequiresFlow(strategy) ? new(strategy, game, database, clock) : null;

    internal static NativeCombatFlowRunner? Create(JsonCombatStrategy strategy, ICombatFlowGame game,
        Func<SkillCatalogSnapshot> readSnapshot, TimeProvider? clock = null, ILogger? logger = null) =>
        JsonCombatFlowExecution.RequiresFlow(strategy) ? new(strategy, game,
            ReadSnapshot(readSnapshot, logger ?? NullLogger.Instance), clock) : null;

    public static NativeCombatFlowRunner? Create(JsonCombatStrategy strategy, CombatScenes scenes)
    {
        if (!JsonCombatFlowExecution.RequiresFlow(strategy)) return null;
        var runner = Create(strategy, new NativeGame(scenes), () => CombatSkillCatalog.Default.Store.ReadSnapshot(), logger: Logger)!;
        try
        {
            ValidateActors(runner._jsonExecution!.Actors, scenes);
            return runner;
        }
        catch { runner.Dispose(); throw; }
    }

    public static NativeCombatFlowRunner? Create(IReadOnlyList<CombatCommand> commands, CombatScenes scenes, bool loop)
    {
        if (!commands.Any(command => command.RequiresFlow)) return null;
        var names = commands.Where(command => !command.Method.IsFlowControl).Select(command => command.Name)
            .Where(name => name != CombatScriptParser.CurrentAvatarName).ToHashSet();
        if (!names.IsSubsetOf(scenes.GetAvatars().Select(avatar => avatar.Name).ToHashSet()))
            throw new InvalidOperationException("增强策略缺少所需角色，不能按部分队伍执行");
        var program = CombatFlowProgram.Compile(new CombatScript(names, commands.ToList()), ReadSnapshot());
        ValidateActors(program.Actors, scenes);
        if (program.Loop && !loop) throw new InvalidOperationException("单次路径策略没有战斗结束宿主，不能运行 loop=battle");
        program.AllowHostLoop(loop);
        return new(program, scenes);
    }

    private static SkillCatalogSnapshot? ReadSnapshot() =>
        ReadSnapshot(() => CombatSkillCatalog.Default.Store.ReadSnapshot(), Logger);

    private static SkillCatalogSnapshot? ReadSnapshot(Func<SkillCatalogSnapshot> readSnapshot, ILogger logger)
    {
        SkillCatalogSnapshot? snapshot = null;
        try { snapshot = readSnapshot(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or Newtonsoft.Json.JsonException)
        {
            logger.LogWarning("技能数据库不可用，尝试策略头部时间定义：{Reason}", exception.Message);
        }
        return snapshot;
    }

    private static void ValidateActors(IEnumerable<string> actors, CombatScenes scenes)
    {
        var missing = actors.Where(name => name != CombatScriptParser.CurrentAvatarName)
            .Except(scenes.GetAvatars().Select(avatar => avatar.Name)).ToArray();
        if (missing.Length != 0) throw new InvalidOperationException("增强策略的动作/接球角色不在当前队伍：" + string.Join("、", missing));
    }

    public void Step(CancellationToken ct) => StepAsync(ct).AsTask().GetAwaiter().GetResult();

    public async ValueTask<CombatFlowStep> StepAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // 整步（包含产球后的接球）独占；进入 atomic 后跨 Step 保留，退出再交还普通观察。
        if (_game is NativeGame) _exclusive ??= AvatarRecognition.BeginExclusiveOperation();
        try
        {
            var step = await (_execution?.StepAsync(ct) ?? _jsonExecution!.StepAsync(ct));
            if (step.RoundCompleted && step.Result == CombatFlowResult.Failed)
                throw new CombatFlowRecoveryException("增强战斗关键流程未满足，停止本场并交还宿主恢复");
            return step;
        }
        catch { EndExclusive(); throw; }
        finally { if (!IsAtomic) EndExclusive(); }
    }

    private void EndExclusive() => Interlocked.Exchange(ref _exclusive, null)?.Dispose();
    public bool HasVisibleTarget => AutoFightSeek.TryCreatePassiveDecision(AvatarRecognition.LatestPassiveObservation,
        DateTime.UtcNow, out _, out _, out _);
    public ValueTask<CombatFlowResult> RunRoundAsync(CancellationToken ct) => _execution!.RunRoundAsync(ct);
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _execution?.Dispose();
            _jsonExecution?.Dispose();
            if (_game is NativeGame)
            {
                var statistics = RuntimeStatistics;
                Logger.LogInformation("战斗流程诊断：{Steps} 步、{Actions} 次游戏执行调用、{Observations} 次观察调用、{FailedPasses} 次失败遍历；核心步总耗时 {Milliseconds:F1}ms。调用计数不是命中或施放成功次数。",
                    statistics.CoreSteps, statistics.GameActionCalls, statistics.GameObservationCalls,
                    statistics.FailedPasses, statistics.CoreStepMilliseconds);
            }
        }
        finally
        {
            try { (_game as IDisposable)?.Dispose(); }
            finally { EndExclusive(); }
        }
    }

    private sealed class NativeGame(CombatScenes scenes) : ICombatFlowGame, IDisposable
    {
        private ImageRegion? _capture;
        private readonly Dictionary<(string Function, string Actor), object?> _observations = new();
        private HashSet<int>? _sideBurstReady;
        private CombatSkillAttempts? _attempts;
        private long _frameId;
        private CombatInputCoordinator.Session? _input;
        private bool _disposed;

        public void BeginStep() => ClearCapture();

        public void ReleaseHeldInput()
        {
            if (_disposed || _input == null) return;
            // 释放仍受本场输入所有权保护；不由旧场的裸回调释放新持有者。
            // 并发关闭时活动输入尚未退出则交由 Session.Dispose 的完成边界释放。
            _input.TryReleaseInput(Simulation.ReleaseAllKey);
        }

        public ValueTask PrepareObservationAsync(CombatFlowAction action, string function, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!action.CanStart || scenes.SelectAvatar(action.Command.Name) is not { } avatar) return ValueTask.CompletedTask;
            _input ??= InputCoordinator.TryAcquire(action.BattleId, ReleaseOwnedInput)
                ?? throw new InvalidOperationException("上一场输入尚未退出或释放，禁止观测准备接管输入");
            using var operation = _input.EnterOperation();
            using var exclusive = AvatarRecognition.BeginExclusiveOperation();
            using var scope = new CombatActionScope(action, ct);
            try
            {
                ClearCapture();
                if (avatar.TrySwitch(3))
                {
                    action.ReportActiveActor(avatar.Name);
                    ClearCapture();
                    scope.Check();
                    if (action.CanStart) _ = Observe(function, [], avatar.Name);
                }
            }
            catch (CombatActionInterruptedException) { Simulation.ReleaseAllKey(); }
            return ValueTask.CompletedTask;
        }

        public object? Observe(string function, IReadOnlyList<object?> args, string actor)
        {
            if (function == "q-energy-sample") return null; // 当前没有可验证的能量增量传感器。
            if (function == "last-check") return Math.Max(0, (DateTime.Now - AutoFightTask.LastFightFinishCheckTime).TotalSeconds);
            if (function is not ("in-party" or "onfield" or "e-ready" or "e-cd" or "q-ready" or "q-cd" or "q-energy-low" or "low-hp")) return null;
            var target = args.FirstOrDefault()?.ToString() ?? actor;
            if (string.IsNullOrWhiteSpace(target) || target == CombatScriptParser.CurrentAvatarName)
            {
                _capture ??= CaptureToRectArea();
                target = scenes.GetAvatars().FirstOrDefault(avatar => avatar.IsActive(_capture))?.Name ?? "";
            }
            if (_observations.TryGetValue((function, target), out var cached)) return cached;
            object? result;
            try { result = ObserveCurrent(function, target); }
            catch (Exception exception) when (exception is not (OperationCanceledException or CombatActionInterruptedException))
            {
                Logger.LogDebug(exception, "战斗观测 {Function}({Actor}) 不可用，保留 Unknown", function, target);
                result = null;
            }
            _observations[(function, target)] = result;
            return result;
        }

        private object? ObserveCurrent(string function, string target)
        {
            var avatar = scenes.SelectAvatar(target);
            if (function == "in-party") return avatar != null;
            if (avatar == null) return null;
            _capture ??= CaptureToRectArea();
            if (!_observations.TryGetValue(("onfield", target), out var observedActive))
                _observations[("onfield", target)] = observedActive = avatar.IsActive(_capture);
            var active = observedActive is true;
            if (function == "onfield") return active;
            if (function is "e-ready" or "e-cd")
            {
                if (active)
                {
                    var cd = avatar.ReadSkillCurrentCd(_capture);
                    _observations[("e-cd", target)] = cd > 0 ? cd : null;
                    var ready = avatar.IsSkillReadyFromCurrentFrame(_capture, cd);
                    _observations[("e-ready", target)] = ready;
                    return function == "e-cd" ? cd > 0 ? cd : null : ready;
                }
                // 后台的已知正 CD 能证明尚未就绪；计时归零不能证明当前 UI 真正可施放。
                return ESkillCdTracker.TryGetKnownRemainingCd(target, out var known) && known > 0
                    ? function == "e-ready" ? false : known : null;
            }
            if (!active)
            {
                if (function != "q-ready") return null;
                if (_sideBurstReady == null)
                {
                    // 旧侧栏算法会原地调整像素，必须隔离，不能污染本步的其他观测。
                    using var clone = new ImageRegion(_capture.SrcMat.Clone(), 0, 0);
                    _sideBurstReady = AutoFightSkill.AvatarQSkillAsync(clone).GetAwaiter().GetResult().ToHashSet();
                }
                return _sideBurstReady.Contains(avatar.Index) ? true : null;
            }
            if (function == "low-hp") return Bv.CurrentAvatarIsLowHp(_capture);
            if (function is "q-ready" or "q-cd" or "q-energy-low")
            {
                var burst = Avatar.ObserveBurst(_capture);
                _observations[("q-cd", target)] = burst.CoolingDown;
                _observations[("q-energy-low", target)] = burst.EnergyFull.HasValue ? !burst.EnergyFull.Value : null;
                _observations[("q-ready", target)] = burst.Ready ? true :
                    burst.CoolingDown == true || burst.EnergyFull == false ? false : null;
                return _observations[(function, target)];
            }
            // 尚无可靠观测的状态保持 Unknown，不能通过旧 bool API 把异常折叠为 false。
            return null;
        }

        public async ValueTask<CombatFlowResult> ExecuteAsync(CombatFlowAction action, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            var command = action.Command;
            if (!action.CanStart) return CombatFlowResult.Skipped;
            // 可选 Q 无可靠就绪证据时不为试探而切人；必要观察由核心在预算内显式准备。
            if (command.Method == Method.Burst && Observe("q-ready", [], command.Name) is not true)
                return CombatFlowResult.Skipped;
            ClearCapture();
            var name = command.Name == CombatScriptParser.CurrentAvatarName ? scenes.CurrentAvatar(true) : command.Name;
            if (name == null || scenes.SelectAvatar(name) is not { } avatar) return CombatFlowResult.Failed;
            if (command.Args?.Contains("fast") == true && ESkillCdTracker.TryGetKnownRemainingCd(name, out var cd) && cd > 0)
                return CombatFlowResult.Skipped;
            _input ??= InputCoordinator.TryAcquire(action.BattleId, ReleaseOwnedInput)
                ?? throw new InvalidOperationException("上一场输入尚未退出或释放，禁止新的战斗输入接管");
            using var operation = _input.EnterOperation();
            using var exclusive = AvatarRecognition.BeginExclusiveOperation();
            using var scope = new CombatActionScope(action, ct);
            try
            {
            if (!avatar.TrySwitch(3)) return CombatFlowResult.Failed;
            action.ReportActiveActor(name);
            ct.ThrowIfCancellationRequested();
            if (command.Method == Method.Skill && command.HasFlag("wait")) await avatar.WaitSkillCd(ct);
            _attempts ??= new(action.BattleId);
            if (_attempts.IsOccupied(name, command.Method))
            {
                using var capture = CaptureToRectArea();
                var observedCd = command.Method == Method.Skill ? avatar.ReadSkillCurrentCd(capture) : 0;
                var skillReady = command.Method == Method.Skill && avatar.IsSkillReadyFromCurrentFrame(capture, observedCd);
                var burst = command.Method == Method.Burst ? Avatar.ObserveBurst(capture) : default;
                bool? cooling = command.Method == Method.Skill
                    ? observedCd > 0 ? true : skillReady ? false : null : burst.CoolingDown;
                _attempts.Observe(name, command.Method, new(action.BattleId, ++_frameId, action.Now,
                    cooling, command.Method == Method.Skill ? skillReady : burst.Ready));
                if (_attempts.IsOccupied(name, command.Method)) return CombatFlowResult.Pending;
            }
            CombatSkillAttempt? attempt = null;
            bool BeginSkillInput()
            {
                if (!action.TryBeginInput()) return false;
                attempt = _attempts.TryBegin(name, command.Method,
                    $"{command.SourceFile}:{command.SourceLine}:{command.SourceColumn}", action.InputAt!.Value,
                    action.Now + action.RemainingBudget);
                return attempt != null;
            }
            if (command.Method == Method.Skill)
            {
                if (!avatar.IsSkillReadyFromCurrentFrame()) return CombatFlowResult.Skipped;
                var before = avatar.LastConfirmedSkillCastAtUtc;
                avatar.UseSkill(command.Args?.Contains("hold") == true, observeCooldown: true, tryBeginInput: BeginSkillInput);
                ct.ThrowIfCancellationRequested();
                if (action.InputAt == null) return CombatFlowResult.Skipped;
                if (avatar.LastConfirmedSkillCastAtUtc > before && attempt != null && _attempts.Confirm(attempt.AttemptId, action.Now))
                    return CombatFlowResult.Succeeded;
                return CombatFlowResult.Pending;
            }
            if (command.Method == Method.Burst)
            {
                var timeout = Math.Min(2.4, action.RemainingBudget);
                if (timeout <= 0) return CombatFlowResult.Skipped;
                var result = avatar.TryUseBurst(timeoutSeconds: timeout,
                    tryBeginInput: BeginSkillInput);
                if (result == BurstCastResult.Confirmed && attempt != null && _attempts.Confirm(attempt.AttemptId, action.Now))
                    return CombatFlowResult.Succeeded;
                if (action.InputAt != null) return CombatFlowResult.Pending;
                return result == BurstCastResult.NotReady ? CombatFlowResult.Skipped : CombatFlowResult.Unknown;
            }
            if (command.Method == Method.Wait)
            {
                if (!action.TryBeginInput()) return CombatFlowResult.Skipped;
                var seconds = double.Parse(command.Args![0], System.Globalization.CultureInfo.InvariantCulture);
                await scope.WaitAsync((int)Math.Ceiling(seconds * 1000));
                return CombatFlowResult.Succeeded;
            }
            var arguments = command.Args?.Where(argument => argument is not ("required" or "refresh")) ?? [];
            var primitive = new CombatCommand(command.Name, command.Method.Alias[0] + "(" + string.Join(",", arguments) + ")");
            if (!action.TryBeginInput()) return CombatFlowResult.Skipped;
            primitive.Execute(avatar);
            ct.ThrowIfCancellationRequested();
            return CombatFlowResult.Succeeded;
            }
            catch (CombatActionInterruptedException)
            {
                // 先在本场仍拥有输入时结束持续键/宏，再让调度器转移；不伪造动作完成。
                Simulation.ReleaseAllKey();
                if (action.InputAt != null && (command.Method == Method.Skill || command.Method == Method.Burst))
                {
                    return CombatFlowResult.Pending;
                }
                return CombatFlowResult.Skipped;
            }
        }

        public async ValueTask YieldAsync(CancellationToken ct)
        {
            ClearCapture();
            await Task.Delay(50, ct);
        }

        private void ClearCapture()
        {
            _capture?.Dispose();
            _capture = null;
            _observations.Clear();
            _sideBurstReady = null;
        }
        private void ReleaseOwnedInput()
        {
            ClearCapture();
            _attempts?.Dispose();
            Simulation.ReleaseAllKey();
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_input != null) _input.Dispose();
            else ClearCapture();
        }
    }
}
