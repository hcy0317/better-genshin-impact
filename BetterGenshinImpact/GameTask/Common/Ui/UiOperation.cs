using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using BetterGenshinImpact.Core.Recognition;

namespace BetterGenshinImpact.GameTask.Common.Ui;

internal enum UiOperationPhase { Check, Pause, Focus, Admission, NativeInput, ExplicitWait, Capture, SceneRecognition, AreaOcr }

/// <summary>仅在当前UI调用链生效的预算与关联诊断，不建立后台观察/输入线程。</summary>
internal sealed class UiOperation : IDisposable
{
    private static readonly AsyncLocal<UiOperation?> Active = new();
    private readonly UiOperation? _parent;
    private readonly CancellationToken _callerToken;
    private readonly CancellationToken _userToken;
    private readonly CancellationTokenSource _deadline;
    private readonly CancellationTokenSource _linked;
    private readonly TimeProvider _clock;
    private readonly long _started;
    private readonly TimeSpan _budget;
    private readonly ILogger _logger;
    private readonly RecognitionExecutionScope _recognition;
    private int? _lastSignature;
    private double _lastStateLog = double.NegativeInfinity;
    private int _debugEvents, _suppressed;
    private bool _ended, _disposed;
    private string? _latestDescription;
    private readonly Queue<string> _stateHistory = new();
    private readonly Dictionary<string, (string First, string Last, int Count)> _milestones = new(StringComparer.Ordinal);
    private int _milestonesOmitted;
    private string? _firstInputReceipt, _lastInputReceipt;
    private readonly double?[] _phaseMilliseconds = new double?[Enum.GetValues<UiOperationPhase>().Length];
    private double _constructionMilliseconds, _logProducerMilliseconds;
    private double? _firstCheckAtMilliseconds, _dispatchMilliseconds;

    public static UiOperation? Current => Active.Value;
    public string Id { get; }
    public string RootId { get; }
    public string Name { get; }
    public CancellationToken Token => _linked.Token;
    public TimeSpan Elapsed => _clock.GetElapsedTime(_started);
    public TimeSpan Remaining => TimeSpan.FromMilliseconds(Math.Max(0, (_budget - Elapsed).TotalMilliseconds));
    public string Expected { get; private set; } = "operation-complete";

