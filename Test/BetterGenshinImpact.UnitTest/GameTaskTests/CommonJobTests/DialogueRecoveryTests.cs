using BetterGenshinImpact.GameTask.Common.Ui;
using Fischless.GameCapture;
using Microsoft.Extensions.Time.Testing;
using OpenCvSharp;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.Model.Area;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class DialogueRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DialogueRecognitionFailurePreservesTheOriginalExceptionWithoutInput(bool duringAction)
    {
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        var original = new InvalidOperationException("dialogue OCR failed");
        var inputs = 0;
        using var driver = new NativeUiDriver(new NativeUiDriverIo
        {
            Clock = clock, Focus = () => { }, BeginExclusive = () => new Noop(), Texts = () => DomainTipUiReaderTests.Texts,
            Capture = () => new ImageRegion(new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black), 0, 0) { FrameStamp = source.Next() },
            ReadScene = image => new UiSnapshot(image.FrameStamp.Sequence)
            { Talk = true, Dialogue = DialogueUiReader.Read(image, new FailingOcr(original)) },
            Ocr = () => new EmptyOcr(), Click = (_, _, _) => inputs++,
            OtherAction = (_, _, _) => { inputs++; return true; }, Delay = (_, _) => Task.CompletedTask
        });
        var stamp = source.Next();
        var observed = new UiSnapshot(stamp.Sequence) { Talk = true,
            Dialogue = new(stamp, DialogueEvidenceState.Confirmed, null, DialogueOptions.Absent, "stable", true) }
            .WithSource(stamp, clock, UiSnapshot.RecoveryMaximumAge);
        var error = duringAction
            ? await Record.ExceptionAsync(() => driver.ActAsync(UiAction.AdvanceDialogueText, observed, default))
            : Record.Exception(() => driver.Capture());
        Assert.Same(original, error);
        Assert.Contains(nameof(FailingOcr), error!.StackTrace);
        Assert.Equal(0, inputs);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task DirectEscapeProbeRejectsDialogueInEitherFrame(bool proposedTalk, bool currentTalk)
    {
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        var inputs = 0;
        using var driver = new NativeUiDriver(new NativeUiDriverIo
        {
            Clock = clock, Focus = () => { }, BeginExclusive = () => new Noop(), Texts = () => DomainTipUiReaderTests.Texts,
            Capture = () => new ImageRegion(new Mat(180, 320, MatType.CV_8UC3, Scalar.Black), 0, 0) { FrameStamp = source.Next() },
            ReadScene = image => new UiSnapshot(image.FrameStamp.Sequence) { Talk = currentTalk },
            Ocr = () => new EmptyOcr(), Click = (_, _, _) => inputs++,
            OtherAction = (_, _, _) => { inputs++; return true; }, Delay = (_, _) => Task.CompletedTask
        });
        var stamp = source.Next();
        var observed = new UiSnapshot(stamp.Sequence) { Talk = proposedTalk }
            .WithSource(stamp, clock, UiSnapshot.RecoveryMaximumAge);
        Assert.False(await driver.ActAsync(UiAction.EscapeProbe, observed, default));
        Assert.Equal(0, inputs);
    }

    private sealed class FailingOcr(Exception error) : IOcrService
    {
        public string Ocr(Mat image) => throw error;
        public string OcrWithoutDetector(Mat image) => throw error;
        public OcrResult OcrResult(Mat image) => throw error;
    }

    [Theory]
    [InlineData("icon_option.png", "普通选项")]
    [InlineData("icon_exclamation.png", "任务选项")]
    [InlineData("icon_option.png", "购买物品")]
    public void RecognizedOrdinaryQuestAndPurchaseOptionsCannotAuthorizeTextAdvance(string icon, string optionText)
    {
        using var image = new ImageRegion(new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black), 0, 0)
        { FrameStamp = new CaptureFrameSource().Next() };
        using var template = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "GameTask", "AutoSkip", "Assets", "1920x1080", icon));
        Assert.False(template.Empty());
        using (var target = new Mat(image.SrcMat, new Rect(1080, 450, template.Width, template.Height))) template.CopyTo(target);
        var observed = DialogueUiReader.Read(image, new OptionOcr(optionText));
        Assert.True(observed.Confirmed);
        Assert.Equal(DialogueOptions.Present, observed.Options);
        var snapshot = new UiSnapshot(1) { Talk = true, Dialogue = observed }.WithSource(image.FrameStamp, TimeProvider.System, UiSnapshot.RecoveryMaximumAge);
        Assert.Null(snapshot.DialogueAction);
        Assert.False(snapshot.CanEscape);
        Assert.False(UiStallEscapeProbe.IsUnrecognizedStall(snapshot));
    }
    private sealed class OptionOcr(string text) : IOcrService
    {
        public string Ocr(Mat image) => image.Height > 150 ? text : "正文";
        public string OcrWithoutDetector(Mat image) => Ocr(image);
        public OcrResult OcrResult(Mat image) => new([]);
    }
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public async Task NativeDialogueRequiresStableTextAndNoOptionsInBothFrames(bool options, bool changedText, bool expected)
    {
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        var actions = new List<UiAction>();
        var captures = 0;
        using var driver = new NativeUiDriver(new NativeUiDriverIo
        {
            Clock = clock, Focus = () => { }, BeginExclusive = () => new Noop(), Texts = () => DomainTipUiReaderTests.Texts,
            Capture = () =>
            {
                clock.Advance(TimeSpan.FromMilliseconds(1)); captures++;
                return new ImageRegion(new Mat(180, 320, MatType.CV_8UC3, Scalar.Black), 0, 0) { FrameStamp = source.Next() };
            },
            ReadScene = image => new UiSnapshot(image.FrameStamp.Sequence) { Talk = true,
                Dialogue = new(image.FrameStamp, DialogueEvidenceState.Confirmed, null, captures > 1 && options ? DialogueOptions.Present : DialogueOptions.Absent,
                    changedText && captures > 1 ? "different" : "stable", true) },
            Ocr = () => new EmptyOcr(), Click = (_, _, _) => throw new InvalidOperationException("No dialogue option click allowed"),
            OtherAction = (action, _, admit) => { admit(); actions.Add(action); return true; },
            Delay = (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        });
        var before = driver.Capture();
        Assert.False(await driver.ActAsync(UiAction.Escape, before, default));
        Assert.Equal(expected, await driver.ActAsync(UiAction.AdvanceDialogueText, before, default));
        Assert.Equal(expected ? 1 : 0, actions.Count);
    }

    private sealed class Noop : IDisposable { public void Dispose() { } }
    private sealed class EmptyOcr : IOcrService
    {
        public string Ocr(Mat image) => "";
        public string OcrWithoutDetector(Mat image) => "";
        public OcrResult OcrResult(Mat image) => new([]);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangingDialoguePagesReachExitWithoutEscapingOrResettingTheParentBudget(bool stageOnly)
    {
        var clock = new FakeTimeProvider();
        var driver = new DialogueReplay(clock, stageOnly);
        var result = await UiRecovery.ToMainAsync(driver, default, clock: clock);
        Assert.True(result.MainReady);
        Assert.Equal(new[] { UiAction.AdvanceDialogueText, UiAction.AdvanceDialogueText, UiAction.ExitDialogue }, driver.Actions);
        Assert.All(driver.Delays, delay => Assert.True(delay >= 200));
    }

    [Fact]
    public async Task BackgroundAndFrameChangesDoNotRenewAStalledDialogue()
    {
        var clock = new FakeTimeProvider();
        var driver = new DialogueReplay(clock, false, true);
        var start = clock.GetUtcNow();
        await Assert.ThrowsAsync<TimeoutException>(() => UiRecovery.ToMainAsync(driver, default, clock: clock));
        Assert.Equal(2, driver.Actions.Count);
        Assert.True(clock.GetUtcNow() - start < TimeSpan.FromSeconds(3));
    }

    private sealed class DialogueReplay(FakeTimeProvider clock, bool stageOnly = false, bool stalled = false) : IUiDriver
    {
        private readonly CaptureFrameSource _source = new(clock);
        private int _page;
        internal readonly List<UiAction> Actions = new();
        internal readonly List<int> Delays = new();
        public UiSnapshot Capture()
        {
            var stamp = _source.Next();
            return new UiSnapshot(stamp.Sequence) { MainHud = _page >= 3, Talk = _page < 3,
                Dialogue = new(stamp, DialogueEvidenceState.Confirmed, _page == 2 ? new Rect(30, 30, 20, 20) : null,
                    DialogueOptions.Absent, stageOnly ? "same-body" : "page-" + _page, true)
                    { StageFingerprint = "stage-" + _page } }.WithSource(stamp, clock, UiSnapshot.RecoveryMaximumAge);
        }
        public Task<bool> ActAsync(UiAction action, UiSnapshot snapshot, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Actions.Add(action); if (!stalled) _page++; return Task.FromResult(true); }
        public Task DelayAsync(int delay, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Delays.Add(delay); clock.Advance(TimeSpan.FromMilliseconds(delay)); return Task.CompletedTask; }
    }
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(1)]
    public void OnlyConfirmedOptionFreeTextCanAdvance(int optionValue)
    {
        var options = (DialogueOptions)optionValue;
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock).Next();
        var snapshot = new UiSnapshot(1) { Talk = true,
            Dialogue = new(source, DialogueEvidenceState.Confirmed, null, options, "body", true) }.WithSource(source, clock, UiSnapshot.RecoveryMaximumAge);
        Assert.False(snapshot.CanEscape);
        Assert.Equal(options == DialogueOptions.Absent ? UiAction.AdvanceDialogueText : null, snapshot.DialogueAction);
        Assert.Equal(UiAction.ExitDialogue, (snapshot with { Dialogue = snapshot.Dialogue with { ExitBounds = new Rect(20, 20, 20, 20) } }).DialogueAction);
        Assert.Null((snapshot with { NetworkWaitReason = "reconnect" }).DialogueAction);
    }
}
