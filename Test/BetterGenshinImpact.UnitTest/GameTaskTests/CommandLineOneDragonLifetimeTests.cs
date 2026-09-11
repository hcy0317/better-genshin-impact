using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.Service;

namespace BetterGenshinImpact.UnitTest.GameTaskTests;

public class CommandLineOneDragonLifetimeTests
{
    [Fact]
    public async Task SuccessfulRunKeepsItsConfiguredCompletionBehavior()
    {
        var shutdowns = 0;
        var reports = 0;
        await CommandLineOneDragonLifetime.RunAsync(() => Task.CompletedTask,
            _ => reports++, _ => shutdowns++);
        Assert.Equal(0, shutdowns);
        Assert.Equal(0, reports);
    }

    [Theory]
    [InlineData("failure", 1)]
    [InlineData("cancelled", 2)]
    [InlineData("normal-end", 0)]
    public async Task TerminalOutcomesRequestExactlyOneShutdown(string outcome, int expectedCode)
    {
        Exception failure = outcome switch
        {
            "cancelled" => new OperationCanceledException(),
            "normal-end" => new NormalEndException("normal"),
            _ => new InvalidOperationException("missing configuration")
        };
        Exception? observed = null;
        var exitCodes = new List<int>();
        // 同步失败（如协调器/配置初始化失败）也必须进入同一收尾边界。
        await CommandLineOneDragonLifetime.RunAsync(() => throw failure,
            error => observed = error, exitCodes.Add);
        Assert.Same(failure, observed);
        Assert.Equal(new[] { expectedCode }, exitCodes);
    }

    [Fact]
    public async Task DiagnosticFailureCannotPreventShutdown()
    {
        var exitCodes = new List<int>();
        await CommandLineOneDragonLifetime.RunAsync(() => Task.FromException(new TimeoutException()),
            _ => throw new IOException("log unavailable"), exitCodes.Add);
        Assert.Equal(new[] { 1 }, exitCodes);
    }

    [Fact]
    public async Task NextLaunchIsNotHeldByThePreviousFailureObserver()
    {
        var taskFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exitCodes = new List<int>();
        var first = CommandLineOneDragonLifetime.RunAsync(() => taskFinished.Task, _ => { }, exitCodes.Add);
        Assert.False(first.IsCompleted);
        Assert.Empty(exitCodes);
        taskFinished.SetException(new CombatNotFinishedException("first run failed"));
        await first.WaitAsync(TimeSpan.FromSeconds(3));
        var secondRan = false;
        await CommandLineOneDragonLifetime.RunAsync(() =>
        {
            secondRan = true;
            return Task.CompletedTask;
        }, _ => { }, exitCodes.Add).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(secondRan);
        Assert.Equal(new[] { 1 }, exitCodes);
    }

    [Fact]
    public async Task FailedOwnedOneDragonRequestsExitOnlyAfterTaskCleanup()
    {
        var cleaned = false;
        var reported = false;
        var exitCodes = new List<int>();
        async Task Run()
        {
            try { await Task.Yield(); throw new CombatNotFinishedException("unconfirmed"); }
            finally { cleaned = true; }
        }
        await CommandLineOneDragonLifetime.RunAsync(Run, error =>
        {
            Assert.True(cleaned);
            Assert.IsType<CombatNotFinishedException>(error);
            reported = true;
        }, code =>
        {
            Assert.True(cleaned);
            Assert.True(reported);
            exitCodes.Add(code);
        });
        Assert.Equal(new[] { 1 }, exitCodes);
    }
}
