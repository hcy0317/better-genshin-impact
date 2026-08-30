using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.ArtifactAnalysis;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.ArtifactAnalysisTests;

public class ArtifactGameIdentityVerifierTests
{
    [Fact]
    public async Task EnsureExpectedUid_InjectedServiceCancellationPreventsOcrCall()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var ocrService = new CountingOcrService();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ArtifactGameIdentityVerifier.EnsureExpectedUidAsync(
                "102550550",
                ocrService,
                cancellation.Token));

        Assert.Equal(0, ocrService.CallCount);
    }

    [Theory]
    [InlineData("UID: 102550550", "102550550")]
    [InlineData("UID  123456789012", "123456789012")]
    public void TryParseUid_AcceptsOnlyACompleteUid(string text, string expected)
    {
        Assert.True(ArtifactGameIdentityVerifier.TryParseUid(text, out var uid));
        Assert.Equal(expected, uid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("UID: 12345")]
    [InlineData("UID: 1234567890123")]
    public void TryParseUid_RejectsMissingOrImplausibleValues(string text)
    {
        Assert.False(ArtifactGameIdentityVerifier.TryParseUid(text, out _));
    }

    private sealed class CountingOcrService : IOcrService
    {
        public int CallCount { get; private set; }

        public string Ocr(Mat mat)
        {
            CallCount++;
            return string.Empty;
        }

        public string OcrWithoutDetector(Mat mat)
        {
            CallCount++;
            return string.Empty;
        }

        public OcrResult OcrResult(Mat mat)
        {
            CallCount++;
            return new OcrResult([]);
        }
    }
}
