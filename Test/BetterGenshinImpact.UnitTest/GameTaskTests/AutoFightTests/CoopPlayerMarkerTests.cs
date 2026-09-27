using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.GameTask.Model.Area;
using OpenCvSharp;
using Xunit.Abstractions;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

[Collection("OfflineNativeDecision")]
public class CoopPlayerMarkerTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("别人进我世界_4人.png")]
    [InlineData("别人进我世界_4人_2.png")]
    public void FourPlayerHostHasThreeRemotePlayerMarkers(string filename)
    {
        using var pixels = Cv2.ImRead(Path.Combine("..", "..", "..", "Assets", "AutoFight", "联机满编", filename));
        Assert.False(pixels.Empty());
        using var frame = new ImageRegion(pixels.Clone(), 0, 0);
        var recognition = RecognitionAssets.Get("AutoFight", "P", frame);
        var markers = frame.FindMulti(recognition);
        try
        {
            output.WriteLine($"roi={recognition.RegionOfInterest}, threshold={recognition.Threshold}");
            foreach (var marker in markers) output.WriteLine(marker.ToRect().ToString());
            Assert.Equal(3, markers.Count);
        }
        finally { foreach (var marker in markers) marker.Dispose(); }
    }

    [Fact]
    public void SinglePlayerHudDoesNotGainRemotePlayers()
    {
        using var pixels = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ui", "inactive-zhongli-20260911.png"));
        Assert.False(pixels.Empty());
        using var frame = new ImageRegion(pixels.Clone(), 0, 0);
        var markers = frame.FindMulti(RecognitionAssets.Get("AutoFight", "P", frame));
        try { Assert.Empty(markers); }
        finally { foreach (var marker in markers) marker.Dispose(); }
    }
}
