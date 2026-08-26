using System.Reflection;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.Common.Job;
using BetterGenshinImpact.GameTask.LogParse;
using BetterGenshinImpact.GameTask.TaskProgress;
using BetterGenshinImpact.Service;
using BetterGenshinImpact.Service.Interface;
using BetterGenshinImpact.ViewModel.Pages;

namespace BetterGenshinImpact.UnitTest.GameTaskTests;

public class TaskRunnerTests
{
    [Fact]
    public void ManagedFailuresAreCollectedWithoutStoppingLaterWork()
    {
        var attempted = new List<string>();
        var first = new InvalidOperationException("commission failed");
        var second = new IOException("cultivation failed");
        var failures = new ManagedTaskFailureCollector();

        foreach (var (_, action) in new (string Name, Action Action)[]
                 {
                     ("commission", () => throw first),
                     ("rewards", () => attempted.Add("rewards")),
                     ("cultivation", () => throw second),
                     ("gathering", () => attempted.Add("gathering"))
                 })
        {
            try
            {
                action();
            }
            catch (Exception failure)
            {
                failures.Add(failure);
            }
        }

        Assert.Equal(["rewards", "gathering"], attempted);
        var report = Assert.Throws<AggregateException>(() =>
        {
            failures.ThrowIfAny("managed automation failed");
        });
        Assert.Equal(new Exception[] { first, second }, report.InnerExceptions);
    }

    [Fact]
    public void NestedManagedFailuresAreFlattenedBeforeFinalReporting()
    {
        var first = new InvalidOperationException("first project failed");
        var second = new IOException("second project failed");
        var failures = new ManagedTaskFailureCollector();

        failures.Add(new AggregateException("script group failed", first, second));

        var report = Assert.Throws<AggregateException>(() =>
        {
            failures.ThrowIfAny("one dragon failed");
        });
        Assert.Equal(new Exception[] { first, second }, report.InnerExceptions);
    }

    [Fact]
    public async Task SuccessfulStateRecoveryAllowsManagedAutomationToContinue()
    {
        var taskFailure = new InvalidOperationException("map could not open");
        var attempted = new List<string>();

        await TaskFailureRecoveryPolicy.RecoverOrThrowAsync(
            taskFailure,
            () =>
            {
                attempted.Add("recover main ui");
                return Task.CompletedTask;
            });
        attempted.Add("next project");

        Assert.Equal(["recover main ui", "next project"], attempted);
    }

    [Fact]
    public async Task FailedStateRecoveryPreservesBothFailuresAndStopsLaterWork()
    {
        var taskFailure = new InvalidOperationException("map could not open");
        var recoveryFailure = new TimeoutException("main ui recovery timed out");
        var attemptedLaterWork = false;

        var report = await Assert.ThrowsAsync<TaskFailureRecoveryException>(async () =>
        {
            await TaskFailureRecoveryPolicy.RecoverOrThrowAsync(
                taskFailure,
                () => Task.FromException(recoveryFailure));
            attemptedLaterWork = true;
        });

        Assert.False(attemptedLaterWork);
        Assert.Equal(new Exception[] { taskFailure, recoveryFailure }, report.InnerExceptions);
    }

    [Fact]
    public void FailedStateRecoveryRemainsFatalOutsidePropagationMode()
    {
        var recoveryFailure = new TaskFailureRecoveryException(
            new InvalidOperationException("project failed"),
            new TimeoutException("recovery failed"));

        var termination = TaskRunnerFailurePolicy.GetTerminationException(
            recoveryFailure,
            isContinuousRunGroup: false,
            propagateExceptions: false);

        Assert.Same(recoveryFailure, termination);
    }

    [Theory]
    [MemberData(nameof(NormalTerminationExceptions))]
    public async Task StateRecoveryNeverConvertsTerminationIntoManagedFailure(Exception termination)
    {
        var recoveryAttempted = false;

        var report = await Assert.ThrowsAsync(
            termination.GetType(),
            () => TaskFailureRecoveryPolicy.RecoverOrThrowAsync(
                termination,
                () =>
                {
                    recoveryAttempted = true;
                    return Task.CompletedTask;
                }));

        Assert.Same(termination, report);
        Assert.False(recoveryAttempted);
    }

