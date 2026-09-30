using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.Model.Area;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatHudReaderTests
{
    [Theory]
    [InlineData("f6")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-1")]
    [InlineData("1.2s")]
    public void InvalidCooldownTextIsNotAZeroCooldown(string raw)
    {
        using var frame = new ImageRegion(new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black), 0, 0);
        var ocr = new CountingOcr(raw);
        var reading = CombatHudReader.ReadCooldown(frame, ocr);
        Assert.Equal(raw, reading.Raw);
        Assert.True(double.IsNaN(reading.Seconds));
        Assert.Equal(1, ocr.Reads);
    }

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

    private sealed class CountingOcr(string text = "4.2") : IOcrService
    {
        public int Reads;
        public string OcrWithoutDetector(Mat mat) { Reads++; return text; }
        public string Ocr(Mat mat) => throw new NotSupportedException();
        public OcrResult OcrResult(Mat mat) => throw new NotSupportedException();
    }
}
