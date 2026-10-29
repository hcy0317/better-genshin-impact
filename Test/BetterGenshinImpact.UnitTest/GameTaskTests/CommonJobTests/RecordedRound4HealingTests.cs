using System.Security.Cryptography;
using System.Text.Json;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using Microsoft.Extensions.Time.Testing;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Helpers;
using Fischless.GameCapture;
using OpenCvSharp;
using Xunit.Abstractions;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

[Collection("OfflineNativeDecision")]
public class RecordedRound4HealingTests(ITestOutputHelper output)
{
    [OfflineNativeDecisionFact]
    public async Task OriginalRedHealthEvidenceCannotGrantHealingCompletion()
    {
        Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
        Assert.Null(System.Windows.Application.Current);
        var results = new List<(int Id, bool? LowHp, bool Confirmed)>();
        foreach (var (id, sequence, timestamp, hash) in new[]
                 {
                     (6121, 300044L, 3854386552093L, "6A5987F3EED7B30C4756E2B3F0819BBC7883A63616FCBB3B79B467996CCC1838"),
                     (6145, 300430L, 3854678265937L, "63815C3EBA8F3CC2E3AD15ACADFEA5CD2E4B04E54636CB1FD2EEFC8D7F3F109E")
                 })
        {
            var path = UiRecoveryFixtures.PathFor($"evidence-{id}.png");
            using var metadata = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(path, ".json")));
            var root = metadata.RootElement;
            Assert.Equal(2, root.GetProperty("SchemaVersion").GetInt32());
            Assert.Equal("5d0379c1-0c67-4559-a1d3-b8d6e6a833ca", root.GetProperty("RunId").GetString());
            Assert.Equal(id, root.GetProperty("Sequence").GetInt32());
            Assert.Equal($"evidence-{id}.png", root.GetProperty("ImageFile").GetString());
            Assert.Equal(hash, root.GetProperty("ImageContentSha256").GetString());
            var captured = root.GetProperty("Source");
            var source = new CaptureFrameStamp(captured.GetProperty("SessionId").GetGuid(),
                captured.GetProperty("Sequence").GetInt64(), captured.GetProperty("CapturedTimestamp").GetInt64(),
                captured.GetProperty("TimestampFrequency").GetInt64(), captured.GetProperty("CapturedAt").GetDateTimeOffset());
            Assert.Equal(Guid.Parse("09dfaf56-4d9c-48ae-a58a-8b8440222560"), source.SessionId);
            Assert.Equal(sequence, source.Sequence);
            Assert.Equal(timestamp, source.CapturedTimestamp);
            Assert.Equal(10000000L, source.TimestampFrequency);
            Assert.True(captured.GetProperty("IsKnown").GetBoolean());
            Assert.True(source.IsKnown);
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(bytes)));
            using var frame = new ImageRegion(Cv2.ImDecode(bytes, ImreadModes.Color), 0, 0) { FrameStamp = source };
            Assert.Equal(new Size(1920, 1080), frame.SrcMat.Size());
            var observed = Bv.ObserveCurrentAvatarLowHp(frame);
            var legacy = Bv.CurrentAvatarIsLowHp(frame);
            using var region = new Mat(frame.SrcMat, new Rect(805, 1008, 7, 5));
            var mean = Cv2.Mean(region);
            output.WriteLine($"HEALTH_REPLAY id={id} source={source.SessionId}/{source.Sequence} sha256={hash} observed={observed?.ToString() ?? "Unknown"} legacy={legacy} roi=(805,1008,7,5) meanBgr=({mean.Val0:F3},{mean.Val1:F3},{mean.Val2:F3})");
            // Preserve the historical image identity above. The synthetic stamps below
            // test only the completion gate, without claiming the old capture is live/fresh.
            var clock = new FakeTimeProvider();
            var replaySource = new CaptureFrameSource(clock);
            var before = replaySource.Next();
            var fence = new CaptureFrameFence(before, clock.GetTimestamp());
            clock.Advance(TimeSpan.FromMilliseconds(1));
            var confirmed = await HealingObservation.WaitAsync(fence,
                () => new(replaySource.Next(), true, observed),
                ms => { clock.Advance(TimeSpan.FromMilliseconds(ms)); return Task.CompletedTask; }, default, clock);
            results.Add((id, observed, confirmed));
        }
        Assert.All(results, result => Assert.False(result.Confirmed, $"Original red HP frame {result.Id} cannot confirm healing: observed={result.LowHp?.ToString() ?? "Unknown"}"));
        Assert.Null(System.Windows.Application.Current);
    }
}
