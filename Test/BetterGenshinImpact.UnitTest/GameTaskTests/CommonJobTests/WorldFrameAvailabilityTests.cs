using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Model.Area;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class WorldFrameAvailabilityTests
{
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

    private sealed class TextOcr : IOcrService
    {
        public string Ocr(Mat mat) => "重新连接服务器";
        public string OcrWithoutDetector(Mat mat) => "重新连接服务器";
        public OcrResult OcrResult(Mat mat) => new([]);
    }
}
