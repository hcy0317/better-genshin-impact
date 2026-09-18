using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.Model.Area;
using Newtonsoft.Json;
using OpenCvSharp;
using Xunit.Abstractions;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class RecordedSeekDiagnosticsTests(ITestOutputHelper output)
{
    [OfflineNativeDecisionFact]
    public void ReportRealRecordedCandidatesWithoutSendingInputOrChangingThresholds()
    {
        var directory = Environment.GetEnvironmentVariable("BGI_SEEK_DIAGNOSTIC_FRAMES");
        Assert.True(Directory.Exists(directory), "显式提供已保存的截图目录，不采集桌面");
        foreach (var file in new[] { "evidence-0009.png", "evidence-0010.png", "evidence-0011.png" })
        {
            using var pixels = Cv2.ImRead(Path.Combine(directory!, file));
            Assert.False(pixels.Empty());
            using var frame = new ImageRegion(pixels.Clone(), 0, 0);
            AutoFightSeek.ResetSeekState();
            var baseline = AutoFightSeek.RecognizeSeekDecision(frame, new Scalar(255, 90, 90), null,
                out _, out _, saveDiagnostics: false);
            AutoFightSeek.ResetSeekState();
            var read = AvatarRecognition.ReadPassiveTargetEvidence(frame);
            var selected = read.Decision;
            Assert.Equal(baseline, selected);
            Assert.Same(read.Diagnostics, AvatarRecognition.ReadPassiveTargetEvidence(frame).Diagnostics);
            using var mask = AutoFightSeek.CreateSeekColorMask(pixels, new Scalar(255, 90, 90), null);
            using var labels = new Mat();
            using var stats = new Mat();
            using var centroids = new Mat();
            var count = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids,
                PixelConnectivity.Connectivity4, MatType.CV_32S);
            var candidates = new List<object>();
            for (var i = 1; i < count; i++)
            {
                var visual = new EnemySeekVisual(stats.At<int>(i, 0), stats.At<int>(i, 1),
                    stats.At<int>(i, 2), stats.At<int>(i, 3), stats.At<int>(i, 4));
                if (visual.X < 200 || visual.Y < 100 || visual.Y > 600 ||
                    visual.Height is < 2 or > 14 || visual.Width is < 8 or > 100) continue;
                candidates.Add(new
                {
                    visual.X, visual.Y, visual.Width, visual.Height, visual.Area,
                    HudExcluded = AutoFightSeek.IsPlayerHudHealthBar(visual, pixels.Width, pixels.Height),
                    BarFeature = AutoFightSeek.MatchesHealthBarFeature(mask, pixels, visual),
                    IndicatorGeometry = AutoFightSeek.IsDirectionIndicatorGeometry(visual, pixels.Width, pixels.Height),
                    Accepted = AutoFightSeek.ClassifySeekVisual(mask, pixels, visual, pixels.Width, pixels.Height)
                });
            }
            output.WriteLine(JsonConvert.SerializeObject(new { file, RawComponents = count - 1, selected, Diagnostics = read.Diagnostics, candidates }));
            Assert.NotEmpty(candidates);
            if (file == "evidence-0011.png")
            {
                Assert.Null(selected.Visual);
                Assert.Contains(read.Diagnostics.NarrowBarSamples, sample => sample.X == 741 && sample.Width == 14 && sample.MinimumWidth == 24);
                Assert.Contains(read.Diagnostics.NarrowBarSamples, sample => sample.X == 1511 && sample.Width == 11 && sample.MinimumWidth == 24);
            }
        }
    }
}
