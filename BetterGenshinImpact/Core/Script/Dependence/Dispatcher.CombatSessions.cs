using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Input;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using Fischless.GameCapture;

namespace BetterGenshinImpact.Core.Script.Dependence;

internal sealed record CombatSessionDependencies(TimeProvider Clock, Func<InputSessionOwnership.Owner> AcquireInput,
    Func<CombatScript, InputSessionOwnership.Owner, Action<Guid>, CancellationToken, Task> Run);

public partial class Dispatcher
{
    private readonly CombatSessionDependencies? _combatSessions;

    public string ReadCombatSessionProtocol() => "bgi.external-combat.v1";

    /// <summary>一个外部目标对应一个受脚本生命周期管理的战斗；不调用JS回调。</summary>
    public CombatSessionHandle CreateCombatSession(string script, string targetId, CaptureFrameStamp initialSource,
        int initialCount, int total, int timeoutMilliseconds = 30000)
    {
        using var ownedTask = _taskGuard.Enter();
        var lifetime = _scriptLifetime ?? throw new InvalidOperationException("持续会话必须属于活动脚本");
        lifetime.Check();
        ArgumentException.ThrowIfNullOrWhiteSpace(script);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        if (targetId.Length > 128 || initialCount < 0 || total is < 1 or > 2 || initialCount >= total ||
            timeoutMilliseconds is < 1 or > 30000)
            throw new ArgumentException("无效的哨塔目标、起点进度或总预算");
        var dependencies = _combatSessions ?? new(TimeProvider.System, InputHub.AcquireSession,
            CombatScriptExecutor.ExecuteExternalSessionAsync);
        if (!initialSource.IsFresh(dependencies.Clock, TimeSpan.FromSeconds(1)))
            throw new InvalidOperationException("BGI_COMBAT_EVIDENCE_UNAVAILABLE: 起点缺少新鲜源帧");
        var parsed = ParseCombatScript(script, null, "Dispatcher.CreateCombatSession", "memory:external-progress");
        var owner = dependencies.AcquireInput();
        try { return CombatSessionHandle.Start(lifetime, parsed, targetId, initialSource, initialCount, total, timeoutMilliseconds, owner, dependencies); }
        catch { owner.Dispose(); throw; }
    }
}

public sealed record CombatSessionResult(string Status, string Reason, string SessionId, string BattleId);

