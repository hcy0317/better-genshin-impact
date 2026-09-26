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

internal enum PathingMacroScene { Unknown, World, Transformed, Cannon }
internal readonly record struct PathingMacroObservation(PathingMacroScene Scene, CaptureFrameStamp Source, bool CanFire = false);
internal enum PathingMacroInputKind { KeyDown, KeyUp, MoveBy, MiddleDown, MiddleUp, LeftDown, LeftUp }
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
    void BeginEvidence(System.Collections.Generic.IReadOnlyList<CombatCommand> commands) { }
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
    private bool _leftHeld;
    internal bool HasTail => _held.Count != 0;
    private bool _navigation;

    internal void AdoptNavigation(PathingMacroObservation observation, bool validPosition, CancellationToken ct)
    {
        if (!HasTail) return;
        Check(_deadline, ct);
        if (_navigation) throw new InvalidOperationException("尾W已经交给导航，不得重复续期");
        if (!validPosition || observation.Scene is PathingMacroScene.Unknown or PathingMacroScene.Cannon ||
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
        try { io.BeginEvidence(segment.Commands); }
        catch { /* 取证初始化失败不改变业务执行。 */ }
        _deadline = Math.Min(overallDeadline, Deadline(segment.RawBudgetSeconds));
        _lease = io.Coordinator.TryAcquire(Guid.NewGuid(), ReleasePhysical)
            ?? throw new InvalidOperationException("路径宏无法取得输入所有权");
        using var exclusive = io.BeginExclusive();
        var observation = await WaitForSceneAsync("entry", null, ct);
        _entry = observation.Source;
        _fence = io.Clock.GetTimestamp();
        for (var commandIndex = 0; commandIndex < segment.Commands.Count; commandIndex++)
        {
            var command = segment.Commands[commandIndex];
            Check(_deadline, ct);
            // 两个原定F是一次交互握手；第一F可能只隐藏HUD，不能在两者之间等待炮台UI。
            // 仅保留已识别炮台程序中的原短等待，不新增重试或扩大Unknown入口准入。
            if (segment.CannonProgram && observation.Scene == PathingMacroScene.World &&
                IsCannonHandshake(segment.Commands, commandIndex))
            {
                observation = await ObserveBoundaryAsync("before-cannon-handshake", ct, PathingMacroScene.World);
                if (io.Map(User32.VK.VK_F) != User32.VK.VK_F)
                    throw new InvalidOperationException("炮台交互按键映射与原握手不一致");
                await PressAsync(User32.VK.VK_F, ct);
                await WaitAsync(Milliseconds(segment.Commands[commandIndex + 1].Args![0]), ct);
                if (io.Map(User32.VK.VK_F) != User32.VK.VK_F)
                    throw new InvalidOperationException("炮台交互期间按键映射改变");
                await PressAsync(User32.VK.VK_F, ct);
                observation = await ObserveBoundaryAsync("cannon-handshake-complete", ct, PathingMacroScene.Cannon);
                commandIndex += 2;
                continue;
            }
            var key = command.Method == Method.KeyPress ? User32Helper.ToVk(command.Args![0]) : default;
            if (segment.CannonProgram && key == User32.VK.VK_RETURN)
                observation = await ObserveBoundaryAsync("before-fire", ct);
            if (observation.Scene == PathingMacroScene.Cannon)
            {
                if (!LegacyPathingMacroPlan.IsCannonCommand(command) ||
                    key == User32.VK.VK_RETURN && !observation.CanFire)
                    throw new InvalidOperationException("炮台宏未取得当前动作能力，禁止发送技能或其他未授权输入");
                // 炮台提示是固定物理键，不能把自定义战斗键映射成炮台外的操作。
                var direction = command.Method == Method.W ? User32.VK.VK_W : command.Method == Method.A ? User32.VK.VK_A :
                    command.Method == Method.S ? User32.VK.VK_S : command.Method == Method.D ? User32.VK.VK_D : key;
                if (direction != default && io.Map(direction) != direction)
                    throw new InvalidOperationException("炮台按键映射与已确认提示不一致");
            }
            else if (segment.CannonProgram && key is User32.VK.VK_RETURN or User32.VK.VK_ESCAPE)
                throw new InvalidOperationException("未确认炮台，不发送炮台发射或退出键");
            if (command.Method == Method.Wait) await WaitAsync(Milliseconds(command.Args![0]), ct);
            else if (command.Method == Method.W || command.Method == Method.A || command.Method == Method.S || command.Method == Method.D)
            {
                var moveKey = command.Method == Method.W ? User32.VK.VK_W : command.Method == Method.A ? User32.VK.VK_A :
                    command.Method == Method.S ? User32.VK.VK_S : User32.VK.VK_D;
                Key(moveKey, PathingMacroInputKind.KeyDown, ct);
                await WaitAsync(Milliseconds(command.Args![0]), ct);
                Key(moveKey, PathingMacroInputKind.KeyUp, ct);
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
            else if (command.Method == Method.MouseDown)
            {
                _leftHeld = true;
                Send(new(PathingMacroInputKind.LeftDown), ct);
            }
            else if (command.Method == Method.MouseUp) ReleaseLeftForHandoff();
            else if (command.Method == Method.KeyPress) await PressAsync(User32Helper.ToVk(command.Args![0]), ct);
            else Key(User32Helper.ToVk(command.Args![0]), command.Method == Method.KeyDown ? PathingMacroInputKind.KeyDown :
                PathingMacroInputKind.KeyUp, ct);
            if (key is User32.VK.VK_F or User32.VK.VK_ESCAPE &&
                (segment.CannonProgram || observation.Scene is PathingMacroScene.Cannon or PathingMacroScene.World))
                observation = await ObserveBoundaryAsync("scene-transition", ct,
                    segment.CannonProgram && key == User32.VK.VK_ESCAPE ? PathingMacroScene.World : null);
        }
        ReleaseLeftForHandoff(); // 原匿名mousedown只在本片段持有，不把射击状态交给下一节点。
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
        var after = await WaitForSceneAsync("post", fence, ct);
        if (after.Scene == PathingMacroScene.Cannon && HasTail)
            throw new InvalidOperationException("炮台持键不能交给普通导航");
        if (!HasTail) Release();
        return new(CombatExecutionKind.Completed, "RAW_INPUT_COMPLETED_NOT_SKILL_CONFIRMATION");
    }

    private void ReleaseLeftForHandoff()
    {
        if (!_leftHeld) return;
        try
        {
            if (!_lease!.TryReleaseInput(() =>
            {
                Exception? failure = null;
                try
                {
                    var result = io.Release(new(PathingMacroInputKind.LeftUp));
                    _fence = Math.Max(_fence, result.ObservableAfterTimestamp ?? io.Clock.GetTimestamp());
                    if (result.Status != CombatBattleHostInputStatus.Sent || result.Error != null)
                        failure = new InvalidOperationException("路径宏左键释放未确认", result.Error);
                }
                catch (Exception error) { failure = error; }
                _leftHeld = false; // 不重复未知的up；其它已持键仍须尝试清理。
                if (failure != null)
                {
                    try { ReleasePhysical(); }
                    catch (Exception cleanup) { failure = new AggregateException(failure, cleanup); }
                    throw failure;
                }
            })) throw new InvalidOperationException("左键清理未取得原输入所有权");
        }
        catch (Exception error) { _poisoned = true; _releaseFailure = ExceptionDispatchInfo.Capture(error); throw; }
    }

    private static bool IsCannonHandshake(IReadOnlyList<CombatCommand> commands, int index)
    {
        static bool IsF(CombatCommand command) => command.Method == Method.KeyPress &&
            command.Args is { Count: 1 } && User32Helper.ToVk(command.Args[0]) == User32.VK.VK_F;
        return index + 2 < commands.Count && IsF(commands[index]) && IsF(commands[index + 2]) &&
            commands[index + 1].Method == Method.Wait && commands[index + 1].Args is { Count: 1 } args &&
            double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var wait) &&
            wait is > 0 and <= .5 && (index + 3 >= commands.Count || !IsF(commands[index + 3]));
    }

    private async Task<PathingMacroObservation> ObserveBoundaryAsync(string phase, CancellationToken ct,
        PathingMacroScene? expected = null)
    {
        var remaining = 60 - io.Clock.GetElapsedTime(_fence).TotalMilliseconds;
        if (remaining > 0) await WaitAsync((int)Math.Ceiling(remaining), ct);
        return await WaitForSceneAsync(phase, new CaptureFrameFence(_entry, _fence), ct, expected);
    }

    private async Task<PathingMacroObservation> WaitForSceneAsync(string phase, CaptureFrameFence? fence, CancellationToken ct,
        PathingMacroScene? expected = null)
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
            if (observation.Scene != PathingMacroScene.Unknown && (expected == null || observation.Scene == expected) &&
                observation.Source.IsFresh(io.Clock, TimeSpan.FromMilliseconds(150)) &&
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
        if (_leftHeld)
            Attempt(new(PathingMacroInputKind.LeftUp));
        if (failure != null) throw new InvalidOperationException("路径宏释放失败，禁止交接", failure);
        _held.Clear();
        _middleHeld = false;
        _leftHeld = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Release();
    }
}
