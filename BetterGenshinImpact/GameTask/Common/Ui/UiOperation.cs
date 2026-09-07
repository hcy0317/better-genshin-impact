using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BetterGenshinImpact.GameTask.Common.Ui;

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
    private int? _lastSignature;
    private double _lastStateLog = double.NegativeInfinity;
    private int _debugEvents, _suppressed;
    private bool _ended, _disposed;
    private UiSnapshot? _latest;

    public static UiOperation? Current => Active.Value;
    public string Id { get; } = Guid.NewGuid().ToString("N");
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
        _parent?.Check();
        _callerToken = ct;
        _userToken = _parent?._userToken ?? ct;
        _clock = _parent?._clock ?? clock ?? TimeProvider.System;
        _started = _clock.GetTimestamp();
        _budget = _parent == null || budget <= _parent.Remaining ? budget : _parent.Remaining;
        _deadline = new CancellationTokenSource(_budget, _clock);
        _linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _deadline.Token, _parent?.Token ?? default);
        _logger = logger ?? _parent?._logger ?? NullLogger.Instance;
        Name = name;
        RootId = _parent?.RootId ?? Id;
        Active.Value = this;
        SafeLog(() => _logger.LogDebug("UI_BEGIN root={RootId} op={OpId} parent={ParentId} operation={Operation} budgetMs={BudgetMs:F0}",
            RootId, Id, _parent?.Id ?? "-", Name, _budget.TotalMilliseconds));
    }

    internal static UiOperation Begin(string name, TimeSpan budget, CancellationToken ct = default,
        ILogger? logger = null, TimeProvider? clock = null) => new(name, budget, ct, logger, clock);

    internal static async Task<T> RunAsync<T>(string name, TimeSpan budget, CancellationToken ct,
        Func<UiOperation, Task<T>> work, ILogger? logger = null, TimeProvider? clock = null,
        Action<Exception, string>? captureFailure = null)
    {
        using var operation = Begin(name, budget, ct, logger, clock);
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
        ObjectDisposedException.ThrowIf(_disposed, this);
        _userToken.ThrowIfCancellationRequested();
        if (_callerToken.IsCancellationRequested && !(_parent?.Token.IsCancellationRequested ?? false))
            _callerToken.ThrowIfCancellationRequested();
        _parent?.Check();
        if (Remaining <= TimeSpan.Zero || _deadline.IsCancellationRequested) throw Timeout();
        Token.ThrowIfCancellationRequested();
    }

    private Exception Translate(Exception error)
    {
        if (error is OperationCanceledException or NormalEndException && !_userToken.IsCancellationRequested)
        {
            try { Check(); }
            catch (TimeoutException timeout) { return new TimeoutException(timeout.Message, error); }
            catch (OperationCanceledException) { }
        }
        return error;
    }

    public TimeoutException Timeout() => new($"界面转换超时：{Name}，期望={Expected}，实际={_latest?.Describe() ?? "未取得观察"}，op={Id}");

    public async Task DelayAsync(int milliseconds, CancellationToken extraToken)
    {
        extraToken.ThrowIfCancellationRequested();
        Check();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(Token, extraToken);
        // 同步Sleep也会桥接到此处，不能依赖被GetResult阻塞的UI线程恢复continuation。
        await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, milliseconds)), _clock, linked.Token).ConfigureAwait(false);
        extraToken.ThrowIfCancellationRequested();
        Check();
    }

    public void Observe(UiSnapshot snapshot, UiTarget expected, string phase = "observe")
    {
        Expected = expected.ToString();
        _latest = snapshot;
        if (_lastSignature == snapshot.Signature && Elapsed.TotalSeconds - _lastStateLog < 2)
        { _suppressed++; return; }
        _lastSignature = snapshot.Signature;
        _lastStateLog = Elapsed.TotalSeconds;
        Debug(() => _logger.LogDebug("UI_STATE root={RootId} op={OpId} phase={Phase} expected={Expected} frame={FrameId} captured={CapturedAt:O} observed={Observed} elapsedMs={ElapsedMs:F0} remainingMs={RemainingMs:F0}",
            RootId, Id, phase, Expected, snapshot.FrameId, snapshot.CapturedAt, snapshot.Describe(), Elapsed.TotalMilliseconds, Remaining.TotalMilliseconds));
    }

    public void Action(UiAction action, bool applied, int attempt, int maxAttempts) => Debug(() =>
        _logger.LogDebug("UI_ACTION root={RootId} op={OpId} action={Action} applied={Applied} attempt={Attempt}/{MaxAttempts} remainingMs={RemainingMs:F0}",
            RootId, Id, action, applied, attempt, maxAttempts, Remaining.TotalMilliseconds));

    private void Debug(Action emit)
    {
        if (_debugEvents++ >= 32) { _suppressed++; return; }
        SafeLog(emit);
    }

    private void End(string outcome, Exception? error = null)
    {
        if (_ended) return;
        _ended = true;
        SafeLog(() => _logger.Log(error == null || outcome == "cancelled" ? LogLevel.Debug : LogLevel.Warning,
            error, "UI_END root={RootId} op={OpId} operation={Operation} outcome={Outcome} expected={Expected} observed={Observed} elapsedMs={ElapsedMs:F0} remainingMs={RemainingMs:F0} suppressed={Suppressed}",
            RootId, Id, Name, outcome, Expected, _latest?.Describe() ?? "未取得观察", Elapsed.TotalMilliseconds, Remaining.TotalMilliseconds, _suppressed));
    }

    private static void SafeLog(Action emit) { try { emit(); } catch { } }

    public void Dispose()
    {
        if (_disposed) return;
        End("scope-closed");
        _disposed = true;
        if (ReferenceEquals(Current, this)) Active.Value = _parent;
        _linked.Dispose();
        _deadline.Dispose();
    }
}
