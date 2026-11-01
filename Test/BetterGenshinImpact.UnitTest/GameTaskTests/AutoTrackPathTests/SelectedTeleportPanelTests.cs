using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.AutoTrackPath;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoTrackPathTests;

public class SelectedTeleportPanelTests
{
    private const string ExpectedName = "荒坠的圣迹";

    [Fact]
    public void CorrectSelectedDomainCanContinueWithoutAnotherMapPointClick()
    {
        var clock = new PanelClock();
        using var panel = Panel(clock);
        Assert.True(SelectedTeleportPanel.Matches(panel, "MasteryDomain", ExpectedName, false,
            ocr: new PanelOcr(ExpectedName), clock: clock));
    }

    [Fact]
    public void VisibleTeleportButtonForAnotherDomainDoesNotAdmitTheRequestedTarget()
    {
        var clock = new PanelClock();
        using var panel = Panel(clock);
        Assert.False(SelectedTeleportPanel.Matches(panel, "MasteryDomain", ExpectedName, false,
            ocr: new PanelOcr("昏识塔"), clock: clock));
    }

    [Theory]
    [InlineData("荒坠")]
    [InlineData("荒坠的圣迹残影")]
    [InlineData("")]
    [InlineData("点击更改标记名称")]
    public void PartialOrUnrelatedTitlesCannotIdentifyTheTarget(string title)
    {
        var clock = new PanelClock(); using var panel = Panel(clock);
        Assert.False(SelectedTeleportPanel.Matches(panel, "MasteryDomain", ExpectedName, false,
            ocr: new PanelOcr(title), clock: clock));
    }

    [Theory]
    [InlineData("TeleportWaypoint", false)]
    [InlineData("ForgeryDomain", false)]
    [InlineData("MasteryDomain", true)]
    [InlineData(null, false)]
    public void OnlyTheRequestedNonForcedMasteryDomainCanUseTheSelectionPath(string? type, bool force)
    {
        var clock = new PanelClock(); using var panel = Panel(clock); var ocr = new PanelOcr(ExpectedName);
        Assert.False(SelectedTeleportPanel.Matches(panel, type, ExpectedName, force, ocr: ocr, clock: clock));
        Assert.Equal(0, ocr.TitleReads);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void BothMapAndTeleportButtonAreRequiredBeforeReadingTitle(bool map, bool button)
    {
        var clock = new PanelClock(); using var panel = Panel(clock, map, button); var ocr = new PanelOcr(ExpectedName);
        Assert.False(SelectedTeleportPanel.Matches(panel, "MasteryDomain", ExpectedName, false, ocr: ocr, clock: clock));
        Assert.Equal(0, ocr.TitleReads);
    }

    [Fact]
    public void UnknownSourceCannotGrantSelectedTargetIdentity()
    {
        var clock = new PanelClock(); using var panel = Panel(clock, source: default(CaptureFrameStamp));
        Assert.False(SelectedTeleportPanel.Matches(panel, "MasteryDomain", ExpectedName, false,
            ocr: new PanelOcr(ExpectedName), clock: clock));
    }

    [Fact]
    public void SourceThatExpiresDuringTitleReadingCannotGrantIdentity()
    {
        var clock = new PanelClock(); using var panel = Panel(clock);
        Assert.False(SelectedTeleportPanel.Matches(panel, "MasteryDomain", ExpectedName, false,
            ocr: new PanelOcr(ExpectedName, () => clock.Advance(2001)), clock: clock));
    }

    [Fact]
    public void CancellationDuringTitleReadingCannotGrantIdentity()
    {
        var clock = new PanelClock(); using var panel = Panel(clock); using var ct = new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() => SelectedTeleportPanel.Matches(panel, "MasteryDomain",
            ExpectedName, false, ct.Token, new PanelOcr(ExpectedName, ct.Cancel), clock));
    }

    [Fact]
    public void AChangedCaptureSessionCannotReuseEarlierSelectionIdentity()
    {
        var clock = new PanelClock(); var selected = clock.Source.Next(); clock.Source.Restart(); clock.Advance(1);
        using var panel = Panel(clock);
        Assert.False(SelectedTeleportPanel.Matches(panel, "MasteryDomain", ExpectedName, false,
            ocr: new PanelOcr(ExpectedName), clock: clock, selectedSource: selected));
    }

