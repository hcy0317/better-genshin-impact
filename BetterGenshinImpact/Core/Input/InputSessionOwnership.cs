using System;
using System.Threading;
using BetterGenshinImpact.Core.Simulator.Extensions;
using Vanara.PInvoke;

namespace BetterGenshinImpact.Core.Input;

/// <summary>任务内输入租约。能力只由C#持有；仅同步发送边界可进入，不随异步上下文传播。</summary>
internal sealed class InputSessionOwnership
{
    private readonly object _gate = new();
    private IInputBackend _backend;
    private GuardedBackend _view;
    private Owner? _owner;
    private int _legacyOperations;
    [ThreadStatic] private static Owner? _sendingOwner;
    [ThreadStatic] private static bool _cleanupOnly;

    internal InputSessionOwnership(IInputBackend backend)
    {
        _backend = backend;
        _view = new(this, backend);
    }

    internal IInputBackend Backend => Volatile.Read(ref _view);

    internal void Attach(IInputBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        lock (_gate)
        {
            if (ReferenceEquals(backend, _backend) || ReferenceEquals(backend, _view)) return;
            if (_owner != null || _legacyOperations != 0)
                throw new InvalidOperationException("活动输入尚未退出，禁止替换后端");
            _backend.Dispose();
            _backend = backend;
            Volatile.Write(ref _view, new(this, backend));
        }
    }

    internal IDisposable EnterLegacyOperation()
    {
        lock (_gate)
        {
            if (_owner != null) throw new InvalidOperationException("持续战斗会话独占输入，拒绝另一战斗或路径操作");
            _legacyOperations++;
            return new LegacyOperation(this);
        }
    }

    internal Owner Acquire()
    {
        lock (_gate)
        {
            if (_owner != null || _legacyOperations != 0)
                throw new InvalidOperationException("前一输入任务尚未安全退出");
            // 不能在旧JS down/sleep/up之间抢占；只读取后端自己的按键记录，不读取真实设备。
            for (var key = 1; key <= 255; key++)
                if (_backend.Foreground.IsKeyDown((User32.VK)key) || _backend.Background.IsKeyDown((User32.VK)key))
                    throw new InvalidOperationException("旧输入仍持键，禁止接管");
            return _owner = new(this);
        }
    }