    private UiOperation(string name, TimeSpan budget, CancellationToken ct, ILogger? logger, TimeProvider? clock)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(budget, TimeSpan.Zero);
        _parent = Current;
        _clock = _parent?._clock ?? clock ?? TimeProvider.System;
        _started = _clock.GetTimestamp();
        Id = Guid.NewGuid().ToString("N");
        _parent?.Check();
        _callerToken = ct;
        _userToken = _parent?._userToken ?? ct;
        _budget = _parent == null || budget <= _parent.Remaining ? budget : _parent.Remaining;
        _deadline = new CancellationTokenSource(_budget, _clock);
        _linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _deadline.Token, _parent?.Token ?? default);
        _recognition = new RecognitionExecutionScope(_linked.Token);
        _logger = logger ?? _parent?._logger ?? NullLogger.Instance;
        Name = name;
        RootId = _parent?.RootId ?? Id;
        Active.Value = this;
        SafeLog(() => _logger.LogDebug("UI_BEGIN root={RootId} op={OpId} parent={ParentId} operation={Operation} budgetMs={BudgetMs:F0}",
            RootId, Id, _parent?.Id ?? "-", Name, _budget.TotalMilliseconds));
        _constructionMilliseconds = Elapsed.TotalMilliseconds;
    }

    internal static UiOperation Begin(string name, TimeSpan budget, CancellationToken ct = default,
        ILogger? logger = null, TimeProvider? clock = null) => new(name, budget, ct, logger, clock);

    internal static async Task<T> RunAsync<T>(string name, TimeSpan budget, CancellationToken ct,
        Func<UiOperation, Task<T>> work, ILogger? logger = null, TimeProvider? clock = null,
        Action<Exception, string>? captureFailure = null, IReadOnlyDictionary<string, string>? evidenceContext = null)
    {
        using var operation = Begin(name, budget, ct, logger, clock);
        if (evidenceContext != null)
            foreach (var item in evidenceContext.Take(8)) operation.RememberMilestone(item.Key, item.Value);
        try
        {
            operation.Check();
            var result = await work(operation);
            operation.Check();
            operation.End("completed");
            return result;
        }
        catch (Exception error)
        {
            var failure = operation.Translate(error);
            operation.End(failure is OperationCanceledException or NormalEndException ? "cancelled" : "failed", failure);
            if (failure is not OperationCanceledException and not NormalEndException && captureFailure != null)
            {
                try { captureFailure(failure, $"UI root={operation.RootId} op={operation.Id} {operation.Name}"); }
                catch { /* 现场保存失败不得覆盖原始失败。 */ }
            }
            ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }
    }

    public void Check()
    {
        _firstCheckAtMilliseconds ??= Elapsed.TotalMilliseconds;
        using var measured = Measure(UiOperationPhase.Check);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _userToken.ThrowIfCancellationRequested();
        TaskExecutionScope.ThrowIfFailed();
        if (_callerToken.IsCancellationRequested && !(_parent?.Token.IsCancellationRequested ?? false))
            _callerToken.ThrowIfCancellationRequested();
        if (Remaining <= TimeSpan.Zero || _deadline.IsCancellationRequested) throw Timeout();
        try { _parent?.Check(); }
        catch (TimeoutException parentTimeout)
        {
            var timeout = Timeout();
            timeout.Data["ParentUiTimeout"] = parentTimeout.Message;
            throw timeout;
        }
        Token.ThrowIfCancellationRequested();
    }

    private Exception Translate(Exception error)
    {
        if (error is OperationCanceledException or NormalEndException && !_userToken.IsCancellationRequested)
        {
            try { Check(); }
            catch (TimeoutException timeout)
            {
                // ClearScript 会取 GetBaseException；把截止产生的取消挂成 InnerException
                // 会让 JS 只看到 "A task was canceled"。业务错误保持为当前页面的 Timeout。
                timeout.Data["UiDeadlineCause"] = error.GetType().Name + ": " + error.Message;
                Debug(() => _logger.LogDebug(error, "UI_DEADLINE root={RootId} op={OpId} expected={Expected}",
                    RootId, Id, Expected));
                return timeout;
            }
            catch (OperationCanceledException) { }
        }
        return error;
    }

    public TimeoutException Timeout() => new($"界面转换超时：{Name}，期望={Expected}，实际={_latestDescription ?? "未取得观察"}，op={Id}");

    public async Task DelayAsync(int milliseconds, CancellationToken extraToken)
    {
        extraToken.ThrowIfCancellationRequested();
        Check();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(Token, extraToken);
        // 同步Sleep也会桥接到此处，不能依赖被GetResult阻塞的UI线程恢复continuation。
        using (Measure(UiOperationPhase.ExplicitWait))
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, milliseconds)), _clock, linked.Token).ConfigureAwait(false);
        extraToken.ThrowIfCancellationRequested();
        Check();
    }

    public void Observe(UiSnapshot snapshot, UiTarget expected, string phase = "observe")
    {
        Expected = expected.ToString();
        _latestDescription = snapshot.Describe();
        RememberMilestone("state:" + phase + ":" + Expected, $"frame={snapshot.FrameId} source={snapshot.SourceStamp.SessionId}/{snapshot.SourceStamp.Sequence} observed={_latestDescription}");
        var milestone = snapshot.Reward.IsCandidate ? "reward" : snapshot.DomainExit.Visible ? "exit-confirmation"
            : snapshot.MainReady ? snapshot.InDomain ? "domain-main" : "overworld-main" : "unknown-or-loading";
        RememberMilestone("scene:" + milestone, $"source={snapshot.SourceStamp.SessionId}/{snapshot.SourceStamp.Sequence} frame={snapshot.FrameId} observed={_latestDescription}");
        if (_lastSignature == snapshot.Signature && Elapsed.TotalSeconds - _lastStateLog < 2)
        { _suppressed++; return; }
        _lastSignature = snapshot.Signature;
        _lastStateLog = Elapsed.TotalSeconds;
        RememberState($"frame={snapshot.FrameId} captured={snapshot.CapturedAt:O} phase={phase} observed={_latestDescription}");
        Debug(() => _logger.LogDebug("UI_STATE root={RootId} op={OpId} phase={Phase} expected={Expected} frame={FrameId} captured={CapturedAt:O} observed={Observed} elapsedMs={ElapsedMs:F0} remainingMs={RemainingMs:F0}",
            RootId, Id, phase, Expected, snapshot.FrameId, snapshot.CapturedAt, snapshot.Describe(), Elapsed.TotalMilliseconds, Remaining.TotalMilliseconds));
    }

    internal async Task<bool> InvokeActionAsync(UiAction action, Func<Task<bool>> execute, int attempt, int maxAttempts)
    {
        using var input = new DiagnosticInputAttempt(_clock);
        bool applied = false;
        Exception? failure = null;
        try { applied = await execute(); return applied; }
        catch (Exception error) { failure = error; throw; }
        finally { Action(action, applied, attempt, maxAttempts, input.Complete(applied, failure)); }
    }

    public void Action(UiAction action, bool applied, int attempt, int maxAttempts, DiagnosticInputReceipt? receipt = null)
    {
        RememberMilestone("action:" + action, $"applied={applied} attempt={attempt}/{maxAttempts} elapsedMs={Elapsed.TotalMilliseconds:F0}");
        if (receipt != null) RememberInputReceipt(action, receipt);
        Debug(() => _logger.LogDebug("UI_ACTION root={RootId} op={OpId} action={Action} applied={Applied} attempt={Attempt}/{MaxAttempts} remainingMs={RemainingMs:F0}",
            RootId, Id, action, applied, attempt, maxAttempts, Remaining.TotalMilliseconds));
    }

    /// <summary>登记专用控制器的实际观察，仅用于诊断，不推断通用界面状态。</summary>
    public void Observe(string expected, string description, long frameId)
    {
        Expected = expected;
        _latestDescription = description;
        RememberMilestone("state:" + expected, $"frame={frameId} observed={description}");
        RememberState($"frame={frameId} expected={expected} observed={description}");
        var signature = HashCode.Combine(expected, description);
        if (_lastSignature == signature && Elapsed.TotalSeconds - _lastStateLog < 2)
        { _suppressed++; return; }
        _lastSignature = signature;
        _lastStateLog = Elapsed.TotalSeconds;
        Debug(() => _logger.LogDebug("UI_STATE root={RootId} op={OpId} expected={Expected} frame={FrameId} observed={Observed} remainingMs={RemainingMs:F0}",
            RootId, Id, Expected, frameId, description, Remaining.TotalMilliseconds));
    }

    private void Debug(Action emit)
    {
        if (_debugEvents++ >= 32) { _suppressed++; return; }
        SafeLog(emit);
    }

    private void End(string outcome, Exception? error = null)
    {
        if (_ended) return;
        _ended = true;
        if (outcome == "failed" || outcome == "cancelled" && (Name is "map-area-selection" or "return-main" or "exit-domain"))
            SafeLog(() => DiagnosticEvidenceScope.Current?.RequestLatestWindow("ui:" + Id, Name,
                $"root={RootId} op={Id} operation={Name} outcome={outcome}; expected={Expected}; observed={_latestDescription}; " +
                $"cancelCaller={_callerToken.IsCancellationRequested} cancelUser={_userToken.IsCancellationRequested} " +
                $"cancelDeadline={_deadline.IsCancellationRequested} cancelParent={_parent?.Token.IsCancellationRequested}; " +
                $"states=[{string.Join(" -> ", _stateHistory)}] milestonesOmitted={_milestonesOmitted}", _logger,
                EvidenceFields()));
        SafeLog(() => _logger.Log(error == null || outcome == "cancelled" ? LogLevel.Debug : LogLevel.Warning,
            error, "UI_END root={RootId} op={OpId} operation={Operation} outcome={Outcome} expected={Expected} observed={Observed} elapsedMs={ElapsedMs:F0} remainingMs={RemainingMs:F0} suppressed={Suppressed} constructionMs={ConstructionMs:F3} dispatchMs={DispatchMs:F3} firstCheckAtMs={FirstCheckAtMs:F3} checksMs={ChecksMs:F3} pauseMs={PauseMs:F3} focusMs={FocusMs:F3} admissionMs={AdmissionMs:F3} nativeInputMs={NativeInputMs:F3} explicitWaitMs={ExplicitWaitMs:F3} logProducerBeforeEndMs={LogProducerBeforeEndMs:F3} captureMs={CaptureMs:F3} sceneMs={SceneMs:F3} areaOcrMs={AreaOcrMs:F3}",
            RootId, Id, Name, outcome, Expected, _latestDescription ?? "未取得观察", Elapsed.TotalMilliseconds, Remaining.TotalMilliseconds, _suppressed,
            _constructionMilliseconds, _dispatchMilliseconds, _firstCheckAtMilliseconds,
            _phaseMilliseconds[(int)UiOperationPhase.Check], _phaseMilliseconds[(int)UiOperationPhase.Pause],
            _phaseMilliseconds[(int)UiOperationPhase.Focus], _phaseMilliseconds[(int)UiOperationPhase.Admission],
            _phaseMilliseconds[(int)UiOperationPhase.NativeInput], _phaseMilliseconds[(int)UiOperationPhase.ExplicitWait],
            _logProducerMilliseconds, _phaseMilliseconds[(int)UiOperationPhase.Capture],
            _phaseMilliseconds[(int)UiOperationPhase.SceneRecognition], _phaseMilliseconds[(int)UiOperationPhase.AreaOcr]));
    }

    private void RememberState(string state)
    {
        if (_stateHistory.Count >= 4) _stateHistory.Dequeue();
        _stateHistory.Enqueue(state.Length > 256 ? state[..256] : state);
    }

    private void RememberInputReceipt(UiAction action, DiagnosticInputReceipt receipt)
    {
        var value = $"action={action} {receipt.Describe()}";
        _firstInputReceipt ??= value;
        _lastInputReceipt = value;
        _parent?.RememberInputReceipt(action, receipt);
    }

    private IReadOnlyDictionary<string, string> EvidenceFields()
    {
        var fields = new Dictionary<string, string>();
        // 类型化回执独立于200字符里程碑，先入有界字段集以保留完整时间和原因。
        if (_firstInputReceipt != null) fields["input:first"] = _firstInputReceipt;
        if (_lastInputReceipt != null) fields["input:last"] = _lastInputReceipt;
        foreach (var item in _milestones)
            fields[item.Key] = $"count={item.Value.Count} first=[{item.Value.First}] last=[{item.Value.Last}]";
        return fields;
    }

    private void RememberMilestone(string key, string value)
    {
        // 固定数量且分字段保留首次/末次；连续loading不能挤掉已见的完成/领奖/退出动作。
        key = key.Length > 64 ? key[..64] : key;
        value = value.Length > 200 ? value[..200] : value;
        if (_milestones.TryGetValue(key, out var previous))
            _milestones[key] = (previous.First, value, previous.Count == int.MaxValue ? int.MaxValue : previous.Count + 1);
        else if (_milestones.Count < 16) _milestones.Add(key, (value, value, 1));
        else if (_milestonesOmitted < int.MaxValue) _milestonesOmitted++;
        _parent?.RememberMilestone(key, value);
    }

    public void RecordDispatch(TimeSpan elapsed) => _dispatchMilliseconds = elapsed.TotalMilliseconds;

    // 未进入的阶段保持null；零只表示实际测量到了零，不能伪造未执行的输入/等待。
    public PhaseMeasurement Measure(UiOperationPhase phase) => new(this, phase);
    internal readonly struct PhaseMeasurement : IDisposable
    {
        private readonly UiOperation _owner;
        private readonly UiOperationPhase _phase;
        private readonly long _started;
        internal PhaseMeasurement(UiOperation owner, UiOperationPhase phase)
        { _owner = owner; _phase = phase; _started = owner._clock.GetTimestamp(); }
        public void Dispose()
        {
            var index = (int)_phase;
            _owner._phaseMilliseconds[index] = (_owner._phaseMilliseconds[index] ?? 0) +
                _owner._clock.GetElapsedTime(_started).TotalMilliseconds;
        }
    }

    private void SafeLog(Action emit)
    {
        var started = _clock.GetTimestamp();
        try { emit(); } catch { }
        finally { _logProducerMilliseconds += _clock.GetElapsedTime(started).TotalMilliseconds; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        End("scope-closed");
        _disposed = true;
        if (ReferenceEquals(Current, this)) Active.Value = _parent;
        _recognition.Dispose();
        _linked.Dispose();
        _deadline.Dispose();
    }
}
