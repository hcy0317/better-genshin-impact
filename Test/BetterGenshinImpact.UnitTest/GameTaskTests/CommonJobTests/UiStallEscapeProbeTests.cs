using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.Core.Recognition.OCR.Paddle;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Helpers;
using BetterGenshinImpact.View.Drawable;
using Fischless.GameCapture;
using Microsoft.Extensions.Time.Testing;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

/// <summary>
/// "世界已渲染、UI 与菜单均未出现"的空转兜底探测：判定条件、宽限期、次数上限、
/// 双帧一致性，以及调用方作用域（只有 requireOverworld 的恢复边界才兜底）。
/// </summary>
[Collection("OfflineNativeDecision")]
public class UiStallEscapeProbeTests
{
    [Fact]
    public void StallPredicateAcceptsOnlyUnrecognizableButUsableFrames()
    {
        Assert.True(UiStallEscapeProbe.IsUnrecognizedStall(new UiSnapshot(1)));
        Assert.False(UiStallEscapeProbe.IsUnrecognizedStall(new UiSnapshot(0)));
        Assert.False(UiStallEscapeProbe.IsUnrecognizedStall(new UiSnapshot(1) { MainHud = true }));
        Assert.False(UiStallEscapeProbe.IsUnrecognizedStall(new UiSnapshot(1) { MenuBack = true }));
        Assert.False(UiStallEscapeProbe.IsUnrecognizedStall(new UiSnapshot(1) { Closable = true }));
        Assert.False(UiStallEscapeProbe.IsUnrecognizedStall(new UiSnapshot(1) { Talk = true }));
        Assert.False(UiStallEscapeProbe.IsUnrecognizedStall(new UiSnapshot(1) { Prompt = true }));
        Assert.False(UiStallEscapeProbe.IsUnrecognizedStall(new UiSnapshot(1) { Revive = true }));
        Assert.False(UiStallEscapeProbe.IsUnrecognizedStall(new UiSnapshot(1) { FullPartyDefeat = true }));
        Assert.False(UiStallEscapeProbe.IsUnrecognizedStall(new UiSnapshot(1) { BlackConfirm = true }));
        Assert.False(UiStallEscapeProbe.IsUnrecognizedStall(new UiSnapshot(1) { InDomain = true }));
        Assert.False(UiStallEscapeProbe.IsUnrecognizedStall(new UiSnapshot(1) { PartyList = true }));
    }

    [Fact]
    public void ProbeWaitsForTheGraceAndStopsAtItsBound()
    {
        var clock = new FakeTimeProvider();
        var probe = new UiStallEscapeProbe(TimeSpan.FromSeconds(6), 2, clock);
        var stall = new UiSnapshot(1);

        Assert.False(probe.ShouldProbe(stall));
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.False(probe.ShouldProbe(stall));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(probe.ShouldProbe(stall));

        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.False(probe.ShouldProbe(stall));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(probe.ShouldProbe(stall));

        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.False(probe.ShouldProbe(stall));
    }

    [Fact]
    public void RecognizedFrameRestartsTheStallWindow()
    {
        var clock = new FakeTimeProvider();
        var probe = new UiStallEscapeProbe(TimeSpan.FromSeconds(6), 2, clock);

        Assert.False(probe.ShouldProbe(new UiSnapshot(1)));
        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.False(probe.ShouldProbe(new UiSnapshot(2) { MainHud = true }));

        Assert.False(probe.ShouldProbe(new UiSnapshot(3)));
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.False(probe.ShouldProbe(new UiSnapshot(4)));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(probe.ShouldProbe(new UiSnapshot(5)));
    }

    [Fact]
    public async Task OverworldRecoveryProbesEscapeWhileEveryFrameIsUnrecognized()
    {
        var clock = new FakeTimeProvider();
        var frames = Enumerable.Range(1, 30).Select(index => new UiSnapshot(index))
            .Append(new UiSnapshot(31) { MainHud = true })
            .Append(new UiSnapshot(32) { MainHud = true })
            .ToArray();
        var driver = new ReplayDriver(clock, frames);

        var result = await UiRecovery.ToMainAsync(driver, default, requireOverworld: true, clock: clock);

        Assert.Contains(UiAction.EscapeProbe, driver.Actions);
        Assert.True(result.MainReady);
    }

