using System;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;

namespace BetterGenshinImpact.Service;

/// <summary>只用于进程启动参数的一条龙；UI/IPC复用实例不拥有进程退出权。</summary>
internal static class CommandLineOneDragonLifetime
{
    internal static async Task RunAsync(Func<Task> run, Action<Exception> report, Action<int> requestShutdown)
    {
        try
        {
            await run();
        }
        catch (Exception error)
        {
            // await 已经过任务自身的 finally；此处不执行任何游戏输入或成功完成动作。
            var exitCode = error is NormalEndException ? 0
                : error is OperationCanceledException ? 2 : CommandLineTaskFailurePolicy.FailureExitCode;
            try { report(error); }
            catch { /* 诊断失败不能让专用命令行进程一直占用调度实例。 */ }
            requestShutdown(exitCode);
        }
    }
}
