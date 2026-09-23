using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Helpers;
using Fischless.GameCapture;
using Vanara.PInvoke;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

internal enum PathingMacroScene { Unknown, World, Transformed }
internal readonly record struct PathingMacroObservation(PathingMacroScene Scene, CaptureFrameStamp Source);
internal enum PathingMacroInputKind { KeyDown, KeyUp, MoveBy, MiddleDown, MiddleUp }
internal readonly record struct PathingMacroInput(PathingMacroInputKind Kind, User32.VK Key = default, int X = 0, int Y = 0);

internal interface IPathingMacroIo
{
    TimeProvider Clock { get; }
    CombatInputCoordinator Coordinator { get; }
    PathingMacroObservation Observe(string phase = "boundary");
    User32.VK Map(User32.VK key);
    CombatBattleHostInputResult Send(PathingMacroInput input, Action admit);
    CombatBattleHostInputResult Release(PathingMacroInput input) => Send(input, () => { });
    Task Delay(int milliseconds, CancellationToken ct);
    IDisposable? BeginExclusive() => null;
}

/// <summary>路径实例拥有物理键和回执，绝不从NativeGame窃取租约或持有截图。</summary>
internal sealed class PathingMacroSession(IPathingMacroIo io) : IDisposable
{
    private readonly Dictionary<User32.VK, User32.VK> _held = new();
    private CombatInputCoordinator.Session? _lease;
    private long _deadline;
    private CaptureFrameStamp _entry;
    private long _fence;
    private bool _disposed, _poisoned;
    private ExceptionDispatchInfo? _releaseFailure;
    private bool _middleHeld;
    internal bool HasTail => _held.Count != 0;
    private bool _navigation;

    internal void AdoptNavigation(PathingMacroObservation observation, bool validPosition, CancellationToken ct)
    {
        if (!HasTail) return;
        Check(_deadline, ct);
        if (_navigation) throw new InvalidOperationException("尾W已经交给导航，不得重复续期");
        if (!validPosition || observation.Scene == PathingMacroScene.Unknown ||
            observation.Source.SessionId != _entry.SessionId ||
            !observation.Source.IsFresh(io.Clock, TimeSpan.FromMilliseconds(150)))
        { Release(); throw new InvalidOperationException("尾W导航接管缺少新鲜场景或有效定位"); }
        if (_held[User32.VK.VK_W] != io.Map(User32.VK.VK_W)) { Release(); return; }
        _navigation = true;
        _deadline = Deadline(240); // 仅开始已有导航，不重新执行或续期宏。
    }

    internal bool TryNavigationForward(bool down, CancellationToken ct)
    {
        if (!HasTail) return false;
        if (!down) { Release(); return true; }
        Check(_deadline, ct);
        if (!_navigation) throw new InvalidOperationException("未取得导航接管许可");
        return true; // 原物理W仍按下；禁止为交接再发down。
    }

    internal void CheckNavigation(CancellationToken ct)
    {
        if (!HasTail) return;
        try { Check(_deadline, ct); }
        catch { if (!_poisoned) Release(); throw; }
    }