    [Fact]
    public void ReturnMainUiRecoveryMustRejectAnUnverifiedFinalState()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => ReturnMainUiRecoveryGuard.ThrowIfNotRecovered(false, 8));

        Assert.Contains("8", exception.Message, StringComparison.Ordinal);
        ReturnMainUiRecoveryGuard.ThrowIfNotRecovered(true, 8);
    }

    [Fact]
    public void ReturnMainUiPassiveRecovery_ShouldUseTenSecondBoundedGraceWindow()
    {
        var policy = new ReturnMainUiPassiveRecoveryPolicy(
            timeout: TimeSpan.FromSeconds(10),
            heartbeatInterval: TimeSpan.FromSeconds(5));

        Assert.False(policy.IsTimedOut(TimeSpan.FromSeconds(9.9)));
        Assert.True(policy.IsTimedOut(TimeSpan.FromSeconds(10)));
        Assert.True(policy.ShouldLogHeartbeat(TimeSpan.FromSeconds(5)));
        Assert.False(policy.ShouldLogHeartbeat(TimeSpan.FromSeconds(5.1)));
    }

    [Fact]
    public void FailedExecutionRecordReceivesAnEndTimeWithoutBecomingSuccessful()
    {
        var record = new ExecutionRecord
        {
            StartTime = new DateTime(2026, 8, 25, 4, 10, 0),
            ServerStartTime = new DateTimeOffset(2026, 8, 25, 4, 10, 0, TimeSpan.FromHours(8))
        };
        var endTime = new DateTime(2026, 8, 25, 4, 12, 30);
        var serverEndTime = new DateTimeOffset(2026, 8, 25, 4, 12, 30, TimeSpan.FromHours(8));

        ExecutionRecordFinalizer.Complete(record, serverEndTime, endTime);

        Assert.Equal(endTime, record.EndTime);
        Assert.Equal(serverEndTime, record.ServerEndTime);
        Assert.False(record.IsSuccessful);
    }

    [Fact]
    public void FailureScreenshotIsCapturedOnceAcrossNestedReportingBoundaries()
    {
        var original = new InvalidOperationException("project failed");
        var outer = new AggregateException("group failed", original);
        var captures = new List<string>();

        TaskFailureDiagnostics.CaptureScreenshotOnce(original, "project", captures.Add);
        TaskFailureDiagnostics.CaptureScreenshotOnce(outer, "group", captures.Add);

        Assert.Equal(["project"], captures);
    }

    [Fact]
    public void FailureScreenshotErrorsNeverReplaceTheOriginalFailure()
    {
        var original = new InvalidOperationException("project failed");

        var exception = Record.Exception(() =>
            TaskFailureDiagnostics.CaptureScreenshotOnce(
                original,
                "project",
                _ => throw new IOException("disk full")));

        Assert.Null(exception);
    }

    [Theory]
    [MemberData(nameof(NormalTerminationExceptions))]
    public void ManagedFailureCollectionNeverConvertsTerminationIntoOrdinaryFailure(Exception termination)
    {
        var failures = new ManagedTaskFailureCollector();

        var report = Assert.Throws(
            termination.GetType(),
            () =>
            {
                failures.Add(termination);
            });

        Assert.Same(termination, report);
    }

    [Fact]
    public void CleanupContinuesAfterAnEarlierStepFails()
    {
        var completed = new List<string>();
        var failures = new List<(string Name, Exception Exception)>();
        var expected = new InvalidOperationException("release input failed");

        var cleanupFailures = TaskRunnerCleanup.RunAll(
        [
            ("release input", () => throw expected),
            ("restore triggers", () => completed.Add("restore triggers")),
            ("clear contexts", () => completed.Add("clear contexts"))
        ],
        (name, exception) => failures.Add((name, exception)));

        Assert.Equal(["restore triggers", "clear contexts"], completed);
        var failure = Assert.Single(failures);
        Assert.Equal("release input", failure.Name);
        Assert.Same(expected, failure.Exception);
        Assert.Equal([expected], cleanupFailures);
    }

    [Fact]
    public void ManagedExecutionContractsExposeOptInExceptionPropagation()
    {
        var runThreadAsync = typeof(TaskRunner).GetMethod(nameof(TaskRunner.RunThreadAsync));
        var runnerParameter = Assert.Single(runThreadAsync!.GetParameters().Skip(1));
        Assert.Equal("propagateExceptions", runnerParameter.Name);
        Assert.Equal(false, runnerParameter.DefaultValue);

        var runMulti = typeof(IScriptService).GetMethod(nameof(IScriptService.RunMulti));
        var serviceParameter = runMulti!.GetParameters().Last();
        Assert.Equal("propagateExceptions", serviceParameter.Name);
        Assert.Equal(false, serviceParameter.DefaultValue);

        var runOneDragonAsync = typeof(OneDragonFlowViewModel).GetMethod(
            "RunOneDragonAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var oneDragonParameter = Assert.Single(runOneDragonAsync!.GetParameters());
        Assert.Equal("propagateExceptions", oneDragonParameter.Name);

        var commandLineRun = typeof(OneDragonFlowViewModel).GetMethod(
            "RunCommandLineAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Equal(typeof(Task), commandLineRun!.ReturnType);
        Assert.Null(typeof(OneDragonFlowViewModel).GetMethod(
            "OnLoaded",
            BindingFlags.Instance | BindingFlags.NonPublic));

        var startGroups = typeof(ScriptControlViewModel).GetMethod(nameof(ScriptControlViewModel.StartGroups));
        var startGroupsParameter = startGroups!.GetParameters().Last();
        Assert.Equal("propagateExceptions", startGroupsParameter.Name);
        Assert.Equal(false, startGroupsParameter.DefaultValue);
    }

    [Fact]
    public void PropagationModeReportsLockContentionAsFailure()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            TaskRunnerFailurePolicy.ThrowIfLockUnavailable(propagateExceptions: true));

        Assert.Contains("存在正在运行中的独立任务", exception.Message);
        TaskRunnerFailurePolicy.ThrowIfLockUnavailable(propagateExceptions: false);
    }

    [Theory]
    [MemberData(nameof(NormalTerminationPropagationModes))]
    public void NormalTerminationPropagationHonorsManagedAndContinuousModes(
        Exception exception,
        bool isContinuousRunGroup,
        bool propagateExceptions,
        bool shouldPropagate)
    {
        var result = TaskRunnerFailurePolicy.GetTerminationException(
                exception,
                isContinuousRunGroup,
                propagateExceptions);

        if (shouldPropagate)
        {
            Assert.Same(exception, result);
        }
        else
        {
            Assert.Null(result);
        }
    }

    public static TheoryData<Exception, bool, bool, bool> NormalTerminationPropagationModes => new()
    {
        { new NormalEndException("managed normal end"), false, true, true },
        { new OperationCanceledException("managed cancellation"), false, true, true },
        { new NormalEndException("continuous normal end"), true, false, true },
        { new OperationCanceledException("continuous cancellation"), true, false, true },
        { new NormalEndException("manual normal end"), false, false, false },
        { new OperationCanceledException("manual cancellation"), false, false, false }
    };

    [Fact]
    public void ManagedStartupCancellationPreservesCancellationToken()
    {
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        var exception = Assert.Throws<OperationCanceledException>(() =>
            TaskRunnerFailurePolicy.ThrowIfStartupCancelled(
                cancellationSource.Token,
                propagateExceptions: true));

        Assert.Equal(cancellationSource.Token, exception.CancellationToken);
    }

    [Fact]
    public void ManualStartupCancellationKeepsCompatibilityBehavior()
    {
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        TaskRunnerFailurePolicy.ThrowIfStartupCancelled(
            cancellationSource.Token,
            propagateExceptions: false);
    }

    [Fact]
    public void ContinuousGroupCancellationPreservesCancellationToken()
    {
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        var exception = Assert.Throws<OperationCanceledException>(() =>
            TaskRunnerFailurePolicy.ThrowIfStartupCancelled(
                cancellationSource.Token,
                propagateExceptions: false,
                isContinuousRunGroup: true));

        Assert.Equal(cancellationSource.Token, exception.CancellationToken);
    }

    [Fact]
    public void CompletedTaskCancellationCheckUsesCapturedToken()
    {
        using var taskCancellationSource = new CancellationTokenSource();
        using var replacementCancellationSource = new CancellationTokenSource();
        var taskCancellationToken = taskCancellationSource.Token;
        taskCancellationSource.Cancel();

        var exception = Assert.Throws<OperationCanceledException>(() =>
            TaskRunnerFailurePolicy.ThrowIfTaskCancelled(
                taskCancellationToken,
                propagateExceptions: true));

        Assert.False(replacementCancellationSource.IsCancellationRequested);
        Assert.Equal(taskCancellationToken, exception.CancellationToken);
    }

    [Theory]
    [InlineData("无法定位任务进度记录：taskProgress为空（missing）")]
    [InlineData("无法定位到下一个要执行的项目：next为空（daily）")]
    public void ManagedTaskProgressResumeReportsMissingStateAsFailure(string message)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            TaskProgressResumeFailurePolicy.ThrowIfManaged(message, propagateExceptions: true));

        Assert.Equal(message, exception.Message);
    }

    [Theory]
    [InlineData("无法定位任务进度记录：taskProgress为空（missing）")]
    [InlineData("无法定位到下一个要执行的项目：next为空（daily）")]
    public void ManualTaskProgressResumeKeepsCompatibilityBehavior(string message)
    {
        TaskProgressResumeFailurePolicy.ThrowIfManaged(message, propagateExceptions: false);
    }

    [Fact]
    public void ManagedScriptGroupSelectionReportsAllMissingNamesBeforeExecution()
    {
        var requestedNames = new[] { "daily", "missing-a", "missing-b", "missing-a" };
        var missingNames = ManagedScriptGroupSelectionPolicy.GetMissingNames(
            requestedNames,
            ["daily"]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ManagedScriptGroupSelectionPolicy.ThrowIfInvalid(requestedNames, missingNames));

        Assert.Equal(["missing-a", "missing-b"], missingNames);
        Assert.Contains("missing-a、missing-b", exception.Message);
    }

    [Fact]
    public void ManagedScriptGroupSelectionRejectsEmptyRequests()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ManagedScriptGroupSelectionPolicy.ThrowIfInvalid([], []));

        Assert.Contains("配置组为空", exception.Message);
    }

    [Fact]
    public void ManagedScriptGroupSelectionAcceptsCompleteRequests()
    {
        var requestedNames = new[] { "daily", "weekly" };
        var missingNames = ManagedScriptGroupSelectionPolicy.GetMissingNames(
            requestedNames,
            ["daily", "weekly"]);

        ManagedScriptGroupSelectionPolicy.ThrowIfInvalid(requestedNames, missingNames);
        Assert.Empty(missingNames);
    }

    [Fact]
    public void CommandLineFailureSetsNonZeroExitCode()
    {
        var exitCode = 0;

        CommandLineTaskFailurePolicy.MarkFailed(value => exitCode = value);

        Assert.Equal(CommandLineTaskFailurePolicy.FailureExitCode, exitCode);
        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public void PropagationModeReportsCleanupFailuresAfterAllStepsRun()
    {
        var completed = false;
        var cleanupException = new InvalidOperationException("release input failed");
        var failures = TaskRunnerCleanup.RunAll(
        [
            ("release input", () => throw cleanupException),
            ("clear context", () => completed = true)
        ],
        (_, _) => { });

        var exception = Assert.Throws<AggregateException>(() =>
            TaskRunnerFailurePolicy.ThrowAfterCleanup(null, failures, propagateCleanupFailures: true));

        Assert.True(completed);
        Assert.Equal(new Exception[] { cleanupException }, exception.InnerExceptions);
    }

    [Fact]
    public void PropagationModeReportsExecutionAndCleanupFailuresTogether()
    {
        var executionException = new InvalidOperationException("script failed");
        var cleanupException = new InvalidOperationException("release input failed");

        var exception = Assert.Throws<AggregateException>(() =>
            TaskRunnerFailurePolicy.ThrowAfterCleanup(
                executionException,
                [cleanupException],
                propagateCleanupFailures: true));

        Assert.Equal(new Exception[] { executionException, cleanupException }, exception.InnerExceptions);
    }

    [Theory]
    [MemberData(nameof(NormalTerminationExceptions))]
    public void CleanupFailureDoesNotReplaceNormalTermination(Exception executionException)
    {
        var exception = Assert.Throws(
            executionException.GetType(),
            () => TaskRunnerFailurePolicy.ThrowAfterCleanup(
                executionException,
                [new InvalidOperationException("release input failed")],
                propagateCleanupFailures: true));

        Assert.Same(executionException, exception);
    }

    public static TheoryData<Exception> NormalTerminationExceptions => new()
    {
        new NormalEndException("normal end"),
        new OperationCanceledException("cancelled")
    };

    [Fact]
    public void CompatibilityModeDoesNotPropagateCleanupFailure()
    {
        TaskRunnerFailurePolicy.ThrowAfterCleanup(
            null,
            [new InvalidOperationException("release input failed")],
            propagateCleanupFailures: false);
    }

    [Fact]
    public async Task OneDragonFinalizerRunsCompletionActionWhenFinalCheckFails()
    {
        var expected = new InvalidOperationException("daily reward was not claimed");
        var events = new List<string>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OneDragonFinalizer.RunAsync(
                () => Task.FromException(expected),
                () => events.Add("completion action"),
                observedException =>
                {
                    Assert.Same(expected, observedException);
                    events.Add("failure recorded");
                }));

        Assert.Equal(["failure recorded", "completion action"], events);
        Assert.Same(expected, exception);
    }

    [Fact]
    public async Task OneDragonFinalizerRunsCompletionActionWhenTaskBodyFails()
    {
        var expected = new InvalidOperationException("role cultivation failed");
        var events = new List<string>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OneDragonFinalizer.RunWithFailureCompletionAsync(
                () => Task.FromException(expected),
                () => events.Add("completion action")));

        Assert.Equal(["completion action"], events);
        Assert.Same(expected, exception);
    }

    [Fact]
    public async Task OneDragonFinalizerReportsBothFailures()
    {
        var finalCheckException = new InvalidOperationException("daily reward was not claimed");
        var completionException = new InvalidOperationException("failed to close the game");

        var exception = await Assert.ThrowsAsync<AggregateException>(() =>
            OneDragonFinalizer.RunAsync(
                () => Task.FromException(finalCheckException),
                () => throw completionException));

        Assert.Equal(new Exception[] { finalCheckException, completionException }, exception.InnerExceptions);
    }

    [Fact]
    public async Task OneDragonFinalizerDoesNotRunCompletionActionAfterCancellation()
    {
        var completionActionRan = false;

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            OneDragonFinalizer.RunAsync(
                () => Task.FromException(new OperationCanceledException()),
                () => completionActionRan = true));

        Assert.False(completionActionRan);
    }

    [Fact]
    public async Task OneDragonFinalizerDoesNotRunCompletionActionAfterNormalEnd()
    {
        var completionActionRan = false;

        await Assert.ThrowsAsync<NormalEndException>(() =>
            OneDragonFinalizer.RunAsync(
                () => Task.FromException(new NormalEndException("cancel automation")),
                () => completionActionRan = true));

        Assert.False(completionActionRan);
    }

    [Fact]
    public void ScriptProgressFinalizerPersistsFailureStateBeforePropagation()
    {
        var endTime = new DateTime(2026, 8, 12, 19, 0, 0);
        var projectInfo = new TaskProgress.ScriptGroupProjectInfo { Status = 2 };
        var progress = new TaskProgress
        {
            CurrentScriptGroupName = "daily",
            CurrentScriptGroupProjectInfo = projectInfo,
            ConsecutiveFailureCount = 2
        };

        ScriptTaskProgressFinalizer.CompleteCurrentProject(progress, endTime);

        Assert.True(projectInfo.TaskEnd);
        Assert.Equal(endTime, projectInfo.EndTime);
        Assert.Equal(3, progress.ConsecutiveFailureCount);
        Assert.Same(projectInfo, Assert.Single(progress.History!));
    }
}
