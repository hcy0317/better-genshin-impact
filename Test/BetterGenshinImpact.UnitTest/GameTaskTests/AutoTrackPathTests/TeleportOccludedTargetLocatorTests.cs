using System.Security.Cryptography;
using System.Text.Json;
using BetterGenshinImpact.GameTask.AutoTrackPath;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoTrackPathTests;

public class TeleportOccludedTargetLocatorTests
{
    [TeleportTargetReplayTheory]
    [InlineData("evidence-0072.json", 946.6974487304688, 543.7127685546875)]
    [InlineData("evidence-0115.json", 959.476806640625, 543.0841674804688)]
    public void RecordedPlayerOverlayStillRequiresVisibleWaypointOutline(string metadataFile, double x, double y)
    {
        using var frame = ReadFrame(metadataFile);
        using var template = ReadTemplate();
        Assert.True(TeleportOccludedTargetLocator.TryLocate(frame, template, x, y, 1, 8, 8.4, out var rect));
        Assert.True(MapTargetClickAdmission.Accepts(false, x, y, rect, []));
        Assert.InRange(rect.X + rect.Width / 2d, x - 6, x + 6);
        Assert.InRange(rect.Y + rect.Height / 2d, y - 6, y + 6);
    }

    [Fact]
    public void VisibleOutlineCanLocateAnIconUnderThePlayerMarker()
    {
        using var template = CreateWaypointTemplate();
        using var frame = CreateScene(template, true);
        Assert.True(TeleportOccludedTargetLocator.TryLocate(frame, template, 60, 60, 1, 8, 4, out var rect));
        Assert.True(MapTargetClickAdmission.Accepts(false, 60, 60, rect, []));
    }

    [Fact]
    public void PlayerMarkerAloneDoesNotAuthorizeGround()
    {
        using var template = CreateWaypointTemplate();
        using var frame = CreateScene(template, false);
        Assert.False(TeleportOccludedTargetLocator.TryLocate(frame, template, 60, 60, 1, 8, 4, out var rect));
        Assert.False(MapTargetClickAdmission.Accepts(false, 60, 60, rect, []));
    }

    [Fact]
    public void AnotherWhiteIconUnderTheMarkerIsNotAWaypoint()
    {
        using var template = CreateWaypointTemplate();
        using var other = new Mat(43, 30, MatType.CV_8UC1, new Scalar(25));
        Cv2.Circle(other, new Point(15, 21), 12, new Scalar(240), -1);
        Cv2.Circle(other, new Point(15, 21), 7, new Scalar(100), -1);
        using var frame = CreateScene(other, true);
        Assert.False(TeleportOccludedTargetLocator.TryLocate(frame, template, 60, 60, 1, 8, 4, out _));
    }

    [Fact]
    public void CompletelyOccludedIconCannotBeInferredFromProjection()
    {
        using var template = CreateWaypointTemplate();
        using var frame = CreateScene(template, true);
        Cv2.Rectangle(frame, new Rect(40, 34, 40, 53), new Scalar(255, 220, 20), -1);
        Assert.False(TeleportOccludedTargetLocator.TryLocate(frame, template, 60, 60, 1, 8, 4, out _));
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(2, 4)]
    [InlineData(8, 11)]
    public void UnregisteredOrUncertainProjectionCannotAuthorizePartialIcon(int anchors, double uncertainty)
    {
        using var template = CreateWaypointTemplate();
        using var frame = CreateScene(template, true);
        Assert.False(TeleportOccludedTargetLocator.TryLocate(frame, template, 60, 60, 1, anchors, uncertainty, out _));
    }

    [Fact]
    public void NearbyIconCannotMoveTheRequestedClickOntoIt()
    {
        using var template = CreateWaypointTemplate();
        using var frame = CreateScene(template, true);
        Assert.False(TeleportOccludedTargetLocator.TryLocate(frame, template, 90, 60, 1, 8, 4, out var rect));
        Assert.False(MapTargetClickAdmission.Accepts(false, 90, 60, rect, []));
    }

