using System.Security.Cryptography;
using System.Text.Json;
using BetterGenshinImpact.Core.Recognition.OpenCv;
using BetterGenshinImpact.GameTask.AutoTrackPath;
using OpenCvSharp;
using Xunit.Abstractions;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoTrackPathTests;

public class TeleportTargetIconLocatorTests(ITestOutputHelper output)
{
    [RecordedTargetTheory]
    [InlineData("5993", 292717, 1009.6934814453125, 530.70458984375,
        "28549CFCC8D2EC27422503C21D459B4068118E9BFFA7233EFFC604668552C4D9")]
    [InlineData("5994", 292790, 982.8910522460938, 537.4142456054688,
        "D99FB5DA241CFDE9320D2C60C7F6F75A43C6827F201AE1AD709FC86D6E264944")]
    [InlineData("5995", 292853, 982.8910522460938, 537.4142456054688,
        "D6DD80F995B74264DF09FEEA9349F24A260988D15605AB9A73447D22116F6525")]
    public void CompleteRecordedWaypointIsDetectedAtTheRequestedTarget(
        string number, int sourceSequence, double x, double y, string expectedHash)
    {
        using var frame = ReadFrame(number, sourceSequence, expectedHash);
        using var template = ReadTemplate();
        using var grey = new Mat();
        Cv2.CvtColor(frame, grey, ColorConversionCodes.BGR2GRAY);

        // 这是生产整图发现入口，保留原0.65阈值及14px对应距离；不能借用右侧另一图标。
        var icons = TeleportTargetIconLocator.FindVisibleWaypoints(grey, template, 0.65);
        var targetIcons = icons.Where(rect => TargetDistance(rect, x, y) <= 14).ToArray();
        var radius = 14;
        var searchRect = new Rect((int)Math.Round(x - template.Width / 2d) - radius,
            (int)Math.Round(y - template.Height / 2d) - radius,
            template.Width + radius * 2, template.Height + radius * 2);
        using var local = new Mat(grey, searchRect);
        using var response = new Mat();
        Cv2.MatchTemplate(local, template, response, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(response, out double _, out double fullTemplateScore);
        output.WriteLine($"frame={number} template={template.Width}x{template.Height} fullTemplateLocalBest={fullTemplateScore:F6} icons={icons.Count} targetIcons={targetIcons.Length}");

        WriteMaskedDiagnostics(number, grey, template, local, x, y);

        Assert.NotEmpty(targetIcons);
        Assert.Contains(targetIcons, rect => MapTargetClickAdmission.Accepts(false, x, y, rect, []));
    }

    [RecordedTargetTheory]
    [InlineData("5993", 292717, 1009.6934814453125, 530.70458984375,
        "28549CFCC8D2EC27422503C21D459B4068118E9BFFA7233EFFC604668552C4D9")]
    [InlineData("5994", 292790, 982.8910522460938, 537.4142456054688,
        "D99FB5DA241CFDE9320D2C60C7F6F75A43C6827F201AE1AD709FC86D6E264944")]
    [InlineData("5995", 292853, 982.8910522460938, 537.4142456054688,
        "D6DD80F995B74264DF09FEEA9349F24A260988D15605AB9A73447D22116F6525")]
    public void ProbeUniformTemplateScaleAtTheRecordedTarget(
        string number, int sourceSequence, double x, double y, string expectedHash)
    {
        using var frame = ReadFrame(number, sourceSequence, expectedHash);
        using var template = ReadTemplate();
        using var grey = new Mat();
        Cv2.CvtColor(frame, grey, ColorConversionCodes.BGR2GRAY);
        // 只改变模板的等比尺度。固定原图、原目标、14px对应范围、插值方法、mask规则和0.85要求。
        var searchRect = new Rect((int)Math.Round(x) - 40, (int)Math.Round(y) - 40, 80, 80);
        using var local = new Mat(grey, searchRect);
        var bestScore = double.NegativeInfinity;
        var bestWidth = 0;
        var bestRect = new Rect();
        var seamAdmittedAtAnyScale = false;
        for (var width = 26; width <= 34; width++)
        {
            var height = (int)Math.Round(template.Height * width / (double)template.Width);
            using var scaled = new Mat();
            Cv2.Resize(template, scaled, new Size(width, height), 0, 0, InterpolationFlags.Linear);
            using var mask = new Mat();
            Cv2.Threshold(scaled, mask, 140, 255, ThresholdTypes.Binary);
            var border = Math.Max(1, (int)Math.Round(2d * scaled.Width / 30d));
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(border * 2 + 1, border * 2 + 1));
            Cv2.Dilate(mask, mask, kernel);
            using var response = new Mat();
            Cv2.MatchTemplate(local, scaled, response, TemplateMatchModes.CCoeffNormed, mask);
            var localBest = double.NegativeInfinity;
            var localBestRect = new Rect();
            for (var row = 0; row < response.Height; row++)
            for (var col = 0; col < response.Width; col++)
            {
                var candidate = new Rect(searchRect.X + col, searchRect.Y + row, width, height);
                if (TargetDistance(candidate, x, y) > 14) continue;
                var value = response.At<float>(row, col);
                if (!float.IsFinite(value) || value <= localBest) continue;
                localBest = value;
                localBestRect = candidate;
            }
            var seamIcons = TeleportTargetIconLocator.FindVisibleWaypoints(local, scaled, 0.65)
                .Select(rect => new Rect(searchRect.X + rect.X, searchRect.Y + rect.Y, rect.Width, rect.Height))
                .Where(rect => TargetDistance(rect, x, y) <= 14).ToArray();
            var admitted = seamIcons.Any(rect => MapTargetClickAdmission.Accepts(false, x, y, rect, []));
            seamAdmittedAtAnyScale |= admitted && localBest >= 0.85 &&
                MapTargetClickAdmission.Accepts(false, x, y, localBestRect, []);
            output.WriteLine($"SCALE_PROBE frame={number} template={width}x{height} scale={width / 30d:F6} maskedLocalBest={localBest:F6} rect={localBestRect} seamTargetCount={seamIcons.Length} admitted={admitted} maskPixels={Cv2.CountNonZero(mask)}");
            if (localBest <= bestScore) continue;
            bestScore = localBest;
            bestWidth = width;
            bestRect = localBestRect;
        }
        output.WriteLine($"SCALE_PROBE_RESULT frame={number} bestWidth={bestWidth} bestScore={bestScore:F6} rect={bestRect} strictMaskAndSeamAdmitted={seamAdmittedAtAnyScale}");
        // 这只检验尺度假设；原三帧生产回归仍独立保留，probe通过不能算生产修复已绿。
        Assert.True(seamAdmittedAtAnyScale,
            $"Uniform-scale hypothesis falsified in 26..34px: best={bestScore:F6}, width={bestWidth}; required=0.85.");
    }

