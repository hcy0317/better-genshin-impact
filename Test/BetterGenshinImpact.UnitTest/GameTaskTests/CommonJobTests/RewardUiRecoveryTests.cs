using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.ONNX;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.Core.Recognition.OCR.Paddle;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Helpers;
using BetterGenshinImpact.View.Drawable;
using Fischless.GameCapture;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

[Collection("OfflineNativeDecision")]
public class RewardUiRecoveryTests
{
    [OfflineNativeDecisionFact]
    public async Task RecordedRewardClosesBeforeHandbookAndMainHandoff()
    {
        Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
        Assert.Null(System.Windows.Application.Current);
        var path = Environment.GetEnvironmentVariable("BGI_S67_REWARD_FRAME");
        Assert.True(File.Exists(path), "Explicit local recording required; private screenshots must not be committed.");
        using var factory = new BgiOnnxFactory(NullLogger<BgiOnnxFactory>.Instance, forceCpuOcr: true);
        using var ocr = new PaddleOcrService(factory, PaddleOcrService.PaddleOcrModelType.V6);
        using var pixels = Cv2.ImRead(path!);
        using var fixture = new RewardFixture(ocr, pixels);
        fixture.SceneReader = frame => fixture.Stage == 0
            ? NativeUiDriver.ReadNativeScene(frame, ocr: ocr,
                reviveDetector: new ReviveUiDetector(RecognitionAssets.Get("AutoFight", "Confirm", frame), ocr, "复苏", "使用道具复苏角色"))
            : fixture.Scene;

        var result = await UiOperation.RunAsync("recorded-reward", TimeSpan.FromSeconds(3), default,
            operation => UiRecovery.ToMainAsync(fixture.Driver, operation.Token, clock: fixture.Clock), clock: fixture.Clock);

        Assert.True(result.MainReady);
        Assert.Equal(1, fixture.Clicks);
        Assert.Equal(new[] { UiAction.Escape }, fixture.Actions);
        Assert.True(fixture.MainFrames >= 2);
        Assert.All(fixture.Frames, frame => Assert.True(frame.SrcMat.IsDisposed));
        Assert.Null(System.Windows.Application.Current);
    }

