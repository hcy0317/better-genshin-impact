using BetterGenshinImpact.GameTask.ArtifactAnalysis;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.ArtifactAnalysisTests;

public class ArtifactOcrDebugModeTests
{
    [Fact]
    public async Task ParseAsync_RecordsRecognitionFailureAndContinuesWhenEnabled()
    {
        Exception? recorded = null;

        var result = await ArtifactOcrDebugMode.ParseAsync(
            enabled: true,
            () => Task.FromException<string>(new FormatException("bad OCR")),
            exception => { recorded = exception; return Task.CompletedTask; });

        Assert.Null(result.Value);
        Assert.IsType<FormatException>(result.Error);
        Assert.Same(result.Error, recorded);
    }

    [Fact]
    public async Task ParseAsync_PreservesFailFastBehaviorWhenDisabled()
    {
        await Assert.ThrowsAsync<FormatException>(() => ArtifactOcrDebugMode.ParseAsync(
            enabled: false,
            () => Task.FromException<string>(new FormatException("bad OCR")),
            _ => Task.CompletedTask));
    }

    [Fact]
    public async Task ParseAsync_NeverSuppressesCancellation()
    {
        await Assert.ThrowsAsync<OperationCanceledException>(() => ArtifactOcrDebugMode.ParseAsync(
            enabled: true,
            () => Task.FromException<string>(new OperationCanceledException()),
            _ => Task.CompletedTask));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ParseAsync_SkipsRecognizedEnhancementMaterialsWithoutFailure(
        bool enabled)
    {
        var recorded = false;

        var result = await ArtifactOcrDebugMode.ParseAsync(
            enabled,
            () => Task.FromException<string>(
                new ArtifactEnhancementMaterialException("祝圣精华")),
            _ => { recorded = true; return Task.CompletedTask; });

        Assert.Null(result.Value);
        Assert.Null(result.Error);
        Assert.False(recorded);
    }
}
