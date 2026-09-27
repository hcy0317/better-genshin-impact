using BetterGenshinImpact.GameTask.Common.Job;
using Fischless.GameCapture;
using OpenCvSharp;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.GameTask.Model.GameUI;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class InventoryScanEvidenceTests
{
    [Theory]
    [InlineData("complete", true)]
    [InlineData("missing-first-row", false)]
    [InlineData("missing-last-row", false)]
    [InlineData("missing-last-column", false)]
    public void CompleteLayoutRequiresBothPageEdgesAndEveryColumn(string fault, bool expected)
    {
        var rects = Enumerable.Range(0, 4).SelectMany(row => Enumerable.Range(0, 8)
            .Select(column => new Rect(10 + column * 146, 10 + row * 175, 124, 153))).ToList();
        if (fault == "missing-first-row") rects.RemoveRange(0, 8);
        if (fault == "missing-last-row") rects.RemoveRange(24, 8);
        if (fault == "missing-last-column") rects.RemoveAt(31);
        Assert.Equal(expected, CountInventoryItem.HasCompleteInventoryLayout(rects, new Rect(0, 0, 1171, 700), 8, true));
    }

    [Fact]
    public void VerifiedPageOwnsItsPixelsAndKeepsTheOriginalFrameIdentity()
    {
        using var source = new ImageRegion(new Mat(20, 20, MatType.CV_8UC3, Scalar.All(42)), 0, 0)
        { FrameStamp = new CaptureFrameSource().Next() };
        var stamp = source.FrameStamp;
        using var page = GridScreen.GridEnumerator.CreateOwnedPage(source, new Rect(2, 3, 10, 10));
        source.SrcMat.SetTo(Scalar.All(99));
        source.Dispose();
        Assert.Equal(42, page.SrcMat.At<Vec3b>(0, 0).Item0);
        Assert.Equal(stamp, page.FrameStamp);
        Assert.Equal(2, page.X);
        Assert.Equal(3, page.Y);
    }

    [OfflineNativeDecisionFact]
    public void RecordedPreciousPageHasTopButNotBottomScrollEvidence()
    {
        var path = Environment.GetEnvironmentVariable("BGI_PRECIOUS_PAGE_FILE");
        Assert.True(File.Exists(path));
        using var frame = Cv2.ImRead(path!);
        var observed = PreciousInventoryScrollReader.Read(frame);
        Assert.True(observed.Known);
        Assert.True(observed.AtTop);
        Assert.False(observed.AtBottom);
        Assert.InRange(observed.Top, 122, 126);
        Assert.InRange(observed.Bottom, 500, 530);
    }

    [Fact]
    public void CompleteOverlappingPagesMayConfirmAMissingItemAsZero()
    {
        var source = new CaptureFrameSource();
        var evidence = new InventoryScanEvidence(["fragile", "transient"], 8);
        evidence.ObservePage(source.Next(), new(true, 122, 612, true, false), Items(0, 24), true);
        evidence.ObservePage(source.Next(), new(true, 450, 940, false, true), Items(16, 24), true);
        var result = evidence.Finish(new Dictionary<string, int> { ["fragile"] = 38 });
        Assert.True(result.CoverageComplete);
        Assert.Equal(38, result.Counts["fragile"]);
        Assert.Equal(0, result.Counts["transient"]);
    }

    [Theory]
    [InlineData("not-top")]
    [InlineData("not-bottom")]
    [InlineData("skipped-page")]
    [InlineData("unknown-item")]
    [InlineData("bad-layout")]
    [InlineData("unknown-scrollbar")]
    [InlineData("foreign-source")]
    public void IncompleteEvidenceNeverTurnsMissingItemsIntoZero(string fault)
    {
        var source = new CaptureFrameSource();
        var evidence = new InventoryScanEvidence(["fragile", "transient"], 8);
        evidence.ObservePage(source.Next(), new(true, 122, 612, fault != "not-top", false), Items(0, 24), true);
        var items = Items(fault == "skipped-page" ? 30 : 16, 24);
        if (fault == "unknown-item") items[9] = null;
        evidence.ObservePage(fault == "foreign-source" ? new CaptureFrameSource().Next() : source.Next(),
            new(fault != "unknown-scrollbar", 450, 940, false, fault != "not-bottom"), items, fault != "bad-layout");
        var result = evidence.Finish(new Dictionary<string, int> { ["fragile"] = 38 });
        Assert.False(result.CoverageComplete);
        Assert.Equal(38, result.Counts["fragile"]);
        Assert.Equal(-1, result.Counts["transient"]);
    }

    [Fact]
    public void RepeatedOrAmbiguousRowsCannotProveCoverage()
    {
        var source = new CaptureFrameSource();
        var evidence = new InventoryScanEvidence(["missing"], 8);
        var rows = Enumerable.Repeat<string?>("same", 24).ToArray();
        evidence.ObservePage(source.Next(), new(true, 122, 612, true, false), rows, true);
        evidence.ObservePage(source.Next(), new(true, 450, 940, false, true), rows, true);
        Assert.Equal(-1, evidence.Finish(new Dictionary<string, int>()).Counts["missing"]);
    }

    [Fact]
    public void UnknownQuantityOfARecognizedItemIsNeverReplacedByZero()
    {
        var evidence = new InventoryScanEvidence(["fragile"], 8);
        evidence.ObservePage(new CaptureFrameSource().Next(), new(true, 122, 940, true, true), Items(0, 8), true);
        Assert.Equal(-2, evidence.Finish(new Dictionary<string, int> { ["fragile"] = -2 }).Counts["fragile"]);
    }

    [Theory]
    [InlineData(122, 395, true, false)]
    [InlineData(545, 395, false, true)]
    public void ScrollThumbEdgesAreReadWithoutInferringBottomFromNoMovement(int y, int height, bool top, bool bottom)
    {
        using var image = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.All(60));
        Cv2.Rectangle(image, new Rect(1286, y, 6, height), Scalar.All(190), -1);
        var observed = PreciousInventoryScrollReader.Read(image);
        Assert.True(observed.Known);
        Assert.Equal(top, observed.AtTop);
        Assert.Equal(bottom, observed.AtBottom);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(255)]
    public void BlankImagesDoNotBecomeFullLengthScrollbars(int color)
    {
        using var image = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.All(color));
        Assert.False(PreciousInventoryScrollReader.Read(image).Known);
    }

    private static string?[] Items(int start, int count) => Enumerable.Range(start, count).Select(x => (string?)$"item-{x}").ToArray();
}
