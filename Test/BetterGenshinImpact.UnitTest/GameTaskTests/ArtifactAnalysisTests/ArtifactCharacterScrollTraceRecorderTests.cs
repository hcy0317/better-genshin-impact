using BetterGenshinImpact.GameTask.ArtifactAnalysis;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.ArtifactAnalysisTests;

public class ArtifactCharacterScrollTraceRecorderTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("true", false)]
    [InlineData("0", false)]
    [InlineData("1", true)]
    [InlineData(" 1 ", true)]
    public void TraceIsEnabledOnlyByExplicitOne(string? value, bool expected)
    {
        Assert.Equal(expected,
            ArtifactCharacterScrollTraceRecorder.IsEnabled(value));
    }

    [Fact]
    public void TraceCanBeEnabledByAProcessIndependentSentinelFile()
    {
        Assert.True(ArtifactCharacterScrollTraceRecorder.IsEnabled(
            value: null,
            enableFileExists: true));
    }

    [Theory]
    [InlineData(48, 2, 8)]
    [InlineData(48, 20, 80)]
    [InlineData(0, 20, 8)]
    public void SampleIntervalBoundsTraceFrameCountWithoutChangingInputTiming(
        int inputCount,
        int inputIntervalMilliseconds,
        int expectedSampleIntervalMilliseconds)
    {
        Assert.Equal(
            expectedSampleIntervalMilliseconds,
            ArtifactCharacterScrollTraceRecorder
                .CalculateSampleIntervalMilliseconds(
                    inputCount, inputIntervalMilliseconds));
    }

    [Fact]
    public void ExtractTextureBandsCopiesThreeFullHeightNarrowStrips()
    {
        using var source = new Mat(
            new Size(240, 120),
            MatType.CV_8UC1,
            Scalar.Black);
        for (var x = 0; x < source.Width; x++)
        {
            source.Col(x).SetTo(x);
        }

        var requestedRoi = new Rect(20, 10, 180, 90);
        using var result = ArtifactCharacterScrollTraceRecorder
            .ExtractTextureBands(source, requestedRoi, out var bandRects);

        Assert.Equal(3, bandRects.Length);
        Assert.All(bandRects, band =>
        {
            Assert.Equal(requestedRoi.Y, band.Y);
            Assert.Equal(requestedRoi.Height, band.Height);
            Assert.InRange(band.X, requestedRoi.X,
                requestedRoi.Right - band.Width);
        });
        Assert.Equal(requestedRoi.Height, result.Height);
        Assert.Equal(
            bandRects.Sum(rect => rect.Width) + 4,
            result.Width);

        var destinationX = 0;
        foreach (var band in bandRects)
        {
            Assert.Equal(
                source.At<byte>(band.Y, band.X),
                result.At<byte>(0, destinationX));
            destinationX += band.Width + 2;
        }
    }
}