/// <summary>JS只持有观察/停止句柄，不暴露输入能力；WaitAsync完成意味着真实runner和清理都已终结。</summary>
public sealed class CombatSessionHandle
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource<CombatSessionResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenSource _timeout;
    private readonly CancellationToken _parent;
    private readonly CancellationTokenRegistration _parentRegistration, _timeoutRegistration;
    private readonly InputSessionOwnership.Owner _owner;
    private readonly CombatSessionDependencies _dependencies;
    private readonly int _baseline, _total;
    private CaptureFrameStamp _lastSource, _candidateSource;
    private int _lastCount, _candidateCount = -1, _acceptedObservations;
    private bool _candidateCompleted, _finished;
    private string? _requestedStatus, _reason;
    private Task? _cancelling;
    private string _battleId = "";

    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public string TargetId { get; }
    public bool IsCompleted => _completion.Task.IsCompleted;

    private CombatSessionHandle(CancellationToken parent, string targetId, CaptureFrameStamp source,
        int baseline, int total, int timeoutMilliseconds, InputSessionOwnership.Owner owner, CombatSessionDependencies dependencies)
    {
        _parent = parent; TargetId = targetId; _lastSource = source;
        _baseline = _lastCount = baseline; _total = total; _owner = owner; _dependencies = dependencies;
        _timeout = new(TimeSpan.FromMilliseconds(timeoutMilliseconds), dependencies.Clock);
        _parentRegistration = parent.Register(() => RequestStop("Cancelled", "parent-cancelled"));
        _timeoutRegistration = _timeout.Token.Register(() => RequestStop("TimedOut", "original-total-budget"));
    }

    internal static CombatSessionHandle Start(ScriptAsyncLifetime lifetime, CombatScript script, string targetId,
        CaptureFrameStamp source, int baseline, int total, int timeoutMilliseconds, InputSessionOwnership.Owner owner,
        CombatSessionDependencies dependencies)
    {
        var handle = new CombatSessionHandle(lifetime.Token, targetId, source, baseline, total, timeoutMilliseconds, owner, dependencies);
        try
        {
            foreach (var command in script.CombatCommands) command.SourceSessionId = handle.SessionId;
            lifetime.RegisterOperation(_ => handle.RunAsync(script), handle.Cancel);
            return handle;
        }
        catch
        {
            handle._parentRegistration.Dispose();
            handle._timeoutRegistration.Dispose();
            handle._timeout.Dispose();
            handle._stop.Dispose();
            throw;
        }
    }

    public bool SubmitProgress(string sessionId, string targetId, CaptureFrameStamp source, int current, int total,
        bool completed)
    {
        lock (_gate)
        {
            if (_finished || _requestedStatus != null || _parent.IsCancellationRequested) return false;
            if (sessionId != SessionId || targetId != TargetId ||
                !source.IsAfter(_lastSource) || !source.IsFresh(_dependencies.Clock, TimeSpan.FromSeconds(1))) return false;
            _lastSource = source;
            if (total != _total || (current >= 0 && (current < _lastCount || current > total)) ||
                current < -1 || (!completed && current < 0))
            { _candidateCount = -1; _candidateSource = default; return false; }
            _acceptedObservations++;
            if (current >= 0) _lastCount = Math.Max(_lastCount, current);
            if (!completed && current <= _baseline) { _candidateCount = -1; _candidateSource = default; return true; }
            if (!_candidateSource.IsKnown || _candidateCount != current || _candidateCompleted != completed)
            {
                _candidateCount = current; _candidateCompleted = completed; _candidateSource = source;
                return true;
            }
            if ((source.CapturedTimestamp - _candidateSource.CapturedTimestamp) / (double)source.TimestampFrequency < .2)
                return true;
            RequestStop("CompletedByExternalProgress", completed || current == total ? "commission-completed" : "target-progress-increased");
            return true;
        }
    }

    public void Cancel() => RequestStop("Cancelled", "session-cancelled");
    public Task<CombatSessionResult> WaitAsync() => _completion.Task;
    public async Task DisposeAsync()
    {
        if (!IsCompleted) Cancel();
        await _completion.Task.ConfigureAwait(false);
    }

    private void RequestStop(string status, string reason)
    {
        lock (_gate)
        {
            if (_finished) return;
            // 已观察取消优先于此前尚未收束的完成；完成绝不能覆盖取消。
            if (_parent.IsCancellationRequested) { status = "Cancelled"; reason = "parent-cancelled"; }
            if (_requestedStatus == null || status == "Cancelled")
            { _requestedStatus = status; _reason = reason; }
            _owner.CloseAdmission();
            _cancelling ??= _stop.CancelAsync();
        }
    }

    private void BindBattle(Guid battle)
    {
        lock (_gate)
        {
            if (battle == Guid.Empty || _battleId.Length != 0) throw new InvalidOperationException("持续会话只能绑定一场战斗");
            _battleId = battle.ToString("N");
        }
    }

    private async Task RunAsync(CombatScript script)
    {
        Exception? failure = null;
        try
        {
            // 只运行C#内核；所有JS帧/进度已经复制为值，不在后台触碰V8。
            await Task.Run(() => _dependencies.Run(script, _owner, BindBattle, _stop.Token)).ConfigureAwait(false);
            lock (_gate)
                if (_requestedStatus == null) failure = new InvalidOperationException("战斗内核在外部完成前意外退出");
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (InputSessionOwnership.RevokedException error) when (IsRequestedStopFor(error)) { }
        catch (Exception error) { failure = error; }
        finally
        {
            RequestStop("Failed", "runner-exited-before-completion");
            Task? cancelling;
            lock (_gate) cancelling = _cancelling;
            try { if (cancelling != null) await cancelling.ConfigureAwait(false); }
            catch (Exception error) { failure = Combine(failure, error); }
            // 只有真实runner返回后才释放输入，再处理注册和CTS；释放失败覆盖外部完成。
            try { _owner.Dispose(); }
            catch (Exception error) { failure = Combine(failure, error); }
            _parentRegistration.Dispose();
            _timeoutRegistration.Dispose();
            _timeout.Dispose();
            _stop.Dispose();
            lock (_gate)
            {
                var status = failure != null ? "Failed" : _parent.IsCancellationRequested ? "Cancelled" : _requestedStatus!;
                if (status == "TimedOut" && _acceptedObservations == 0) status = "EvidenceUnavailable";
                _finished = true;
                _completion.TrySetResult(new(status, failure?.Message ?? _reason!, SessionId, _battleId));
            }
        }
        // 即使JS忽略结果/不调用Wait，脚本退休也必须观察到原生失败。
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private bool IsRequestedStopFor(InputSessionOwnership.RevokedException error)
    {
        lock (_gate)
            return _requestedStatus != null && ReferenceEquals(error.RevokedOwner, _owner);
    }

    private static Exception Combine(Exception? first, Exception second) =>
        first == null ? second : new AggregateException(first, second);
}
