using BetterGenshinImpact.Core.Recognition.OCR.Paddle;
using BetterGenshinImpact.GameTask.ArtifactAnalysis;
using System.Globalization;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.ArtifactAnalysisTests;

public class ArtifactOcrModelPolicyTests
{
    [Fact]
    public void SimplifiedChineseArtifactsUseTheBenchmarkedV6Model()
    {
        Assert.False(ArtifactInventoryUi.ForceCpuOcr);
        Assert.Same(
            PaddleOcrService.PaddleOcrModelType.V6,
            ArtifactInventoryUi.SelectOcrModel(new CultureInfo("zh-Hans")));
    }
}
