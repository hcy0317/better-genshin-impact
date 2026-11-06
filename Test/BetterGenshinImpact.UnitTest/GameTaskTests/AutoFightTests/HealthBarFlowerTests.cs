using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.Model.Area;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

[Collection("OfflineNativeDecision")]
public sealed class HealthBarFlowerTests : IDisposable
{
    public HealthBarFlowerTests() => AutoFightSeek.ResetSeekState();
    public void Dispose() => AutoFightSeek.ResetSeekState();
    [Theory]
    [InlineData(1.0)]
    [InlineData(0.75)]
    [InlineData(1.25)]
    public void HistoricalFrameWithVisibleEnemyAndHealthBarRemainsRecognized(double scale)
    {
        using var image = LoadFrame("s28-source83077-historical-bar-uid-safe.png", scale);
        var decision = AutoFightSeek.RecognizeSeekDecision(image, new Scalar(255, 90, 90), null, out _, out _, saveDiagnostics: false);
        Assert.Equal(SeekCueKind.HealthBar, decision.Cue);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.75)]
    [InlineData(1.25)]
    public void RecordedFlowerPixelsMustNotProjectAnEnemyHealthBar(double scale)
    {
        using var image = LoadFrame("s28-source188642-flower-uid-safe.png", scale);
        var decision = AutoFightSeek.RecognizeSeekDecision(image, new Scalar(255, 90, 90), null, out _, out _, saveDiagnostics: false);
        Assert.NotEqual(SeekCueKind.HealthBar, decision.Cue);
    }

    private static ImageRegion LoadFrame(string filename, double scale)
    {
        using var original = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Combat", filename));
        Assert.Equal(1920, original.Width);
        Assert.Equal(1080, original.Height);
        var pixels = new Mat();
        try
        {
            Cv2.Resize(original, pixels, new Size((int)(1920 * scale), (int)(1080 * scale)),
                interpolation: scale < 1 ? InterpolationFlags.Area : InterpolationFlags.Linear);
            return new ImageRegion(pixels, 0, 0);
        }
        catch
        {
            pixels.Dispose();
            throw;
        }
    }
}