    public static IEnumerable<object[]> RecordedNegativeCases()
    {
        object[][] frames =
        [
            ["5993", 292717, 1009.6934814453125, 530.70458984375,
                "28549CFCC8D2EC27422503C21D459B4068118E9BFFA7233EFFC604668552C4D9"],
            ["5994", 292790, 982.8910522460938, 537.4142456054688,
                "D99FB5DA241CFDE9320D2C60C7F6F75A43C6827F201AE1AD709FC86D6E264944"],
            ["5995", 292853, 982.8910522460938, 537.4142456054688,
                "D6DD80F995B74264DF09FEEA9349F24A260988D15605AB9A73447D22116F6525"]
        ];
        foreach (var frame in frames)
        foreach (var variant in new[] { "erase-target", "neighbor-only", "player-only", "wrong-glyph", "fully-occluded" })
            yield return frame.Append(variant).ToArray();
    }

    [RecordedTargetTheory]
    [MemberData(nameof(RecordedNegativeCases))]
    public void RecordedTargetRemainsRejectedWithoutItsWaypointGlyph(
        string number, int sourceSequence, double x, double y, string expectedHash, string variant)
    {
        using var original = ReadFrame(number, sourceSequence, expectedHash);
        using var template = ReadTemplate();
        using var grey = new Mat();
        Cv2.CvtColor(original, grey, ColorConversionCodes.BGR2GRAY);
        var originalIcons = TeleportTargetIconLocator.FindVisibleWaypoints(grey, template, 0.65);
        Assert.Contains(originalIcons, rect => TargetDistance(rect, x, y) <= 14 &&
            MapTargetClickAdmission.Accepts(false, x, y, rect, []));

        // 固定标注区域覆盖原图目标及外缘；不借检测结果决定擦除范围。
        var targetArea = number == "5993" ? new Rect(990, 503, 40, 54) : new Rect(961, 511, 40, 54);
        var neighborArea = number == "5993" ? new Rect(1052, 503, 52, 54) : new Rect(1023, 510, 52, 54);
        var playerArea = number == "5993" ? new Rect(936, 516, 45, 48) : new Rect(906, 522, 45, 48);
        using var background = new Mat(original, new Rect(targetArea.X, targetArea.Y + 120,
            targetArea.Width, targetArea.Height));
        using var altered = original.Clone();
        switch (variant)
        {
            case "erase-target":
                using (var destination = new Mat(altered, targetArea))
                    background.CopyTo(destination);
                AssertOutsideAreaUnchanged(original, altered, targetArea);
                break;
            case "neighbor-only":
                altered.SetTo(Cv2.Mean(background));
                CopyRecordedPatch(original, altered, neighborArea, neighborArea);
                Assert.False(MapTargetClickAdmission.Accepts(false, x, y, neighborArea, [neighborArea]));
                break;
            case "player-only":
                altered.SetTo(Cv2.Mean(background));
                CopyRecordedPatch(original, altered, playerArea,
                    new Rect((int)Math.Round(x - playerArea.Width / 2d),
                        (int)Math.Round(y - playerArea.Height / 2d), playerArea.Width, playerArea.Height));
                break;
            case "wrong-glyph":
                altered.SetTo(Cv2.Mean(background));
                CopyRecordedPatch(original, altered, neighborArea,
                    new Rect((int)Math.Round(x - neighborArea.Width / 2d),
                        (int)Math.Round(y - neighborArea.Height / 2d), neighborArea.Width, neighborArea.Height));
                break;
            case "fully-occluded":
                using (var occlusion = new Mat(altered, targetArea))
                    occlusion.SetTo(new Scalar(255, 220, 20));
                AssertOutsideAreaUnchanged(original, altered, targetArea);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(variant));
        }

