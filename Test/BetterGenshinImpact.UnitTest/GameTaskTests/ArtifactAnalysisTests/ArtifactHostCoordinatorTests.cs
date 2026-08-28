using BetterGenshinImpact.GameTask.ArtifactAnalysis;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.ArtifactAnalysisTests;

public class ArtifactHostCoordinatorTests
{
    [Fact]
    public void PreflightReadyIsNotTheMissingValueDefault()
    {
        Assert.Equal(0, (int)ArtifactPreflightStatus.Unknown);
        Assert.NotEqual(0, (int)ArtifactPreflightStatus.Ready);
    }

    [Fact]
    public async Task ScanCharacterRoster_ScansAndSubmitsCompleteRoster()
    {
        var roster = new ArtifactCharacterRosterDto(
            "102550550",
            [
                new ArtifactCharacterRosterEntryDto("Furina", 90, false),
                new ArtifactCharacterRosterEntryDto("Noelle", 80, true)
            ]);
        var rosterScanner = new FakeRosterScanner(roster);
        var client = new FakeClient();
        var coordinator = new ArtifactHostCoordinator(
            new FakeScanner(Snapshot()), client,
            new FakeLockExecutor(), new FakeNativeExecutor(), rosterScanner);

        await coordinator.RunAsync(
            Request(ArtifactHostOperation.ScanCharacterRoster),
            "token", CancellationToken.None);

        Assert.Equal(1, rosterScanner.Calls);
        Assert.Equal("眇", rosterScanner.GameNickname);
        Assert.Equal("遥", rosterScanner.MiliastraNickname);
        Assert.Equal("MannequinGirl", rosterScanner.MiliastraCharacterKey);
        Assert.Same(roster, client.SubmittedCharacterRoster);
        Assert.True(client.CompletionSuccess);
    }

    [Fact]
    public async Task Analyze_ScansAndSubmitsSnapshot()
    {
        var scanner = new FakeScanner(Snapshot());
        var client = new FakeClient();
        var coordinator = new ArtifactHostCoordinator(scanner, client, new FakeLockExecutor(), new FakeNativeExecutor());

        await coordinator.RunAsync(Request(ArtifactHostOperation.Analyze), "token", CancellationToken.None);

        Assert.Equal(1, scanner.Calls);
        Assert.Same(scanner.Snapshot, client.SubmittedSnapshot);
        Assert.Equal("job-1", client.SubmittedJobId);
    }

    [Fact]
    public async Task SuccessfulGameOperationUsesIndependentCompletionToken()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var client = new FakeClient();
        var coordinator = new ArtifactHostCoordinator(
            new FakeScanner(Snapshot()), client,
            new FakeLockExecutor(), new FakeNativeExecutor());

        await coordinator.RunAsync(
            Request(ArtifactHostOperation.Analyze),
            "token",
            cancelled.Token);