    [Fact]
    public async Task ChangedTargetOnTheActualConfirmationFrameSendsNoInput()
    {
        var clock = new PanelClock(); using var panel = Panel(clock); var keys = 0; var clicks = 0;
        var result = await TeleportPanelConfirmation.TryConfirmWithFeedbackAsync(panel,
            () => Panel(clock, false, false), _ => { keys++; return Task.CompletedTask; },
            (_, _, _) => { clicks++; return Task.CompletedTask; },
            (ms, _) => { clock.Advance(ms); return Task.CompletedTask; }, default,
            ocr: new PanelOcr("昏识塔"), clock: clock, canConfirm: _ => false);
        Assert.False(result); Assert.Equal(0, keys); Assert.Equal(0, clicks);
    }

    [Fact]
    public void CanonicalWhitespaceDoesNotChangeTheSelectedName()
    {
        var clock = new PanelClock(); using var panel = Panel(clock); var ocr = new PanelOcr(" 荒坠的 圣迹 \n");
        Assert.True(SelectedTeleportPanel.Matches(panel, "MasteryDomain", ExpectedName, false, ocr: ocr, clock: clock));
        Assert.Equal(1, ocr.TitleReads);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void MissingCanonicalNameCannotGrantIdentity(string? name)
    {
        var clock = new PanelClock(); using var panel = Panel(clock); var ocr = new PanelOcr(ExpectedName);
        Assert.False(SelectedTeleportPanel.Matches(panel, "MasteryDomain", name, false, ocr: ocr, clock: clock));
        Assert.Equal(0, ocr.TitleReads);
    }

    [Fact]
    public void RepeatedOrSameTimestampFrameCannotConfirmAnEarlierSelection()
    {
        var clock = new PanelClock(); var selected = clock.Source.Next();
        using var repeated = Panel(clock, source: selected); using var sameTime = Panel(clock);
        Assert.False(SelectedTeleportPanel.Matches(repeated, "MasteryDomain", ExpectedName, false,
            ocr: new PanelOcr(ExpectedName), clock: clock, selectedSource: selected));
        Assert.False(SelectedTeleportPanel.Matches(sameTime, "MasteryDomain", ExpectedName, false,
            ocr: new PanelOcr(ExpectedName), clock: clock, selectedSource: selected));
        clock.Advance(1); using var updated = Panel(clock);
        Assert.True(SelectedTeleportPanel.Matches(updated, "MasteryDomain", ExpectedName, false,
            ocr: new PanelOcr(ExpectedName), clock: clock, selectedSource: selected));
    }

    [Fact]
    public void ParentDeadlineDuringTitleReadingCannotGrantIdentity()
    {
        var clock = new PanelClock(); using var panel = Panel(clock);
        using var operation = BetterGenshinImpact.GameTask.Common.Ui.UiOperation.Begin("selected-panel",
            TimeSpan.FromMilliseconds(500), clock: clock);
        Assert.Throws<TimeoutException>(() => SelectedTeleportPanel.Matches(panel, "MasteryDomain", ExpectedName,
            false, ocr: new PanelOcr(ExpectedName, () => clock.Advance(600)), clock: clock));
    }

    [Fact]
    public async Task VerifiedTitleStillNeedsTwoNewPostInputMapClosedFrames()
    {
        var clock = new PanelClock(); var selected = clock.Source.Next(); clock.Advance(1);
        using var panel = Panel(clock); var keys = 0; var captures = 0; var closed = default(CaptureFrameStamp);
        var ocr = new PanelOcr(ExpectedName);
        var result = await TeleportPanelConfirmation.TryConfirmWithFeedbackAsync(panel,
            () => { captures++; return Panel(clock, false, false); },
            _ => { keys++; return Task.CompletedTask; }, (_, _, _) => throw new InvalidOperationException("Unexpected click"),
            (ms, _) => { clock.Advance(ms); return Task.CompletedTask; }, default, ocr: ocr, clock: clock,
            mapClosed: source => closed = source,
            canConfirm: frame => SelectedTeleportPanel.Matches(frame, "MasteryDomain", ExpectedName, false,
                ocr: ocr, clock: clock, selectedSource: selected));
        Assert.True(result); Assert.Equal(1, keys); Assert.Equal(2, captures); Assert.True(closed.IsKnown);
        Assert.True(new CaptureFrameFence(panel.FrameStamp, panel.FrameStamp.CapturedTimestamp).Accepts(closed));
    }

    [Fact]
    public async Task TitleChangeBeforeFallbackClickCannotSendAnotherConfirmation()
    {
        var clock = new PanelClock(); using var panel = Panel(clock); var keys = 0; var clicks = 0;
        var checks = 0; var ocr = new PanelOcr(ExpectedName);
        var result = await TeleportPanelConfirmation.TryConfirmWithFeedbackAsync(panel,
            () => Panel(clock), _ => { keys++; return Task.CompletedTask; },
            (_, _, _) => { clicks++; return Task.CompletedTask; },
            (ms, _) => { clock.Advance(ms); return Task.CompletedTask; }, default, ocr: ocr, clock: clock,
            canConfirm: frame => SelectedTeleportPanel.Matches(frame, "MasteryDomain", ExpectedName, false,
                ocr: new PanelOcr(++checks == 1 ? ExpectedName : "昏识塔"), clock: clock));
        Assert.False(result); Assert.Equal(1, keys); Assert.Equal(0, clicks); Assert.Equal(2, checks);
    }

    [Fact]
    public async Task ACorrectTitleWithoutMapClosureDoesNotCompleteTeleport()
    {
        var clock = new PanelClock(); using var panel = Panel(clock); var keys = 0; var clicks = 0;
        var ocr = new PanelOcr(ExpectedName);
        var result = await TeleportPanelConfirmation.TryConfirmWithFeedbackAsync(panel,
            () => Panel(clock), _ => { keys++; return Task.CompletedTask; },
            (_, _, _) => { clicks++; return Task.CompletedTask; },
            (ms, _) => { clock.Advance(ms); return Task.CompletedTask; }, default, ocr: ocr, clock: clock,
            canConfirm: frame => SelectedTeleportPanel.Matches(frame, "MasteryDomain", ExpectedName, false,
                ocr: ocr, clock: clock));
        Assert.False(result); Assert.Equal(1, keys); Assert.Equal(1, clicks);
    }

    [Fact]
    public async Task PostInputFramesFromAnotherCaptureSessionCannotCompleteTheSelection()
    {
        var clock = new PanelClock(); using var panel = Panel(clock); var ocr = new PanelOcr(ExpectedName);
        var keys = 0; var clicks = 0;
        var result = await TeleportPanelConfirmation.TryConfirmWithFeedbackAsync(panel,
            () => { clock.Source.Restart(); return Panel(clock, false, false); },
            _ => { keys++; return Task.CompletedTask; }, (_, _, _) => { clicks++; return Task.CompletedTask; },
            (ms, _) => { clock.Advance(ms); return Task.CompletedTask; }, default, ocr: ocr, clock: clock,
            canConfirm: _ => true);
        Assert.False(result); Assert.Equal(1, keys); Assert.Equal(0, clicks);
    }

    internal static ImageRegion Panel(PanelClock clock, bool map = true, bool button = true,
        CaptureFrameStamp? source = null)
    {
        var image = new ImageRegion(new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black), 0, 0);
        image.FrameStamp = source ?? clock.Source.Next();
        if (map) Paint(image.SrcMat, "MapScaleButton.png", 30, 440);
        if (button) Paint(image.SrcMat, "GoTeleport.png", 1440, 960);
        return image;
    }

