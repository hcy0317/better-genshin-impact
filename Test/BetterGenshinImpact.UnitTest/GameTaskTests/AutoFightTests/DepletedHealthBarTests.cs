using BetterGenshinImpact.GameTask.AutoFight;
using OpenCvSharp;
using Xunit.Abstractions;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class DepletedHealthBarTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(11)]
    [InlineData(14)]
    public void RedRemainderWithAdjacentBoundedDarkTrackReachesHealthBarSelection(int redWidth)
    {
        using var source = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(110, 65, 33));
        var red = new EnemySeekVisual(741, 397, redWidth, 6, redWidth * 6);
        Cv2.Rectangle(source, new Rect(red.X, red.Y, 70, 6), new Scalar(70, 42, 40), -1);
        Cv2.Rectangle(source, new Rect(red.X, red.Y, red.Width, red.Height), new Scalar(90, 90, 255), -1);
        AssertHealthBar(source, red);

        // 指定现场帧时同时回放原始像素；常规CI只需要上面的可移植同色几何样本。
        var path = Environment.GetEnvironmentVariable("BGI_HEALTHBAR_REPLAY_FILE");
        if (!string.IsNullOrEmpty(path))
        {
            using var recorded = Cv2.ImRead(path);
            Assert.False(recorded.Empty());
            AssertHealthBar(recorded, redWidth == 14 ? new(741, 147, 14, 6, 79) : new(1511, 397, 11, 6, 62));
            output.WriteLine($"Recorded original frame verified: {Path.GetFileName(path)}, redWidth={redWidth}");
            if (redWidth == 14) ReplayRecordedDirectory(Path.GetDirectoryName(path)!);
        }
    }

    private void ReplayRecordedDirectory(string directory)
    {
        foreach (var path in Directory.EnumerateFiles(directory, "evidence-*.png").Order())
        {
            using var source = Cv2.ImRead(path);
            List<EnemySeekVisual> Run(bool diagnostic)
            {
                using var mask = AutoFightSeek.CreateSeekColorMask(source, new Scalar(255, 90, 90), null);
                using var labels = new Mat();
                using var stats = new Mat();
                using var centers = new Mat();
                var count = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centers);
                var accepted = new List<EnemySeekVisual>();
                var trace = diagnostic ? new SeekRecognitionDiagnostics() : null;
                for (var i = 1; i < count; i++)
                {
                    var visual = new EnemySeekVisual(stats.At<int>(i, 0), stats.At<int>(i, 1), stats.At<int>(i, 2),
                        stats.At<int>(i, 3), stats.At<int>(i, 4));
                    if (AutoFightSeek.ClassifySeekVisual(mask, source, visual, source.Width, source.Height, trace) is { } found)
                        accepted.Add(found);
                }
                return accepted;
            }
            var accepted = Run(false);
            Assert.Equal(accepted, Run(true));
            var added = accepted.Where(item => item.HealthBarTrackWidth > 0).ToArray();
            output.WriteLine($"REPLAY {Path.GetFileName(path)} added={string.Join(";", added.Select(item => $"{item.X},{item.Y},red={item.Width},track={item.HealthBarTrackWidth}"))}");
            if (Path.GetFileName(path) == "evidence-0011.png")
            {
                Assert.Equal(2, added.Length);
                Assert.Contains(added, item => item.X == 741 && item.Y == 147);
                Assert.Contains(added, item => item.X == 1511 && item.Y == 397);
            }
            var elapsed = new List<double>();
            for (var i = 0; i < 12; i++)
            {
                var start = System.Diagnostics.Stopwatch.GetTimestamp();
                Run(false);
                elapsed.Add(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            elapsed.Sort();
            output.WriteLine($"REPLAY_PERF {Path.GetFileName(path)} warm-p95-ms={elapsed[11]:F2} includes-mask-components-classify=true");
            Assert.InRange(elapsed[11], 0, 150);
        }
    }

    private static void AssertHealthBar(Mat source, EnemySeekVisual red)
    {
        using var mask = AutoFightSeek.CreateSeekColorMask(source, new Scalar(255, 90, 90), null);
        var actual = AutoFightSeek.ClassifySeekVisual(mask, source, red, source.Width, source.Height);
        Assert.NotNull(actual);
        Assert.Equal(red.Width, actual.Value.Width);
        Assert.InRange(actual.Value.HealthBarTrackWidth, 60, 75);
        Assert.False(AutoFightSeek.IsFixedTopEliteHealthBar(actual.Value, source.Width, source.Height));
        var selected = AutoFightSeek.SelectSeekDecision([actual.Value], source.Width, source.Height, false);
        Assert.Equal(SeekCueKind.HealthBar, selected.Cue);
    }

    [Theory]
    [InlineData("no-track")]
    [InlineData("black-background")]
    [InlineData("no-top-edge")]
    [InlineData("no-bottom-edge")]
    [InlineData("gap")]
    [InlineData("vertical-offset")]
    [InlineData("bright-track")]
    [InlineData("unbounded-track")]
    [InlineData("tiny-red")]
    [InlineData("no-red")]
    [InlineData("bottom-hud")]
    [InlineData("party-hud")]
    [InlineData("right-edge")]
    public void DarkBackgroundAndInvalidTracksCannotCreateEnemyTargets(string kind)
    {
        using var source = new Mat(1080, 1920, MatType.CV_8UC3,
            kind == "black-background" ? Scalar.Black : new Scalar(110, 65, 33));
        var x = kind == "party-hud" ? 1800 : kind == "right-edge" ? 1910 : 741;
        var y = kind == "bottom-hud" ? 1005 : 397;
        var width = kind == "tiny-red" ? 2 : 11;
        var red = new EnemySeekVisual(x, y, width, 6, width * 6);
        var track = new Rect(x, y, kind == "unbounded-track" ? 300 : 70, 6);
        if (kind == "no-top-edge") track = new Rect(x, y - 20, 70, 26);
        if (kind == "no-bottom-edge") track = new Rect(x, y, 70, 26);
        if (kind == "gap") track.X += 16;
        if (kind == "vertical-offset") track.Y += 5;
        if (kind is not ("no-track" or "black-background"))
            Cv2.Rectangle(source, track, kind == "bright-track" ? new Scalar(160, 160, 160) : new Scalar(70, 42, 40), -1);
        if (kind != "no-red") Cv2.Rectangle(source, new Rect(x, y, width, 6), new Scalar(90, 90, 255), -1);
        using var mask = AutoFightSeek.CreateSeekColorMask(source, new Scalar(255, 90, 90), null);
        Assert.Null(AutoFightSeek.ClassifySeekVisual(mask, source, red, 1920, 1080));
    }

    [Theory]
    [InlineData(720)]
    [InlineData(1080)]
    [InlineData(1440)]
    public void TrackScalesWithTheSourceAndDiagnosticsDoNotChangeClassification(int height)
    {
        var scale = height / 1080d;
        using var source = new Mat(height, (int)(height * 16d / 9), MatType.CV_8UC3, new Scalar(110, 65, 33));
        var red = new EnemySeekVisual((int)(741 * scale), (int)(397 * scale), (int)Math.Round(11 * scale),
            (int)Math.Round(6 * scale), 0);
        red = red with { Area = red.Width * red.Height };
        Cv2.Rectangle(source, new Rect(red.X, red.Y, (int)(70 * scale), red.Height), new Scalar(70, 42, 40), -1);
        Cv2.Rectangle(source, new Rect(red.X, red.Y, red.Width, red.Height), new Scalar(90, 90, 255), -1);
        using var mask = AutoFightSeek.CreateSeekColorMask(source, new Scalar(255, 90, 90), null);
        var plain = AutoFightSeek.ClassifySeekVisual(mask, source, red, source.Width, source.Height);
        var diagnostics = new SeekRecognitionDiagnostics();
        Assert.Equal(plain, AutoFightSeek.ClassifySeekVisual(mask, source, red, source.Width, source.Height, diagnostics));
        Assert.NotNull(plain);
        Assert.Equal(1, diagnostics.DarkTrackAccepted);
        Assert.Equal(1, diagnostics.Accepted);
    }

    [Fact]
    public void NormalRedBarsKeepTheirOriginalGeometryWithoutDarkTrack()
    {
        using var source = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(110, 65, 33));
        var red = new EnemySeekVisual(741, 397, 65, 7, 455);
        Cv2.Rectangle(source, new Rect(red.X, red.Y, red.Width, red.Height), new Scalar(90, 90, 255), -1);
        using var mask = AutoFightSeek.CreateSeekColorMask(source, new Scalar(255, 90, 90), null);
        Assert.Equal(red, AutoFightSeek.ClassifySeekVisual(mask, source, red, 1920, 1080));
    }
}
