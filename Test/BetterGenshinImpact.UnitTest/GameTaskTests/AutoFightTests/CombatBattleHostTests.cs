using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using Fischless.GameCapture;
using Microsoft.Extensions.Time.Testing;
using BetterGenshinImpact.GameTask.Common.BgiVision;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatBattleHostTests
{
    [Fact]
    public async Task RealProgressAfterAnExhaustedSearchDoesNotPoisonTheNextFinishProbe()
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId) { SourcePeriodMilliseconds = 50 };
        using var host = new CombatBattleHost(io, new() { FinishCheckIntervalSeconds = .1 });
        for (var i = 0; i < 1000 && !(host.CameraRequests == 24 && host.State == "BeforeParty"); i++)
            await host.AdvanceAsync(flow, default);
        Assert.Equal(24, host.CameraRequests);
        Assert.Equal("BeforeParty", host.State);
        clock.Advance(TimeSpan.FromMilliseconds(400));
        io.TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Available,
            new(AutoFightSeekAction.KeepFighting, EnemyIndicatorDirection.None,
                new(700, 400, 80, 30, 2400), 1, SeekCueKind.DamageNumber), 1920, 1080, 1);
        Assert.Equal(CombatBattleHostResult.Continue, await host.AdvanceAsync(flow, default));
        Assert.Equal("Fighting", host.State);
        io.TargetFactory = null;
        var result = CombatBattleHostResult.Continue;
        for (var i = 0; i < 100 && result == CombatBattleHostResult.Continue && host.State != "Searching"; i++)
            result = await host.AdvanceAsync(flow, default);
        Assert.Equal(CombatBattleHostResult.Continue, result);
        Assert.Equal("Searching", host.State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public async Task SearchWaitsForTheCameraToSettleBeforeSendingAnotherMovement(int sourceLagMilliseconds)
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId) { SourcePeriodMilliseconds = 50 };
        using var host = new CombatBattleHost(io, new() { FinishCheckIntervalSeconds = .1 });
        io.TargetFactory = stamp => new(stamp with
        { CapturedTimestamp = stamp.CapturedTimestamp - clock.TimestampFrequency * sourceLagMilliseconds / 1000 },
            flow.Context.BattleId, CombatObservationQuality.Available, null, 1920, 1080);
        for (var i = 0; i < 100 && host.CameraRequests == 0; i++)
            await host.AdvanceAsync(flow, default);
        Assert.Equal(1, host.CameraRequests);
        var completedFirstMovement = clock.GetTimestamp();
        for (var i = 0; i < 100 && host.CameraRequests == 1; i++)
            await host.AdvanceAsync(flow, default);
        Assert.Equal(2, host.CameraRequests);
        Assert.True(clock.GetElapsedTime(completedFirstMovement) >= TimeSpan.FromMilliseconds(350 + sourceLagMilliseconds),
            $"第二次镜头输入仅间隔{clock.GetElapsedTime(completedFirstMovement).TotalMilliseconds}ms，未给场景稳定机会");
    }

    [Fact]
    public void UnknownPostureMayPermitOnlyObservedAlignedBoundedMovement()
    {
        var clock = new FakeTimeProvider();
        var battle = Guid.NewGuid();
        var frame = new CombatBattleObservation(new CaptureFrameSource(clock).Next(), battle,
            CombatObservationQuality.Available,
            new(AutoFightSeekAction.ApproachVisibleEnemy, EnemyIndicatorDirection.None, new(910, 400, 100, 4, 400), 1, SeekCueKind.HealthBar),
            1920, 1080) { Control = new(MotionStatus.Unknown, false) };
        Assert.True(CombatBattleHost.CanApproach(frame, battle, clock));
        Assert.Equal(MotionStatus.Unknown, frame.Motion);
        Assert.False(CombatBattleHost.CanApproach(frame with { Control = default }, battle, clock));
        Assert.False(CombatBattleHost.CanApproach(frame with { Control = new(MotionStatus.Unknown, true) }, battle, clock));
        Assert.False(CombatBattleHost.CanApproach(frame with { Control = new(MotionStatus.Climb, false) }, battle, clock));
        Assert.False(CombatBattleHost.CanApproach(frame with { Control = new(MotionStatus.Fly, false) }, battle, clock));
        Assert.False(CombatBattleHost.CanApproach(frame with { Target = null }, battle, clock));
        Assert.False(CombatBattleHost.CanApproach(frame with { Target = frame.Target!.Value with { Cue = SeekCueKind.FixedTopHealth } }, battle, clock));
        Assert.False(CombatBattleHost.CanApproach(frame with { Target = frame.Target!.Value with { Cue = SeekCueKind.DamageNumber } }, battle, clock));
        Assert.False(CombatBattleHost.CanApproach(frame with { Target = frame.Target!.Value with { Visual = new(100, 400, 100, 4, 400) } }, battle, clock));
        Assert.False(CombatBattleHost.CanApproach(frame, Guid.NewGuid(), clock));
        clock.Advance(TimeSpan.FromMilliseconds(151));
        Assert.False(CombatBattleHost.CanApproach(frame, battle, clock));
    }

    [Fact]
    public async Task ExhaustingCameraAttemptsCannotAuthorizeWalkingInAnUnconfirmedDirection()
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        io.TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Available,
            new(AutoFightSeekAction.ApproachVisibleEnemy, EnemyIndicatorDirection.None,
                new(350, 400, 100, 4, 400), 1, SeekCueKind.HealthBar), 1920, 1080)
            { Motion = MotionStatus.Normal, Control = new(MotionStatus.Normal, false) };
        using var host = new CombatBattleHost(io, new());
        var result = CombatBattleHostResult.Continue;
        for (var i = 0; i < 15000 && result == CombatBattleHostResult.Continue; i++)
            result = await host.AdvanceAsync(flow, default);
        Assert.InRange(host.CameraRequests, 1, 24);
        Assert.DoesNotContain(io.Inputs, input => input.Kind == CombatBattleHostInputKind.Approach);
        Assert.Equal(CombatBattleHostResult.Unconfirmed, result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewEnemyEvidenceWithdrawsOnlyAnUnsentFinishProbe(bool alreadyPrepared)
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        using var host = new CombatBattleHost(io, new() { FinishCheckIntervalSeconds = .1 });
        for (var i = 0; i < 50 && host.State != "BeforeParty"; i++) await host.AdvanceAsync(flow, default);
        Assert.Equal("BeforeParty", host.State);
        if (alreadyPrepared) { await host.AdvanceAsync(flow, default); Assert.Equal("OpenParty", host.State); }
        io.TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Available,
            new(AutoFightSeekAction.KeepFighting, EnemyIndicatorDirection.None,
                new(700, 400, 80, 30, 2400), 1, SeekCueKind.DamageNumber), 1920, 1080, 1);
        for (var i = 0; i < 10; i++) await host.AdvanceAsync(flow, default);
        Assert.DoesNotContain(io.Requests, request => request.Kind == CombatBattleHostInputKind.OpenParty);
        Assert.Equal("Fighting", host.State);
    }

    [Fact]
    public async Task LateNativeObservationRejectsThisDecisionWithoutDestroyingTheBattle()
    {
        var clock = new FakeTimeProvider();
        var game = new ReturningGame(clock);
        using var flow = CreateFlow(false, game, clock);
        var io = new ReplayIo(clock, flow.Context.BattleId)
        { AfterTargetCapture = () => { Thread.Sleep(180); clock.Advance(TimeSpan.FromMilliseconds(180)); } };
        using var host = new CombatBattleHost(io, new());
        Assert.Equal(CombatBattleHostResult.Continue, await host.AdvanceAsync(flow, default));
        Assert.Equal(0, game.Inputs);
        Assert.Empty(io.Requests);
        io.AfterTargetCapture = null;
        for (var i = 0; i < 20 && game.Inputs == 0; i++) await host.AdvanceAsync(flow, default);
        Assert.True(game.Inputs > 0);
    }

    [Fact]
    public async Task AControlHintDuringSearchReturnsToTheKernelInsteadOfSendingAnotherCameraPulse()
    {
        var clock = new FakeTimeProvider();
        var game = new ReturningGame(clock);
        using var flow = CreateFlow(false, game, clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        using var host = new CombatBattleHost(io, new() { FinishDetectionEnabled = false, FinishCheckIntervalSeconds = .1 });
        for (var i = 0; i < 20 && host.State != "Searching"; i++) await host.AdvanceAsync(flow, default);
        Assert.Equal("Searching", host.State);
        io.TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Available, null, 1920, 1080)
        { Control = new(MotionStatus.Unknown, true) };
        var requests = io.Requests.Count;
        var kernelSteps = flow.RuntimeStatistics.CoreSteps;
        await host.AdvanceAsync(flow, default);
        Assert.Equal(requests, io.Requests.Count);
        Assert.True(flow.RuntimeStatistics.CoreSteps > kernelSteps);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContinuouslyUnsentRequestsKeepOneDeadlineEvenWithUnlimitedBattleTimeout(bool party)
    {
        var clock = new FakeTimeProvider();
        var started = clock.GetTimestamp();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId) { UnsentAttempts = int.MaxValue };
        using var host = new CombatBattleHost(io, new()
        { TimeoutSeconds = 0, FinishDetectionEnabled = party, FinishCheckIntervalSeconds = .1 });
        var result = CombatBattleHostResult.Continue;
        for (var i = 0; i < 1000 && result == CombatBattleHostResult.Continue; i++)
            result = await host.AdvanceAsync(flow, default);
        Assert.Equal(CombatBattleHostResult.Unconfirmed, result);
        Assert.Empty(io.Inputs);
        Assert.NotEmpty(io.Requests);
        Assert.Single(io.Requests.Select(x => x.RequestId).Distinct());
        Assert.Single(io.Requests.Select(x => x.DeadlineTimestamp).Distinct());
        Assert.Equal(0, host.CameraRequests);
        Assert.Equal(0, host.ApproachRequests);
        Assert.InRange(clock.GetElapsedTime(started).TotalSeconds, 1, party ? 3 : 20);
    }

    [Fact]
    public async Task UnknownInputIsNotRetriedOrCountedAsSent()
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId)
        { ForcedResult = new(CombatBattleHostInputStatus.Unknown, Reason: "partial-native-send") };
        using var host = new CombatBattleHost(io, new() { FinishCheckIntervalSeconds = .1 });
        var result = CombatBattleHostResult.Continue;
        for (var i = 0; i < 50 && result == CombatBattleHostResult.Continue; i++)
            result = await host.AdvanceAsync(flow, default);
        Assert.Equal(CombatBattleHostResult.Unconfirmed, result);
        Assert.Single(io.Requests);
        Assert.Empty(io.Inputs);
        Assert.Equal(result, await host.AdvanceAsync(flow, default));
        Assert.Single(io.Requests);
    }

    [Fact]
    public async Task UnsentPartyRequestDoesNotEnterConfirmationAndCanContinueOnANewFrame()
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId) { UnsentAttempts = 1 };
        io.PartyFactory = stamp => new(stamp.Sequence, stamp.CapturedAt, 1920, 1080, io.PartyOpen, (ulong)stamp.Sequence)
        { Source = stamp };
        using var host = new CombatBattleHost(io, new() { FinishCheckIntervalSeconds = .1 });
        for (var i = 0; i < 50 && io.Requests.Count == 0; i++) await host.AdvanceAsync(flow, default);
        Assert.Single(io.Requests);
        Assert.Empty(io.Inputs);
        Assert.Equal("BeforeParty", host.State);
        var result = CombatBattleHostResult.Continue;
        for (var i = 0; i < 100 && result == CombatBattleHostResult.Continue; i++)
            result = await host.AdvanceAsync(flow, default);
        Assert.Equal(CombatBattleHostResult.Completed, result);
        Assert.Single(io.Inputs, input => input.Kind == CombatBattleHostInputKind.OpenParty);
        Assert.NotEqual(Guid.Empty, io.Requests[0].RequestId);
        Assert.Equal(io.Requests[0].RequestId, io.Requests[1].RequestId);
        Assert.Equal(io.Requests[0].DeadlineTimestamp, io.Requests[1].DeadlineTimestamp);
        Assert.True(io.Requests[1].Source.IsAfter(io.Requests[0].Source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryIsDisabledByDefaultAndOnlyExplicitReplayAllowsBoundedDetach(bool enableReplayRecovery)
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        io.TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Available,
            new(AutoFightSeekAction.ApproachVisibleEnemy, EnemyIndicatorDirection.None,
                new(910, 400, 100, 4, 400), 1, SeekCueKind.HealthBar), 1920, 1080) { Motion = MotionStatus.Climb };
        using var host = new CombatBattleHost(io, new() { ControlRecoveryEnabled = enableReplayRecovery });
        var result = CombatBattleHostResult.Continue;
        for (var i = 0; i < 15000 && result == CombatBattleHostResult.Continue; i++) result = await host.AdvanceAsync(flow, default);
        Assert.Equal(CombatBattleHostResult.Unconfirmed, result);
        Assert.Equal(enableReplayRecovery ? 2 : 0, io.Inputs.Count(input => input.Kind == CombatBattleHostInputKind.Detach));
        Assert.DoesNotContain(io.Inputs, input => input.Kind == CombatBattleHostInputKind.Approach);
    }
    [Fact]
    public async Task UnknownPostureDoesNotAuthorizeWalkingIntoAnUnreachableTarget()
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        io.TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Available,
            new(AutoFightSeekAction.ApproachVisibleEnemy, EnemyIndicatorDirection.None,
                new(910, 400, 100, 4, 400), 1, SeekCueKind.HealthBar), 1920, 1080) { Motion = MotionStatus.Unknown };
        using var host = new CombatBattleHost(io, new());
        var result = CombatBattleHostResult.Continue;
        for (var i = 0; i < 15000 && result == CombatBattleHostResult.Continue; i++) result = await host.AdvanceAsync(flow, default);
        Assert.Equal(CombatBattleHostResult.Unconfirmed, result);
        Assert.DoesNotContain(io.Inputs, input => input.Kind == CombatBattleHostInputKind.Approach);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExternalSceneOwnsCompletionDuringABoundedSearchAndAnInvulnerablePhase(bool json)
    {
        var clock = new FakeTimeProvider();
        var started = clock.GetTimestamp();
        var game = new ReturningGame(clock);
        using var flow = CreateFlow(json, game, clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        using var host = new CombatBattleHost(io, new() { ExternalCompletionAuthority = true, TimeoutSeconds = 120 });
        var result = CombatBattleHostResult.Continue;
        for (var i = 0; i < 10000 && clock.GetElapsedTime(started).TotalSeconds < 65 && result == CombatBattleHostResult.Continue; i++)
            result = await host.AdvanceAsync(flow, default);
        Assert.Equal(CombatBattleHostResult.Continue, result);
        Assert.True(game.Inputs > 0);
        Assert.DoesNotContain(io.Inputs, input => input.Kind is CombatBattleHostInputKind.OpenParty or CombatBattleHostInputKind.CloseParty);
        Assert.InRange(host.CameraRequests, 1, 24);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await host.AdvanceAsync(flow, cancelled.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulStrategyCallsCannotKeepATargetlessBattleAliveForever(bool json)
    {
        var clock = new FakeTimeProvider();
        var started = clock.GetTimestamp();
        var game = new ReturningGame(clock);
        using var flow = CreateFlow(json, game, clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        using var host = new CombatBattleHost(io, new CombatBattleHostOptions());
        var result = CombatBattleHostResult.Continue;
        for (var i = 0; i < 5000 && result == CombatBattleHostResult.Continue; i++)
            result = await host.AdvanceAsync(flow, default);

        Assert.True(game.Inputs > 0);
        Assert.Equal(CombatBattleHostResult.Unconfirmed, result);
        Assert.Contains(io.Inputs, input => input.Kind == CombatBattleHostInputKind.Camera);
        Assert.DoesNotContain(io.Inputs, input => input.Kind == CombatBattleHostInputKind.Approach);
        Assert.False(io.PartyOpen);
        Assert.InRange(clock.GetElapsedTime(started).TotalSeconds, 1, 120);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealHealthChangesKeepALongBattleRunningAcrossSuccessiveTargets(bool json)
    {
        var clock = new FakeTimeProvider();
        var started = clock.GetTimestamp();
        var game = new ReturningGame(clock);
        using var flow = CreateFlow(json, game, clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        io.TargetFactory = stamp =>
        {
            var elapsed = clock.GetElapsedTime(started).TotalSeconds;
            var width = 240 - (int)(elapsed % 60) * 3;
            return new(stamp, flow.Context.BattleId, CombatObservationQuality.Available,
                new(AutoFightSeekAction.ApproachVisibleEnemy, EnemyIndicatorDirection.None,
                    new(900, 400, width, 4, width * 4), 1, SeekCueKind.HealthBar), 1920, 1080);
        };
        using var host = new CombatBattleHost(io, new());
        var result = CombatBattleHostResult.Continue;
        for (var i = 0; i < 25000 && clock.GetElapsedTime(started).TotalSeconds < 180 && result == CombatBattleHostResult.Continue; i++)
            result = await host.AdvanceAsync(flow, default);
        Assert.Equal(CombatBattleHostResult.Continue, result);
        Assert.InRange(clock.GetElapsedTime(started).TotalSeconds, 180, 181);
        Assert.Empty(io.Inputs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedOrForeignSourceFramesCannotExtendObservationAvailability(bool foreignBattle)
    {
        var clock = new FakeTimeProvider();
        var game = new ReturningGame(clock);
        using var flow = CreateFlow(false, game, clock);
        var io = new ReplayIo(clock, flow.Context.BattleId) { RepeatSource = !foreignBattle };
        if (foreignBattle) io.TargetFactory = stamp => new(stamp, Guid.NewGuid(), CombatObservationQuality.Available, null, 1920, 1080);
        using var host = new CombatBattleHost(io, new());
        var result = CombatBattleHostResult.Continue;
        for (var i = 0; i < 1000 && result == CombatBattleHostResult.Continue; i++)
            result = await host.AdvanceAsync(flow, default);
        Assert.Equal(CombatBattleHostResult.Unconfirmed, result);
        Assert.InRange(game.Inputs, 0, 2); // 未过期帧可供纯调度复用，但不能续期或无界推进。
        Assert.Empty(io.Inputs);
    }

    [Fact]
    public async Task CancellationInThePartyWaitClosesTheOwnedUiBeforeReturning()
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        using var host = new CombatBattleHost(io, new());
        for (var i = 0; i < 1000 && !io.PartyOpen; i++) await host.AdvanceAsync(flow, default);
        Assert.True(io.PartyOpen);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await host.AdvanceAsync(flow, cts.Token));
        Assert.False(io.PartyOpen);
    }

    [Fact]
    public async Task AStationaryUnreachableTargetGetsOnlyBoundedApproachAndCannotBecomeCompleted()
    {
        var clock = new FakeTimeProvider();
        var started = clock.GetTimestamp();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        io.TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Available,
            new(AutoFightSeekAction.ApproachVisibleEnemy, EnemyIndicatorDirection.None,
                new(910, 400, 100, 4, 400), 1, SeekCueKind.HealthBar), 1920, 1080) { Control = new(MotionStatus.Unknown, false) };
        using var host = new CombatBattleHost(io, new());
        var result = CombatBattleHostResult.Continue;
        for (var i = 0; i < 15000 && result == CombatBattleHostResult.Continue; i++)
            result = await host.AdvanceAsync(flow, default);
        Assert.Equal(CombatBattleHostResult.Unconfirmed, result);
        Assert.InRange(io.Inputs.Count(x => x.Kind == CombatBattleHostInputKind.Approach), 1, 12);
        Assert.InRange(clock.GetElapsedTime(started).TotalSeconds, 45, 120);
        Assert.False(io.PartyOpen);
    }

    [Fact]
    public async Task HealthBarGeometryChangedByOurOwnCameraCannotResetTheSearchBudget()
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        io.TargetFactory = stamp =>
        {
            var width = 200 - (io.Inputs.Count(x => x.Kind == CombatBattleHostInputKind.Camera) % 50) * 2;
            return new(stamp, flow.Context.BattleId, CombatObservationQuality.Available,
                new(AutoFightSeekAction.ApproachVisibleEnemy, EnemyIndicatorDirection.None,
                    new(400, 400, width, 4, width * 4), 1, SeekCueKind.HealthBar), 1920, 1080);
        };
        using var host = new CombatBattleHost(io, new());
        var result = CombatBattleHostResult.Continue;
        for (var i = 0; i < 15000 && result == CombatBattleHostResult.Continue; i++)
            result = await host.AdvanceAsync(flow, default);
        Assert.Equal(CombatBattleHostResult.Unconfirmed, result);
        Assert.InRange(host.CameraRequests, 1, 24);
    }

    [Fact]
    public async Task OnlyFreshIndependentPostInputPartyEvidenceCompletesAndClosesTheUi()
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(true, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        io.PartyFactory = stamp => new(stamp.Sequence, stamp.CapturedAt, 1920, 1080,
            io.PartyOpen, (ulong)stamp.Sequence) { Source = stamp };
        using var host = new CombatBattleHost(io, new());
        var result = CombatBattleHostResult.Continue;
        for (var i = 0; i < 1000 && result == CombatBattleHostResult.Continue; i++)
            result = await host.AdvanceAsync(flow, default);
        Assert.Equal(CombatBattleHostResult.Completed, result);
        Assert.False(io.PartyOpen);
        Assert.Equal(1, io.Inputs.Count(x => x.Kind == CombatBattleHostInputKind.OpenParty));
        Assert.Equal(1, io.Inputs.Count(x => x.Kind == CombatBattleHostInputKind.CloseParty));
    }

    [Fact]
    public async Task StalePartyCandidatesNeitherCompleteNorAuthorizeASecondMenuToggle()
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        CaptureFrameStamp before = default;
        io.PartyFactory = stamp =>
        {
            if (!io.PartyOpen) before = stamp;
            return new(before.Sequence, before.CapturedAt, 1920, 1080, io.PartyOpen, (ulong)stamp.Sequence) { Source = before };
        };
        using var host = new CombatBattleHost(io, new());
        var result = CombatBattleHostResult.Continue;
        for (var i = 0; i < 5000 && result == CombatBattleHostResult.Continue; i++)
            result = await host.AdvanceAsync(flow, default);
        Assert.Equal(CombatBattleHostResult.Unconfirmed, result);
        Assert.All(io.Inputs.Where(x => x.Kind == CombatBattleHostInputKind.CloseParty), input => Assert.False(input.PartyEvidence));
    }

    [Fact]
    public async Task AFixedTopBossBarNeverAuthorizesWalkingTowardAScreenCoordinate()
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        io.TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Available,
            new(AutoFightSeekAction.ApproachFixedTopHealthTarget, EnemyIndicatorDirection.None,
                new(700, 20, 500, 8, 4000), 1, SeekCueKind.FixedTopHealth), 1920, 1080);
        using var host = new CombatBattleHost(io, new());
        var result = CombatBattleHostResult.Continue;
        for (var i = 0; i < 15000 && result == CombatBattleHostResult.Continue; i++)
            result = await host.AdvanceAsync(flow, default);
        Assert.Equal(CombatBattleHostResult.Unconfirmed, result);
        Assert.DoesNotContain(io.Inputs, x => x.Kind is CombatBattleHostInputKind.Approach or CombatBattleHostInputKind.Camera);
    }

    [Fact]
    public async Task ChangingDamageEvidenceSupportsLongCombatWithoutAVisibleHealthBar()
    {
        var clock = new FakeTimeProvider();
        var started = clock.GetTimestamp();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        io.TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Available,
            new(AutoFightSeekAction.KeepFighting, EnemyIndicatorDirection.None,
                new(700, 400, 80, 30, 2400), 1, SeekCueKind.DamageNumber), 1920, 1080,
            (ulong)(clock.GetElapsedTime(started).TotalSeconds / 2) + 1);
        using var host = new CombatBattleHost(io, new());
        var result = CombatBattleHostResult.Continue;
        for (var i = 0; i < 20000 && clock.GetElapsedTime(started).TotalSeconds < 120 && result == CombatBattleHostResult.Continue; i++)
            result = await host.AdvanceAsync(flow, default);
        Assert.Equal(CombatBattleHostResult.Continue, result);
        Assert.InRange(clock.GetElapsedTime(started).TotalSeconds, 120, 121);
        Assert.Empty(io.Inputs);
    }

    [Fact]
    public async Task FreshSharedObservationDoesNotThrottlePureStrategySteps()
    {
        var clock = new FakeTimeProvider();
        var game = new ReturningGame(clock);
        using var flow = CreateFlow(false, game, clock);
        var io = new ReplayIo(clock, flow.Context.BattleId) { SourcePeriodMilliseconds = 50 };
        using var host = new CombatBattleHost(io, new());
        for (var i = 0; i < 100 && game.Inputs < 10; i++) await host.AdvanceAsync(flow, default);
        Assert.Equal(10, game.Inputs);
        Assert.Equal(0, io.DelayCalls);
    }

    private static NativeCombatFlowRunner CreateFlow(bool json, ICombatFlowGame game, FakeTimeProvider clock)
    {
        if (json)
            return NativeCombatFlowRunner.Create(new JsonCombatStrategy
            {
                Actions = [new() { Character = "琴", Action = "attack(0.1,required)" }]
            }, game, clock: clock)!;
        return NativeCombatFlowRunner.Create(CombatFlowProgram.Compile("strategy(loop=battle)\n琴 attack(0.1,required)"), game, clock);
    }

    private sealed class ReturningGame(FakeTimeProvider clock) : ICombatFlowGame
    {
        public int Inputs { get; private set; }
        public object? Observe(string function, IReadOnlyList<object?> args, string actor) => true;
        public ValueTask<CombatFlowResult> ExecuteAsync(CombatFlowAction action, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            action.TryBeginInput();
            Inputs++;
            clock.Advance(TimeSpan.FromMilliseconds(100));
            return ValueTask.FromResult(CombatFlowResult.Succeeded);
        }
        public ValueTask YieldAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class ReplayIo(FakeTimeProvider clock, Guid battleId) : ICombatBattleHostIo
    {
        private readonly CaptureFrameSource _source = new(clock);
        public TimeProvider Clock => clock;
        public Guid BattleId => battleId;
        public List<CombatBattleHostInput> Inputs { get; } = [];
        public List<CombatBattleHostInput> Requests { get; } = [];
        public int UnsentAttempts { get; set; }
        public CombatBattleHostInputResult? ForcedResult { get; init; }
        public bool PartyOpen { get; private set; }
        public bool RepeatSource { get; init; }
        public int SourcePeriodMilliseconds { get; init; }
        public int DelayCalls { get; private set; }
        public Func<CaptureFrameStamp, CombatBattleObservation>? TargetFactory { get; set; }
        public Action? AfterTargetCapture { get; set; }
        public Func<CaptureFrameStamp, PartySetupFinishObservation>? PartyFactory { get; set; }
        private CaptureFrameStamp _first;
        private CaptureFrameStamp _produced;
        public CombatBattleObservation ObserveTarget()
        {
            if (!_produced.IsKnown || clock.GetElapsedTime(_produced.CapturedTimestamp).TotalMilliseconds >= SourcePeriodMilliseconds)
                _produced = _source.Next();
            var stamp = _produced;
            if (!_first.IsKnown) _first = stamp;
            if (RepeatSource) stamp = _first;
            AfterTargetCapture?.Invoke();
            return TargetFactory?.Invoke(stamp) ?? new(stamp, battleId,
                CombatObservationQuality.Available, null, 1920, 1080);
        }
        public PartySetupFinishObservation ObservePartyBar()
        {
            var stamp = _source.Next();
            if (PartyFactory != null) return PartyFactory(stamp);
            return new(stamp.Sequence, stamp.CapturedAt, 1920, 1080, false, (ulong)stamp.Sequence)
            { Source = stamp };
        }
        public ValueTask<CombatBattleHostInputResult> SendAsync(CombatBattleHostInput input, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(input);
            if (ForcedResult is { } forced) return ValueTask.FromResult(forced);
            if (UnsentAttempts > 0)
            {
                UnsentAttempts--;
                clock.Advance(TimeSpan.FromMilliseconds(292));
                return ValueTask.FromResult(new CombatBattleHostInputResult(CombatBattleHostInputStatus.NotSent));
            }
            Inputs.Add(input);
            if (input.Kind == CombatBattleHostInputKind.OpenParty) PartyOpen = true;
            if (input.Kind == CombatBattleHostInputKind.CloseParty) PartyOpen = false;
            clock.Advance(TimeSpan.FromMilliseconds(10));
            return ValueTask.FromResult(new CombatBattleHostInputResult(CombatBattleHostInputStatus.Sent, clock.GetTimestamp()));
        }
        public ValueTask DelayAsync(int milliseconds, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            DelayCalls++;
            clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
            return ValueTask.CompletedTask;
        }
        public void ReleaseInput() { PartyOpen = false; }
    }
}
