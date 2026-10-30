using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.OCR.Paddle;
using BetterGenshinImpact.Core.Recognition.ONNX;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;
using Xunit.Abstractions;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class ReviveScreenshotTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("s32-food-revive-20260916.png")]
    [InlineData("food-revive-controller-20260910.png")]
    public void ActualFoodDialogIsARecoveryObstructionInTheProductionReader(string file)
    {
        Assert.True(BetterGenshinImpact.Helpers.ApplicationHostBootstrapGuard.IsProhibited);
        using var factory = new BgiOnnxFactory(NullLogger<BgiOnnxFactory>.Instance, forceCpuOcr: true);
        using var ocr = new PaddleOcrService(factory, PaddleOcrService.PaddleOcrModelType.V6);
        using var image = new ImageRegion(Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ui", file)), 0, 0)
        { FrameStamp = new CaptureFrameSource().Next() };
        var detector = new ReviveUiDetector(RecognitionAssets.Get("AutoFight", "Confirm", image),
            ocr, "复苏", "使用道具复苏角色");
        Assert.Equal(ReviveUiState.FoodPrompt, detector.Read(image));
        var observed = NativeUiDriver.Read(image, ocr: ocr, reviveDetector: detector);
        Assert.True(observed.Prompt);
        Assert.True(observed.Revive);
        Assert.False(observed.MainReady);
        Assert.False(observed.MapReady);
        Assert.False(observed.Matches(UiTarget.Party));
        Assert.Null(System.Windows.Application.Current);
    }

    [Theory]
    [InlineData("使用道具复苏角色", true)]
    [InlineData("使用 道具 复苏 角色", true)]
    [InlineData("复苏", true)]
    [InlineData("无法复苏", false)]
    [InlineData("复苏道具不足", false)]
    [InlineData("复苏选中的角色，为其恢复50点生命值。", false)]
    public void FoodTitleIsDistinctFromButtonAndDescription(string text, bool expected)
    {
        Assert.Equal(expected, Bv.IsReviveFoodTitle(text, "复苏", "使用道具复苏角色"));
        if (text != "复苏") Assert.False(Bv.IsReviveText(text, "复苏"));
    }

    [Fact]
    public void ActualControllerScreenshotIsRecognizedAsFoodPrompt()
    {
        var path=Path.Combine(AppContext.BaseDirectory,"Fixtures","Ui","food-revive-controller-20260910.png");
        using var image=new ImageRegion(Cv2.ImRead(path),0,0);
        Assert.Equal(1920,image.Width);
        using var confirm=image.Find(RecognitionAssets.Get("AutoFight","Confirm",image));
        Assert.True(confirm.IsExist(),"The production confirmation template must match the real controller screenshot");
        using var factory=new BgiOnnxFactory(NullLogger<BgiOnnxFactory>.Instance,forceCpuOcr:true);
        using var ocr=new PaddleOcrService(factory,PaddleOcrService.PaddleOcrModelType.V5);
        using var titleRoi=new Mat(image.SrcMat,new Rect(0,0,image.Width,image.Height/2));
        var recognized=ocr.OcrResult(titleRoi).Regions.Select(r=>r.Text).ToArray();
        output.WriteLine(string.Join(" | ",recognized));
        var hasTitle=recognized.Any(text=>Bv.IsReviveFoodTitle(text,"复苏","使用道具复苏角色"));
        Assert.Equal(ReviveUiState.FoodPrompt,Bv.ClassifyReviveEvidence(confirm.IsExist(),hasTitle,false));
    }

    [UiRecoveryTheory("s17-cold-before-20261030.png", "s17-cold-after-20261030.png")]
    [InlineData("s17-cold-before-20261030.png", "C3950ADA7C21D0B7D70DC15E18885B0EC167FF278CF7773C2A0E17CF714224FD")]
    [InlineData("s17-cold-after-20261030.png", "5C1EC3E0F85D9CE7B668BF85ECED950F1AC8E56B58FE3AB85D134F211B8977A9")]
    public void ActualColdDefeatNeedsNoApplicationAndBlocksOverworldRecoveryUntilRevival(string file, string sha256)
    {
        Assert.True(BetterGenshinImpact.Helpers.ApplicationHostBootstrapGuard.IsProhibited);
        var bytes = File.ReadAllBytes(UiRecoveryFixtures.PathFor(file));
        Assert.Equal(sha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
        using var factory = new BgiOnnxFactory(NullLogger<BgiOnnxFactory>.Instance, forceCpuOcr: true);
        using var ocr = new PaddleOcrService(factory, PaddleOcrService.PaddleOcrModelType.V5);
        // 回放源只供本次离线帧的新鲜度检查，不冒充历史输入时刻。
        using var image = new ImageRegion(Cv2.ImDecode(bytes, ImreadModes.Color), 0, 0)
        { FrameStamp = new CaptureFrameSource().Next() };
        var detector = new ReviveUiDetector(RecognitionAssets.Get("AutoFight", "Confirm", image),
            ocr, "复苏", "使用道具复苏角色");
        var observed = detector.Observe(image);
        Assert.Equal(ReviveUiState.FullPartyDefeat, observed.State);
        Assert.True(observed.DefeatOverlay);
        Assert.True(observed.ButtonBounds.Y >= image.Height * 2 / 3);
        var snapshot = NativeUiDriver.Read(image, ocr: ocr, reviveDetector: detector);
        Assert.True(snapshot.Revive);
        Assert.True(snapshot.FullPartyDefeat);
        Assert.False(snapshot.MainReady);
        Assert.False(snapshot.MapReady);
        Assert.False(snapshot.Matches(UiTarget.Overworld));
        Assert.Null(System.Windows.Application.Current);
    }
}
