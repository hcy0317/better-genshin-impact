using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Model.Area;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class BigMapRecognitionTests
{
    [Fact]
    public void SettingsIconWithoutMapCloseControlIsNotABigMap()
    {
        using var image = new ImageRegion(new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black), 0, 0);
        PaintTemplate(image, "MapSettingsButton.png", 25, 990);

        Assert.False(Bv.IsInBigMapUi(image));
    }

    [Theory]
    [InlineData("MapSettingsButton.png", 25, 990)]
    [InlineData("MapScaleButton.png", 30, 440)]
    public void IndependentMapControlsStillRecognizeTheMap(string name, int x, int y)
    {
        using var image = new ImageRegion(new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black), 0, 0);
        PaintTemplate(image, name, x, y);
        PaintTemplate(image, "MapCloseButton.png", 1813, 19);

        Assert.True(Bv.IsInBigMapUi(image));
    }

    [Fact]
    public void GenericCloseControlAloneIsNotABigMap()
    {
        using var image = new ImageRegion(new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black), 0, 0);
        PaintTemplate(image, "MapCloseButton.png", 1813, 19);

        Assert.False(Bv.IsInBigMapUi(image));
    }

    private static void PaintTemplate(ImageRegion image, string name, int x, int y)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "BetterGenshinImpact.sln"))) root = root.Parent;
        Assert.NotNull(root);
        using var template = Cv2.ImRead(Path.Combine(root!.FullName,
            "BetterGenshinImpact", "GameTask", "QuickTeleport", "Assets", "1920x1080", name));
        Assert.False(template.Empty());
        using var target = new Mat(image.SrcMat, new Rect(x, y, template.Width, template.Height));
        template.CopyTo(target);
    }
}
