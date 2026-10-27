using System;
using System.IO;
using System.Linq;
using BetterGenshinImpact.GameTask.AutoFight;
using OpenCvSharp;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public partial class CombatComponentHealthBarTests
{
    [Fact]
    public void WhiteAndGoldComponentFillRequiresTheSameFrameDownwardMarker()
    {
        using var frame = ComponentFrame();
        var bar = Assert.Single(CombatComponentHealthBarReader.Read(frame));
        Assert.Equal(new Rect(920, 450, 80, 6), bar);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("upward")]
    [InlineData("hud")]
    [InlineData("no-bar")]
    [InlineData("unrelated-bar")]
    public void BrightEffectsAndHudCannotAcquireComponentTargetIdentity(string change)
    {
        using var frame = ComponentFrame(change);
        Assert.Empty(CombatComponentHealthBarReader.Read(frame));
    }

    [RecordedConstructFact]
    public void RecordedHiddenConstructComponentPublishesItsActualRemainingFill()
    {
        var root = Environment.GetEnvironmentVariable("BGI_CONSTRUCT_EVIDENCE_ROOT")!;
        using var frame = Cv2.ImRead(Path.Combine(root, "evidence-5155.png"));
        Assert.False(frame.Empty());
        var bars = CombatComponentHealthBarReader.Read(frame);
        Assert.Contains(bars, bar => bar.X is >= 1035 and <= 1042 && bar.Y is >= 531 and <= 535 &&
            bar.Width is >= 34 and <= 44 && bar.Height is >= 4 and <= 7);
        // This frame's red arrow is directional evidence, never a component health bar.
        using var arrow = Cv2.ImRead(Path.Combine(root, "evidence-5206.png"));
        Assert.Empty(CombatComponentHealthBarReader.Read(arrow));
    }

    private static Mat ComponentFrame(string change = "")
    {
        var frame = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(45, 58, 70));
        var x = change == "hud" ? 1770 : 960;
        if (change != "missing")
        {
            var triangle = change == "upward"
                ? new[] { new Point(x, 405), new Point(x - 20, 432), new Point(x + 20, 432) }
                : new[] { new Point(x - 20, 405), new Point(x + 20, 405), new Point(x, 432) };
            Cv2.FillConvexPoly(frame, triangle, new Scalar(245, 205, 105));
        }
        if (change != "no-bar")
        {
            var left = x - 40 + (change == "unrelated-bar" ? 150 : 0);
            Cv2.Rectangle(frame, new Rect(left, 450, 80, 6), new Scalar(126, 209, 255), -1);
            Cv2.Rectangle(frame, new Rect(left, 450, 12, 6), Scalar.White, -1);
        }
        return frame;
    }

    private sealed class RecordedConstructFactAttribute : FactAttribute
    {
        public RecordedConstructFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BGI_CONSTRUCT_EVIDENCE_ROOT")))
                Skip = "Requires explicitly supplied original construct combat evidence; missing pixels are not a pass.";
        }
    }
}
