using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Helpers;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class MailToastAvailabilityTests
{
    [Theory]
    [InlineData("您的邮箱内附件已全部领取完毕", null)]
    [InlineData("重新连接服务器", "reconnect-panel")]
    [InlineData("", "connection-panel-unconfirmed")]
    [InlineData("您的邮箱内附件已全部领取完毕 重新连接服务器", "reconnect-panel")]
    [InlineData("未知确认提示", "connection-panel-unconfirmed")]
    public void CenteredMailToastDoesNotPauseUiRecoveryButUncertainPanelsStillDo(string text, string? expected)
    {
        Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
        using var pixels = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(140, 140, 140));
        Cv2.Rectangle(pixels, new Rect(660, 495, 600, 90), new Scalar(55, 46, 28), -1);
        using var frame = new ImageRegion(pixels.Clone(), 0, 0);
        Assert.Equal(expected, WorldFrameAvailability.ReadConnectionWait(frame, new TextOcr(text)));
        if (expected == null)
            Assert.Equal(WorldFrameKind.Unknown, WorldFrameAvailability.Read(frame, false, new TextOcr(text)));
    }

    private sealed class TextOcr(string text) : IOcrService
    {
        public string Ocr(Mat mat) => text;
        public string OcrWithoutDetector(Mat mat) => text;
        public OcrResult OcrResult(Mat mat) => new([]);
    }
}