    private T Access<T>(IInputBackend origin, Func<T> action, bool release = false)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(origin, _backend)) throw new InvalidOperationException("输入通道已退休");
            if (_owner is { } owner)
            {
                if (!ReferenceEquals(_sendingOwner, owner) || owner.Poison != null ||
                    (_cleanupOnly && !release) || (owner.Revoked && !release))
                    throw new InvalidOperationException("非当前会话所有者或已撤销的输入请求");
            }
            try { return action(); }
            catch (Exception error)
            {
                if (release && _owner != null) _owner.Poison ??= error;
                throw;
            }
        }
    }

    private sealed class LegacyOperation(InputSessionOwnership inputs) : IDisposable
    {
        private InputSessionOwnership? _inputs = inputs;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _inputs, null);
            if (current != null) lock (current._gate) current._legacyOperations--;
        }
    }

    internal sealed class Owner : IDisposable
    {
        private readonly InputSessionOwnership _inputs;
        internal volatile bool Revoked;
        internal Exception? Poison;
        private bool _released;
        private int _activeCalls;
        internal Owner(InputSessionOwnership inputs) => _inputs = inputs;

        internal void CloseAdmission() => Revoked = true;

        internal void Invoke(Action action) => InvokeCore(action, cleanup: false);
        internal void ReleaseInput(Action release) => InvokeCore(release, cleanup: true);

        private void InvokeCore(Action action, bool cleanup)
        {
            lock (_inputs._gate)
            {
                if (_released || !ReferenceEquals(_inputs._owner, this) || Poison != null || Revoked && !cleanup)
                    throw new InvalidOperationException("输入租约已停止准入或释放失败", Poison);
                _activeCalls++;
            }
            // The capability spans synchronous legacy commands, but the mutex spans only
            // their actual channel calls. Cancellation must reach a command while it waits.
            var previous = _sendingOwner;
            var wasCleanup = _cleanupOnly;
            _sendingOwner = this;
            _cleanupOnly = cleanup;
            try { action(); }
            finally
            {
                _sendingOwner = previous;
                _cleanupOnly = wasCleanup;
                lock (_inputs._gate) _activeCalls--;
            }
        }

        public void Dispose()
        {
            CloseAdmission();
            lock (_inputs._gate)
            {
                if (_released) return;
                if (_activeCalls != 0) throw new InvalidOperationException("输入调用仍未退出，禁止释放并交接所有者");
                // 释放异常保留owner，禁止下一场接管，也不在后续Dispose中猜测释放已成功。
                ReleaseInput(() => _inputs.Backend.ReleaseAll());
                _released = true;
                _inputs._owner = null;
            }
        }
    }

    private sealed class GuardedBackend : IInputBackend
    {
        private readonly InputSessionOwnership _inputs;
        private readonly IInputBackend _backend;
        internal GuardedBackend(InputSessionOwnership inputs, IInputBackend backend)
        {
            _inputs = inputs; _backend = backend;
            Foreground = new GuardedChannel(inputs, backend, backend.Foreground);
            Background = ReferenceEquals(backend.Foreground, backend.Background)
                ? Foreground : new GuardedChannel(inputs, backend, backend.Background);
        }
        public InputBackendKind Kind => _backend.Kind;
        public IInputChannel Foreground { get; }
        public IInputChannel Background { get; }
        public void ReleaseAll() => _inputs.Access(_backend, () => { _backend.ReleaseAll(); return true; }, release: true);
        public void Dispose() => _inputs.Access(_backend, () =>
        {
            if (_inputs._owner != null || _inputs._legacyOperations != 0)
                throw new InvalidOperationException("活动输入尚未退出，禁止销毁后端");
            _backend.Dispose(); return true;
        });
    }

    private sealed class GuardedChannel(InputSessionOwnership inputs, IInputBackend backend, IInputChannel channel)
        : IInputChannel, IKeyboardInput, IMouseInput
    {
        public IKeyboardInput Keyboard => this;
        public IMouseInput Mouse => this;
        public IInputChannel SimulateAction(GIActions action, KeyType type = KeyType.KeyPress)
        { inputs.Access(backend, () => channel.SimulateAction(action, type), type == KeyType.KeyUp); return this; }
        public bool IsKeyDown(User32.VK key) => inputs.Access(backend, () => channel.IsKeyDown(key));
        public void ReleaseAll() => inputs.Access(backend, () => { channel.ReleaseAll(); return true; }, release: true);
        IInputChannel IInputChannel.Sleep(int ms) { inputs.Access(backend, () => channel.Sleep(ms)); return this; }
        public IKeyboardInput KeyDown(User32.VK key) { inputs.Access(backend, () => channel.Keyboard.KeyDown(key)); return this; }
        public IKeyboardInput KeyUp(User32.VK key) { inputs.Access(backend, () => channel.Keyboard.KeyUp(key), release: true); return this; }
        public IKeyboardInput KeyPress(User32.VK key) { inputs.Access(backend, () => channel.Keyboard.KeyPress(key)); return this; }
        IKeyboardInput IKeyboardInput.Sleep(int ms) { inputs.Access(backend, () => channel.Keyboard.Sleep(ms)); return this; }
        public IMouseInput MoveMouseBy(int x, int y) { inputs.Access(backend, () => channel.Mouse.MoveMouseBy(x, y)); return this; }
        public IMouseInput MoveMouseTo(double x, double y) { inputs.Access(backend, () => channel.Mouse.MoveMouseTo(x, y)); return this; }
        public IMouseInput LeftButtonDown() { inputs.Access(backend, () => channel.Mouse.LeftButtonDown()); return this; }
        public IMouseInput LeftButtonUp() { inputs.Access(backend, () => channel.Mouse.LeftButtonUp(), release: true); return this; }
        public IMouseInput LeftButtonClick() { inputs.Access(backend, () => channel.Mouse.LeftButtonClick()); return this; }
        public IMouseInput RightButtonDown() { inputs.Access(backend, () => channel.Mouse.RightButtonDown()); return this; }
        public IMouseInput RightButtonUp() { inputs.Access(backend, () => channel.Mouse.RightButtonUp(), release: true); return this; }
        public IMouseInput RightButtonClick() { inputs.Access(backend, () => channel.Mouse.RightButtonClick()); return this; }
        public IMouseInput MiddleButtonDown() { inputs.Access(backend, () => channel.Mouse.MiddleButtonDown()); return this; }
        public IMouseInput MiddleButtonUp() { inputs.Access(backend, () => channel.Mouse.MiddleButtonUp(), release: true); return this; }
        public IMouseInput MiddleButtonClick() { inputs.Access(backend, () => channel.Mouse.MiddleButtonClick()); return this; }
        public IMouseInput XButtonDown(int id) { inputs.Access(backend, () => channel.Mouse.XButtonDown(id)); return this; }
        public IMouseInput XButtonUp(int id) { inputs.Access(backend, () => channel.Mouse.XButtonUp(id), release: true); return this; }
        public IMouseInput XButtonClick(int id) { inputs.Access(backend, () => channel.Mouse.XButtonClick(id)); return this; }
        public IMouseInput VerticalScroll(int clicks) { inputs.Access(backend, () => channel.Mouse.VerticalScroll(clicks)); return this; }
        IMouseInput IMouseInput.Sleep(int ms) { inputs.Access(backend, () => channel.Mouse.Sleep(ms)); return this; }
    }
}
