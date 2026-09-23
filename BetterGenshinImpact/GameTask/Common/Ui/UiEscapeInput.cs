using System;
using System.Runtime.ExceptionServices;
using Fischless.WindowsInput;

namespace BetterGenshinImpact.GameTask.Common.Ui;

/// <summary>ESC短按的原生边界；down准入后始终尝试up，不重放不确定输入。</summary>
internal static class UiEscapeInput
{
    internal static void Run(Action admission, Action down, Action up, Action<int> wait)
    {
        var enteredNative = false;
        Exception? failure = null;
        try
        {
            Submit(down, () => { admission(); enteredNative = true; });
            wait(35);
        }
        catch (Exception error) { failure = error; }
        finally
        {
            if (enteredNative)
            {
                try { Submit(up, null); }
                catch (Exception cleanup)
                {
                    failure = failure == null ? cleanup : new AggregateException("ESC输入及松键均失败", failure, cleanup);
                }
            }
        }
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void Submit(Action input, Action? admission)
    {
        using var capture = new InputDispatchCapture(admission);
        input();
        if (capture.NativeCalls == 0 || capture.Requested <= 0 || capture.Submitted != capture.Requested || capture.Uncertain)
            throw new InvalidOperationException("ESC未取得完整原生输入回执");
    }
}
