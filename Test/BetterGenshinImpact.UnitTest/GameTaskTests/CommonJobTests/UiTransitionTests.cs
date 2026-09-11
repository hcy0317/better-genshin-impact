using BetterGenshinImpact.GameTask.Common.Ui;
using Microsoft.Extensions.Time.Testing;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class UiTransitionTests
{
    [Theory]
    [InlineData(7, 1)]
    [InlineData(2, 2)]
    public async Task RetryAdmissionPreservesFailureWhenRecoveryLeavesTooLittleBudget(int attemptSeconds, int expectedAttempts)
    {
        var clock = new FakeTimeProvider();
        var original = new InvalidOperationException("map drag did not converge");
        var attempts = 0;
        var recoveries = 0;
        async Task<int> Run() => await UiOperation.RunAsync("teleport", TimeSpan.FromSeconds(10), default,
            operation => UiRecovery.RunWithRecoveryAsync<int>(_ =>
            {
                attempts++;
                if (attempts > 1) return Task.FromResult(42);
                clock.Advance(TimeSpan.FromSeconds(attemptSeconds));
                throw original;
            }, _ =>
            {
                recoveries++;
                clock.Advance(TimeSpan.FromSeconds(1));
                return Task.CompletedTask;
            }, operation.Token, minimumRetryBudget: TimeSpan.FromSeconds(3)), clock: clock);
        if (expectedAttempts == 1)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(Run);
            Assert.Same(original, error);
            Assert.True(error.Data.Contains("UiRetrySkipped"));
        }
        else Assert.Equal(42, await Run());
        Assert.Equal(expectedAttempts, attempts);
        Assert.Equal(1, recoveries);
    }

    [Fact]
    public void FullPartyDefeatVetoesConflictingPageEvidence()
    {
        UiSnapshot[] samples = [new(1) { FullPartyDefeat = true, MainHud = true },
            new(2) { FullPartyDefeat = true, Party = true },
            new(3) { FullPartyDefeat = true, PartyList = true },
            new(4) { FullPartyDefeat = true, BigMap = true },
            new(5) { FullPartyDefeat = true, Crafting = true },
            new(6) { FullPartyDefeat = true, MenuBack = true }];
        foreach (var snapshot in samples)
        {
            foreach (var target in Enum.GetValues<UiTarget>()) Assert.False(snapshot.Matches(target));
            Assert.False(snapshot.MapReady);
            Assert.False(snapshot.CanEscape);
        }
    }

    [Fact]
    public async Task FoodRevivePromptIsClosedWithoutConfirmingConsumption()
    {
        var clock = new FakeTimeProvider();
        var driver = new ReplayDriver(clock, new(1) { Revive = true, Prompt = true, BlackConfirm = true },
            new(2) { MainHud = true }, new(3) { MainHud = true });
        await UiRecovery.ToMainAsync(driver, default, clock: clock);
        Assert.Equal(new[] { UiAction.Escape }, driver.Actions);
    }

    [Fact]
    public async Task FullPartyDefeatUsesReviveInsteadOfEscapeAndWaitsForStableHud()
    {
        var clock = new FakeTimeProvider();
        var driver = new ReplayDriver(clock,
            new(1) { Revive = true, FullPartyDefeat = true },
            new(2), new(3) { MainHud = true }, new(4) { MainHud = true });
        var result = await UiRecovery.ToMainAsync(driver, default, clock: clock);
        Assert.Equal(4, result.FrameId);
        Assert.Equal(new[] { UiAction.ReviveParty }, driver.Actions);
    }

    [Fact]
    public async Task RecognizedCommissionHandbookCanExitBeforeTrackingStarts()
    {
        var clock = new FakeTimeProvider();
        var handbook = HandbookUiRecognition.IsCommissionPage(
            ["见闻", "委托", "秘境", "讨伐", "向导", "备战"],
            ["每日委托奖励0/4", "选择委托任务倾向地域", "长效历练点778.3"]);
        Assert.True(handbook);
        var driver = new ReplayDriver(clock, new(1) { Handbook = handbook }, new(2),
            new(3) { MainHud = true }, new(4) { MainHud = true });
        Assert.Equal(4, (await UiRecovery.ToMainAsync(driver, default, clock: clock)).FrameId);
        Assert.Equal(new[] { UiAction.Escape }, driver.Actions);
        Assert.False(HandbookUiRecognition.IsCommissionPage(["委托"], ["每日委托奖励"]));
        Assert.False(HandbookUiRecognition.IsCommissionPage(["委托", "秘境"], ["普通任务详情"]));
    }

    [Fact]
    public async Task NestedRecoveryTimeoutReportsTheInnerPageAndItsObservation()
    {
        var clock = new FakeTimeProvider();
        var driver = new ReplayDriver(clock, new UiSnapshot(1) { Handbook = true });
        var error = await Assert.ThrowsAsync<TimeoutException>(() => UiOperation.RunAsync(
            "failure-recovery", TimeSpan.FromSeconds(1), default,
            operation => UiRecovery.ToMainAsync(driver, operation.Token, requireOverworld: true, clock: clock), clock: clock));
        Assert.Contains("return-main", error.Message);
        Assert.Contains("Overworld", error.Message);
        Assert.Contains("handbook=True", error.Message);
    }

    [Fact]
    public async Task CraftingResultOverlayMustClearBeforeTheNextMaterialCanStart()
    {
        var clock = new FakeTimeProvider();
        var driver = new ReplayDriver(clock,
            new(1) { Crafting = true, Prompt = true }, new(2) { Crafting = true },
            new(3), new(4) { Crafting = true }, new(5) { Crafting = true });
        var result = await UiTransition.WaitAsync("craft-result", UiTarget.Crafting, driver,
            default, TimeSpan.FromSeconds(3), clock: clock);
        Assert.Equal(5, result.FrameId);
        Assert.Empty(driver.Actions);
    }

    [Fact]
    public async Task TransitionNeedsTwoFreshUnambiguousSamplesAfterAnyUnknownState()
    {
        var clock = new FakeTimeProvider();
        var driver = new ReplayDriver(clock,
            new(1) { MainHud = true, BigMap = true },
            new(2) { MainHud = true },
            new(2) { MainHud = true },
            new(3),
            new(4) { MainHud = true },
            new(5) { MainHud = true });
        var result = await UiTransition.WaitAsync("test-main", UiTarget.Main, driver, default,
            TimeSpan.FromSeconds(3), clock: clock);
        Assert.Equal(5, result.FrameId);
        Assert.Empty(driver.Actions);
    }

    [Fact]
    public async Task PartySelectionUsesPositivePageConfirmationAndLeavesDomainStartToCaller()
    {
        var clock = new FakeTimeProvider();
        var driver = new ReplayDriver(clock, new(1) { Party = true, PartyList = true },
            new(2) { Party = true, PartyList = true }, new(3) { Party = true }, new(4) { Party = true });
        var applied = false;
        var result = await UiRecovery.ConfirmPartyAsync(driver, _ => Task.FromResult(true),
            _ => { applied = true; return Task.FromResult(true); }, default, deferApplyToCaller: true, clock: clock);
        Assert.True(result.Matches(UiTarget.Party));
        Assert.False(applied);
    }

    [Fact]
    public async Task PartyListOwnConfirmButtonDoesNotPreventSelectingTheParty()
    {
        var clock = new FakeTimeProvider();
        var driver = new ReplayDriver(clock,
            new(1) { PartyList = true, BlackConfirm = true },
            new(2) { PartyList = true, BlackConfirm = true },
            new(3) { Party = true, BlackConfirm = true },
            new(4) { Party = true, BlackConfirm = true });
        var selected = false;
        var result = await UiRecovery.ConfirmPartyAsync(driver,
            _ => { selected = true; return Task.FromResult(true); },
            _ => Task.FromResult(true), default, deferApplyToCaller: true, clock: clock);
        Assert.True(selected);
        Assert.True(result.Matches(UiTarget.Party));
    }

    [Fact]
    public async Task FailedPartySelectionCannotApplyOrReportSuccess()
    {
        var clock = new FakeTimeProvider();
        var driver = new ReplayDriver(clock, new(1) { PartyList = true }, new(2) { PartyList = true });
        var applied = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => UiRecovery.ConfirmPartyAsync(driver,
            _ => Task.FromResult(false), _ => { applied = true; return Task.FromResult(true); }, default, clock: clock));
        Assert.False(applied);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PublicPartyApplyRequiresSuccessfulInputAndPositiveFinalState(bool applySucceeds, bool stateReady)
    {
        var clock = new FakeTimeProvider();
        var driver = new ReplayDriver(clock, new(1) { PartyList = true }, new(2) { PartyList = true },
            new(3) { Party = true }, new(4) { Party = true },
            new(5), new(6) { MainHud = stateReady }, new(7) { MainHud = stateReady });
        var applied = 0;
        var pending = UiRecovery.ConfirmPartyAsync(driver, _ => Task.FromResult(true),
            _ => { applied++; return Task.FromResult(applySucceeds); }, default, clock: clock);
        if (!applySucceeds) await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        else if (!stateReady) await Assert.ThrowsAsync<TimeoutException>(() => pending);
        else Assert.Equal(7, (await pending).FrameId);
        Assert.Equal(1, applied);
    }

    [Fact]
    public async Task DifferentMatchingSignaturesDoNotCountAsStableConfirmation()
    {
        var clock = new FakeTimeProvider();
        var driver = new ReplayDriver(clock, new(1) { Party = true }, new(2) { MainHud = true },
            new(3) { Party = true }, new(4) { MainHud = true }, new(5) { MainHud = true });
        var result = await UiTransition.WaitAsync("party-or-main", UiTarget.PartyOrMain, driver,
            default, TimeSpan.FromSeconds(3), clock: clock);
        Assert.Equal(5, result.FrameId);
    }

    [Theory]
    [InlineData(UiTarget.Party, false)]
    [InlineData(UiTarget.Party, true)]
    [InlineData(UiTarget.PartyList, false)]
    [InlineData(UiTarget.PartyList, true)]
    internal void PartyReadinessRejectsRecognizedOverlayEvenWhenDialogTemplateIsMissing(UiTarget target, bool exitDoor)
    {
        var snapshot = new UiSnapshot(1)
        {
            Party = true, PartyList = target == UiTarget.PartyList,
            ExitDoor = exitDoor, Prompt = !exitDoor, BlackConfirm = !exitDoor
        };
        Assert.False(snapshot.Matches(target));
    }

    [Fact]
    public async Task TeleportRequiresVerifiedPostconditionNotJustSuccessfulInput()
    {
        var clock = new FakeTimeProvider();
        var driver = new ReplayDriver(clock, new(1) { BigMap = true }, new(2),
            new(3) { MainHud = true }, new(4) { MainHud = true });
        Assert.Equal(42, await UiRecovery.TeleportAsync(driver, _ => Task.FromResult(42), default, clock: clock));
        var unknown = new ReplayDriver(clock, new(1) { BigMap = true }, new(2));
        await Assert.ThrowsAsync<TimeoutException>(() => UiRecovery.TeleportAsync(unknown,
            _ => Task.FromResult(42), default, clock: clock));
    }

    [Fact]
    public async Task MapWithOverlappingDialogCannotBypassTeleportPreparation()
    {
        var clock = new FakeTimeProvider();
        var driver = new ReplayDriver(clock, new(1) { BigMap = true, Prompt = true }, new(2));
        var attempted = false;
        await Assert.ThrowsAsync<TimeoutException>(() => UiRecovery.TeleportAsync(driver,
            _ => { attempted = true; return Task.FromResult(42); }, default, clock: clock));
        Assert.False(attempted);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task KnownDomainOrReviveStateRejectsTeleportBeforeAnyInput(bool domain, bool revive)
    {
        var clock = new FakeTimeProvider();
        var driver = new ReplayDriver(clock, new UiSnapshot(1) { MainHud = true, InDomain = domain, Revive = revive });
        var attempted = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => UiRecovery.TeleportAsync(driver,
            _ => { attempted = true; return Task.FromResult(1); }, default, clock: clock));
        Assert.False(attempted);
        Assert.Empty(driver.Actions);
    }

    [Fact]
    public async Task FailedUiRecoveryStopsRetryAndPreservesBothCauses()
    {
        var original = new InvalidOperationException("teleport failed");
        var recovery = new TimeoutException("map stayed open");
        var attempts = 0;
        var error = await Assert.ThrowsAsync<BetterGenshinImpact.GameTask.TaskFailureRecoveryException>(() =>
            UiRecovery.RunWithRecoveryAsync<int>(_ => { attempts++; throw original; }, _ => throw recovery, default));
        Assert.Equal(1, attempts);
        Assert.Equal(new Exception[] { original, recovery }, error.InnerExceptions);
    }

    [Fact]
    public async Task SuccessfulUiRecoveryAllowsOnlyTheBoundedNextAttempt()
    {
        var attempts = 0;
        var result = await UiRecovery.RunWithRecoveryAsync(_ =>
        {
            if (++attempts == 1) throw new InvalidOperationException("temporary map state");
            return Task.FromResult(42);
        }, _ => Task.CompletedTask, default);
        Assert.Equal(42, result);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task ExhaustedParentBudgetPreservesOriginalFailureWithoutRestartingRecovery()
    {
        var clock = new FakeTimeProvider();
        var original = new InvalidOperationException("teleport failed at deadline");
        var recoveryStarted = false;
        var failure = await Assert.ThrowsAsync<BetterGenshinImpact.GameTask.TaskFailureRecoveryException>(() =>
            UiOperation.RunAsync<int>("parent", TimeSpan.FromSeconds(1), default, operation =>
                UiRecovery.RunWithRecoveryAsync<int>(_ =>
                {
                    clock.Advance(TimeSpan.FromSeconds(1));
                    throw original;
                }, _ => { recoveryStarted = true; return Task.CompletedTask; }, operation.Token), clock: clock));
        Assert.Same(original, failure.InnerExceptions[0]);
        Assert.IsType<TimeoutException>(failure.InnerExceptions[1]);
        Assert.False(recoveryStarted);
    }

    [Fact]
    public async Task MainRecoveryWaitsOnUnknownAndClosesOnlyAnIdentifiedUi()
    {
        var clock = new FakeTimeProvider();
        var driver = new ReplayDriver(clock, new(1), new(2) { BigMap = true },
            new(3) { MainHud = true }, new(4) { MainHud = true });
        var result = await UiRecovery.ToMainAsync(driver, default, clock: clock);
        Assert.Equal(4, result.FrameId);
        Assert.Equal(new[] { UiAction.Escape }, driver.Actions);
    }

    [Fact]
    public async Task DomainExitClosesReviveThenConfirmsItsOwnExitAndWaitsForOverworld()
    {
        var clock = new FakeTimeProvider();
        var driver = new ReplayDriver(clock,
            new(1) { Revive = true, InDomain = true },
            new(2) { MainHud = true, InDomain = true },
            new(3) { MainHud = true, InDomain = true },
            new(4) { Prompt = true, BlackConfirm = true }, new(5),
            new(6) { MainHud = true }, new(7) { MainHud = true });
        var result = await UiRecovery.ExitDomainAsync(driver, default, clock: clock);
        Assert.Equal(7, result.FrameId);
        Assert.Equal(new[] { UiAction.Escape, UiAction.RequestDomainExit, UiAction.ConfirmDomainExit }, driver.Actions);
    }

    [Fact]
    public async Task FailedExitRequestNeverAuthorizesConfirmingAFollowingDialog()
    {
        var clock = new FakeTimeProvider();
        var driver = new ReplayDriver(clock, new(1) { MainHud = true, InDomain = true },
            new(2) { MainHud = true, InDomain = true }, new(3) { Prompt = true, BlackConfirm = true })
        { ApplyAction = _ => false };
        await Assert.ThrowsAsync<TimeoutException>(() => UiRecovery.ExitDomainAsync(driver, default, clock: clock));
        Assert.Contains(UiAction.RequestDomainExit, driver.Actions);
        Assert.DoesNotContain(UiAction.ConfirmDomainExit, driver.Actions);
        Assert.InRange(driver.Actions.Count, 1, 8);
    }

    [Fact]
    public async Task ExitIntentDoesNotConfirmReviveAndSuccessfulConfirmIsNotRepeated()
    {
        var clock = new FakeTimeProvider();
        var driver = new ReplayDriver(clock, new(1) { MainHud = true, InDomain = true },
            new(2) { MainHud = true, InDomain = true }, new(3) { Revive = true, Prompt = true, BlackConfirm = true },
            new(4) { Prompt = true, BlackConfirm = true }, new(5) { Prompt = true, BlackConfirm = true },
            new(6) { MainHud = true }, new(7) { MainHud = true });
        Assert.Equal(7, (await UiRecovery.ExitDomainAsync(driver, default, clock: clock)).FrameId);
        Assert.Equal(new[] { UiAction.RequestDomainExit, UiAction.ConfirmDomainExit }, driver.Actions);
        Assert.Equal(new long[] { 2, 4 }, driver.ActionFrames);
    }

    [Fact]
    public async Task MainHudInsideDomainIsNotASafeOverworldRecovery()
    {
        var clock = new FakeTimeProvider();
        var driver = new ReplayDriver(clock, new UiSnapshot(1) { MainHud = true, InDomain = true });
        await Assert.ThrowsAsync<TimeoutException>(() => UiRecovery.ToMainAsync(driver, default,
            requireOverworld: true, clock: clock));
        Assert.Empty(driver.Actions);
    }

    [Fact]
    public void NestedUiOperationCannotExtendTheParentBudgetAndRestoresItsScope()
    {
        var clock = new FakeTimeProvider();
        using (var parent = UiOperation.Begin("parent", TimeSpan.FromSeconds(10), clock: clock))
        {
            clock.Advance(TimeSpan.FromSeconds(3));
            using (var child = UiOperation.Begin("child", TimeSpan.FromSeconds(30)))
            {
                Assert.Equal(parent.RootId, child.RootId);
                Assert.Equal(TimeSpan.FromSeconds(7), child.Remaining);
                clock.Advance(TimeSpan.FromSeconds(7));
                Assert.Throws<TimeoutException>(child.Check);
            }
            Assert.Same(parent, UiOperation.Current);
        }
        Assert.Null(UiOperation.Current);
    }

    [Theory]
    [InlineData("pause", false)]
    [InlineData("focus", false)]
    [InlineData("pause", true)]
    [InlineData("focus", true)]
    public async Task ProductionManagedWaitExitsOnUiBudgetOrUserCancellation(string operation, bool cancelByUser)
    {
        var clock = new FakeTimeProvider();
        using var user = new CancellationTokenSource();
        var cleaned = false;
        var task = UiOperation.RunAsync(operation, TimeSpan.FromMilliseconds(150), user.Token, _ =>
        {
            try
            {
                TaskControl.WaitWhileManaged(() => true, () => { }, milliseconds =>
                {
                    clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
                    if (cancelByUser) user.Cancel();
                });
                return Task.FromResult(true);
            }
            finally { cleaned = true; }
        }, clock: clock);
        if (cancelByUser) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        else await Assert.ThrowsAsync<TimeoutException>(() => task);
        Assert.True(cleaned);
        Assert.Null(UiOperation.Current);
    }

    [Fact]
    public async Task LegacyStopFromExpiredUiBudgetBecomesTimeoutRatherThanUserStop()
    {
        var clock = new FakeTimeProvider();
        await Assert.ThrowsAsync<TimeoutException>(() => UiOperation.RunAsync<int>("legacy", TimeSpan.FromSeconds(1), default, _ =>
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            throw new NormalEndException("取消自动任务");
        }, clock: clock));
    }

    [Fact]
    public async Task UiDelayCanCompleteWithoutTheCallingUiSynchronizationContext()
    {
        var clock = new FakeTimeProvider();
        using var operation = UiOperation.Begin("sync-sleep", TimeSpan.FromSeconds(1), clock: clock);
        var context = new QueuedSynchronizationContext();
        var previous = SynchronizationContext.Current;
        Task pending;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            pending = operation.DelayAsync(100, default);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        clock.Advance(TimeSpan.FromMilliseconds(100));
        try { await pending.WaitAsync(TimeSpan.FromSeconds(1)); }
        finally { context.Drain(); }
    }

    [Fact]
    public async Task DiagnosticsAreCorrelatedAndBoundedWithoutChangingOutcome()
    {
        var clock = new FakeTimeProvider();
        var logger = new RecordingLogger();
        var samples = Enumerable.Range(1, 100).Select(i => new UiSnapshot(i) { BigMap = i % 2 == 0 }).ToArray();
        var driver = new ReplayDriver(clock, samples);
        await Assert.ThrowsAsync<TimeoutException>(() => UiTransition.WaitAsync("bounded-log", UiTarget.Main,
            driver, default, TimeSpan.FromSeconds(15), logger: logger, clock: clock));
        Assert.Contains(logger.Messages, message => message.Contains("UI_BEGIN"));
        Assert.Contains(logger.Messages, message => message.Contains("UI_END") && message.Contains("outcome=failed"));
        Assert.InRange(logger.Messages.Count, 2, 34);
        Assert.All(logger.Messages, message => Assert.Contains("root=", message));
    }

    [Fact]
    public async Task NestedDiagnosticsShareRootAndDescribeSuppressedObservations()
    {
        var clock = new FakeTimeProvider();
        var logger = new RecordingLogger();
        await UiOperation.RunAsync("parent", TimeSpan.FromSeconds(10), default, async parent =>
        {
            await UiOperation.RunAsync("child", TimeSpan.FromSeconds(2), parent.Token, child =>
            {
                for (var i = 1; i <= 10; i++) child.Observe(new(i), UiTarget.Main);
                Assert.Contains(logger.Messages, message => message.Contains($"op={child.Id} parent={parent.Id}"));
                return Task.FromResult(true);
            });
            Assert.All(logger.Messages, message => Assert.Contains($"root={parent.Id}", message));
            return true;
        }, logger, clock);
        Assert.Single(logger.Messages.Where(message => message.Contains("UI_STATE")));
        Assert.Contains(logger.Messages, message => message.Contains("UI_END") && message.Contains("suppressed=9"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalFailureCaptureIsCorrelatedAndPrecedesRecoveryEvenIfCaptureFails(bool captureFails)
    {
        var original = new InvalidOperationException("original");
        var events = new List<string>();
        await BetterGenshinImpact.GameTask.TaskFailureRecoveryPolicy.RecoverOrThrowAsync(original,
            () => { events.Add("recover"); return Task.CompletedTask; },
            captureFailure: (error, context) =>
            {
                Assert.Same(original, error);
                Assert.NotNull(UiOperation.Current);
                Assert.Contains($"root={UiOperation.Current.RootId} op={UiOperation.Current.Id}", context);
                events.Add("capture");
                if (captureFails) throw new IOException("capture failed");
            });
        Assert.Equal(new[] { "capture", "recover" }, events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledOrFailingDiagnosticsDoNotAffectSuccessOrReplaceFailure(bool throws)
    {
        var clock = new FakeTimeProvider();
        var logger = new RecordingLogger { Enabled = false, ThrowOnLog = throws };
        var driver = new ReplayDriver(clock, new(1) { MainHud = true }, new(2) { MainHud = true });
        Assert.Equal(2, (await UiRecovery.ToMainAsync(driver, default, logger: logger, clock: clock)).FrameId);
        var original = new InvalidOperationException("original");
        var observed = await Assert.ThrowsAsync<InvalidOperationException>(() => UiOperation.RunAsync<int>(
            "capture-failed", TimeSpan.FromSeconds(1), default, _ => throw original, logger, clock,
            (_, _) => throw new IOException("screenshot failed")));
        Assert.Same(original, observed);
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public bool Enabled { get; init; } = true;
        public bool ThrowOnLog { get; init; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => Enabled;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (ThrowOnLog) throw new IOException("log sink failed");
            if (Enabled) Messages.Add(formatter(state, exception));
        }
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();
        public override void Post(SendOrPostCallback callback, object? state) => _queue.Enqueue((callback, state));
        public void Drain()
        {
            while (_queue.TryDequeue(out var work)) work.Callback(work.State);
        }
    }

    private sealed class ReplayDriver(FakeTimeProvider clock, params UiSnapshot[] snapshots) : IUiDriver
    {
        private int _index;
        public List<UiAction> Actions { get; } = [];
        public List<long> ActionFrames { get; } = [];
        public Func<UiAction, bool> ApplyAction { get; init; } = _ => true;
        public UiSnapshot Capture()
        {
            var sample = snapshots[Math.Min(_index++, snapshots.Length - 1)];
            return _index > snapshots.Length ? sample with { FrameId = sample.FrameId + _index - snapshots.Length } : sample;
        }
        public Task DelayAsync(int milliseconds, CancellationToken ct)
        {
            clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
        public Task<bool> ActAsync(UiAction action, UiSnapshot observed, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Actions.Add(action);
            ActionFrames.Add(observed.FrameId);
            return Task.FromResult(ApplyAction(action));
        }
    }
}