    internal async Task<CombatExecutionResult> ExecuteAsync(LegacyPathingMacroPlan plan,
        Func<IReadOnlyList<CombatCommand>, CancellationToken, Task<CombatExecutionResult>> native,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Release();
        var end = Deadline(plan.BudgetSeconds);
        using var timer = new CancellationTokenSource(TimeSpan.FromSeconds(plan.BudgetSeconds), io.Clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
        var completed = false;
        try
        {
            foreach (var segment in plan.Segments)
            {
                Check(end, linked.Token);
                CombatExecutionResult result;
                if (segment.IsRaw)
                    result = await ExecuteRawAsync(segment, end, linked.Token);
                else
                {
                    Release();
                    result = await native(segment.Commands, linked.Token);
                }
                Check(end, linked.Token);
                if (!result.CanContinue) { Release(); return result; }
                completed |= result.Kind == CombatExecutionKind.Completed;
            }
            return new(completed ? CombatExecutionKind.Completed : CombatExecutionKind.Skipped,
                completed ? "PATHING_MACRO_INPUT_COMPLETED" : "ALL_OPTIONAL_ACTIONS_SKIPPED");
        }
        catch { if (!_poisoned) Release(); throw; }
    }

    private async Task<CombatExecutionResult> ExecuteRawAsync(LegacyPathingMacroPlan.Segment segment,
        long overallDeadline, CancellationToken ct)
    {
        _deadline = Math.Min(overallDeadline, Deadline(segment.RawBudgetSeconds));
        _lease = io.Coordinator.TryAcquire(Guid.NewGuid(), ReleasePhysical)
            ?? throw new InvalidOperationException("路径宏无法取得输入所有权");
        using var exclusive = io.BeginExclusive();
        var observation = await WaitForSceneAsync("entry", null, ct);
        _entry = observation.Source;
        _fence = io.Clock.GetTimestamp();
        foreach (var command in segment.Commands)
        {
            Check(_deadline, ct);
            if (command.Method == Method.Wait) await WaitAsync(Milliseconds(command.Args![0]), ct);
            else if (command.Method == Method.W || command.Method == Method.A || command.Method == Method.S || command.Method == Method.D)
            {
                var key = command.Method == Method.W ? User32.VK.VK_W : command.Method == Method.A ? User32.VK.VK_A :
                    command.Method == Method.S ? User32.VK.VK_S : User32.VK.VK_D;
                Key(key, PathingMacroInputKind.KeyDown, ct);
                await WaitAsync(Milliseconds(command.Args![0]), ct);
                Key(key, PathingMacroInputKind.KeyUp, ct);
            }
            else if (command.Method == Method.Jump) await PressAsync(User32.VK.VK_SPACE, ct);
            else if (command.Method == Method.MoveBy)
                Send(new(PathingMacroInputKind.MoveBy, X: int.Parse(command.Args![0], CultureInfo.InvariantCulture),
                    Y: int.Parse(command.Args[1], CultureInfo.InvariantCulture)), ct);
            else if (command.Method == Method.Click)
            {
                _middleHeld = true;
                Send(new(PathingMacroInputKind.MiddleDown), ct);
                await WaitAsync(35, ct);
                Send(new(PathingMacroInputKind.MiddleUp), ct);
                _middleHeld = false;
            }
            else if (command.Method == Method.KeyPress) await PressAsync(User32Helper.ToVk(command.Args![0]), ct);
            else Key(User32Helper.ToVk(command.Args![0]), command.Method == Method.KeyDown ? PathingMacroInputKind.KeyDown :
                PathingMacroInputKind.KeyUp, ct);
        }
        // 旧NativeRunner在片段完成时释放X。保留这一既有语义，不声称跨挖矿节点持X。
        if (_held.TryGetValue(User32.VK.VK_X, out var heldX))
        {
            try
            {
                if (!_lease.TryReleaseInput(() =>
                {
                    Exception? failure = null;
                    try
                    {
                        var released = io.Release(new(PathingMacroInputKind.KeyUp, heldX));
                        _fence = Math.Max(_fence, released.ObservableAfterTimestamp ?? io.Clock.GetTimestamp());
                        if (released.Status != CombatBattleHostInputStatus.Sent || released.Error != null)
                            failure = new InvalidOperationException("路径宏尾X释放未确认", released.Error);
                    }
                    catch (Exception error) { failure = error; }
                    _held.Remove(User32.VK.VK_X); // 不重放不确定的X-up；仍须清理其他键。
                    if (failure != null)
                    {
                        try { ReleasePhysical(); }
                        catch (Exception cleanup) { failure = new AggregateException(failure, cleanup); }
                        throw failure; // Coordinator保留失败所有权，禁止交接。
                    }
                })) throw new InvalidOperationException("尾X清理未取得输入所有权");
            }
            catch (Exception error) { _poisoned = true; _releaseFailure = ExceptionDispatchInfo.Capture(error); throw; }
        }
        if (_held.Keys.Any(key => key != User32.VK.VK_W))
            throw new InvalidOperationException("路径宏只允许未配对的尾W交给导航");
        var fence = new CaptureFrameFence(_entry, _fence);
        // 用户指定游戏有30~60ms响应延迟；已有显式wait可覆盖，不每键加识图等待。
        var responseDelay = 60 - io.Clock.GetElapsedTime(_fence).TotalMilliseconds;
        if (responseDelay > 0) await WaitAsync((int)Math.Ceiling(responseDelay), ct);
        await WaitForSceneAsync("post", fence, ct);
        if (!HasTail) Release();
        return new(CombatExecutionKind.Completed, "RAW_INPUT_COMPLETED_NOT_SKILL_CONFIRMATION");
    }

    private async Task<PathingMacroObservation> WaitForSceneAsync(string phase, CaptureFrameFence? fence, CancellationToken ct)
    {
        var session = fence?.Before.SessionId ?? Guid.Empty;
        while (true)
        {
            Check(_deadline, ct);
            var observation = io.Observe(phase);
            Check(_deadline, ct);
            if (observation.Source.IsKnown)
            {
                if (session != Guid.Empty && observation.Source.SessionId != session)
                    throw new InvalidOperationException("路径宏待稳期间采集源改变，不继续输入");
                session = observation.Source.SessionId;
            }
            if (observation.Scene != PathingMacroScene.Unknown && observation.Source.IsFresh(io.Clock, TimeSpan.FromMilliseconds(150)) &&
                (fence == null || fence.Value.Accepts(observation.Source))) return observation;
            // 只等稳定新画面；不延长期限，也不重发已经执行的原宏。
            await WaitAsync(100, ct);
        }
    }

    private static int Milliseconds(string value)
    {
        var seconds = double.Parse(value, CultureInfo.InvariantCulture);
        if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(value));
        return checked((int)Math.Ceiling(seconds * 1000));
    }

