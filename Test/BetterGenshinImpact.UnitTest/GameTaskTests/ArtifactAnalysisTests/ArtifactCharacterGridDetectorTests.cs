using BetterGenshinImpact.GameTask.CharacterDevelopment;
using BetterGenshinImpact.GameTask.ArtifactAnalysis;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.ArtifactAnalysisTests;

public class ArtifactCharacterGridDetectorTests
{
    [Fact]
    public void SelectedCardWithChangedBottomColorIsRestoredFromTheCompleteGrid()
    {
        using var grid = new Mat(new Size(681, 917), MatType.CV_8UC3, Scalar.Black);
        using var hsvPixel = new Mat(new Size(1, 1), MatType.CV_8UC3, new Scalar(20, 30, 235));
        using var bgrPixel = hsvPixel.CvtColor(ColorConversionCodes.HSV2BGR);
        var beige = bgrPixel.At<Vec3b>(0, 0);

        for (var row = 0; row < 5; row++)
        for (var column = 0; column < 5; column++)
        {
            var x = 23 + column * 129;
            var y = 52 + row * 156;
            Cv2.Rectangle(grid, new Rect(x, y, 115, 140), new Scalar(30, 80, 140), -1);
            for (var line = 10; line < 105; line += 12)
            {
                Cv2.Line(
                    grid,
                    new Point(x + line, y + 5),
                    new Point(x + line, y + 100),
                    new Scalar(180, 20, 20),
                    2);
            }
            if (row == 2 && column == 4) continue;
            Cv2.Rectangle(
                grid,
                new Rect(x + 2, y + 110, 112, 30),
                new Scalar(beige.Item0, beige.Item1, beige.Item2),
                -1);
        }

        for (var column = 0; column < 5; column++)
        {
            var x = 23 + column * 129;
            var y = 52 + 5 * 156;
            Cv2.Rectangle(grid, new Rect(x, y, 115, 85), new Scalar(30, 80, 140), -1);
            for (var line = 10; line < 105; line += 12)
            {
                Cv2.Line(
                    grid,
                    new Point(x + line, y + 5),
                    new Point(x + line, y + 75),
                    new Scalar(180, 20, 20),
                    2);
            }
        }

        var cards = CharacterSelectionHelper.DetectCharacterCards(
            grid, 1, out _, out _);

        Assert.Equal(25, cards.Count);
        var pageRows = ArtifactCharacterPageDetector.Detect(grid, 1);
        Assert.Equal(6, pageRows.Count);
        Assert.Equal(5, pageRows[5].Cards.Count);
    }
}
