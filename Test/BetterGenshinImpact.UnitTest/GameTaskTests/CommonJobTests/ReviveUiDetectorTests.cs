using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Model.Area;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class ReviveUiDetectorTests
{
    [Fact]
    public void CombatHandoffIdentifiesAnAbnormalHudWithoutRunningPopupOcr()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ui", "food-revive-controller-20260910.png");
        using var image = new ImageRegion(Cv2.ImRead(path), 0, 0);
        var detector = new ReviveUiDetector(RecognitionAssets.Get("AutoFight", "Confirm", image),
            new UnavailableOcr(), "复苏", "使用道具复苏角色");
        Assert.False(detector.IsCombatHud(image));
    }

    [Fact]
    public void CapturedFoodPromptCanBeJudgedWithoutAnApplicationHost()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ui", "food-revive-controller-20260910.png");
        using var image = new ImageRegion(Cv2.ImRead(path), 0, 0);
        var ocr = new FixedOcr("使用道具复苏角色");
        var detector = new ReviveUiDetector(RecognitionAssets.Get("AutoFight", "Confirm", image),
            ocr, "复苏", "使用道具复苏角色");

        Assert.Equal(ReviveUiState.FoodPrompt, detector.Read(image));
        Assert.Equal(ReviveUiState.FoodPrompt, detector.Read(image));
        Assert.Equal(1, ocr.Reads);
    }

    [Fact]
    public void AnUnobscuredLivingHudDoesNotRequireTextRecognition()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ui", "inactive-zhongli-20260911.png");
        using var image = new ImageRegion(Cv2.ImRead(path), 0, 0);
        var detector = new ReviveUiDetector(RecognitionAssets.Get("AutoFight", "Confirm", image),
            new UnavailableOcr(), "复苏", "使用道具复苏角色");

        Assert.Equal(ReviveUiState.None, detector.Read(image));
    }

    [Fact]
    public void FoodConfirmationWinsEvenIfTheBackgroundHealthBarIsBright()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ui", "food-revive-controller-20260910.png");
        using var image = new ImageRegion(Cv2.ImRead(path), 0, 0);
        for (var x = 808; x <= 812; x++) image.SrcMat.Set(1010, x, new Vec3b(34, 215, 150));
        var detector = new ReviveUiDetector(RecognitionAssets.Get("AutoFight", "Confirm", image),
            new FixedOcr("使用道具复苏角色"), "复苏", "使用道具复苏角色");
        Assert.Equal(ReviveUiState.FoodPrompt, detector.Read(image));
    }

    [Fact]
    public void MissingHealthEvidenceCannotSuppressAReviveButton()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ui", "inactive-zhongli-20260911.png");
        using var image = new ImageRegion(Cv2.ImRead(path), 0, 0);
        for (var x = 808; x <= 812; x++) image.SrcMat.Set(1010, x, new Vec3b(0, 0, 0));
        var detector = new ReviveUiDetector(RecognitionAssets.Get("AutoFight", "Confirm", image),
            new FixedOcr("复苏"), "复苏", "使用道具复苏角色");
        Assert.Equal(ReviveUiState.FullPartyDefeat, detector.Read(image));
    }

    [Fact]
    public void BgraCaptureHasTheSameLivingHudAsTheRecordedBgrFrame()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ui", "inactive-zhongli-20260911.png");
        using var source = Cv2.ImRead(path);
        using var image = new ImageRegion(source.CvtColor(ColorConversionCodes.BGR2BGRA), 0, 0);
        var detector = new ReviveUiDetector(RecognitionAssets.Get("AutoFight", "Confirm", image),
            new UnavailableOcr(), "复苏", "使用道具复苏角色");
        Assert.Equal(ReviveUiState.None, detector.Read(image));
    }

    private sealed class UnavailableOcr : IOcrService
    {
        public string Ocr(Mat mat) => throw new InvalidOperationException("Text recognition is not available");
        public string OcrWithoutDetector(Mat mat) => throw new InvalidOperationException("Text recognition is not available");
        public OcrResult OcrResult(Mat mat) => throw new InvalidOperationException("Text recognition is not available");
    }

    private sealed class FixedOcr(string text) : IOcrService
    {
        public int Reads { get; private set; }
        public string Ocr(Mat mat) => text;
        public string OcrWithoutDetector(Mat mat) => text;
        public OcrResult OcrResult(Mat mat)
        {
            Reads++;
            return new([new(new RotatedRect(new Point2f(100, 100), new Size2f(180, 30), 0), text, 1)]);
        }
    }
}