        Cv2.CvtColor(altered, grey, ColorConversionCodes.BGR2GRAY);
        var icons = TeleportTargetIconLocator.FindVisibleWaypoints(grey, template, 0.65);
        var targetIcons = icons.Where(rect => TargetDistance(rect, x, y) <= 14).ToArray();
        output.WriteLine($"NEGATIVE frame={number} variant={variant} icons={icons.Count} targetIcons={targetIcons.Length} targetRects=[{string.Join(",", targetIcons)}]");
        Assert.Empty(targetIcons);
        Assert.DoesNotContain(icons, rect => MapTargetClickAdmission.Accepts(false, x, y, rect, []));
    }

    private static void CopyRecordedPatch(Mat original, Mat altered, Rect sourceArea, Rect destinationArea)
    {
        using var source = new Mat(original, sourceArea);
        using var destination = new Mat(altered, destinationArea);
        source.CopyTo(destination);
        Assert.Equal(0d, Cv2.Norm(source, destination, NormTypes.INF));
    }

    private static void AssertOutsideAreaUnchanged(Mat original, Mat altered, Rect area)
    {
        using var outside = new Mat(original.Size(), MatType.CV_8UC1, Scalar.All(255));
        using (var changed = new Mat(outside, area))
            changed.SetTo(Scalar.All(0));
        Assert.Equal(0d, Cv2.Norm(original, altered, NormTypes.INF, outside));
    }

    private void WriteMaskedDiagnostics(string number, Mat grey, Mat template, Mat local, double x, double y)
    {
        using var mask = new Mat();
        Cv2.Threshold(template, mask, 140, 255, ThresholdTypes.Binary);
        var seedPixels = Cv2.CountNonZero(mask);
        var border = Math.Max(1, (int)Math.Round(2d * template.Width / 30d));
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(border * 2 + 1, border * 2 + 1));
        Cv2.Dilate(mask, mask, kernel);
        var edgePixels = 0;
        for (var col = 0; col < mask.Width; col++)
        {
            if (mask.At<byte>(0, col) != 0) edgePixels++;
            if (mask.At<byte>(mask.Height - 1, col) != 0) edgePixels++;
        }
        for (var row = 1; row < mask.Height - 1; row++)
        {
            if (mask.At<byte>(row, 0) != 0) edgePixels++;
            if (mask.At<byte>(row, mask.Width - 1) != 0) edgePixels++;
        }
        using var response = new Mat();
        Cv2.MatchTemplate(local, template, response, TemplateMatchModes.CCoeffNormed, mask);
        var finiteLocalBest = double.NegativeInfinity;
        var finiteLocalLocation = new Point();
        for (var row = 0; row < response.Height; row++)
        for (var col = 0; col < response.Width; col++)
        {
            var value = response.At<float>(row, col);
            if (!float.IsFinite(value) || value <= finiteLocalBest) continue;
            finiteLocalBest = value;
            finiteLocalLocation = new Point(col, row);
        }
        var projectedScore = response.At<float>(14, 14);
        var matches = MatchTemplateHelper.FindMatches(grey, template, TemplateMatchModes.CCoeffNormed, mask, 0.85, -1);
        var finite = matches.Where(match => double.IsFinite(match.Score)).ToArray();
        var near = finite.Where(match => TargetDistance(new Rect(match.Location.X, match.Location.Y,
            template.Width, template.Height), x, y) <= 14).Select(match => $"{match.Location}:{match.Score:F6}");
        output.WriteLine($"frame={number} maskSeedPixels={seedPixels}/{template.Total()} maskPixels={Cv2.CountNonZero(mask)} maskEdgePixels={edgePixels} templateCorners={template.At<byte>(0, 0)},{template.At<byte>(0, template.Width - 1)},{template.At<byte>(template.Height - 1, 0)},{template.At<byte>(template.Height - 1, template.Width - 1)}");
        output.WriteLine($"frame={number} maskedLocalBest={finiteLocalBest:F6} localBestLocation={finiteLocalLocation} projectedScore={projectedScore:F6} returnedCount={matches.Count} finiteCount={finite.Length} cap={grey.Width * grey.Height / template.Width / template.Height} nearTargetScores=[{string.Join(",", near)}]");
    }

    private static double TargetDistance(Rect rect, double x, double y) =>
        Math.Sqrt(Math.Pow(rect.X + rect.Width / 2d - x, 2) + Math.Pow(rect.Y + rect.Height / 2d - y, 2));

    private static Mat ReadFrame(string number, int sourceSequence, string expectedHash)
    {
        var root = Environment.GetEnvironmentVariable("BGI_TELEPORT_TARGET_REPLAY_ROOT")!;
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, $"evidence-{number}.json")));
        var identity = metadata.RootElement;
        Assert.Equal("600adc55bd3d4c13942fcad2e19fec82", identity.GetProperty("UiRootId").GetString());
        Assert.Equal("09dfaf56-4d9c-48ae-a58a-8b8440222560", identity.GetProperty("Source").GetProperty("SessionId").GetString());
        Assert.Equal(sourceSequence, identity.GetProperty("Source").GetProperty("Sequence").GetInt32());
        Assert.Contains("targetId=228", identity.GetProperty("Detail").GetString());
        Assert.Contains("zoom=1", identity.GetProperty("Detail").GetString());
        Assert.Equal(expectedHash, identity.GetProperty("ImageContentSha256").GetString());
        var image = Path.Combine(root, identity.GetProperty("ImageFile").GetString()!);
        using (var stream = File.OpenRead(image))
            Assert.Equal(expectedHash, Convert.ToHexString(SHA256.HashData(stream)));
        var frame = Cv2.ImRead(image, ImreadModes.Color);
        Assert.False(frame.Empty());
        Assert.Equal(new Size(1920, 1080), frame.Size());
        return frame;
    }

    private static Mat ReadTemplate()
    {
        var path = Environment.GetEnvironmentVariable("BGI_TELEPORT_WAYPOINT_TEMPLATE")!;
        using (var stream = File.OpenRead(path))
            Assert.Equal("92D82990FDDA8ED698E099C9FF988F6034A845F4B186E27A145F5F9973B3E8AA",
                Convert.ToHexString(SHA256.HashData(stream)));
        using var colour = Cv2.ImRead(path, ImreadModes.Color);
        var grey = new Mat();
        Cv2.CvtColor(colour, grey, ColorConversionCodes.BGR2GRAY);
        return grey;
    }

    private sealed class RecordedTargetTheoryAttribute : TheoryAttribute
    {
        public RecordedTargetTheoryAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BGI_TELEPORT_TARGET_REPLAY_ROOT")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BGI_TELEPORT_WAYPOINT_TEMPLATE")))
                Skip = "Set BGI_TELEPORT_TARGET_REPLAY_ROOT and BGI_TELEPORT_WAYPOINT_TEMPLATE to run original-frame regression.";
        }
    }
}
