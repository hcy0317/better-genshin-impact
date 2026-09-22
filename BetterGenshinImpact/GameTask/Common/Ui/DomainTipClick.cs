using System;
using System.Runtime.ExceptionServices;
using Fischless.WindowsInput;

namespace BetterGenshinImpact.GameTask.Common.Ui;

// T7's click sequence only; neither scene recognition nor coordinate conversion lives here.
internal static class DomainTipClick
{
    internal static void Run(Action admission, Action move, Action down, Action up, Action<int> wait)
    {
        Submit(move, admission);
        var downEnteredNative = false;
        Exception? failure = null;
        try
        {
            // A new capture is necessary: BeforeNative invokes admission only for
            // the first native call within each capture, after transport preparation.
            Submit(down, () => { admission(); downEnteredNative = true; });
            wait(50);
        }
        catch (Exception error) { failure = error; }
        finally
        {
            if (downEnteredNative)
            {
                try { Submit(up, null); } // Cleanup must remain possible after timeout/cancellation.
                catch (Exception cleanup)
                {
                    failure = failure == null ? cleanup : new AggregateException("Domain tip click and button release failed.", failure, cleanup);
                }
            }
        }
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        wait(50);
    }

    private static void Submit(Action input, Action? admission)
    {
        using var capture = new InputDispatchCapture(admission);
        input();
        if (capture.NativeCalls == 0 || capture.Requested <= 0 || capture.Submitted != capture.Requested || capture.Uncertain)
            throw new InvalidOperationException("Domain tip click did not produce a complete native receipt.");
    }
}
