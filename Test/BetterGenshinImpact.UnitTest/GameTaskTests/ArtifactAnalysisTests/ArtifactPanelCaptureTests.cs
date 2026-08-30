using BetterGenshinImpact.GameTask.ArtifactAnalysis;
using BetterGenshinImpact.Core.Recognition.OCR;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.ArtifactAnalysisTests;

public class ArtifactPanelCaptureTests
{
    [Theory]
    [InlineData(4, 530)]
    [InlineData(3, 500)]
    [InlineData(2, 465)]
    public void SetNameTop_MovesBelowTheFourthSubstatWhenPresent(
        int substatCount,
        double expected)
    {
        Assert.Equal(expected, ArtifactInventoryUi.SetNameTop(substatCount));
    }

    [Fact]
    public void LegacySetNameRegion_StaysInsideTheCoreCapture()
    {
        using var fullCapture = new Mat(
            new Size(1920, 1080),
            MatType.CV_8UC3,
            Scalar.Black);
        using var frame = ArtifactCapturedItem.Create(0, false, fullCapture);
        var region = ArtifactInventoryUi.LegacySetNameRegion(4);

        Assert.Equal((525d, 37d), region);

        using var setName = frame.CropBaseRect(
            1090, region.Top, 340, region.Height);

        Assert.Equal(new Size(340, 37), setName.Size());
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1366, 768)]
    [InlineData(1600, 900)]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    [InlineData(3840, 2160)]
    public void Create_NormalizesEverySixteenByNineCaptureToThe1600By900LogicalPanel(
        int width,
        int height)
    {
        using var fullCapture = new Mat(
            new Size(width, height),
            MatType.CV_8UC3,
            new Scalar(12, 34, 56));
        using var frame = ArtifactCapturedItem.Create(
            scanIndex: 7,
            locked: true,
            fullCapture);

        Assert.Equal(new Size(410, 462), frame.CoreCapture.Size());
        Assert.Equal(new Size(340, 70), frame.EquipmentCapture.Size());
        Assert.Equal(new Size(width, height), frame.SourceSize);
        Assert.Equal(5, frame.Rarity);
        Assert.True(frame.Locked);
        Assert.Equal(7, frame.ScanIndex);
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1366, 768)]
    [InlineData(1600, 900)]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    [InlineData(3840, 2160)]
    public void CropBaseRect_PreservesTheSameLogicalPixelsAcrossSixteenByNineCaptures(
        int width,
        int height)
    {
        using var logicalCapture = new Mat(
            new Size(1600, 900),
            MatType.CV_8UC3,
            Scalar.Black);
        Cv2.Rectangle(
            logicalCapture,
            new Rect(1105, 148, 200, 50),
            new Scalar(17, 91, 203),
            -1);
        using var sourceCapture = new Mat();
        Cv2.Resize(
            logicalCapture,
            sourceCapture,
            new Size(width, height),
            interpolation: InterpolationFlags.Nearest);
        using var frame = ArtifactCapturedItem.Create(0, false, sourceCapture);

        using var slot = frame.CropBaseRect(1110, 153, 190, 40);
        var mean = Cv2.Mean(slot);

        Assert.InRange(mean.Val0, 16, 18);
        Assert.InRange(mean.Val1, 90, 92);
        Assert.InRange(mean.Val2, 202, 204);
    }

    [Fact]
    public void LegacyDetectionRegion_StartsAtSlotAndEndsAfterSetName()
    {
        using var fullCapture = new Mat(
            new Size(1920, 1080),
            MatType.CV_8UC3,
            Scalar.Black);
        using var frame = ArtifactCapturedItem.Create(0, false, fullCapture);

        var region = ArtifactInventoryUi.LegacyDetectionRegion;
        Assert.Equal(new Rect(1090, 153, 410, 409), region);
        using var card = frame.CropBaseRect(
            region.X, region.Y, region.Width, region.Height);

        Assert.Equal(new Size(410, 409), card.Size());
    }

    [Fact]
    public void LegacyDetectionBands_MergeOnlyConfidentTextOnTheSameSemanticLine()
    {
        var result = new OcrResult([
            Region(90, 20, "死之羽"),
            Region(25, 80, "攻击"),
            Region(90, 80, "力"),
            Region(30, 115, "47"),
            Region(150, 80, "噪声", score: 0.2f)
        ]);

        Assert.Equal("死之羽", ArtifactInventoryUi.ReadDetectedBand(result, 0, 55));
        Assert.Equal("攻击力", ArtifactInventoryUi.ReadDetectedBand(result, 55, 48));
        Assert.Equal("47", ArtifactInventoryUi.ReadDetectedBand(result, 90, 62));
    }

    [Theory]
    [InlineData(4, 372, 37)]
    [InlineData(3, 342, 37)]
    [InlineData(2, 307, 37)]
    public void LegacyDetectionSetBand_FollowsTheVisibleSubstatCount(
        int substatCount,
        double expectedTop,
        double expectedHeight)
    {
        Assert.Equal(
            (expectedTop, expectedHeight),
            ArtifactInventoryUi.LegacyDetectedSetNameBand(substatCount));
    }

    private static OcrResultRegion Region(
        float x,
        float y,
        string text,
        float score = 0.99f) =>
        new(new RotatedRect(
            new Point2f(x, y),
            new Size2f(40, 20),
            0), text, score);

    [Fact]
    public void Create_NormalizesFourKPanelToThe1600By900LogicalScale()
    {
        using var fullCapture = new Mat(
            new Size(3840, 2160),
            MatType.CV_8UC3,
            new Scalar(12, 34, 56));
        using var frame = ArtifactCapturedItem.Create(0, false, fullCapture);

        using var slot = frame.CropBaseRect(1110, 153, 190, 40);
        using var setName = frame.CropBaseRect(1110, 500, 280, 32);
        using var equipped = frame.CropBaseRect(1154.9, 762.6, 243.5, 25.2);

        Assert.Equal(new Size(410, 462), frame.CoreCapture.Size());
        Assert.Equal(new Size(340, 70), frame.EquipmentCapture.Size());
        Assert.Equal(new Size(3840, 2160), frame.SourceSize);
        Assert.Equal(new Size(190, 40), slot.Size());
        Assert.Equal(new Size(280, 32), setName.Size());
        Assert.Equal(new Size(244, 25), equipped.Size());
    }

    [Fact]
    public void CreateOcrDebugCapture_KeepsOnlyTheVisibleArtifactCard()
    {
        using var fullCapture = new Mat(
            new Size(1920, 1080),
            MatType.CV_8UC3,
            Scalar.Black);
        using var frame = ArtifactCapturedItem.Create(0, false, fullCapture);

        using var debugCapture = frame.CreateOcrDebugCapture();

        Assert.Equal(new Size(410, 462), debugCapture.Size());
    }

    [Fact]
    public void CropBaseRect_MapsFixedOcrCoordinatesIntoTheCorePanel()
    {
        using var fullCapture = new Mat(
            new Size(1920, 1080),
            MatType.CV_8UC3,
            Scalar.Black);
        using var frame = ArtifactCapturedItem.Create(0, false, fullCapture);

        using var slot = frame.CropBaseRect(1110, 153, 190, 40);
        using var setName = frame.CropBaseRect(1110, 500, 280, 32);
        using var equipped = frame.CropBaseRect(1154.9, 762.6, 243.5, 25.2);

        Assert.Equal(new Size(190, 40), slot.Size());
        Assert.Equal(new Size(280, 32), setName.Size());
        Assert.Equal(new Size(244, 25), equipped.Size());
    }
}