    [Fact]
    public async Task MainReturnWithoutOverworldNeverProbesEscape()
    {
        var clock = new FakeTimeProvider();
        var frames = Enumerable.Range(1, 30).Select(index => new UiSnapshot(index))
            .Append(new UiSnapshot(31) { MainHud = true })
            .ToArray();
        var driver = new ReplayDriver(clock, frames);

        var result = await UiRecovery.ToMainAsync(driver, default, clock: clock);

        Assert.Empty(driver.Actions);
        Assert.True(result.MainReady);
    }

    [Fact]
    public async Task NativeDriverSendsTheProbeOnlyWhenBothFramesStayUnrecognized()
    {
        using var fixture = new ProbeFixture();
        fixture.Scene = new UiSnapshot(1);
        var observed = fixture.Driver.Capture();
        Assert.True(UiStallEscapeProbe.IsUnrecognizedStall(observed));

        Assert.True(await fixture.Driver.ActAsync(UiAction.EscapeProbe, observed, default));
        Assert.Equal([UiAction.EscapeProbe], fixture.Actions);
    }

    [Fact]
    public async Task NativeDriverRejectsTheProbeOnceAnyKnownUiAppears()
    {
        using var fixture = new ProbeFixture();
        fixture.Scene = new UiSnapshot(1);
        var observed = fixture.Driver.Capture();
        // 提出意图与真正发送之间的新帧出现主界面：兜底输入必须作废。
        fixture.Scene = new UiSnapshot(1) { MainHud = true };
        Assert.False(await fixture.Driver.ActAsync(UiAction.EscapeProbe, observed, default));

        var known = fixture.Driver.Capture();
        Assert.False(await fixture.Driver.ActAsync(UiAction.EscapeProbe, known, default));
        Assert.Empty(fixture.Actions);
    }

    private sealed class ReplayDriver(FakeTimeProvider clock, params UiSnapshot[] snapshots) : IUiDriver
    {
        private int _index;
        public List<UiAction> Actions { get; } = [];

        public UiSnapshot Capture()
        {
            var sample = snapshots[Math.Min(_index++, snapshots.Length - 1)];
            if (_index > snapshots.Length)
            {
                sample = sample with { FrameId = sample.FrameId + _index - snapshots.Length };
            }

            return sample;
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
            return Task.FromResult(true);
        }
    }

    private sealed class ProbeFixture : IDisposable
    {
        internal readonly FakeTimeProvider Clock = new();
        internal readonly CaptureFrameSource Producer;
        internal readonly NativeUiDriver Driver;
        internal readonly List<ImageRegion> Frames = new();
        internal readonly List<UiAction> Actions = new();
        internal UiSnapshot Scene = new(1);

        internal ProbeFixture()
        {
            Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
            Producer = new(Clock);
            Driver = new(new NativeUiDriverIo
            {
                Capture = () =>
                {
                    Clock.Advance(TimeSpan.FromMilliseconds(1));
                    var frame = new ImageRegion(new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black), 0, 0,
                        drawContent: new DrawContent());
                    frame.FrameStamp = Producer.Next();
                    Frames.Add(frame);
                    return frame;
                },
                ReadScene = _ => Scene,
                Focus = () => { },
                Ocr = () => new NoOcr(),
                Texts = () => new("", ""),
                Click = (_, _, admission) => admission(),
                OtherAction = (action, _, admission) =>
                {
                    admission();
                    Actions.Add(action);
                    return true;
                },
                Delay = (milliseconds, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    Clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
                    return Task.CompletedTask;
                },
                Clock = Clock,
                BeginExclusive = () => new Lease()
            });
        }

        public void Dispose()
        {
            Driver.Dispose();
            foreach (var frame in Frames)
            {
                frame.Dispose();
            }
        }

        private sealed class Lease : IDisposable
        {
            public void Dispose() { }
        }
    }

    private sealed class NoOcr : IOcrService
    {
        public string Ocr(Mat mat) => throw new InvalidOperationException();

        public string OcrWithoutDetector(Mat mat) => throw new InvalidOperationException();

        public OcrResult OcrResult(Mat mat) => new([]);
    }
}