    [TeleportTargetReplayTheory]
    [InlineData("evidence-0070.json", 954.3729435847356, 539.6826019287109)]
    public void RecordedFrameWithTooLittleOutlineStillRejectsClick(string metadataFile, double x, double y)
    {
        using var frame = ReadFrame(metadataFile);
        using var template = ReadTemplate();
        Assert.False(TeleportOccludedTargetLocator.TryLocate(frame, template, x, y, 1, 26, 5, out var rect));
        Assert.False(MapTargetClickAdmission.Accepts(false, x, y, rect, []));
    }

    [TeleportTargetReplayTheory]
    [InlineData("evidence-0115.json", 959.476806640625, 543.0841674804688)]
    public void RemovingVisibleIconButKeepingRecordedPlayerPixelsRejectsClick(string metadataFile, double x, double y)
    {
        using var frame = ReadFrame(metadataFile);
        using var template = ReadTemplate();
        using var patch = new Mat(frame, new Rect(930, 510, 60, 70));
        using var hsv = new Mat();
        Cv2.CvtColor(patch, hsv, ColorConversionCodes.BGR2HSV);
        using var marker = new Mat();
        Cv2.InRange(hsv, new Scalar(80, 80, 100), new Scalar(110, 255, 255), marker);
        using var background = new Mat();
        Cv2.BitwiseNot(marker, background);
        patch.SetTo(new Scalar(65, 80, 90), background);
        Assert.False(TeleportOccludedTargetLocator.TryLocate(frame, template, x, y, 1, 8, 6.9, out var rect));
        Assert.False(MapTargetClickAdmission.Accepts(false, x, y, rect, []));
    }

    private static Mat CreateWaypointTemplate()
    {
        var template = new Mat(43, 30, MatType.CV_8UC1, new Scalar(25));
        Cv2.FillConvexPoly(template, [new(15, 2), new(27, 15), new(15, 40), new(3, 15)], new Scalar(240));
        Cv2.FillConvexPoly(template, [new(15, 8), new(22, 15), new(15, 23), new(8, 15)], new Scalar(100));
        return template;
    }

    private static Mat CreateScene(Mat template, bool includeIcon)
    {
        var frame = new Mat(120, 120, MatType.CV_8UC3, new Scalar(65, 80, 90));
        if (includeIcon)
        {
            using var colour = new Mat();
            Cv2.CvtColor(template, colour, ColorConversionCodes.GRAY2BGR);
            using var target = new Mat(frame, new Rect(45, 39, 30, 43));
            colour.CopyTo(target);
        }
        Cv2.FillConvexPoly(frame, [new(60, 47), new(85, 60), new(60, 74), new(65, 60)], new Scalar(255, 220, 20));
        return frame;
    }

    private static string Root => Environment.GetEnvironmentVariable("BGI_TELEPORT_TARGET_REPLAY_ROOT")!;

    private static Mat ReadTemplate() => Cv2.ImRead(Environment.GetEnvironmentVariable("BGI_TELEPORT_WAYPOINT_TEMPLATE")!, ImreadModes.Grayscale);

    private static Mat ReadFrame(string file)
    {
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, file)));
        var image = Path.Combine(Root, metadata.RootElement.GetProperty("ImageFile").GetString()!);
        using var stream = File.OpenRead(image);
        Assert.Equal(metadata.RootElement.GetProperty("ImageContentSha256").GetString(), Convert.ToHexString(SHA256.HashData(stream)));
        var frame = Cv2.ImRead(image);
        Assert.False(frame.Empty());
        return frame;
    }

    private sealed class TeleportTargetReplayTheoryAttribute : TheoryAttribute
    {
        public TeleportTargetReplayTheoryAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BGI_TELEPORT_TARGET_REPLAY_ROOT")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BGI_TELEPORT_WAYPOINT_TEMPLATE")))
                Skip = "Set BGI_TELEPORT_TARGET_REPLAY_ROOT and BGI_TELEPORT_WAYPOINT_TEMPLATE to the private original frames and existing template.";
        }
    }
}
