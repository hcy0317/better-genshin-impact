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
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Model.Area;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

/// <summary>宿主拥有结束检测；此适配器只驱动一份本场流程，不启动后台输入循环。</summary>
internal sealed class NativeCombatFlowRunner : IDisposable
{
    internal const int SwitchAttempts = 10;
    private static readonly CombatInputCoordinator InputCoordinator = new();
    private readonly ICombatFlowGame _game;
    private readonly CombatFlowExecution? _execution;
    private readonly JsonCombatFlowExecution? _jsonExecution;
    private readonly ILogger _diagnosticLogger;
    private readonly CombatFlowDiagnosticWriter _diagnosticWriter;
    private string? _pendingDiagnosticBoundary;
    private IDisposable? _exclusive;
    private bool _disposed;
    private int _consecutiveFailedPasses;
    private long _lastFailedActionCalls = -1;
    private double _lastNewFailureAt;
    private bool _failureFinishCheckRequested;
    private string? _lastReportedMaintenanceDecision;
    private string? MaintenanceDecision => _execution?.LastMaintenanceDecision ?? _jsonExecution?.LastMaintenanceDecision;
    public CombatFlowContext Context => _execution?.Context ?? _jsonExecution!.Context;
    public CombatFlowStatistics RuntimeStatistics => _execution?.RuntimeStatistics ?? _jsonExecution!.RuntimeStatistics;
    public bool IsAtomic => _execution?.IsAtomic ?? _jsonExecution!.IsAtomic;
    public bool HasPendingConfirmation => _execution?.HasPendingConfirmation ?? _jsonExecution!.HasPendingConfirmation;
    public bool IsAtRootBoundary => _execution?.IsAtRootBoundary ?? _jsonExecution!.IsAtRootBoundary;
    public bool TakeFinishCheckRequest()
    {
        var requested = _execution?.TakeFinishCheckRequest() ?? _jsonExecution!.TakeFinishCheckRequest();
        var failedPass = _failureFinishCheckRequested;
        _failureFinishCheckRequested = false;
        return requested || failedPass;
    }

    private NativeCombatFlowRunner(CombatFlowProgram program, CombatScenes scenes) : this(program, new NativeGame(scenes), null) { }

    private NativeCombatFlowRunner(CombatFlowProgram program, ICombatFlowGame game, TimeProvider? clock)
    {
        _diagnosticLogger = game is NativeGame ? Logger : NullLogger.Instance;
        _diagnosticWriter = new(_diagnosticLogger);
        _game = game;
        _execution = new(program, _game, clock);
        if (game is NativeGame)
            foreach (var diagnostic in program.Diagnostics) Logger.LogWarning("战斗策略数据：{Diagnostic}", diagnostic);
    }

    internal static NativeCombatFlowRunner Create(CombatFlowProgram program, ICombatFlowGame game, TimeProvider? clock = null) =>
        new(program, game, clock);

    private NativeCombatFlowRunner(JsonCombatStrategy strategy, ICombatFlowGame game,
        SkillCatalogSnapshot? database, TimeProvider? clock, ILogger? logger = null)
    {
        _diagnosticLogger = logger ?? (game is NativeGame ? Logger : NullLogger.Instance);
        _diagnosticWriter = new(_diagnosticLogger);
        _game = game;
        _jsonExecution = new(strategy, game, database, clock);
        if (game is NativeGame)
            foreach (var diagnostic in _jsonExecution.Diagnostics) Logger.LogWarning("战斗策略数据：{Diagnostic}", diagnostic);
    }

    internal static NativeCombatFlowRunner? Create(JsonCombatStrategy strategy, ICombatFlowGame game,
        SkillCatalogSnapshot? database = null, TimeProvider? clock = null, ILogger? logger = null) =>
        JsonCombatFlowExecution.RequiresFlow(strategy) ? new(strategy, game, database, clock, logger) : null;

