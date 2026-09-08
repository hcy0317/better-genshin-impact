using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;

namespace BetterGenshinImpact.GameTask;

/// <summary>独立任务锁内的终止状态。脚本宿主捕获所属任务，不能被 JS catch 或迟到回调清空。</summary>
internal sealed class TaskExecutionScope : IDisposable
{
    internal sealed class State
    {
        internal readonly object Gate = new();
        internal bool Closed;
        internal ExceptionDispatchInfo? Failure;
    }

    private static readonly AsyncLocal<State?> Active = new();
    private readonly State? _previous;
    private readonly State _state = new();
    private bool _disposed;

    private TaskExecutionScope()
    {
        if (Active.Value is { Closed: false }) throw new InvalidOperationException("不能在子调用中重建活动任务的终止状态");
        _previous = null;
        Active.Value = _state;
    }

    internal static TaskExecutionScope BeginOwned() => new();
    internal static Guard Capture() => new(Active.Value);
    internal static void ThrowIfFailed() => Capture().Check();
    internal static Exception? Failure => Active.Value?.Failure?.SourceException;

    internal static void StopUnconfirmedCombat(string reason)
    {
        var failure = new CombatNotFinishedException(reason);
        Capture().Report(failure);
        throw failure;
    }

    internal static void RethrowCombatFailure(Exception error, bool detectionEnabled, bool endConfirmed, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var failure = detectionEnabled && !endConfirmed && error is not OperationCanceledException and not NormalEndException
            && !IsUnconfirmedCombat(error)
            ? new CombatNotFinishedException("结束确认前退出战斗：" + error.Message, error)
            : error;
        Capture().Report(failure);
        ExceptionDispatchInfo.Capture(failure).Throw();
    }

    internal static async Task RunCheckedAsync(Func<Task> action)
    {
        var guard = Capture();
        using var inherited = guard.Enter();
        try { await action().ConfigureAwait(false); }
        catch (Exception error)
        {
            guard.Report(error);
            guard.Check();
            throw;
        }
        guard.Check();
    }

    internal static bool IsUnconfirmedCombat(Exception error) => FindCombatFailure(error) != null;
    private static Exception? FindCombatFailure(Exception error)
    {
        if (error is CombatNotFinishedException) return error;
        if (error is AggregateException aggregate)
            foreach (var child in aggregate.InnerExceptions)
                if (FindCombatFailure(child) is { } found) return found;
        if (error.InnerException != null && FindCombatFailure(error.InnerException) is { } inner) return inner;
        return error.Message.Contains(CombatNotFinishedException.ErrorCode, StringComparison.Ordinal)
            ? new CombatNotFinishedException(error.Message, error) : null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_state.Gate) _state.Closed = true;
        if (ReferenceEquals(Active.Value, _state)) Active.Value = _previous;
    }

    internal sealed class Guard(State? state)
    {
        internal void Check()
        {
            var owner = state ?? Active.Value;
            if (owner == null) return;
            ExceptionDispatchInfo? failure;
            lock (owner.Gate)
            {
                if (owner.Closed) throw new OperationCanceledException("所属自动化任务已结束，拒绝迟到的游戏操作");
                failure = owner.Failure;
            }
            failure?.Throw();
        }

        internal void Report(Exception error)
        {
            var owner = state ?? Active.Value;
            if (owner == null || FindCombatFailure(error) is not { } terminal) return;
            lock (owner.Gate)
                if (!owner.Closed) owner.Failure ??= ExceptionDispatchInfo.Capture(terminal);
        }

        internal IDisposable Enter()
        {
            Check();
            var previous = Active.Value;
            Active.Value = state ?? previous;
            return new Inherited(previous);
        }

        private sealed class Inherited(State? previous) : IDisposable
        {
            public void Dispose() => Active.Value = previous;
        }

        private async Task ObserveAsync(Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch (Exception error) { Report(error); Check(); throw; }
            Check();
        }

        private async Task<T> ObserveResultAsync<T>(Task<T> task)
        {
            try
            {
                var result = await task.ConfigureAwait(false);
                Check();
                return result;
            }
            catch (Exception error) { Report(error); Check(); throw; }
        }

        // 保持原委托参数/返回类型，不增加 JS API；异步返回也必须在完成前检查所属任务。
        internal T Bind<T>(T action) where T : Delegate
        {
            var invoke = typeof(T).GetMethod("Invoke")!;
            var parameters = invoke.GetParameters().Select(parameter => Expression.Parameter(parameter.ParameterType, parameter.Name)).ToArray();
            var self = Expression.Constant(this);
            MethodInfo Method(string name) => typeof(Guard).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
            Expression call = Expression.Invoke(Expression.Constant(action), parameters);
            if (invoke.ReturnType == typeof(Task))
                call = Expression.Call(self, Method(nameof(ObserveAsync)), call);
            else if (invoke.ReturnType.IsGenericType && invoke.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
                call = Expression.Call(self, Method(nameof(ObserveResultAsync)).MakeGenericMethod(invoke.ReturnType.GetGenericArguments()), call);
            var error = Expression.Parameter(typeof(Exception), "error");
            var guarded = Expression.TryCatch(call, Expression.Catch(error,
                Expression.Block(Expression.Call(self, Method(nameof(Report)), error), Expression.Rethrow(call.Type))));
            var scope = Expression.Variable(typeof(IDisposable), "scope");
            return Expression.Lambda<T>(Expression.Block([scope],
                Expression.Assign(scope, Expression.Call(self, Method(nameof(Enter)))),
                Expression.TryFinally(guarded, Expression.Call(scope, typeof(IDisposable).GetMethod(nameof(IDisposable.Dispose))!))), parameters).Compile();
        }
    }
}
