using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Vanara.PInvoke;

namespace Fischless.WindowsInput;

internal class WindowsInputMessageDispatcher : IInputMessageDispatcher
{
    private readonly Action? _beforeInputDispatch;
    private readonly Func<User32.INPUT[], uint> _dispatch = DispatchNative;
    private readonly Func<int> _readError = Marshal.GetLastWin32Error;

    [DllImport("kernel32.dll")]
    private static extern void SetLastError(uint errorCode);

    internal WindowsInputMessageDispatcher()
    {
    }

    internal WindowsInputMessageDispatcher(Action beforeInputDispatch)
    {
        _beforeInputDispatch = beforeInputDispatch ?? throw new ArgumentNullException(nameof(beforeInputDispatch));
    }

    internal WindowsInputMessageDispatcher(Action? beforeInputDispatch,
        Func<User32.INPUT[], uint> dispatch, Func<int> readError)
    {
        _beforeInputDispatch = beforeInputDispatch;
        _dispatch = dispatch;
        _readError = readError;
    }

    private static uint DispatchNative(User32.INPUT[] inputs)
    {
        SetLastError(0);
        return User32.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(User32.INPUT)));
    }

    public void DispatchInput(User32.INPUT[] inputs)
    {
        if (inputs == null)
        {
            throw new ArgumentNullException(nameof(inputs));
        }

        if (inputs.Length == 0)
        {
            throw new ArgumentException("The input array was empty", nameof(inputs));
        }

        InvokeBeforeInputDispatch();

        var capture = InputDispatchCapture.BeforeNative();
        capture?.Begin(inputs.Length);
        uint num;
        try { num = _dispatch(inputs); }
        catch { capture?.MarkUncertain(); throw; }
        var errorCode = num == inputs.Length ? 0 : _readError();
        capture?.Complete(num);

        if (num != inputs.Length)
        {
            using var process = Process.GetCurrentProcess();
            throw new InputDispatchException(CreateFailureMessage(
                inputs.Length,
                num,
                errorCode,
                new Win32Exception(errorCode).Message,
                process.Id,
                process.SessionId));
        }
    }

    internal void InvokeBeforeInputDispatch()
    {
        _beforeInputDispatch?.Invoke();
    }

    internal static string CreateFailureMessage(
        int requested,
        uint sent,
        int errorCode,
        string errorMessage,
        int processId,
        int sessionId)
    {
        return $"模拟键鼠消息发送失败: requested={requested}, sent={sent}, "
               + $"win32Error={errorCode} ({errorMessage}), pid={processId}, sessionId={sessionId}. "
               + "常见原因包括权限级别不一致或安全软件拦截；UIPI 拦截时 SendInput 可能返回 0，"
               + "但 GetLastError 不一定提供有效原因。";
    }
}
