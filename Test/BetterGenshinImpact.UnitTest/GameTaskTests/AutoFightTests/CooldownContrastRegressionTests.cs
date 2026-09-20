using BetterGenshinImpact.Core.Recognition.OCR.Paddle;
using BetterGenshinImpact.Core.Recognition.ONNX;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.Model.Area;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

[Collection("OfflineNativeDecision")]
public class CooldownContrastRegressionTests
{
    [Fact]
    public void NativeOcrStillReadsBrightCooldownDigits()
    {
        using var factory = new BgiOnnxFactory(NullLogger<BgiOnnxFactory>.Instance, forceCpuOcr: true);
        using var ocr = new PaddleOcrService(factory, PaddleOcrService.PaddleOcrModelType.V6);
        using var crop = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ui", "e-cooldown-gray-11_2-20260920.png"));
        using var bright = new Mat();
        Cv2.ConvertScaleAbs(crop, bright, 255d / 211);
        using var source = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black);
        using (var roi = new Mat(source, new Rect(1679, 983, 41, 18))) bright.CopyTo(roi);
        using var frame = new ImageRegion(source.Clone(), 0, 0);
        Assert.Equal(11.2, CombatHudReader.ReadCooldown(frame, ocr).Seconds);
        Assert.Equal(0, Cv2.Norm(source, frame.SrcMat, NormTypes.L1));
    }

    [Fact]
    public void NativeOcrReadsGrayCooldownWithoutTurningRecordedIconsIntoNumbers()
    {
        using var factory = new BgiOnnxFactory(NullLogger<BgiOnnxFactory>.Instance, forceCpuOcr: true);
        using var ocr = new PaddleOcrService(factory, PaddleOcrService.PaddleOcrModelType.V6);
        foreach (var (name, expected) in new[]
        {
            ("gray-11_2", 11.2), ("commission-before", 0d), ("commission-icon", 0d),
            ("commission-deadline", 0d), ("mining-before", 0d), ("mining-deadline", 0d)
        })
        {
            using var crop = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ui", $"e-cooldown-{name}-20260920.png"));
            Assert.False(crop.Empty());
            using var source = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black);
            using (var roi = new Mat(source, new Rect(1679, 983, 41, 18))) crop.CopyTo(roi);
            using var frame = new ImageRegion(source.Clone(), 0, 0);
            var reading = CombatHudReader.ReadCooldown(frame, ocr);
            Assert.Equal(expected, reading.Seconds);
            if (expected == 0) Assert.True(string.IsNullOrWhiteSpace(reading.Raw), name);
            Assert.Equal(reading, CombatHudReader.ReadCooldown(frame, ocr));
            Assert.Equal(0, Cv2.Norm(source, frame.SrcMat, NormTypes.L1));
        }
    }
}