        Assert.True(client.CompletionSuccess);
        Assert.False(client.CompletionTokenWasCancelledAtCall);
    }

    [Fact]
    public async Task Analyze_ManualCancellationReportsStoppedStateAndPropagates()
    {
        var cancellation = new OperationCanceledException("The operation was canceled.");
        var client = new FakeClient();
        var coordinator = new ArtifactHostCoordinator(
            new ThrowingScanner(cancellation),
            client,
            new FakeLockExecutor(),
            new FakeNativeExecutor());

        var thrown = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            coordinator.RunAsync(
                Request(ArtifactHostOperation.Analyze),
                "token",
                CancellationToken.None));

        Assert.Same(cancellation, thrown);
        Assert.False(client.CompletionSuccess);
        Assert.Equal("用户已停止任务", client.CompletionMessage);
    }

    [Fact]
    public async Task CharacterRosterFailure_ReportsExceptionTypeAndStackForRuntimeDiagnosis()
    {
        var failure = new NullReferenceException("roster null");
        var client = new FakeClient();
        var coordinator = new ArtifactHostCoordinator(
            new FakeScanner(Snapshot()), client,
            new FakeLockExecutor(), new FakeNativeExecutor(),
            new ThrowingRosterScanner(failure));

        var thrown = await Assert.ThrowsAsync<NullReferenceException>(() =>
            coordinator.RunAsync(
                Request(ArtifactHostOperation.ScanCharacterRoster),
                "token", CancellationToken.None));

        Assert.Same(failure, thrown);
        Assert.Contains("NullReferenceException", client.CompletionMessage);
        Assert.Contains("roster null", client.CompletionMessage);
    }

    [Fact]
    public async Task ExecuteLockPlan_StalePreflightNeverCallsExecutor()
    {
        var client = new FakeClient
        {
            Preflight = new ArtifactPreflightResult(ArtifactPreflightStatus.StaleAbort, [], ["changed"])
        };
        var executor = new FakeLockExecutor();
        var coordinator = new ArtifactHostCoordinator(new FakeScanner(Snapshot()), client, executor, new FakeNativeExecutor());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.RunAsync(Request(ArtifactHostOperation.ExecuteLockPlan), "token", CancellationToken.None));

        Assert.Equal(0, executor.Calls);
    }

    [Fact]
    public async Task ExecuteLockPlan_ReadyPreflightExecutesExactActions()
    {
        var action = new ArtifactExecutionActionDto(0, false, true, "fingerprint");
        var client = new FakeClient
        {
            Preflight = new ArtifactPreflightResult(ArtifactPreflightStatus.Ready, [action], [])
        };
        var executor = new FakeLockExecutor();
        var coordinator = new ArtifactHostCoordinator(new FakeScanner(Snapshot()), client, executor, new FakeNativeExecutor());

        await coordinator.RunAsync(Request(ArtifactHostOperation.ExecuteLockPlan), "token", CancellationToken.None);

        Assert.True(client.PreflightObservation?.CountOnly);
        Assert.Equal(1, executor.Calls);
        Assert.Equal([action], executor.Actions);
        Assert.True(executor.ReusedPreparedInventory);
    }

    [Fact]
    public async Task ExecuteLockPlan_ChangedCountUsesSingleFullRescanAndReturnsForReview()
    {
        var changedSnapshot = ArtifactSnapshotDto.Create(
            "102550550", "rescan", "CURRENT_INVENTORY_ORDER", "genshin-7.0",
            [
                new ArtifactItemDto(0, "GoldenTroupe", "circlet", 20, 5, "hp_", [], "", false),
                new ArtifactItemDto(1, "GoldenTroupe", "sands", 0, 5, "atk_", [], "", false)
            ]);
        var scanner = new FakeScanner(changedSnapshot);
        var client = new FakeClient
        {
            Preflight = new ArtifactPreflightResult(
                ArtifactPreflightStatus.RescanRequired, [], ["count changed"])
        };
        var executor = new FakeLockExecutor();
        var coordinator = new ArtifactHostCoordinator(
            scanner, client, executor, new FakeNativeExecutor());

        await coordinator.RunAsync(
            Request(ArtifactHostOperation.ExecuteLockPlan), "token", CancellationToken.None);

        Assert.Equal(1, scanner.InspectionCalls);
        Assert.Equal(0, scanner.Calls);
        Assert.Equal(0, executor.Calls);
        Assert.True(client.CompletionSuccess);
    }

    [Fact]
    public async Task RebuildNativePlans_NoGoNeverCallsReplacer()
    {
        var client = new FakeClient
        {
            NativePlan = new ArtifactNativeSyncPlanDto(
                "NO_GO_CAPACITY", false, false, 100, 0, [], "", "CONSERVATIVE_SET_UNION", "capacity")
        };
        var executor = new FakeNativeExecutor();
        var coordinator = new ArtifactHostCoordinator(new FakeScanner(Snapshot()), client, new FakeLockExecutor(), executor);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.RunAsync(Request(ArtifactHostOperation.RebuildNativePlans), "token", CancellationToken.None));

        Assert.Equal(0, executor.Calls);
    }

    private static ArtifactHostRequest Request(ArtifactHostOperation operation)
    {
        var snapshot = Snapshot();
        return new ArtifactHostRequest(
            1, "artifact-analysis", "102550550", "job-1", operation,
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(4),
            operation == ArtifactHostOperation.ExecuteLockPlan ? snapshot.ArtifactCount : null,
            operation == ArtifactHostOperation.ExecuteLockPlan
                ? [new ArtifactLaunchTargetDto(0, snapshot.Artifacts[0].ContentFingerprint, false)]
                : null,
            operation == ArtifactHostOperation.RebuildNativePlans ? 100 : null,
            operation == ArtifactHostOperation.RebuildNativePlans ? "digest" : null,
            operation == ArtifactHostOperation.ScanCharacterRoster ? 80 : null,
            operation == ArtifactHostOperation.ScanCharacterRoster ? true : null,
            operation == ArtifactHostOperation.ScanCharacterRoster ? "眇" : null,
            operation == ArtifactHostOperation.ScanCharacterRoster ? "遥" : null,
            operation == ArtifactHostOperation.ScanCharacterRoster ? "MannequinGirl" : null);
    }

    private static ArtifactSnapshotDto Snapshot() => ArtifactSnapshotDto.Create(
        "102550550", "scan", "CURRENT_INVENTORY_ORDER", "genshin-7.0",
        [new ArtifactItemDto(0, "GoldenTroupe", "circlet", 20, 5, "critRate_", [], "", false)]);

    private sealed class FakeScanner(ArtifactSnapshotDto snapshot) : IArtifactInventoryScanner
    {
        public ArtifactSnapshotDto Snapshot { get; } = snapshot;
        public int Calls { get; private set; }
        public int InspectionCalls { get; private set; }
        public Task<ArtifactSnapshotDto> ScanAsync(string uid, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Snapshot);
        }

        public Task<ArtifactExecutionObservationDto> InspectForExecutionAsync(
            string uid,
            int expectedArtifactCount,
            IReadOnlyList<ArtifactLaunchTargetDto> targets,
            CancellationToken cancellationToken)
        {
            InspectionCalls++;
            var changed = Snapshot.ArtifactCount != expectedArtifactCount;
            return Task.FromResult(new ArtifactExecutionObservationDto(
                uid,
                Snapshot.ArtifactCount,
                changed ? Snapshot.Artifacts : [],
                changed ? Snapshot : null,
                CountOnly: !changed));
        }
    }

    private sealed class ThrowingScanner(Exception exception) : IArtifactInventoryScanner
    {
        public Task<ArtifactSnapshotDto> ScanAsync(string uid, CancellationToken cancellationToken) =>
            Task.FromException<ArtifactSnapshotDto>(exception);

        public Task<ArtifactExecutionObservationDto> InspectForExecutionAsync(
            string uid,
            int expectedArtifactCount,
            IReadOnlyList<ArtifactLaunchTargetDto> targets,
            CancellationToken cancellationToken) =>
            Task.FromException<ArtifactExecutionObservationDto>(exception);
    }

    private sealed class FakeClient : IArtifactToolsClient
    {
        public ArtifactSnapshotDto? SubmittedSnapshot { get; private set; }
        public string? SubmittedJobId { get; private set; }
        public ArtifactPreflightResult Preflight { get; init; } = new(ArtifactPreflightStatus.Ready, [], []);
        public ArtifactNativeSyncPlanDto NativePlan { get; init; } = new(
            "READY", true, true, 100, 158, [], "digest", "CONSERVATIVE_SET_UNION", "ready");
        public bool? CompletionSuccess { get; private set; }
        public string? CompletionMessage { get; private set; }
        public ArtifactExecutionObservationDto? PreflightObservation { get; private set; }
        public ArtifactCharacterRosterDto? SubmittedCharacterRoster { get; private set; }
        public bool CompletionTokenWasCancelledAtCall { get; private set; }

        public Task ClaimAsync(string jobId, string uid, ArtifactHostOperation operation, string requestToken, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SubmitSnapshotAsync(string jobId, string requestToken, ArtifactSnapshotDto snapshot, CancellationToken cancellationToken)
        {
            SubmittedJobId = jobId;
            SubmittedSnapshot = snapshot;
            return Task.CompletedTask;
        }

        public Task<ArtifactPreflightResult> PreflightAsync(string jobId, string requestToken, ArtifactExecutionObservationDto observation, CancellationToken cancellationToken)
        {
            PreflightObservation = observation;
            return Task.FromResult(Preflight);
        }

        public Task SubmitCharacterRosterAsync(
            string jobId,
            string requestToken,
            ArtifactCharacterRosterDto roster,
            CancellationToken cancellationToken)
        {
            SubmittedCharacterRoster = roster;
            return Task.CompletedTask;
        }
        public Task<ArtifactNativeSyncPlanDto> GetNativeSyncPlanAsync(string jobId, string requestToken, CancellationToken cancellationToken) => Task.FromResult(NativePlan);

        public Task ReportCompletionAsync(string jobId, string requestToken, ArtifactHostOperation operation, bool success, string? message, CancellationToken cancellationToken)
        {
            CompletionSuccess = success;
            CompletionMessage = message;
            CompletionTokenWasCancelledAtCall = cancellationToken.IsCancellationRequested;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLockExecutor : IArtifactLockPlanExecutor
    {
        public int Calls { get; private set; }
        public IReadOnlyList<ArtifactExecutionActionDto> Actions { get; private set; } = [];
        public bool ReusedPreparedInventory { get; private set; }
        public Task ExecuteAsync(
            IReadOnlyList<ArtifactExecutionActionDto> actions,
            int expectedArtifactCount,
            bool reusePreparedInventory,
            CancellationToken cancellationToken)
        {
            Calls++;
            Actions = actions;
            ReusedPreparedInventory = reusePreparedInventory;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeNativeExecutor : IArtifactNativePlanExecutor
    {
        public int Calls { get; private set; }
        public Task ReplaceAllAsync(
            ArtifactNativeSyncPlanDto plan,
            string expectedUid,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRosterScanner(ArtifactCharacterRosterDto roster)
        : IArtifactCharacterRosterScanner
    {
        public int Calls { get; private set; }
        public string? GameNickname { get; private set; }
        public string? MiliastraNickname { get; private set; }
        public string? MiliastraCharacterKey { get; private set; }

        public Task<ArtifactCharacterRosterDto> ScanAsync(
            string uid,
            string? gameNickname,
            string? miliastraNickname,
            string? miliastraCharacterKey,
            CancellationToken cancellationToken)
        {
            Calls++;
            GameNickname = gameNickname;
            MiliastraNickname = miliastraNickname;
            MiliastraCharacterKey = miliastraCharacterKey;
            return Task.FromResult(roster);
        }
    }

    private sealed class ThrowingRosterScanner(Exception failure)
        : IArtifactCharacterRosterScanner
    {
        public Task<ArtifactCharacterRosterDto> ScanAsync(
            string uid,
            string? gameNickname,
            string? miliastraNickname,
            string? miliastraCharacterKey,
            CancellationToken cancellationToken) =>
            Task.FromException<ArtifactCharacterRosterDto>(failure);
    }
}