    private async Task PressAsync(User32.VK key, CancellationToken ct)
    {
        Key(key, PathingMacroInputKind.KeyDown, ct);
        await WaitAsync(35, ct);
        Key(key, PathingMacroInputKind.KeyUp, ct);
    }

    private void Key(User32.VK logical, PathingMacroInputKind kind, CancellationToken ct)
    {
        var physical = _held.TryGetValue(logical, out var existing) ? existing : io.Map(logical);
        if (!Enum.IsDefined(physical) || (int)physical <= 6) throw new InvalidOperationException("路径宏物理按键映射无效");
        // 提交结果未知也必须按原物理键清理；不能等Sent后才登记down。
        _held[logical] = physical;
        Send(new(kind, physical), ct);
        if (kind != PathingMacroInputKind.KeyDown) _held.Remove(logical);
    }

    private void Send(PathingMacroInput input, CancellationToken ct)
    {
        Check(_deadline, ct);
        using var operation = _lease!.EnterOperation();
        var receipt = io.Send(input, () => Check(_deadline, ct));
        _fence = Math.Max(_fence, receipt.ObservableAfterTimestamp ?? io.Clock.GetTimestamp());
        ct.ThrowIfCancellationRequested();
        if (receipt.Error is OperationCanceledException cancellation) throw cancellation;
        if (receipt.Status != CombatBattleHostInputStatus.Sent || receipt.Error != null)
            throw new InvalidOperationException("路径宏原生输入失败或不确定，禁止重放：" + receipt.Reason, receipt.Error);
        Check(_deadline, ct);
    }

    internal async Task WaitAsync(int milliseconds, CancellationToken ct)
    {
        try
        {
        if (HasTail || _lease != null)
        {
            Check(_deadline, ct);
            var remaining = (int)Math.Min(int.MaxValue, Math.Ceiling((_deadline - io.Clock.GetTimestamp()) * 1000d / io.Clock.TimestampFrequency));
            await io.Delay(Math.Min(milliseconds, remaining), ct);
            Check(_deadline, ct);
        }
        else await io.Delay(milliseconds, ct);
        }
        catch { if (!_poisoned) Release(); throw; }
    }

    private long Deadline(double seconds) => checked(io.Clock.GetTimestamp() + (long)(seconds * io.Clock.TimestampFrequency));
    private void Check(long deadline, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        TaskExecutionScope.ThrowIfFailed();
        if (_poisoned) _releaseFailure?.Throw();
        if (_poisoned || io.Clock.GetTimestamp() >= deadline) throw new InvalidOperationException("路径宏输入失效或原期限耗尽，不重放");
    }

    internal void Release()
    {
        if (_poisoned)
        {
            _releaseFailure?.Throw();
            throw new InvalidOperationException("路径宏释放结果不确定，输入所有权仍被阻断");
        }
        if (_lease == null) return;
        try { _lease.Dispose(); _lease = null; _navigation = false; }
        catch (Exception error) { _poisoned = true; _releaseFailure = ExceptionDispatchInfo.Capture(error); throw; }
    }

    private void ReleasePhysical()
    {
        Exception? failure = null;
        void Attempt(PathingMacroInput input)
        {
            try
            {
                var receipt = io.Release(input);
                if (receipt.Status != CombatBattleHostInputStatus.Sent || receipt.Error != null)
                    failure ??= new InvalidOperationException("路径宏物理输入释放未确认", receipt.Error);
            }
            catch (Exception error) { failure ??= error; }
        }
        foreach (var physical in _held.Values.Distinct().ToArray())
            Attempt(new(PathingMacroInputKind.KeyUp, physical));
        if (_middleHeld)
            Attempt(new(PathingMacroInputKind.MiddleUp));
        if (failure != null) throw new InvalidOperationException("路径宏释放失败，禁止交接", failure);
        _held.Clear();
        _middleHeld = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Release();
    }
}
