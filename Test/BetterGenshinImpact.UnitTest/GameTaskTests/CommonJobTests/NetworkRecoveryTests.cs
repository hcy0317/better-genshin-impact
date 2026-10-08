using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Model.Area;
using Microsoft.Extensions.Time.Testing;
using OpenCvSharp;
using Fischless.GameCapture;
using BetterGenshinImpact.Core.Script;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class NetworkRecoveryTests
{
    [Fact]
    public async Task NetworkRecoveryCannotReplayTheCurrentScriptAfterASideEffect()
    {
        using var scope = TaskExecutionScope.BeginOwned();
        var attempts = 0;
        var original = new TimeoutException("server");
        var failure = await Assert.ThrowsAsync<TaskFailureRecoveryException>(() => ScriptStepOutcomeRunner.RunAsync(
            () =>
            {
                attempts++;
                TaskExecutionScope.MarkUnsafeNetworkReplay("cannon-fired");
                throw new NetworkTaskRetryException(original);
            }, _ => Task.CompletedTask, default));
        Assert.Contains("BGI_NETWORK_REPLAY_UNSAFE", failure.Message + failure.InnerExceptions[1].Message);
        Assert.Same(original, failure.InnerExceptions[0]);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task FreshHandoffFailureStopsNativeTaskReplay()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<IOException>(() => OneDragonStepRunner.RunAsync(true,
            (action, _) => action(), () => { attempts++; throw new NetworkTaskRetryException(new TimeoutException("server")); },
            _ => throw new IOException("handoff")));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task FreshGroupHandoffFailureDoesNotRerunTheCurrentScript()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<IOException>(() => ScriptStepOutcomeRunner.RunAsync(
            () => { attempts++; throw new NetworkTaskRetryException(new TimeoutException("server")); },
            _ => Task.CompletedTask, default, verifyNetworkRestart: _ => throw new IOException("handoff")));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void NetworkWaitCanLeaveUiDeadlineButKeepsUserAndIndependentCallerCancellation()
    {
        using var user = new CancellationTokenSource();
        using var independent = new CancellationTokenSource();
        using var operation = UiOperation.Begin("failed-ui", TimeSpan.FromSeconds(20), user.Token);
        Assert.Equal(user.Token, operation.CancellationForRecovery(operation.Token));
        Assert.Equal(independent.Token, operation.CancellationForRecovery(independent.Token));
    }

    [Fact]
    public void OfflineNativeAdapterBlocksBeforeSlowSceneOrOcrAndDisposesTheImage()
    {
        var clock = new FakeTimeProvider();
        var producer = new CaptureFrameSource(clock);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        using var frame = new ImageRegion(new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black), 0, 0)
        { FrameStamp = producer.Next() };
        using var driver = new NativeUiDriver(new NativeUiDriverIo
        {
            Capture = () => frame, NetworkAvailable = () => false,
            ReadScene = _ => throw new InvalidOperationException("Slow recognition must not run offline"),
            Ocr = () => throw new InvalidOperationException("OCR must not initialize offline"),
            Texts = () => throw new InvalidOperationException("Localization must not initialize offline"),
            Focus = () => { }, Click = (_, _, _) => throw new InvalidOperationException("no input"),
            OtherAction = (_, _, _) => throw new InvalidOperationException("no input"),
            Delay = (_, _) => Task.CompletedTask, Clock = clock, BeginExclusive = () => new Noop()
        });
        var observed = driver.Capture();
        Assert.Equal("network-interface-offline", observed.NetworkWaitReason);
        Assert.False(observed.MainReady);
        Assert.False(observed.CanEscape);
        Assert.True(frame.SrcMat.IsDisposed);
    }

    [UiRecoveryFact("server-wait-20261008.png")]
    public void WaitingSpinnerIsCheckedBeforeWorldRecognition()
    {
        using var frame = new ImageRegion(Cv2.ImRead(UiRecoveryFixtures.PathFor("server-wait-20261008.png")), 0, 0);
        Assert.Equal(WorldFrameKind.Loading, WorldFrameAvailability.ReadWorld(frame,
            _ => throw new InvalidOperationException("transformation"), _ => throw new InvalidOperationException("hud"),
            _ => throw new InvalidOperationException("map"), new NoOcr()));
    }

    [Fact]
    public async Task PendingReplayDoesNotMaskAnActualCleanupAggregate()
    {
        using var scope = TaskExecutionScope.BeginOwned();
        var cleanup = new AggregateException(new IOException("cleanup"));
        var actual = await Assert.ThrowsAsync<AggregateException>(() => TaskExecutionScope.RunCheckedAsync(() =>
        {
            TaskExecutionScope.Capture().Report(new NetworkTaskRetryException(new TimeoutException("server")));
            throw cleanup;
        }));
        Assert.Same(cleanup, actual);
        Assert.Throws<AggregateException>(TaskExecutionScope.ConsumeNetworkRetry);
    }

    [Fact]
    public async Task CompletedGroupStepsRemainCompleteWhenCurrentScriptRestartsEvenAfterCatch()
    {
        using var scope = TaskExecutionScope.BeginOwned();
        var completedEarlier = 1;
        TaskExecutionScope.MarkUnsafeNetworkReplay("previous completed script");
        var attempts = 0;
        var step = await ScriptStepOutcomeRunner.RunAsync(async () =>
        {
            if (++attempts == 1)
            {
                try { await TaskExecutionScope.RunCheckedAsync(() => throw new NetworkTaskRetryException(new TimeoutException("server"))); }
                catch { /* Model a JS catch that swallows the typed exception. */ }
            }
            return new(ScriptOutcomeKind.Completed, "current-script");
        }, _ => throw new InvalidOperationException("Network recovery was already verified"), default);
        Assert.Equal(ScriptOutcomeKind.Completed, step.Outcome.Kind);
        Assert.Equal(2, attempts);
        Assert.Equal(1, completedEarlier);
        TaskExecutionScope.ThrowIfFailed();
    }

    [Fact]
    public async Task ConfirmedInterruptionNeverUsesARepeatedFrameAsRecovery()
    {
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var observed = new UiSnapshot(1) { MainHud = true }.WithSource(source.Next(), clock, UiSnapshot.RecoveryMaximumAge);
        await Assert.ThrowsAsync<TimeoutException>(() => NetworkRecovery.WaitAsync(true, () => observed,
            () => { }, () => true,
            (ms, _) => { clock.Advance(TimeSpan.FromMilliseconds(ms)); return Task.CompletedTask; }, default, clock));
    }

    [Fact]
    public async Task ExhaustedNetworkRetriesDoNotLoopOrPretendCompletion()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<NetworkTaskRetryException>(() => OneDragonStepRunner.RunAsync(true,
            (action, _) => action(), () => { attempts++; throw new NetworkTaskRetryException(new TimeoutException("server")); }, _ => Task.CompletedTask));
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task FailedReleaseNeverCapturesOrRetries()
    {
        await Assert.ThrowsAsync<IOException>(() => NetworkRecovery.WaitAsync(true,
            () => throw new InvalidOperationException("capture must not run"), () => throw new IOException("release"),
            () => true, (_, _) => Task.CompletedTask, default));
    }

    [UiRecoveryFact("server-wait-20261008.png")]
    public void RecordedServerWaitBlocksTheBackgroundMapWithoutOcr()
    {
        using var frame = new ImageRegion(Cv2.ImRead(UiRecoveryFixtures.PathFor("server-wait-20261008.png")), 0, 0);
        Assert.Equal(WorldFrameKind.Loading, WorldFrameAvailability.Read(frame, true, new NoOcr()));
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public void SpinnerRequiresThreeSmallRoundComponents(int dots, bool expected)
    {
        using var frame = new ImageRegion(new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black), 0, 0);
        for (var i = 0; i < dots; i++)
            Cv2.Circle(frame.SrcMat, new Point(960 + i * 20, 560 - i * 15), 7, new Scalar(40, 210, 255), -1);
        Assert.Equal(expected, WorldFrameAvailability.HasServerWaitSpinner(frame));
    }

    [Fact]
    public async Task DelayedSpinnerIsObservedBeforeRecoveryAndRepeatedFramesCannotConfirmIt()
    {
        var clock = new FakeTimeProvider();
        var started = clock.GetTimestamp();
        var sequence = 0;
        var released = 0;
        var recovered = await NetworkRecovery.WaitAsync(false, () =>
        {
            var seconds = clock.GetElapsedTime(started).TotalSeconds;
            return new UiSnapshot(++sequence) { BigMap = true, NetworkWaitReason = seconds is >= 40 and < 90 ? "server-wait" : null };
        }, () => released++, () => true,
            (ms, token) => { token.ThrowIfCancellationRequested(); clock.Advance(TimeSpan.FromMilliseconds(ms)); return Task.CompletedTask; },
            default, clock);
        Assert.True(recovered);
        Assert.Equal(1, released);
        Assert.True(clock.GetElapsedTime(started) >= TimeSpan.FromSeconds(93));
    }

    [Fact]
    public async Task UnconfirmedStallDoesNotAuthorizeRestart()
    {
        var clock = new FakeTimeProvider();
        Assert.False(await NetworkRecovery.WaitAsync(false, () => new UiSnapshot(1) { BigMap = true },
            () => { }, () => true,
            (ms, _) => { clock.Advance(TimeSpan.FromMilliseconds(ms)); return Task.CompletedTask; }, default, clock));
    }

    [Fact]
    public async Task OfflineLinkBlocksReadyHudAndCancellationStopsTheWait()
    {
        using var cancel = new CancellationTokenSource();
        var clock = new FakeTimeProvider();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NetworkRecovery.WaitAsync(true,
            () => new UiSnapshot(1) { MainHud = true }, () => { }, () => false,
            (ms, _) => { cancel.Cancel(); return Task.CompletedTask; }, cancel.Token, clock));
    }

    [Fact]
    public async Task CurrentStepRestartsOnlyAfterARecoveredNetworkInterruption()
    {
        var attempts = 0;
        var recoveries = 0;
        var failure = await OneDragonStepRunner.RunAsync(true, (action, _) => action(),
            () => ++attempts == 1 ? Task.FromException(new NetworkTaskRetryException(new TimeoutException("original"))) : Task.CompletedTask,
            _ => { recoveries++; return Task.CompletedTask; });
        Assert.Null(failure);
        Assert.Equal(2, attempts);
        Assert.Equal(1, recoveries);
    }

    [Fact]
    public async Task CleanupFailureCannotBeHiddenByNetworkRestart()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<AggregateException>(() => OneDragonStepRunner.RunAsync(true, async (action, _) =>
        {
            try { await action(); }
            catch (Exception error) { throw new AggregateException(error, new IOException("cleanup")); }
        }, () => { attempts++; throw new NetworkTaskRetryException(new TimeoutException("original")); }, _ => Task.CompletedTask));
        Assert.Equal(1, attempts);
    }

    [Theory]
    [InlineData("角色处于雷元素附着状态下或携带雷种子时可激活", "requires-electro-or-electrogranum")]
    [InlineData("获得雷种子", null)]
    [InlineData("", null)]
    public void CannonActivationRequiresExplicitGameRejection(string text, string? expected)
    {
        using var frame = new ImageRegion(new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black), 0, 0);
        Assert.Equal(expected, BetterGenshinImpact.GameTask.Common.BgiVision.CannonUiReader.ReadActivationRejection(frame, new FixedOcr(text)));
    }

    private sealed class FixedOcr(string text) : BetterGenshinImpact.Core.Recognition.OCR.IOcrService
    {
        public string Ocr(Mat mat) => text;
        public string OcrWithoutDetector(Mat mat) => text;
        public BetterGenshinImpact.Core.Recognition.OCR.OcrResult OcrResult(Mat mat) => new([]);
    }

    private sealed class Noop : IDisposable { public void Dispose() { } }

    private sealed class NoOcr : BetterGenshinImpact.Core.Recognition.OCR.IOcrService
    {
        public string Ocr(Mat mat) => throw new InvalidOperationException("Spinner detection must not call OCR");
        public string OcrWithoutDetector(Mat mat) => Ocr(mat);
        public BetterGenshinImpact.Core.Recognition.OCR.OcrResult OcrResult(Mat mat) => throw new InvalidOperationException();
    }
}
