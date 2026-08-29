using BetterGenshinImpact.GameTask.ArtifactAnalysis;
using System.Text.RegularExpressions;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.ArtifactAnalysisTests;

public class ArtifactOcrBoundaryTests
{
    [Fact]
    public void ProviderPolicy_AlwaysExcludesTensorRt()
    {
        Assert.True(ArtifactOcrProviderPolicy.ExcludeTensorRt);

        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "BetterGenshinImpact",
            "GameTask",
            "ArtifactAnalysis",
            "ArtifactOcrProviderPolicy.cs"));

        Assert.Contains(
            "excludeTensorRtForOcr: ExcludeTensorRt",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("OcrFactory.Paddle", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionCode_CannotBypassTheArtifactOcrProviderPolicy()
    {
        var sourceDirectory = Path.Combine(
            FindRepoRoot(),
            "BetterGenshinImpact",
            "GameTask",
            "ArtifactAnalysis");

        foreach (var path in Directory.EnumerateFiles(sourceDirectory, "*.cs"))
        {
            var isProviderPolicy = Path.GetFileName(path).Equals(
                    "ArtifactOcrProviderPolicy.cs",
                    StringComparison.Ordinal);

            var source = File.ReadAllText(path);
            Assert.DoesNotContain("OcrFactory.Paddle", source, StringComparison.Ordinal);
            if (!isProviderPolicy)
            {
                Assert.DoesNotContain("new BgiOnnxFactory(", source, StringComparison.Ordinal);
            }

            foreach (Match match in Regex.Matches(
                         source,
                         @"\.Find(?:Multi)?\(\s*RecognitionObject\.Ocr\([\s\S]*?;"))
            {
                Assert.Contains("ocrService:", match.Value, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void ScanTasks_ReuseTheirUidOcrSessionForSubsequentReading()
    {
        var sourceDirectory = Path.Combine(
            FindRepoRoot(),
            "BetterGenshinImpact",
            "GameTask",
            "ArtifactAnalysis");
        var inventorySource = File.ReadAllText(Path.Combine(
            sourceDirectory, "ArtifactInventoryScanner.cs"));
        var characterSource = File.ReadAllText(Path.Combine(
            sourceDirectory, "ArtifactCharacterRosterScanner.cs"));

        Assert.DoesNotContain(
            "EnsureExpectedUidAsync(uid, ct);",
            inventorySource,
            StringComparison.Ordinal);
        Assert.Contains(
            "new ArtifactInventoryUi(_logger, ocrSession.Service)",
            inventorySource,
            StringComparison.Ordinal);
        Assert.Contains(
            "uid, ocrSession.Service, cancellationToken",
            characterSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "new ArtifactCharacterDetailsReader(ocrSession.Service)",
            characterSource,
            StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BetterGenshinImpact.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate BetterGenshinImpact.sln from the test output directory.");
    }
}
