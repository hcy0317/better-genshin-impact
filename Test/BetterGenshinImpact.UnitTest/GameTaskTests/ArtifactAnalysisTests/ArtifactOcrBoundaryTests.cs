using BetterGenshinImpact.GameTask.ArtifactAnalysis;
using BetterGenshinImpact.Core.Recognition.ONNX;
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
    public void ScanTasks_ReuseRecognitionOnlyUidSessionsForSubsequentReading()
    {
        var sourceDirectory = Path.Combine(
            FindRepoRoot(),
            "BetterGenshinImpact",
            "GameTask",
            "ArtifactAnalysis");
        var inventorySource = File.ReadAllText(Path.Combine(
            sourceDirectory, "ArtifactInventoryScanner.cs"))
            .ReplaceLineEndings("\n");
        var characterSource = File.ReadAllText(Path.Combine(
            sourceDirectory, "ArtifactCharacterRosterScanner.cs"));

        Assert.DoesNotContain(
            "EnsureExpectedUidAsync(uid, ct);",
            inventorySource,
            StringComparison.Ordinal);
        Assert.Contains(
            "new ArtifactRecognitionOnlyOcrSession",
            inventorySource,
            StringComparison.Ordinal);
        Assert.Contains(
            "new ArtifactInventoryUi(\n            _logger,\n            ocrSession.RecognizeWithoutDetector,\n            ocrSession.RecognizeBatch)",
            inventorySource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "new ArtifactInventoryUi(_logger, ocrSession.Service)",
            inventorySource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "new ArtifactPaddleOcrSession",
            characterSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "new ArtifactCharacterDetailsReader()",
            characterSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "detailsReader.RecognizeWithoutDetector",
            characterSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "new ArtifactCharacterDetailsReader(ocrSession.Service)",
            characterSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void InventoryDetectionModel_IsConstructedOnlyByTheLegacyFallback()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "BetterGenshinImpact",
            "GameTask",
            "ArtifactAnalysis",
            "ArtifactInventoryScanner.cs"));

        Assert.Contains("LegacyOcrService", source, StringComparison.Ordinal);
        Assert.Contains(
            "_ownedLegacyOcrSession.GetOrCreate",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "return new ArtifactPaddleOcrSession",
            source,
            StringComparison.Ordinal);
        Assert.Contains("_fixedRegionRecognizer(region)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_ocrService.OcrWithoutDetector(region)", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetryableLazySession_ConcurrentRequestsCreateOnlyOneResource()
    {
        var lazy = new ArtifactRetryableLazy<object>();
        using var start = new ManualResetEventSlim();
        var factoryCalls = 0;
        var tasks = Enumerable.Range(0, 24)
            .Select(_ => Task.Run(() =>
            {
                start.Wait();
                return lazy.GetOrCreate(() =>
                {
                    Interlocked.Increment(ref factoryCalls);
                    Thread.Sleep(40);
                    return new object();
                });
            }))
            .ToArray();

        start.Set();
        var resources = await Task.WhenAll(tasks);

        Assert.Equal(1, factoryCalls);
        Assert.All(resources, resource => Assert.Same(resources[0], resource));
    }

    [Fact]
    public void CharacterOcr_RetriesOnlyTheFieldsMissingFromTheFirstFixedRegionRead()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "BetterGenshinImpact",
            "GameTask",
            "ArtifactAnalysis",
            "ArtifactCharacterRosterScanner.cs"));

        var firstSuccessReturn = source.IndexOf(
            "if (first.IsComplete) return first.RequireComplete();",
            StringComparison.Ordinal);
        var retryDelay = source.IndexOf(
            "await Delay(160, cancellationToken);",
            StringComparison.Ordinal);

        Assert.True(firstSuccessReturn >= 0);
        Assert.True(retryDelay > firstSuccessReturn);
        Assert.Contains(
            "readName: !sameDetail || first.CharacterName is null",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "readLevel: !sameDetail || !first.Level.HasValue",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FixedUidRegion_DoesNotLoadOrRunTheDetectionModel()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "BetterGenshinImpact",
            "GameTask",
            "ArtifactAnalysis",
            "ArtifactGameIdentityVerifier.cs"));

        Assert.Contains("OcrWithoutDetector(uidRegion)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OcrResult(uidRegion)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ArtifactTraversal_NeverAssumesTheFirstGridItemIsSelected()
    {
        var sourceDirectory = Path.Combine(
            FindRepoRoot(),
            "BetterGenshinImpact",
            "GameTask",
            "ArtifactAnalysis");
        var inventorySource = File.ReadAllText(Path.Combine(
            sourceDirectory, "ArtifactInventoryScanner.cs"));
        var lockSource = File.ReadAllText(Path.Combine(
            sourceDirectory, "ArtifactLockPlanExecutor.cs"));

        Assert.DoesNotContain(
            "? reader.CaptureInitiallySelectedItem",
            inventorySource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "? await reader.ReadInitiallySelectedItemAsync",
            lockSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "CaptureInitiallySelectedItem(",
            inventorySource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ArtifactDetailSwitchWait_UsesTheLatestFrameAfterYasLockTimeout()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "BetterGenshinImpact",
            "GameTask",
            "ArtifactAnalysis",
            "ArtifactInventoryScanner.cs"));

        Assert.Contains(
            "ArtifactDetailCapturePolicy.Decide",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "按 yas-lock 语义使用当前详情帧",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "未证明目标格已选中",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "catch (InvalidDataException",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("补点一次", source, StringComparison.Ordinal);
        Assert.Contains("同详情选中加速", source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ScanCaptureProjectsDirectlyToOcrRegionsWithoutAFullFrameClone()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "BetterGenshinImpact",
            "GameTask",
            "ArtifactAnalysis",
            "ArtifactInventoryScanner.cs"));

        Assert.Contains("CaptureAfterDetailSwitchedAsync(", source,
            StringComparison.Ordinal);
        Assert.Contains("ArtifactCapturedItem.Create", source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("return capture.SrcMat.Clone();", source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FixedArtifactRegions_UseIndependentRecognizersWithoutUnsafeModelBatching()
    {
        var root = FindRepoRoot();
        var inventorySource = File.ReadAllText(Path.Combine(
            root,
            "BetterGenshinImpact",
            "GameTask",
            "ArtifactAnalysis",
            "ArtifactInventoryScanner.cs"));
        var recognizerSource = File.ReadAllText(Path.Combine(
            root,
            "BetterGenshinImpact",
            "GameTask",
            "ArtifactAnalysis",
            "ArtifactOcrProviderPolicy.cs"));

        Assert.Contains("InferFixedBatch", inventorySource, StringComparison.Ordinal);
        Assert.Contains("ocrSession.RecognizeBatch", inventorySource, StringComparison.Ordinal);
        Assert.Contains(
            "InventoryParallelRecognizerCount = 6",
            recognizerSource,
            StringComparison.Ordinal);
        Assert.Contains("Parallel.For", recognizerSource, StringComparison.Ordinal);
        Assert.Contains("parallelRecognizerCount:", inventorySource, StringComparison.Ordinal);
    }

    [Fact]
    public void ArtifactRecognizerConcurrencyIsCappedOnCpuButKeepsCudaParallelism()
    {
        Assert.Equal(
            2,
            ArtifactRecognitionOnlyOcrSession.ResolveParallelRecognizerCount(
                requested: 6,
                [ProviderType.Cpu]));
        Assert.Equal(
            6,
            ArtifactRecognitionOnlyOcrSession.ResolveParallelRecognizerCount(
                requested: 6,
                [ProviderType.Cuda, ProviderType.Cpu]));
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
