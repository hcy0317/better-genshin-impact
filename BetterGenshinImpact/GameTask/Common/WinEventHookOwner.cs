using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Threading;
using Vanara.PInvoke;

namespace BetterGenshinImpact.GameTask.Common;

internal readonly record struct WinEventHookObservation(
    Guid RegistrationId, string Phase, string Hook, uint OwnerThreadId, uint CurrentThreadId,
    int OwnerManagedThreadId, int CurrentManagedThreadId, bool Succeeded, int? Win32Error,
    double Milliseconds, int Suppressed);

internal interface IWinEventHookApi
{
    uint CurrentThreadId { get; }
    (User32.HWINEVENTHOOK Handle, int Error) Register(uint first, uint last, User32.WinEventProc callback, uint processId);
    (bool Succeeded, int Error) Unregister(User32.HWINEVENTHOOK handle);
}

/// <summary>两个窗口钩子共同保有其消息循环、句柄及delegate，异步停止只回到原owner卸载。</summary>
internal sealed class WinEventHookOwner
{
    private readonly Dispatcher _owner;
    private readonly User32.WinEventProc _callback;
    private readonly IWinEventHookApi _native;
    private readonly Action<WinEventHookObservation>? _observe;
    private readonly uint _processId;
    private readonly uint _ownerThreadId;
    private readonly int _ownerManagedThreadId;
    private readonly Guid _registrationId = Guid.NewGuid();
    private readonly object _gate = new();
    private readonly OwnedHook[] _hooks = [new("move-size", 0x000A, 0x000B), new("location", 0x800B, 0x800B)];
    private Task? _releaseTask;
    private bool _registrationAttempted;
    private bool _retiring;
    private int _diagnosticCount;
    private int _suppressedDiagnostics;
    private bool _summaryWritten;

    internal WinEventHookOwner(Dispatcher owner, User32.WinEventProc callback,
        Action<WinEventHookObservation>? observe = null, uint processId = 0, IWinEventHookApi? native = null)
    {
        owner.VerifyAccess();
        _owner = owner;
        _callback = callback;
        _observe = observe;
        _processId = processId;
        _native = native ?? new WindowsHookApi();
        _ownerThreadId = _native.CurrentThreadId;
        _ownerManagedThreadId = Environment.CurrentManagedThreadId;
    }

    internal void Register()
    {
        _owner.VerifyAccess();
        lock (_gate)
        {
            if (_registrationAttempted || _retiring) throw new InvalidOperationException("窗口钩子代际已经开始或退役");
            _registrationAttempted = true;
            try
            {
                foreach (var hook in _hooks)
                {
                    var started = Stopwatch.GetTimestamp();
                    var registered = _native.Register(hook.First, hook.Last, _callback, _processId);
                    hook.Handle = registered.Handle;
                    Observe("register", hook.Name, hook.Handle != default, registered.Error, started);
                    if (hook.Handle == default)
                        throw new System.ComponentModel.Win32Exception(registered.Error, $"注册窗口钩子失败: {hook.Name}");
                }
            }
            catch (Exception registrationError)
            {
                // 第二个注册失败时，第一个仍由此对象拥有；清理失败也不能遗失它。
                try { ReleaseOnOwner(); }
                catch (Exception cleanupError) { throw new AggregateException(registrationError, cleanupError); }
                throw;
            }
        }
    }

    internal Task ReleaseAsync()
    {
        lock (_gate)
        {
            _retiring = true;
            // 成功共享同一终态，失败允许再尝试原句柄；从不创建替代owner线程。
            if (_releaseTask == null || _releaseTask.IsFaulted || _releaseTask.IsCanceled)
                _releaseTask = _owner.InvokeAsync(ReleaseOnOwner, DispatcherPriority.Send).Task;
            return _releaseTask;
        }
    }

    private void ReleaseOnOwner()
    {
        _owner.VerifyAccess();
        var releaseStarted = Stopwatch.GetTimestamp();
        List<Exception> failures = [];
        foreach (var hook in _hooks)
        {
            if (hook.Handle == default) continue;
            var started = Stopwatch.GetTimestamp();
            try
            {
                var released = _native.Unregister(hook.Handle);
                Observe("unregister", hook.Name, released.Succeeded, released.Error, started);
                if (!released.Succeeded)
                    throw new System.ComponentModel.Win32Exception(released.Error, $"移除窗口钩子失败: {hook.Name}");
                hook.Handle = default;
            }
            catch (Exception error) { failures.Add(error); }
        }
        GC.KeepAlive(_callback);
        if (failures.Count != 0) throw new AggregateException("窗口钩子尚未完全卸载", failures);
        if (!_summaryWritten)
        {
            _summaryWritten = true;
            Observe("released", "all", true, 0, releaseStarted, summary: true);
        }
    }

    private void Observe(string phase, string hook, bool succeeded, int error, long started, bool summary = false)
    {
        if (_observe == null) return;
        if (!summary && _diagnosticCount++ >= 64)
        {
            _suppressedDiagnostics++;
            return;
        }
        try
        {
            _observe(new(_registrationId, phase, hook, _ownerThreadId, _native.CurrentThreadId,
                _ownerManagedThreadId, Environment.CurrentManagedThreadId, succeeded, succeeded ? 0 : error,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds, _suppressedDiagnostics));
        }
        catch { /* 仅诊断失败，绝不替代注册结果或阻断其余句柄清理。 */ }
    }

    private sealed class OwnedHook(string name, uint first, uint last)
    {
        internal string Name { get; } = name;
        internal uint First { get; } = first;
        internal uint Last { get; } = last;
        internal User32.HWINEVENTHOOK Handle;
    }

    private sealed class WindowsHookApi : IWinEventHookApi
    {
        public uint CurrentThreadId => Kernel32.GetCurrentThreadId();

        public (User32.HWINEVENTHOOK Handle, int Error) Register(uint first, uint last,
            User32.WinEventProc callback, uint processId)
        {
            var handle = User32.SetWinEventHook(first, last, default, callback, processId, 0,
                User32.WINEVENT.WINEVENT_SKIPOWNPROCESS | User32.WINEVENT.WINEVENT_SKIPOWNTHREAD);
            return (handle, handle == default ? Marshal.GetLastWin32Error() : 0);
        }

        public (bool Succeeded, int Error) Unregister(User32.HWINEVENTHOOK handle)
        {
            var succeeded = User32.UnhookWinEvent(handle);
            return (succeeded, succeeded ? 0 : Marshal.GetLastWin32Error());
        }
    }
}
