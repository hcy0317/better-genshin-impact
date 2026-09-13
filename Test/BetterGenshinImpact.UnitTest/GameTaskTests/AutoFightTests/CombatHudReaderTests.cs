using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.Model.Area;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatHudReaderTests
{
    [Fact]
    public void MultipleCooldownPredicatesReuseOneNativeOcrReadAndDoNotOwnTheFrame()
    {
        using var frame = new ImageRegion(new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black), 0, 0);
        var ocr = new CountingOcr();
        Assert.Equal(4.2, CombatHudReader.ReadCooldown(frame, ocr).Seconds);
        Assert.Equal(4.2, CombatHudReader.ReadCooldown(frame, ocr).Seconds);
        Assert.Equal(1, ocr.Reads);
        Assert.False(frame.SrcMat.Empty());
    }

    private sealed class CountingOcr : IOcrService
    {
        public int Reads;
        public string OcrWithoutDetector(Mat mat) { Reads++; return "4.2"; }
        public string Ocr(Mat mat) => throw new NotSupportedException();
        public OcrResult OcrResult(Mat mat) => throw new NotSupportedException();
    }
}
