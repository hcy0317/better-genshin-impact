using BetterGenshinImpact.GameTask.AutoPathing.Handler;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.GameTask;
using Fischless.GameCapture;
using Microsoft.Extensions.Time.Testing;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public partial class CombatNativeAdapterReplayTests
{
    [Theory]
    [InlineData("normal")]
    [InlineData("full")]
    [InlineData("failed")]
    public async Task MiningSuccessRetainsSameNodeBeforeAndSettledContextWithoutChangingExecution(string mode)
    {
        async Task<(int Captures, long End, string[] Inputs)> Run(bool collect)
        {
            var clock = new FakeTimeProvider();
            var saved = new List<(DiagnosticEvidence Evidence, byte Pixel)>();
            await using var scope = collect ? new DiagnosticEvidenceScope((item, image) =>
            {
                if (mode == "failed") throw new IOException("test-writer-failure");
                saved.Add((item, image.At<Vec3b>(0, 0).Item0));
                return Task.CompletedTask;
            }, maximumBytes: mode == "full" ? 1 : null) : null;
            using var io = new PhysicalReplay(clock, false, 50, LoadProgram("钟离 e(hold,wait)"))
            { CompleteSkillReceipts = true };
            io.SetFrontActor("钟离");
            var point = new AutoPathingTests.PathReplay().Point("walk");
            point.Id = 15;
            point.PathingTaskFileName = "07-跳崖点东x18.json";
            await new MiningHandler(io).RunAsync(default, point);
            var result = (io.CapturedFrames.Count, clock.GetTimestamp(), io.Inputs.Select(input => input.ToString()).ToArray());
            Assert.Single(io.Inputs);
            Assert.Empty(io.Primitives);
            // Existing later path frames, not additional captures/waits requested by mining.
            for (byte i = 1; i <= 5; i++)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                using var frame = new GameCaptureFrame(new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(i, 0, 0)),
                    stamp: io.Producer.Next());
                using var content = new CaptureContent(frame, 0, 50, new FakeSystemInfo(new(0, 0, 1920, 1080), 1));
            }
            if (scope != null)
            {
                await scope.DisposeAsync();
                if (mode == "normal")
                {
                    var before = Assert.Single(saved.Where(item => item.Evidence.Phase == "mining-before-cast")).Evidence;
                    var anchor = Assert.Single(saved.Where(item => item.Evidence.Phase == "mining-cast-result" && item.Evidence.Window?.RelativeIndex == 0)).Evidence;
                    Assert.True(anchor.Source.IsAfter(before.Source));
                    Assert.Equal(before.Request, anchor.Request);
                    Assert.Contains("originalReceipt", string.Join(" ", anchor.Fields!.Keys));
                    Assert.Contains(saved, item => item.Evidence.Phase == "mining-cast-result" && item.Pixel >= 4 && item.Evidence.Window?.RelativeSeconds > 3);
                    Assert.All(saved.Where(item => item.Evidence.Phase.StartsWith("mining-")), item =>
                    {
                        Assert.Equal(DiagnosticEvidencePriority.Routine, item.Evidence.Priority);
                        Assert.Contains("07-跳崖点东x18.json", item.Evidence.Fields!["miningSource"]);
                        Assert.Contains("node=15", item.Evidence.Fields["miningSource"]);
                        Assert.Equal("not-observed", item.Evidence.Fields["oreOutcome"]);
                    });
                }
                else Assert.Empty(saved);
            }
            return result;
        }
        var baseline = await Run(false);
        var observed = await Run(true);
        Assert.Equal(baseline.Captures, observed.Captures);
        Assert.Equal(baseline.End, observed.End);
        Assert.Equal(baseline.Inputs, observed.Inputs);
    }
}
