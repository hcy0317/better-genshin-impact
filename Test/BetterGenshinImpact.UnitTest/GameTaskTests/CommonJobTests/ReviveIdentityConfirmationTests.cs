using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using Fischless.GameCapture;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class ReviveIdentityConfirmationTests
{
    private static readonly ReviveTarget Target = new("钟离", 1);
    private static readonly IReadOnlyDictionary<int, string> Party = new Dictionary<int, string> { [1] = "钟离", [2] = "娜维娅" };

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public Task NativeAvatarVerifierPublishesTheSecondExistingFrameHealthAndOriginalUiOrigin(bool? secondLowHp) =>
        RunNativeAvatarVerifierCounterAsync(secondLowHp, null);

    [Theory]
    [InlineData("wrong-target")]
    [InlineData("stale")]
    [InlineData("cancelled")]
    [InlineData("foreign-root")]
    public Task NativeAvatarVerifierCannotUpgradeInvalidEvidenceToARouteReceipt(string fault) =>
        RunNativeAvatarVerifierCounterAsync(false, fault);

    private static async Task RunNativeAvatarVerifierCounterAsync(bool? secondLowHp, string? fault)
    {
        BetterGenshinImpact.Helpers.ApplicationHostBootstrapGuard.ProhibitForCurrentProcess();
        using var taskOwner = TaskExecutionScope.BeginOwned();
        using var cancellation = new CancellationTokenSource();
        var clock = new FakeTimeProvider();
        var producer = new CaptureFrameSource(clock);
        using var routeOwner = BetterGenshinImpact.GameTask.Common.Ui.UiOperation.Begin(
            "native-revive-original-route", TimeSpan.FromSeconds(240), cancellation.Token,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, clock);
        var originalDeadline = clock.GetUtcNow() + routeOwner.Remaining;
        var origin = Avatar.CaptureReviveUiOrigin();
        Assert.Equal(routeOwner.RootId, origin.RootId);
        Assert.Equal(routeOwner.Id, origin.OperationId);
        if (fault == "foreign-root") origin = origin with { RootId = Guid.NewGuid().ToString("N") };
        using var selection = BetterGenshinImpact.GameTask.Common.Ui.UiOperation.Begin(
            "native-revive-selection-child", TimeSpan.FromSeconds(60), routeOwner.Token,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, clock);
        Assert.NotEqual(origin.OperationId, selection.Id);
        var captures = new List<BetterGenshinImpact.GameTask.Model.Area.ImageRegion>();
        var targetRegions = new List<BetterGenshinImpact.GameTask.Model.Area.ImageRegion>();
        var healthRegions = new List<BetterGenshinImpact.GameTask.Model.Area.ImageRegion>();
        var targetFrames = new List<ReviveRecoveryFrame>();
        var health = new List<bool?>();
        var recoveries = 0;
        var inputs = 0;
        long recoveryCompleted = 0;
        var io = new Avatar.ReviveVerificationIo
        {
            Capture = () =>
            {
                clock.Advance(TimeSpan.FromMilliseconds(1));
                var pixels = new OpenCvSharp.Mat(1080, 1920, OpenCvSharp.MatType.CV_8UC3, OpenCvSharp.Scalar.Black);
                var lowHp = captures.Count == 0 ? true : secondLowHp;
                if (lowHp.HasValue)
                {
                    using var bar = new OpenCvSharp.Mat(pixels, new OpenCvSharp.Rect(805, 1008, 7, 5));
                    bar.SetTo(lowHp.Value ? new OpenCvSharp.Scalar(0, 0, 255) : new OpenCvSharp.Scalar(0, 200, 0));
                }
                var timestamp = clock.GetTimestamp();
                if (fault == "stale") timestamp -= (long)((BetterGenshinImpact.GameTask.Common.Ui.UiSnapshot.RecoveryMaximumAge +
                    TimeSpan.FromMilliseconds(1)).TotalSeconds * clock.TimestampFrequency);
                var frame = new BetterGenshinImpact.GameTask.Model.Area.ImageRegion(pixels, 0, 0)
                    { FrameStamp = producer.Next(timestamp) };
                captures.Add(frame);
                return frame;
            },
            ReadTarget = frame =>
            {
                targetRegions.Add(frame);
                var party = System.Collections.Immutable.ImmutableDictionary<int, string>.Empty
                    .Add(1, fault == "wrong-target" ? "娜维娅" : Target.Name);
                var observed = new ReviveRecoveryFrame(0, true, ReviveUiState.None, 1, party)
                    .WithSource(frame.FrameStamp, clock);
                targetFrames.Add(observed);
                return observed;
            },
            ReadLowHp = frame =>
            {
                healthRegions.Add(frame);
                Assert.Same(targetRegions[^1], frame);
                Assert.Equal(targetFrames[^1].SourceStamp, frame.FrameStamp);
                var observed = Bv.ObserveCurrentAvatarLowHp(frame, 1d); // Actual pure nullable pixel reader, no OCR/model.
                health.Add(observed);
                if (fault == "cancelled") cancellation.Cancel();
                return observed;
            },
            Select = _ => inputs++,
            BeginExclusive = () => new NativeVerificationLease(),
            Clock = clock,
            Logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            Delay = (milliseconds, token) =>
            {
                token.ThrowIfCancellationRequested();
                clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
                return Task.CompletedTask;
            }
        };
        Task Recover()
        {
            recoveries++;
            clock.Advance(TimeSpan.FromSeconds(5));
            recoveryCompleted = clock.GetTimestamp();
            return Task.CompletedTask;
        }
        var error = await Record.ExceptionAsync(() =>
            Avatar.RecoverVerifiedTargetAsync(Recover, Target, io, origin, selection.Token));
        Assert.Equal(1, recoveries);
        Assert.Equal(0, inputs);
        Assert.Equal(captures.Count, targetRegions.Count);
        Assert.Equal(captures.Count, healthRegions.Count);
        for (var index = 0; index < captures.Count; index++)
        {
            Assert.Same(captures[index], targetRegions[index]);
            Assert.Same(captures[index], healthRegions[index]);
        }
        if (fault is "wrong-target" or "stale" or "cancelled")
        {
            Assert.NotNull(error);
            Assert.IsNotType<CombatRecoveryCompletedException>(error);
            if (fault == "cancelled") Assert.IsAssignableFrom<OperationCanceledException>(error);
            else if (fault == "stale") Assert.True(error is InvalidOperationException or TimeoutException);
            else Assert.IsType<InvalidOperationException>(error);
            return;
        }
        Assert.Equal(2, captures.Count); // Existing Wait's two frames only; neither receipt construction nor catch may recapture.
        Assert.True(health[0]);
        Assert.Equal(secondLowHp, health[1]);
        Assert.True(targetFrames[1].IsAfter(targetFrames[0]));
        Assert.True(targetFrames[1].SourceStamp.CapturedTimestamp > recoveryCompleted);
        Assert.Equal(originalDeadline, clock.GetUtcNow() + routeOwner.Remaining);
        var signal = Assert.IsType<CombatRecoveryCompletedException>(error);
        if (fault == "foreign-root")
        {
            Assert.Null(signal.Proof); // Invalid origin cannot grant a route receipt; positive cases must first pass.
            return;
        }
        var proof = Assert.IsType<ReviveRecoveryProof>(signal.Proof); // Native producer RED: original verify body still returns Task/true.
        Assert.Same(targetFrames[1], proof.Frame);
        Assert.Equal(targetFrames[1].SourceStamp, proof.Frame.SourceStamp);
        Assert.Equal(health[1], proof.LowHp);
        Assert.Equal(Target, proof.Target);
        Assert.Equal(origin.RootId, proof.UiRootId);
        Assert.Equal(origin.OperationId, proof.UiOperationId);
        Assert.NotEqual(selection.Id, proof.UiOperationId);
        Assert.Equal(recoveryCompleted, proof.RecoveryCompletedTimestamp);
    }

    private sealed class NativeVerificationLease : IDisposable
    {
        public void Dispose() { }
    }

    [Fact]
    public async Task AnotherLivingActorCannotProveTheKnownDefeatedActorRecovered()
    {
        long frame = 0;
        var party = new Dictionary<int, string> { [1] = "钟离", [2] = "娜维娅" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => CombatRecoveryCompletedException.RecoverAsync(
            () => Task.CompletedTask,
            () => new(++frame, true, ReviveUiState.None, 2, party),
            new ReviveTarget("钟离", 1), () => Task.CompletedTask, default));
    }

    [Fact]
    public async Task SameNamedActorInTheSameSlotNeedsTwoFreshSuccessfulFrames()
    {
        long frame = 0;
        await Assert.ThrowsAsync<CombatRecoveryCompletedException>(() => CombatRecoveryCompletedException.RecoverAsync(
            () => Task.CompletedTask, () => new(++frame, true, ReviveUiState.None, 1, Party),
            Target, () => Task.CompletedTask, default));
        Assert.Equal(2, frame);
    }

    [Theory]
    [InlineData("same-frame")]
    [InlineData("mapping-changed")]
    [InlineData("missing-target")]
    [InlineData("revive")]
    [InlineData("unknown")]
    public async Task StaleOrChangedRecoveryEvidenceCannotAuthorizeTheRoute(string failure)
    {
        var calls = 0;
        ReviveRecoveryFrame Capture()
        {
            var sample = new ReviveRecoveryFrame(++calls, true, ReviveUiState.None, 1, Party);
            if (calls == 1) return sample;
            return failure switch
            {
                "same-frame" => sample with { FrameId = 1 },
                "mapping-changed" => sample with { Party = new Dictionary<int, string> { [1] = "娜维娅", [2] = "钟离" } },
                "missing-target" => sample with { Party = new Dictionary<int, string>() },
                "revive" => sample with { Revive = ReviveUiState.FoodPrompt },
                _ => sample with { FrameId = 0, MainReady = false }
            };
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => CombatRecoveryCompletedException.RecoverAsync(
            () => Task.CompletedTask, Capture, Target, () => Task.CompletedTask, default));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RealSwitchObservationOrderDoesNotBindAPreexistingRevivePrompt(bool preexistingPrompt)
    {
        var inputs = 0;
        long frame = 0;
        ReviveRecoveryFrame? before = null;
        ReviveTarget? bound = null;
        Assert.Throws<ReviveObserved>(() => AvatarSwitchConfirmationPolicy.TryConfirm(1, 4, () =>
        {
            var sample = new ReviveRecoveryFrame(++frame, true,
                preexistingPrompt || inputs > 0 ? ReviveUiState.FoodPrompt : ReviveUiState.None, 2, Party);
            if (sample.Revive != ReviveUiState.None)
            {
                bound = ReviveTarget.FromSelection(Target, inputs > 0, before, sample);
                throw new ReviveObserved();
            }
            before = sample;
            return sample.ActiveIndex;
        }, _ => inputs++, _ => { }, default));
        Assert.Equal(preexistingPrompt ? 0 : 1, inputs);
        Assert.Equal(preexistingPrompt ? null : Target, bound);
    }

    [Fact]
    public void SelectionBindingRejectsChangedIdentityBadFramesAndUnsubmittedInput()
    {
        var before = new ReviveRecoveryFrame(1, true, ReviveUiState.None, 2, Party);
        var after = new ReviveRecoveryFrame(2, true, ReviveUiState.FoodPrompt, 2, Party);
        Assert.Null(ReviveTarget.FromSelection(Target, false, before, after));
        Assert.Null(ReviveTarget.FromSelection(Target, true, before with { FrameId = 0 }, after));
        Assert.Null(ReviveTarget.FromSelection(Target, true, before, after with { FrameId = 1 }));
        Assert.Null(ReviveTarget.FromSelection(Target, true, before, after with { Party = new Dictionary<int, string> { [1] = "娜维娅" } }));
        Assert.Null(ReviveTarget.FromSelection(Target, true, before, after with { Revive = ReviveUiState.FullPartyDefeat }));
    }

    [Fact]
    public async Task CancellationAndCaptureFailureNeverBecomeRecoveryCompletion()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var ran = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CombatRecoveryCompletedException.RecoverAsync(
            () => { ran = true; return Task.CompletedTask; }, () => new(1, true, ReviveUiState.None, 1, Party),
            Target, () => Task.CompletedTask, cancellation.Token));
        Assert.False(ran);
        await Assert.ThrowsAsync<IOException>(() => CombatRecoveryCompletedException.RecoverAsync(
            () => Task.CompletedTask, () => throw new IOException("capture failed"), Target, () => Task.CompletedTask, default));
    }

    private sealed class ReviveObserved : Exception;

    [Fact]
    public async Task UnknownFramesDoNotSpendTheRemainingRecoveryDeadline()
    {
        var clock = new FakeTimeProvider();
        var producer = new CaptureFrameSource(clock);
        var samples = new Queue<int>([2, -1, -1, -1, -1, 1, 1]);
        var result = await RecoveredAvatarConfirmation.WaitAsync(Target, () =>
        {
            var active = samples.Dequeue();
            return new ReviveRecoveryFrame(0, active > 0, ReviveUiState.None, active, Party)
                .WithSource(producer.Next(), clock);
        }, _ => { }, milliseconds =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(8), default, clock);
        Assert.True(result.Confirms(Target));
        Assert.Empty(samples);
    }

    [Fact]
    public async Task FrozenCaptureNeverAuthorizesRecoveryAndConsumesOnlyTheOriginalDeadline()
    {
        var clock = new FakeTimeProvider();
        var producer = new CaptureFrameSource(clock);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        ReviveRecoveryFrame? frozen = null;
        var started = clock.GetTimestamp();
        await Assert.ThrowsAsync<InvalidOperationException>(() => RecoveredAvatarConfirmation.WaitAsync(Target,
            () => frozen ??= new ReviveRecoveryFrame(0, true, ReviveUiState.None, 1, Party).WithSource(producer.Next(), clock),
            _ => throw new InvalidOperationException("不应重选已经在场的角色"),
            ms => { clock.Advance(TimeSpan.FromMilliseconds(ms)); return Task.CompletedTask; },
            TimeSpan.FromSeconds(8), default, clock));
        Assert.Equal(TimeSpan.FromSeconds(8), clock.GetElapsedTime(started));
    }
}
