using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Model.Area;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class WorldFrameAvailabilityTests
{
    [UiRecoveryFact("dark-world-20261004.png")]
    public void DarkRockBackgroundIsNotAReconnectPanel()
    {
        using var frame = Frame("dark-world-20261004.png");
        Assert.Equal(WorldFrameKind.Playable, WorldFrameAvailability.Read(frame, true, new TextOcr()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UniformPanelColoredWorldDoesNotAcquireModalEvidence(bool hud)
    {
        using var frame = new ImageRegion(new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(55, 46, 28)), 0, 0);
        Assert.Equal(hud ? WorldFrameKind.Playable : WorldFrameKind.Unknown,
            WorldFrameAvailability.Read(frame, hud, new TextOcr()));
    }

    [UiRecoveryFact("map-reconnect-20261004.png")]
    public void RecordedMapReconnectStillBlocksInput()
    {
        using var frame = Frame("map-reconnect-20261004.png");
        Assert.Equal(WorldFrameKind.Reconnecting, WorldFrameAvailability.Read(frame, true, new TextOcr()));
        Assert.Equal(WorldFrameKind.Unknown, WorldFrameAvailability.Read(frame, true, new TextOcr("")));
    }

    [UiRecoveryTheory("handbook-commission-20261003.png", "reconnect-20261003.png")]
    [InlineData("handbook-commission-20261003.png", (int)WorldFrameKind.Playable)]
    [InlineData("reconnect-20261003.png", (int)WorldFrameKind.Reconnecting)]
    public void KnownTransformationAvoidsOrdinaryReviveOcrButStillRejectsReconnect(string file, int expected)
    {
        using var frame = Frame(file);
        var kind = WorldFrameAvailability.ReadWorld(frame, _ => true,
            _ => throw new InvalidOperationException("Slow ordinary revival OCR must not run for known transformation"),
            _ => false, new TextOcr());
        Assert.Equal((WorldFrameKind)expected, kind);
    }

    [UiRecoveryFact("handbook-commission-20261003.png", "loading-20261003.png", "reconnect-20261003.png")]
    public void ReconnectOverlayBlocksEvenWhenBackgroundHudIsPresent()
    {
        using var frame = Frame("reconnect-20261003.png");
        Assert.Equal(WorldFrameKind.Reconnecting, WorldFrameAvailability.Read(frame, true, new TextOcr()));
    }

    [UiRecoveryFact("handbook-commission-20261003.png", "loading-20261003.png", "reconnect-20261003.png")]
    public void RecordedLoadingIsNotAnOperableWorld()
    {
        using var frame = Frame("loading-20261003.png");
        Assert.Equal(WorldFrameKind.Loading, WorldFrameAvailability.Read(frame, false, new TextOcr()));
    }

    [UiRecoveryFact("handbook-commission-20261003.png", "loading-20261003.png", "reconnect-20261003.png")]
    public void HandbookAndUnknownPicturesCannotPretendToBeLoading()
    {
        using var frame = Frame("handbook-commission-20261003.png");
        Assert.Equal(WorldFrameKind.Unknown, WorldFrameAvailability.Read(frame, false, new TextOcr()));
    }

    private static ImageRegion Frame(string name) => new(Cv2.ImRead(UiRecoveryFixtures.PathFor(name)), 0, 0);

    private sealed class TextOcr(string text = "重新连接服务器") : IOcrService
    {
        public string Ocr(Mat mat) => text;
        public string OcrWithoutDetector(Mat mat) => text;
        public OcrResult OcrResult(Mat mat) => new([]);
    }
}
