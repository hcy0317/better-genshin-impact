using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using Fischless.WindowsInput;
using Microsoft.Extensions.Time.Testing;
using OpenCvSharp;
using Vanara.PInvoke;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class BigMapOpenAttemptTests
{
    [Fact]
    public async Task AdvancingProducerNumbersWithIdenticalPixelsAreNotVisualFeedback()
    {
        var replay = new Replay();
        Assert.Equal(BigMapOpenOutcome.NoVisualFeedback, await replay.Run());
        Assert.Equal(1, replay.Inputs);
        Assert.Equal(100, replay.Delays[0]);
        Assert.Contains(replay.Evidence, x => x.Phase == "failure-before" && x.Source.Sequence == 1);
        Assert.Contains(replay.Evidence, x => x.Phase == "timeout" && x.Source.Sequence > 1 && x.Detail.Contains("pixelsChanged=False"));
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Fact]
    public async Task ChangedWorldPixelsWithoutAMapKeepTheExistingBoundedRetry()
    {
        var replay = new Replay { ChangePixels = true };
        Assert.Equal(BigMapOpenOutcome.TimedOut, await replay.Run());
        Assert.False(await BigMapOpenAttempt.CompleteAsync(BigMapOpenOutcome.TimedOut, null,
            (_, _) => throw new Exception("No passive wait for a changing world."), default));
    }

    [Fact]
    public async Task TwoFreshPostInputMapFramesConfirmOpening()
    {
        var replay = new Replay { ChangePixels = true, MapAfter = 2 };
        Assert.Equal(BigMapOpenOutcome.Opened, await replay.Run());
        Assert.Equal(3, replay.Frames);
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("changed-source")]
    [InlineData("queued-before-input")]
    public async Task AnUnusablePostInputSourceCannotConfirmTheMap(string kind)
    {
        var replay = new Replay { ChangePixels = true, MapAfter = 2, SourceKind = kind };
        Assert.Equal(BigMapOpenOutcome.SourceUnavailable, await replay.Run());
        Assert.Equal(1, replay.Inputs);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("stale")]
    [InlineData("slow-environment")]
    public async Task UnusableAdmissionFrameDoesNotSendTheMapKey(string kind)
    {
        var replay = new Replay { SourceKind = kind };
        Assert.Equal(BigMapOpenOutcome.SourceUnavailable, await replay.Run());
        Assert.Equal(0, replay.Inputs);
    }

    [Fact]
    public async Task MissingNativeReceiptDoesNotBlindlyRetryInput()
    {
        var replay = new Replay { EmitReceipt = false };
        Assert.Equal(BigMapOpenOutcome.InputUnconfirmed, await replay.Run());
        Assert.Equal(1, replay.Inputs);
        Assert.Equal(1, replay.Frames);
    }

    [Fact]
    public async Task WaitingBeforeInputPreventsMapKeyAndUsesTheOriginalInterruption()
    {
        var replay = new Replay { Waiting = true };
        var failure = await Assert.ThrowsAsync<NetworkInterruptionException>(() => replay.Run());
        Assert.Equal(0, replay.Inputs);
        using var owner = TaskExecutionScope.BeginOwned();
        await Assert.ThrowsAsync<NetworkTaskRetryException>(() => BigMapOpenAttempt.CompleteAsync(
            BigMapOpenOutcome.SourceUnavailable, failure, (error, _) =>
            { Assert.Same(failure, error); return Task.FromResult(true); }, default));
    }

    [Fact]
    public async Task PassiveWaitWithoutConfirmedRecoveryLatchesFailureEvenIfScriptCatchesIt()
    {
        using var owner = TaskExecutionScope.BeginOwned();
        var failure = await Assert.ThrowsAsync<TaskFailureRecoveryException>(() => BigMapOpenAttempt.CompleteAsync(
            BigMapOpenOutcome.NoVisualFeedback, null, (_, _) => Task.FromResult(false), default));
        Assert.Contains("BGI_MAP_NO_FEEDBACK", failure.InnerExceptions[0].Message);
        Assert.Same(failure, Assert.Throws<TaskFailureRecoveryException>(TaskExecutionScope.ThrowIfFailed));
    }

    [Fact]
    public async Task PassiveWaitFailureKeepsTheMapFailureAndStopsTheOwner()
    {
        using var owner = TaskExecutionScope.BeginOwned();
        var passiveFailure = new IOException("capture source unavailable");
        var failure = await Assert.ThrowsAsync<TaskFailureRecoveryException>(() => BigMapOpenAttempt.CompleteAsync(
            BigMapOpenOutcome.NoVisualFeedback, null, (_, _) => throw passiveFailure, default));
        Assert.Same(passiveFailure, failure.InnerExceptions[1]);
        Assert.Same(failure, TaskExecutionScope.Failure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmedNetworkRecoveryCannotReplayAnUnsafeScript(bool unsafeReplay)
    {
        using var owner = TaskExecutionScope.BeginOwned();
        if (unsafeReplay) TaskExecutionScope.MarkUnsafeNetworkReplay("cannon attempted");
        var error = await Record.ExceptionAsync(() => BigMapOpenAttempt.CompleteAsync(
            BigMapOpenOutcome.NoVisualFeedback, null, (_, _) => Task.FromResult(true), default));
        if (unsafeReplay) Assert.IsType<TaskFailureRecoveryException>(error);
        else Assert.IsType<NetworkTaskRetryException>(error);
        Assert.Same(error, Record.Exception(TaskExecutionScope.ThrowIfFailed));
    }

    [Fact]
    public async Task CancellationAfterInputDisposesPixelsAndNeverSendsAgain()
    {
        using var cancellation = new CancellationTokenSource();
        var replay = new Replay { CancelAfterWait = cancellation };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replay.Run(cancellation.Token));
        Assert.Equal(1, replay.Inputs);
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    private sealed class Replay
    {
        internal FakeTimeProvider Time { get; } = new();
        private readonly CaptureFrameSource _source;
        private CaptureFrameStamp _first;
        internal int Frames, Inputs, MapAfter = int.MaxValue;
        internal bool ChangePixels, Waiting, EmitReceipt = true;
        internal string SourceKind = "advancing";
        internal CancellationTokenSource? CancelAfterWait;
        internal List<int> Delays = [];
        internal List<ImageRegion> Images = [];
        internal List<(string Phase, CaptureFrameStamp Source, string Detail)> Evidence = [];
        internal Replay() => _source = new(Time);
        internal Task<BigMapOpenOutcome> Run(CancellationToken ct = default) => BigMapOpenAttempt.RunAsync(
            Capture, _ => Frames >= MapAfter, _ => Waiting ? "reconnect-panel" : null,
            () => { }, () =>
            {
                Inputs++;
                if (EmitReceipt) new WindowsInputMessageDispatcher(null, inputs => (uint)inputs.Length, () => 0)
                    .DispatchInput(new User32.INPUT[2]);
                Time.Advance(TimeSpan.FromMilliseconds(35));
            }, (ms, token) =>
            {
                token.ThrowIfCancellationRequested();
                Delays.Add(ms);
                Time.Advance(TimeSpan.FromMilliseconds(ms));
                CancelAfterWait?.Cancel();
                return Task.CompletedTask;
            }, ct, TimeSpan.FromMilliseconds(500), Time, () =>
            {
                if (SourceKind == "slow-environment") Time.Advance(TimeSpan.FromSeconds(3));
                return "backend=fake foregroundIsGame=True; submission is not gameplay acceptance";
            }, (phase, image, detail) => Evidence.Add((phase, image.FrameStamp, detail)));
        private ImageRegion Capture()
        {
            Frames++;
            var stamp = _source.Next();
            if (Frames == 1) _first = stamp;
            if (SourceKind == "unknown") stamp = default;
            if (SourceKind == "stale") Time.Advance(TimeSpan.FromSeconds(3));
            if (Frames > 1)
            {
                if (SourceKind == "duplicate") stamp = _first;
                if (SourceKind == "queued-before-input") stamp = stamp with { CapturedTimestamp = _first.CapturedTimestamp };
                if (SourceKind == "changed-source") stamp = new CaptureFrameSource(Time).Next();
            }
            var image = new ImageRegion(new Mat(16, 16, MatType.CV_8UC3,
                ChangePixels && Frames > 1 ? Scalar.White : Scalar.Black), 0, 0) { FrameStamp = stamp };
            Images.Add(image);
            return image;
        }
    }
}
