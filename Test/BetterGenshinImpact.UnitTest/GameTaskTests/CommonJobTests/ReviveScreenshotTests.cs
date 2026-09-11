using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.OCR.Paddle;
using BetterGenshinImpact.Core.Recognition.ONNX;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Model.Area;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;
using Xunit.Abstractions;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class ReviveScreenshotTests(ITestOutputHelper output)
{
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
}