    internal static NativeCombatFlowRunner? Create(JsonCombatStrategy strategy, ICombatFlowGame game,
        Func<SkillCatalogSnapshot> readSnapshot, TimeProvider? clock = null, ILogger? logger = null) =>
        JsonCombatFlowExecution.RequiresFlow(strategy) ? new(strategy, game,
            ReadSnapshot(readSnapshot, logger ?? NullLogger.Instance), clock, logger) : null;

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
        ct.ThrowIfCancellationRequested();
        // 整步（包含产球后的接球）独占；进入 atomic 后跨 Step 保留，退出再交还普通观察。
        if (_game is NativeGame) _exclusive ??= AvatarRecognition.BeginExclusiveOperation(allowPassiveObservation: true);
        try
        {
            TaskExecutionScope.ThrowIfFailed();
            var stalled = _consecutiveFailedPasses > 0 &&
                Context.Now - _lastNewFailureAt >= CombatFlowPolicy.EpisodeTimeoutSeconds &&
                RuntimeStatistics.GameActionCalls == _lastFailedActionCalls;
            if (_consecutiveFailedPasses >= 3 || stalled)
            {
                // 先核实新画面中的败北，不能把复苏弹窗当作第三轮普通流程失败。
                _game.CheckDefeated(ct);
                ct.ThrowIfCancellationRequested();
                TaskExecutionScope.StopUnconfirmedCombat(_consecutiveFailedPasses >= 3
                    ? "增强战斗连续 3 轮关键流程失败，不执行复活重试"
                    : "增强战斗失败后持续无新执行进展，恢复观察时限已到；不切队或传送。" + MaintenanceDecision);
            }
            var step = await (_execution?.StepAsync(ct) ?? _jsonExecution!.StepAsync(ct));
            if (_game is NativeGame && MaintenanceDecision is { } decision && decision != _lastReportedMaintenanceDecision)
            {
                _lastReportedMaintenanceDecision = decision;
                Logger.LogInformation("战斗 {BattleId} 维护恢复：{Decision}", Context.BattleId, decision);
            }
            if (step.RoundCompleted && step.Result == CombatFlowResult.Failed)
            {
                _pendingDiagnosticBoundary = "failed-pass";
                _game.CheckDefeated(ct);
                ct.ThrowIfCancellationRequested();
                var actionCalls = RuntimeStatistics.GameActionCalls;
                var newAttempt = actionCalls != _lastFailedActionCalls;
                if (newAttempt)
                {
                    _consecutiveFailedPasses++;
                    _lastFailedActionCalls = actionCalls;
                    _lastNewFailureAt = Context.Now;
                }
                _failureFinishCheckRequested |= newAttempt;
                if (_game is NativeGame && newAttempt)
                    Logger.LogWarning("战斗 {BattleId} 第 {Failures}/3 轮关键流程失败，未确认结束；保留本场记录并交回宿主检测，不传送",
                        Context.BattleId, _consecutiveFailedPasses);
                // 秘境/幽境的独立检测器按约一秒节拍工作；失败时让出执行线程，且保留取消。
                if (!IsAtomic) EndExclusive();
                await _game.WaitAfterFailedPassAsync(ct);
            }
            else if (step.RoundCompleted && step.Result == CombatFlowResult.Deferred)
            {
                // 暂未确认的施放/维护让出不是失败，也不能假装恢复成功清除既有失败。
                // Pending/Deferred is ordinary waiting, not a new request to open
                // the party menu. The host retains its periodic finish detector.
                if (!IsAtomic) EndExclusive();
                await _game.YieldAsync(ct);
            }
            else if (step.RoundCompleted && step.Result == CombatFlowResult.Succeeded &&
                (_execution?.LastRoundHadAction ?? _jsonExecution!.LastRoundHadAction))
            {
                _consecutiveFailedPasses = 0;
                _lastFailedActionCalls = -1;
            }
            return step;
        }
        catch { _pendingDiagnosticBoundary = "exception"; EndExclusive(); throw; }
        finally { if (!IsAtomic) EndExclusive(); }
    }

    private void EndExclusive()
    {
        var exclusive = Interlocked.Exchange(ref _exclusive, null);
        if (exclusive != null)
        {
            if (_game is NativeGame native) native.InvalidateActorConfirmation();
            exclusive.Dispose();
        }
        if (_pendingDiagnosticBoundary is { } boundary)
        {
            _pendingDiagnosticBoundary = null;
            WriteDiagnosticTrace(boundary);
        }
    }

    private void WriteDiagnosticTrace(string boundary)
    {
        if (CombatFlowDiagnosticWriter.IsEnabled(_diagnosticLogger))
            _diagnosticWriter.Write(Context.BattleId, RuntimeStatistics, boundary);
    }
    public bool HasVisibleTarget => AutoFightSeek.TryCreatePassiveDecision(AvatarRecognition.LatestPassiveObservation,
        DateTime.UtcNow, out _, out _, out _);

    internal static CombatSkillObservation GatePendingSkillObservation(CombatSkillObservation sample, bool expectedActorActive)
        => expectedActorActive ? sample : sample with { CoolingDown = null, Ready = null };

    internal static CombatFlowResult? ReconcilePendingSkill(CombatSkillAttempts attempts, CombatFlowAction action,
        string actor, Func<CombatSkillObservation> capture)
    {
        if (action.IsConfirmationOnly && attempts.GetAttempt(actor, action.Command.Method)?.AttemptId != action.PendingAttempt?.AttemptId)
        {
            action.DiagnosticReason = "原请求已释放或替换，仅确认步骤不能领取其他请求或重发输入";
            return CombatFlowResult.Skipped;
        }
        var before = attempts.GetState(actor, action.Command.Method, action.Now);
        if (before == CombatSkillAttemptState.Empty) return null;
        if (!action.CanStart) return action.RemainingBudget <= 0 ? CombatFlowResult.Failed : CombatFlowResult.Skipped;
        attempts.Observe(actor, action.Command.Method, capture());
        var after = attempts.GetState(actor, action.Command.Method, action.Now);
        if (after == CombatSkillAttemptState.Empty) return null;
        if (before == CombatSkillAttemptState.Expired || after == CombatSkillAttemptState.Expired)
        {
            action.DiagnosticReason = "原技能请求确认预算耗尽，未确认新的施放，不重发输入";
            return CombatFlowResult.Failed;
        }
        var confirmation = attempts.TakeConfirmation(actor, action.Command.Method, action.CommandId, action.Now);
        if (confirmation != null && action.AcceptConfirmation(confirmation))
        {
            action.DiagnosticReason = $"迟到冷却确认原请求，原输入时点 {confirmation.InputAt:F3}s；本步没有发送技能输入";
            return CombatFlowResult.Succeeded;
        }
        action.DiagnosticReason = after == CombatSkillAttemptState.Confirmed
            ? "原施放已确认且仍处于冷却，不重复完成或发送技能输入"
            : "已有未决施放请求，等待原请求的新证据，不重复发送技能输入";
        return after == CombatSkillAttemptState.Confirmed ? CombatFlowResult.Deferred : CombatFlowResult.Pending;
    }
    public async ValueTask<CombatFlowResult> RunRoundAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_game is NativeGame) _exclusive ??= AvatarRecognition.BeginExclusiveOperation(allowPassiveObservation: true);
        try { return await _execution!.RunRoundAsync(ct); }
        finally { EndExclusive(); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _execution?.Dispose();
            _jsonExecution?.Dispose();
        }
        finally
        {
            try { (_game as IDisposable)?.Dispose(); }
            finally
            {
                EndExclusive();
                // 接收器只在输入所有权释放后调用，不能延长 atomic/角色独占。
                WriteDiagnosticTrace("dispose");
                try
                {
                    var statistics = RuntimeStatistics;
                    _diagnosticLogger.LogInformation("战斗流程诊断：{Steps} 步、{Actions} 次游戏执行调用、{Observations} 次观察调用、{FailedPasses} 次失败遍历；核心步总耗时 {Milliseconds:F1}ms。调用计数不是命中或施放成功次数。",
                        statistics.CoreSteps, statistics.GameActionCalls, statistics.GameObservationCalls,
                        statistics.FailedPasses, statistics.CoreStepMilliseconds);
                }
                catch { /* 诊断不能覆盖原失败或取消。 */ }
            }
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
        private string? _confirmedActor;

        public void InvalidateActorConfirmation() => _confirmedActor = null;

        public void BeginStep() => ClearCapture();
        public void CheckDefeated(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            TaskExecutionScope.ThrowIfFailed();
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var recoveryBudget = CombatActionScope.Suspend();
            // 不能凭截图失败后的缓存帧取得恢复许可。
            var image = CaptureGameImageNoRetry(TaskTriggerDispatcher.GlobalGameCapture);
            if (image == null) return;
            if (image.Empty()) { image.Dispose(); return; }
            using var capture = new CaptureContent(image, 0, 0).CaptureRectArea;
            if (Bv.IsInRevivePrompt(capture))
            {
                ct.ThrowIfCancellationRequested();
                ReleaseHeldInput();
                Avatar.ThrowWhenDefeated(capture, ct);
                return;
            }
            if (_attempts == null || !NativeUiDriver.Read(capture).Matches(UiTarget.Main)) return;
            // 原角色倒下后游戏可能自动换人，不会自行弹出复苏框。每个在途请求只探查一次，
            // 仅重新选择原角色；必须由真实复苏框取得恢复许可，不清空旧请求或伪造施放成功。
            var pending = _attempts.TakeInactiveActorProbe(actor =>
                scenes.SelectAvatar(actor) is { IndexRect: var rect } avatar && rect != default
                    ? avatar.IsActive(capture) : null);
            if (pending == null) return;
            ct.ThrowIfCancellationRequested();
            TaskExecutionScope.ThrowIfFailed();
            Logger.LogWarning("未决技能角色 {Actor} 已不在前台，有界重选以核实复苏状态，request={Request}",
                pending.Actor, pending.AttemptId);
            ReleaseHeldInput();
            _confirmedActor = null;
            scenes.SelectAvatar(pending.Actor)?.TrySwitch(4);
        }
        public bool HasPendingSkill(CombatFlowAction action) =>
            _attempts?.HasUnresolved(action.Command.Name, action.Command.Method) == true;

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
            action.CaptureDiagnostics = CombatFlowDiagnosticWriter.IsEnabled(Logger);
            action.Trace("prepare-observation", function);
            if (action.CaptureDiagnostics && _attempts != null)
                action.DiagnosticAttemptId = _attempts.GetDiagnosticAttemptId(action.Command.Name, action.Command.Method);
            if (!action.CanStart || scenes.SelectAvatar(action.Command.Name) is not { } avatar) return ValueTask.CompletedTask;
            _input ??= InputCoordinator.TryAcquire(action.BattleId, ReleaseOwnedInput)
                ?? throw new InvalidOperationException("上一场输入尚未退出或释放，禁止观测准备接管输入");
            using var operation = _input.EnterOperation();
            using var exclusive = AvatarRecognition.BeginExclusiveOperation(allowPassiveObservation: true);
            using var scope = new CombatActionScope(action, ct);
            try
            {
                ClearCapture();
                _confirmedActor = null;
                if (avatar.TrySwitch(SwitchAttempts))
                {
                    _confirmedActor = avatar.Name;
                    action.ReportActiveActor(avatar.Name);
                    ClearCapture();
                    scope.Check();
                    if (action.CanStart) _ = Observe(function, [], avatar.Name);
                }
            }
            catch (CombatActionInterruptedException) { Simulation.ReleaseAllKey(); }
            return ValueTask.CompletedTask;
        }

        public async ValueTask<CombatSkillAttempt?> TryRecoverExpiredSkillAsync(CombatFlowAction action, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            action.CaptureDiagnostics = CombatFlowDiagnosticWriter.IsEnabled(Logger);
            var command = action.Command;
            if (action.CaptureDiagnostics && _attempts != null)
                action.DiagnosticAttemptId = _attempts.GetDiagnosticAttemptId(command.Name, command.Method);
            if (_attempts == null || !action.CanStart ||
                command.Method != Method.Skill && command.Method != Method.Burst ||
                _attempts.GetState(command.Name, command.Method, action.Now) != CombatSkillAttemptState.Expired ||
                scenes.SelectAvatar(command.Name) is not { } avatar) return null;
            await PrepareObservationAsync(action, command.Method == Method.Skill ? "e-ready" : "q-ready", ct);
            if (!action.CanStart || _confirmedActor != avatar.Name || _input == null) return null;
            using var operation = _input.EnterOperation();
            using var exclusive = AvatarRecognition.BeginExclusiveOperation(allowPassiveObservation: true);
            using var scope = new CombatActionScope(action, ct);

            CombatSkillObservation Read()
            {
                try
                {
                ct.ThrowIfCancellationRequested();
                // Do not use the cached-frame fallback to grant a fresh attempt.
                var image = CaptureGameImageNoRetry(TaskTriggerDispatcher.GlobalGameCapture);
                if (image == null) return new(action.BattleId, ++_frameId, action.Now, null, null);
                if (image.Empty()) { image.Dispose(); return new(action.BattleId, ++_frameId, action.Now, null, null); }
                using var capture = new CaptureContent(image, 0, 0).CaptureRectArea;
                if (!avatar.IsActive(capture)) return new(action.BattleId, ++_frameId, action.Now, null, null);
                if (command.Method == Method.Skill)
                {
                    var cd = avatar.ReadSkillCurrentCd(capture);
                    var ready = avatar.IsSkillReadyFromCurrentFrame(capture, cd);
                    return new(action.BattleId, ++_frameId, action.Now, cd > 0 ? true : ready ? false : null, ready);
                }
                var burst = Avatar.ObserveBurst(capture, expectedActorActive: true);
                return new(action.BattleId, ++_frameId, action.Now, burst.CoolingDown, burst.Ready);
                }
                catch (Exception error) when (error is not OperationCanceledException and not CombatNotFinishedException
                    and not CombatRecoveryCompletedException and not CombatActionInterruptedException)
                {
                    Logger.LogDebug(error, "恢复探测截图/技能识别不可用，保留Unknown");
                    return new(action.BattleId, ++_frameId, action.Now, null, null);
                }
            }

            var first = Read();
            await Task.Delay(250, ct);
            if (!action.CanStart) return null;
            var second = Read();
            ct.ThrowIfCancellationRequested();
            if (!action.CanStart) return null;
            var reset = _attempts.TryReleaseExpiredReady(avatar.Name, command.Method, first, second);
            action.DiagnosticReason = $"{avatar.Name} 过期技能恢复探测：双帧就绪={first.Ready}/{second.Ready}，冷却={first.CoolingDown}/{second.CoolingDown}，释放旧请求={reset != null}";
            return reset;
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
                var burst = Avatar.ObserveBurst(_capture, expectedActorActive: true);
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
            action.CaptureDiagnostics = CombatFlowDiagnosticWriter.IsEnabled(Logger);
            var command = action.Command;
            if (!action.CanStart)
            {
                action.DiagnosticReason = "动作预算或前置条件不再满足";
                return CombatFlowResult.Skipped;
            }
            ClearCapture();
            var name = command.Name == CombatScriptParser.CurrentAvatarName ? scenes.CurrentAvatar(true) : command.Name;
            if (name == null || scenes.SelectAvatar(name) is not { } avatar) return CombatFlowResult.Failed;
            _attempts ??= new(action.BattleId);
            if (action.IsConfirmationOnly && _attempts.GetAttempt(name, command.Method)?.AttemptId != action.PendingAttempt?.AttemptId)
                return CombatFlowResult.Skipped;
            if (action.CaptureDiagnostics)
                action.DiagnosticAttemptId = _attempts.GetDiagnosticAttemptId(name, command.Method);
            var hasUnresolved = _attempts.HasUnresolved(name, command.Method);
            action.Trace("dispatch", $"unresolved={hasUnresolved} confirmedActor={_confirmedActor}");
            // 先允许原请求领取迟到证据。就绪/CD 门禁只控制新输入，不能挡住已发送请求的确认。
            if (!hasUnresolved && command.Method == Method.Burst && Observe("q-ready", [], name) is not true)
            {
                action.DiagnosticReason = "Q 没有可靠的当前就绪证据";
                return CombatFlowResult.Deferred;
            }
            if (!hasUnresolved && command.Method == Method.Skill && command.Args?.Contains("fast") == true
                && ESkillCdTracker.TryGetKnownRemainingCd(name, out var cd) && cd > 0)
            {
                action.DiagnosticReason = $"fast 动作仍有已知 E 冷却 {cd:F2}s";
                return CombatFlowResult.Deferred;
            }
            _input ??= InputCoordinator.TryAcquire(action.BattleId, ReleaseOwnedInput)
                ?? throw new InvalidOperationException("上一场输入尚未退出或释放，禁止新的战斗输入接管");
            using var operation = _input.EnterOperation();
            using var exclusive = AvatarRecognition.BeginExclusiveOperation(allowPassiveObservation:
                command.Method == Method.Skill || command.Method == Method.Burst || command.Method == Method.Attack ||
                command.Method == Method.Charge || command.Method == Method.Wait);
            using var scope = new CombatActionScope(action, ct);
            try
            {
            if (!action.CanReuseConfirmedActor || _confirmedActor != name)
            {
                _confirmedActor = null;
                if (!avatar.TrySwitch(SwitchAttempts))
                {
                    action.DiagnosticReason = "在切换窗口内未取得连续两帧角色确认";
                    return CombatFlowResult.Failed;
                }
                _confirmedActor = name;
            }
            action.ReportActiveActor(name);
            ct.ThrowIfCancellationRequested();
            var pendingCd = 0d;
            var pendingResult = ReconcilePendingSkill(_attempts, action, name, () =>
            {
                using var capture = CaptureToRectArea();
                var observedCd = command.Method == Method.Skill ? avatar.ReadSkillCurrentCd(capture) : 0;
                pendingCd = observedCd;
                var skillReady = command.Method == Method.Skill && avatar.IsSkillReadyFromCurrentFrame(capture, observedCd);
                var active = avatar.IsActive(capture);
                var burst = command.Method == Method.Burst ? Avatar.ObserveBurst(capture, active) : default;
                bool? cooling = command.Method == Method.Skill
                    ? observedCd > 0 ? true : skillReady ? false : null : burst.CoolingDown;
                var sample = new CombatSkillObservation(action.BattleId, ++_frameId, action.Now,
                    cooling, command.Method == Method.Skill ? skillReady : burst.Ready);
                action.Trace("pending-observation", $"actorActive={active} cd={observedCd:F3} cooling={sample.CoolingDown} ready={sample.Ready}");
                return GatePendingSkillObservation(sample, active);
            });
            if (pendingResult != null)
            {
                if (pendingResult == CombatFlowResult.Succeeded && command.Method == Method.Skill)
                    avatar.ConfirmSkillUsed(pendingCd,
                        DateTime.UtcNow.AddSeconds(-(action.Now - action.EffectiveInputAt!.Value)));
                return pendingResult.Value;
            }
            if (action.IsConfirmationOnly) return CombatFlowResult.Skipped;
            if (command.Method == Method.Skill && command.HasFlag("wait")) await avatar.WaitSkillCd(ct);
            CombatSkillAttempt? attempt = null;
            bool BeginSkillInput()
            {
                if (!action.TryBeginInput()) return false;
                attempt = _attempts.TryBegin(name, command.Method,
                    action.CommandId, action.InputAt!.Value,
                    action.AbsoluteDeadline);
                if (attempt != null && !action.RegisterPendingAttempt(attempt))
                    throw new InvalidOperationException("物理技能请求与当前动作身份不一致");
                action.DiagnosticAttemptId = attempt?.AttemptId;
                action.Trace("input-admission", $"accepted={attempt != null} deadline={attempt?.Deadline:F3}");
                return attempt != null;
            }
            if (command.Method == Method.Skill)
            {
                if (!avatar.IsSkillReadyFromCurrentFrame())
                {
                    action.DiagnosticReason = "当前截图未确认该角色 E 就绪";
                    return CombatFlowResult.Deferred;
                }
                var before = avatar.LastConfirmedSkillCastAtUtc;
                avatar.UseSkill(command.Args?.Contains("hold") == true, observeCooldown: true, tryBeginInput: BeginSkillInput);
                ct.ThrowIfCancellationRequested();
                if (action.InputAt == null) return CombatFlowResult.Skipped;
                if (avatar.LastConfirmedSkillCastAtUtc > before && attempt != null && _attempts.Confirm(attempt.AttemptId, action.Now))
                    return CombatFlowResult.Succeeded;
                action.DiagnosticReason = "已发送 E 输入，尚未确认新的施放冷却";
                return CombatFlowResult.Pending;
            }
            if (command.Method == Method.Burst)
            {
                var timeout = Math.Min(2.4, action.RemainingBudget);
                if (timeout <= 0) return CombatFlowResult.Skipped;
                var result = avatar.TryUseBurst(timeoutSeconds: timeout,
                    tryBeginInput: BeginSkillInput);
                action.Trace("q-result", result.ToString());
                action.DiagnosticReason = $"Q 初次施放协议结果：{result}；仅新冷却证据可确认原请求";
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
            // 原始按键可能包含角色切换；下一条宏原语不能复用按键前的角色身份。
            if (command.Method == Method.KeyPress || command.Method == Method.KeyDown || command.Method == Method.KeyUp)
                _confirmedActor = null;
            ct.ThrowIfCancellationRequested();
            return CombatFlowResult.Succeeded;
            }
            catch (CombatActionInterruptedException)
            {
                action.DiagnosticReason = $"动作被维护/条件/预算边界中断，剩余预算 {action.RemainingBudget:F3}s";
                // 先在本场仍拥有输入时结束持续键/宏，再让调度器转移；不伪造动作完成。
                Simulation.ReleaseAllKey();
                if (action.RemainingBudget <= 0) return CombatFlowResult.Failed;
                if (action.InputAt != null && (command.Method == Method.Skill || command.Method == Method.Burst))
                {
                    return CombatFlowResult.Pending;
                }
                return CombatFlowResult.Deferred;
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