    private static void Paint(Mat image, string name, int x, int y)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "BetterGenshinImpact.sln"))) root = root.Parent;
        Assert.NotNull(root);
        using var template = Cv2.ImRead(Path.Combine(root!.FullName, "BetterGenshinImpact", "GameTask",
            "QuickTeleport", "Assets", "1920x1080", name));
        Assert.False(template.Empty());
        using var roi = new Mat(image, new Rect(x, y, template.Width, template.Height));
        template.CopyTo(roi);
    }

    internal sealed class PanelClock : TimeProvider
    {
        private long _timestamp = 10_000_000;
        internal CaptureFrameSource Source { get; }
        internal PanelClock() => Source = new(this);
        public override long TimestampFrequency => 10_000_000;
        public override long GetTimestamp() => _timestamp;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_timestamp);
        internal void Advance(int ms) => _timestamp += ms * 10_000L;
    }

    internal sealed class PanelOcr(string title, Action? onTitle = null) : IOcrService
    {
        internal int TitleReads { get; private set; }
        public string Ocr(Mat mat) => "传送";
        public string OcrWithoutDetector(Mat mat)
        {
            TitleReads++;
            onTitle?.Invoke();
            return title;
        }
        public OcrResult OcrResult(Mat mat) => new([new OcrResultRegion(
            new RotatedRect(new Point2f(mat.Width / 2f, mat.Height / 2f), new Size2f(100, 30), 0), "传送", 1)]);
    }
}
