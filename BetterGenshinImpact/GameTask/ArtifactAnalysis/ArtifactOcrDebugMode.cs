using OpenCvSharp;
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.GameTask.ArtifactAnalysis;

internal sealed class ArtifactEnhancementMaterialException(string itemName)
    : Exception(itemName);

internal sealed record ArtifactDebugParseResult<T>(T? Value, Exception? Error)
    where T : class;

internal static class ArtifactOcrDebugMode
{
    internal static async Task<ArtifactDebugParseResult<T>> ParseAsync<T>(
        bool enabled,
        Func<Task<T>> parse,
        Func<Exception, Task> recordFailure)
        where T : class
    {
        try
        {
            return new ArtifactDebugParseResult<T>(await parse(), null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (OutOfMemoryException)
        {
            throw;
        }
        catch (ArtifactEnhancementMaterialException)
        {
            return new ArtifactDebugParseResult<T>(null, null);
        }
        catch (Exception exception) when (enabled)
        {
            await recordFailure(exception);
            return new ArtifactDebugParseResult<T>(null, exception);
        }
    }
}

internal sealed class ArtifactOcrDebugCollector
{
    private const string ModelVersion = "paddle-v6-cpu-cropped-fixed-regions+yas-rarity+dormant-substat+legacy-fallback";
    private readonly string _root;
    private readonly string _manifestPath;
    private int _failureCount;

    private ArtifactOcrDebugCollector(string root)
    {
        _root = root;
        _manifestPath = Path.Combine(root, "manifest.jsonl");
        Directory.CreateDirectory(root);
    }

    internal static ArtifactOcrDebugCollector? TryCreate()
    {
        var debugRoot = Path.Combine(
            AppContext.BaseDirectory, "User", "artifact-analysis");
        var sentinel = Path.Combine(debugRoot, "ocr-debug.enabled");
        if (!File.Exists(sentinel)) return null;

        var runName = $"{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        return new ArtifactOcrDebugCollector(
            Path.Combine(debugRoot, "ocr-debug", runName));
    }

    internal Task RecordAsync(
        ArtifactCapturedItem frame,
        Exception exception,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fileName = $"artifact-{frame.ScanIndex:D5}.png";
        using var debugCapture = frame.CreateOcrDebugCapture();
        Cv2.ImWrite(Path.Combine(_root, fileName), debugCapture);

        var entry = new
        {
            scanIndex = frame.ScanIndex,
            image = fileName,
            width = debugCapture.Width,
            height = debugCapture.Height,
            sourceWidth = frame.SourceSize.Width,
            sourceHeight = frame.SourceSize.Height,
            modelVersion = ModelVersion,
            errorType = exception.GetType().FullName,
            error = exception.Message,
            capturedAtUtc = DateTimeOffset.UtcNow
        };
        File.AppendAllText(
            _manifestPath,
            JsonSerializer.Serialize(entry) + Environment.NewLine,
            Encoding.UTF8);
        _failureCount++;
        return Task.CompletedTask;
    }

    internal string Complete(int observedCount)
    {
        File.WriteAllText(
            Path.Combine(_root, "summary.json"),
            JsonSerializer.Serialize(new
            {
                observedCount,
                failureCount = _failureCount,
                modelVersion = ModelVersion,
                completedAtUtc = DateTimeOffset.UtcNow
            }, new JsonSerializerOptions { WriteIndented = true }),
            Encoding.UTF8);
        return _root;
    }
}