    [Theory]
    [InlineData("hud")]
    [InlineData("map")]
    [InlineData("handbook")]
    [InlineData("unknown")]
    public async Task RecoveryReadsOverlayBeforeAcceptingAnyBackground(string background)
    {
        using var pixels = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black);
        using var fixture = new RewardFixture(new BoxesOcr(), pixels);
        fixture.SceneReader = _ => fixture.Stage > 0 ? fixture.Scene : Background(background);
        var result = await UiRecovery.ToMainAsync(fixture.Driver, default, clock: fixture.Clock);
        Assert.True(result.MainReady);
        Assert.Equal(1, fixture.Clicks);
        Assert.Equal(new[] { UiAction.Escape }, fixture.Actions);
        Assert.True(fixture.MainFrames >= 2);
        Assert.All(fixture.Frames, frame => Assert.True(frame.SrcMat.IsDisposed));
    }

    [Theory]
    [InlineData("title-only")]
    [InlineData("footer-only")]
    [InlineData("duplicate")]
    [InlineData("outside")]
    [InlineData("tiny")]
    [InlineData("vague")]
    [InlineData("none")]
    public async Task IncompleteOrAmbiguousRewardCannotAuthorizeInput(string fault)
    {
        using var pixels = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black);
        using var fixture = new RewardFixture(new BoxesOcr { Fault = fault }, pixels);
        await Assert.ThrowsAsync<TimeoutException>(() => UiOperation.RunAsync("limited-recovery", TimeSpan.FromSeconds(1), default,
            op => UiRecovery.ToMainAsync(fixture.Driver, op.Token, clock: fixture.Clock), clock: fixture.Clock));
        Assert.Equal(0, fixture.Clicks);
        Assert.Empty(fixture.Actions);
    }

    [Theory]
    [InlineData("prompt")]
    [InlineData("revive")]
    [InlineData("party")]
    [InlineData("talk")]
    [InlineData("black-confirm")]
    public async Task ConflictingPageCannotBeClickedAsAReward(string conflict)
    {
        using var pixels = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black);
        using var fixture = new RewardFixture(new BoxesOcr(), pixels);
        using var op = UiOperation.Begin("return-main", TimeSpan.FromSeconds(20), clock: fixture.Clock);
        var before = fixture.Driver.Capture();
        fixture.SceneReader = _ => Background(conflict);
        Assert.False(await fixture.Driver.ActAsync(UiAction.DismissReward, before, default));
        Assert.Equal(0, fixture.Clicks);
    }

    [Theory]
    [InlineData("disappeared")]
    [InlineData("repeat")]
    [InlineData("foreign")]
    [InlineData("unknown")]
    [InlineData("current-stale")]
    [InlineData("observed-stale")]
    [InlineData("old-fence")]
    public async Task InputRequiresFreshSuccessorEvidenceFromTheSameSession(string fault)
    {
        using var pixels = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black);
        using var fixture = new RewardFixture(new BoxesOcr(), pixels);
        using var op = UiOperation.Begin("return-main", TimeSpan.FromSeconds(20), clock: fixture.Clock);
        var before = fixture.Driver.Capture();
        Assert.True(before.CanDismissReward);
        switch (fault)
        {
            case "disappeared": fixture.OnFocus = () => fixture.Stage = 1; break;
            case "repeat": fixture.SourceOverride = _ => before.SourceStamp; break;
            case "foreign": fixture.SourceOverride = _ => new CaptureFrameSource(fixture.Clock).Next(); break;
            case "unknown": fixture.SourceOverride = _ => default; break;
            case "current-stale": fixture.AfterCapture = () => fixture.Clock.Advance(TimeSpan.FromSeconds(3)); break;
            case "observed-stale": fixture.OnFocus = () => fixture.Clock.Advance(TimeSpan.FromSeconds(3)); break;
            case "old-fence":
                fixture.Driver.MarkInputCompleted(before);
                fixture.SourceOverride = _ => fixture.Producer.Next(before.SourceStamp.CapturedTimestamp);
                break;
        }
        Assert.False(await fixture.Driver.ActAsync(UiAction.DismissReward, before, default));
        Assert.Equal(0, fixture.Clicks);
        Assert.All(fixture.Frames, frame => Assert.True(frame.SrcMat.IsDisposed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAndDeadlineBeforeInputAreNeverSwallowed(bool cancel)
    {
        using var pixels = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black);
        using var fixture = new RewardFixture(new BoxesOcr(), pixels);
        using var ct = new CancellationTokenSource();
        using var op = UiOperation.Begin("return-main", TimeSpan.FromSeconds(20), ct.Token, clock: fixture.Clock);
        var before = fixture.Driver.Capture();
        fixture.OnFocus = () => { if (cancel) ct.Cancel(); else fixture.Clock.Advance(TimeSpan.FromSeconds(21)); };
        var failure = await Record.ExceptionAsync(() => fixture.Driver.ActAsync(UiAction.DismissReward, before, ct.Token));
        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(failure);
        else Assert.IsType<TimeoutException>(failure);
        Assert.Equal(0, fixture.Clicks);
    }

    [Fact]
    public void NormalCaptureOutsideRecoveryDoesNotAddRewardOcr()
    {
        var ocr = new BoxesOcr();
        using var pixels = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black);
        using var fixture = new RewardFixture(ocr, pixels);
        fixture.SceneReader = _ => Background("hud");
        Assert.True(fixture.Driver.Capture().MainReady);
        Assert.Equal(0, ocr.Calls);
    }

    private static UiSnapshot Background(string name) => name switch
    {
        "hud" => new(1) { MainHud = true },
        "map" => new(1) { BigMap = true },
        "handbook" => new(1) { Handbook = true },
        "prompt" => new(1) { Prompt = true },
        "revive" => new(1) { Revive = true },
        "party" => new(1) { Party = true },
        "talk" => new(1) { Talk = true },
        "black-confirm" => new(1) { BlackConfirm = true },
        _ => new(1)
    };

    private sealed class BoxesOcr : IOcrService
    {
        internal string Fault = "";
        internal int Calls;
        public string Ocr(Mat mat) => throw new InvalidOperationException();
        public string OcrWithoutDetector(Mat mat) => throw new InvalidOperationException();
        public OcrResult OcrResult(Mat mat)
        {
            Calls++;
            bool title = mat.Height == 80;
            if (Fault == "none" || title && Fault == "footer-only" || !title && Fault == "title-only") return new([]);
            var box = title ? new RotatedRect(new Point2f(160, 40), new Size2f(64, 30), 0)
                : new RotatedRect(new Point2f(260, 40), new Size2f(192, 22), 0);
            if (!title && Fault == "outside") box = new(new Point2f(-20, 20), new Size2f(190, 20), 0);
            if (!title && Fault == "tiny") box = new(new Point2f(260, 40), new Size2f(1, 1), 0);
            var text = Fault == "vague" ? "继续" : title ? "获得" : "点击空白区域继续";
            var region = new OcrResultRegion(box, text, 1);
            return Fault == "duplicate" ? new([region, region]) : new([region]);
        }
    }

    private sealed class RewardFixture : IDisposable
    {
        internal readonly FakeTimeProvider Clock = new();
        internal readonly CaptureFrameSource Producer;
        internal readonly NativeUiDriver Driver;
        internal readonly List<ImageRegion> Frames = new();
        internal readonly List<UiAction> Actions = new();
        internal int Stage, Clicks, MainFrames;
        internal Func<ImageRegion, UiSnapshot>? SceneReader;
        internal Action? OnFocus, AfterCapture;
        internal Func<CaptureFrameStamp, CaptureFrameStamp>? SourceOverride;
        internal UiSnapshot Scene => Stage == 1 ? new(1) { Handbook = true } : new(1) { MainHud = Stage >= 2 };

        internal RewardFixture(IOcrService ocr, Mat pixels)
        {
            Producer = new(Clock);
            Driver = new(new NativeUiDriverIo
            {
                Capture = () =>
                {
                    Clock.Advance(TimeSpan.FromMilliseconds(1));
                    var frame = new ImageRegion(Stage == 0 ? pixels.Clone() : new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black),
                        0, 0, drawContent: new DrawContent());
                    var stamp = Producer.Next();
                    frame.FrameStamp = SourceOverride?.Invoke(stamp) ?? stamp;
                    Frames.Add(frame);
                    if (Stage >= 2) MainFrames++;
                    AfterCapture?.Invoke();
                    return frame;
                },
                ReadScene = frame => SceneReader?.Invoke(frame) ?? Scene,
                Focus = () => OnFocus?.Invoke(),
                Ocr = () => Stage == 0 ? ocr : new BoxesOcr { Fault = "none" },
                Texts = () => new("", ""),
                Click = (_, bounds, admission) =>
                {
                    admission();
                    Assert.InRange(bounds.X, 840, 880);
                    Assert.InRange(bounds.Y, 720, 760);
                    Clicks++;
                    Stage = 1;
                },
                OtherAction = (action, _, admission) =>
                {
                    admission();
                    Assert.Equal(1, Stage);
                    Assert.Equal(UiAction.Escape, action);
                    Actions.Add(action);
                    Stage = 2;
                    return true;
                },
                Delay = (ms, ct) => { ct.ThrowIfCancellationRequested(); Clock.Advance(TimeSpan.FromMilliseconds(ms)); return Task.CompletedTask; },
                Clock = Clock,
                BeginExclusive = () => new Lease()
            });
        }
        public void Dispose() { Driver.Dispose(); foreach (var frame in Frames) frame.Dispose(); }
        private sealed class Lease : IDisposable { public void Dispose() { } }
    }
}
