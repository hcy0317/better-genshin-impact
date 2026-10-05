using System;
using System.Runtime.ExceptionServices;
using Fischless.WindowsInput;

namespace BetterGenshinImpact.GameTask.Common.Ui;

/// <summary>ESC短按的原生边界；down准入后始终尝试up，不重放不确定输入。</summary>
internal static class UiEscapeInput
{
    internal static void Run(Action admission, Action down, Action up, Action<int> wait, string name = "ESC")
    {
        var enteredNative = false;
        Exception? failure = null;
        try
        {
            Submit(down, () => { admission(); enteredNative = true; }, name);
            wait(35);
        }
        catch (Exception error) { failure = error; }
        finally
        {
            if (enteredNative)
            {
                try { Submit(up, null, name); }
                catch (Exception cleanup)
                {
                    failure = failure == null ? cleanup : new AggregateException(name + "输入及松键均失败", failure, cleanup);
                }
            }
        }
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void Submit(Action input, Action? admission, string name)
    {
        using var capture = new InputDispatchCapture(admission);
        input();
        if (!capture.HasCompleteReceipt)
            throw new InvalidOperationException(name + "未取得完整原生输入回执");
    }
}
