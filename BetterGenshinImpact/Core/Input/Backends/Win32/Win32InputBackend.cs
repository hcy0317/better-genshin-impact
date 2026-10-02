using System;

namespace BetterGenshinImpact.Core.Input.Backends.Win32;

/// <summary>
/// Windows 键鼠后端：前台 SendInput，后台 PostMessage。两路总是同时可用
/// </summary>
public sealed class Win32InputBackend : IInputBackend
{
    /// <param name="hWnd">游戏窗口句柄。为 IntPtr.Zero 时后台通道的所有操作都 warn</param>
    public Win32InputBackend(IntPtr hWnd)
    {
        Foreground = new SendInputChannel();
        Background = new PostMessageChannel(hWnd);
    }

    public InputBackendKind Kind => InputBackendKind.Win32;

    public IInputChannel Foreground { get; }

    public IInputChannel Background { get; }

    public void ReleaseAll()
    {
        Exception? backgroundFailure = null;
        try { Background.ReleaseAll(); }
        catch (Exception error) { backgroundFailure = error; }
        try { Foreground.ReleaseAll(); }
        catch (Exception error)
        {
            if (backgroundFailure != null) throw new AggregateException(backgroundFailure, error);
            throw;
        }
        if (backgroundFailure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(backgroundFailure).Throw();
    }

    public void Dispose()
    {
    }
}
