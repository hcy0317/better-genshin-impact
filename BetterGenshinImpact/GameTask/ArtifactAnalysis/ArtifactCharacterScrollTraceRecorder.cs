using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.ArtifactAnalysis;

/// <summary>
/// Opt-in diagnostic recorder for the physical character-list motion produced by
/// one existing page-scroll input batch. The captured frames are evidence only:
/// they must never feed the pagination decision.
/// </summary>
internal sealed class ArtifactCharacterScrollTraceRecorder
{
    private const string EnvironmentVariableName =
        "BGI_ARTIFACT_CHARACTER_SCROLL_TRACE";
    private const string EnableFileName =
        "enable-artifact-character-scroll-trace";
    private const int MaximumTraceFrames = 12;
    private const int MinimumSampleIntervalMilliseconds = 8;
    private const int TextureBandCount = 3;
    private const int TextureBandGap = 2;

    private static readonly ILogger Logger =
        App.GetLogger<ArtifactCharacterScrollTraceRecorder>();

    private readonly Rect _gridRoi;
    private readonly int _inputCount;
    private readonly int _direction;
    private readonly int _inputIntervalMilliseconds;
    private readonly int _sampleIntervalMilliseconds;
    private readonly string _traceDirectory;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly CancellationTokenSource _samplingCancellation = new();
    private readonly List<CapturedFrame> _frames = [];
    private readonly Func<CapturedTextureBands> _capture;
    private readonly Task _samplingTask;
    private int _completionStarted;

    private ArtifactCharacterScrollTraceRecorder(
        Rect gridRoi,
        int inputCount,
        int direction,
        int inputIntervalMilliseconds,
        Func<CapturedTextureBands> capture)
    {
        _gridRoi = gridRoi;
        _inputCount = inputCount;
        _direction = direction;
        _inputIntervalMilliseconds = inputIntervalMilliseconds;
        _sampleIntervalMilliseconds = CalculateSampleIntervalMilliseconds(
            inputCount, inputIntervalMilliseconds);
        _capture = capture;
        _traceDirectory = Path.Combine(
            AppContext.BaseDirectory,
            "log",
            "screenshot",
            "artifact-character-scroll-trace",
            $"{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}");

        CaptureFrame("before");
        _samplingTask = Task.Run(SampleDuringInputAsync);
    }

    internal static ArtifactCharacterScrollTraceRecorder? TryStart(
        Rect gridRoi,
        int inputCount,
        int direction,
        int inputIntervalMilliseconds)
    {
        if (!IsEnabled(
                Environment.GetEnvironmentVariable(EnvironmentVariableName),
                File.Exists(Path.Combine(
                    AppContext.BaseDirectory,
                    "log",
                    EnableFileName))))
        {
            return null;
        }

        try
        {
            return new ArtifactCharacterScrollTraceRecorder(
                gridRoi,
                inputCount,
                direction,
                inputIntervalMilliseconds,
                () => CaptureTextureBands(gridRoi));
        }
        catch (Exception exception)
        {
            Logger.LogWarning(exception,
                "角色滚动物理回执诊断启动失败，已跳过本次采集");
            return null;
        }
    }

    /// <summary>
    /// Stops sampling without awaiting capture or file I/O on the input path.
    /// </summary>
    internal void Complete()
    {
        if (Interlocked.Exchange(ref _completionStarted, 1) != 0)
        {
            return;
        }

        _samplingCancellation.Cancel();
        _ = Task.Run(CompleteAndPersistAsync);
    }

    internal static bool IsEnabled(string? value, bool enableFileExists = false) =>
        enableFileExists ||
        string.Equals(value?.Trim(), "1", StringComparison.Ordinal);

    internal static int CalculateSampleIntervalMilliseconds(
        int inputCount,
        int inputIntervalMilliseconds)
    {
        var estimatedDuration = Math.Max(0, inputCount) *
                                Math.Max(0, inputIntervalMilliseconds);
        return Math.Max(
            MinimumSampleIntervalMilliseconds,
            (int)Math.Ceiling(estimatedDuration /
                              (double)MaximumTraceFrames));
    }

    internal static Mat ExtractTextureBands(
        Mat source,
        Rect requestedRoi,
        out Rect[] bandRects)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Empty())
        {
            throw new ArgumentException("Capture is empty.", nameof(source));
        }

        var imageBounds = new Rect(0, 0, source.Width, source.Height);
        var roi = requestedRoi.Intersect(imageBounds);
        if (roi.Width < TextureBandCount || roi.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedRoi),
                "Character grid ROI does not intersect the captured frame.");
        }

        var bandWidth = Math.Clamp(roi.Width / 48, 8, 24);
        bandWidth = Math.Min(bandWidth, roi.Width / TextureBandCount);
        var centerFractions = new[] { 0.2, 0.5, 0.8 };
        bandRects = centerFractions
            .Select(fraction =>
            {
                var centerX = roi.X + (int)Math.Round(
                    (roi.Width - 1) * fraction);
                var x = Math.Clamp(
                    centerX - bandWidth / 2,
                    roi.X,
                    roi.Right - bandWidth);
                return new Rect(x, roi.Y, bandWidth, roi.Height);
            })
            .ToArray();

        var resultWidth = bandRects.Sum(rect => rect.Width) +
                          TextureBandGap * (bandRects.Length - 1);
        var result = new Mat(
            roi.Height,
            resultWidth,
            source.Type(),
            Scalar.All(0));
        var destinationX = 0;
        foreach (var bandRect in bandRects)
        {
            using var sourceBand = new Mat(source, bandRect);
            using var destinationBand = new Mat(
                result,
                new Rect(destinationX, 0, bandRect.Width, bandRect.Height));
            sourceBand.CopyTo(destinationBand);
            destinationX += bandRect.Width + TextureBandGap;
        }

        return result;
    }

    private async Task SampleDuringInputAsync()
    {
        for (var index = 0; index < MaximumTraceFrames; index++)
        {
            try
            {
                await Task.Delay(
                    _sampleIntervalMilliseconds,
                    _samplingCancellation.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                CaptureFrame($"trace-{index:D3}");
            }
            catch (Exception exception)
            {
                Logger.LogDebug(exception,
                    "角色滚动物理回执诊断中间帧采集失败");
            }
        }
    }

    private async Task CompleteAndPersistAsync()
    {
        try
        {
            await _samplingTask;
            CaptureFrame("after");
            Persist();
            Logger.LogInformation(
                "角色滚动物理回执诊断已保存：{Path}",
                _traceDirectory);
        }
        catch (Exception exception)
        {
            Logger.LogWarning(exception,
                "角色滚动物理回执诊断保存失败");
        }
        finally
        {
            foreach (var frame in _frames)
            {
                frame.Image.Dispose();
            }

            _samplingCancellation.Dispose();
        }
    }

    private void CaptureFrame(string name)
    {
        var capturedAtUtc = DateTimeOffset.UtcNow;
        var elapsedTicks = _stopwatch.ElapsedTicks;
        var captured = _capture();
        _frames.Add(new CapturedFrame(
            name,
            capturedAtUtc,
            elapsedTicks,
            captured.Image,
            captured.BandRects));
    }

    private static CapturedTextureBands CaptureTextureBands(Rect gridRoi)
    {
        using var capture = CaptureToRectArea();
        var image = ExtractTextureBands(
            capture.SrcMat, gridRoi, out var bandRects);
        return new CapturedTextureBands(image, bandRects);
    }

    private void Persist()
    {
        Directory.CreateDirectory(_traceDirectory);
        foreach (var frame in _frames)
        {
            Cv2.ImWrite(
                Path.Combine(_traceDirectory, $"{frame.Name}.png"),
                frame.Image);
        }

        var metadata = new
        {
            version = 1,
            capturedAtUtc = DateTimeOffset.UtcNow,
            gridRoi = RectMetadata.From(_gridRoi),
            inputCount = _inputCount,
            direction = _direction,
            inputIntervalMilliseconds = _inputIntervalMilliseconds,
            sampleIntervalMilliseconds = _sampleIntervalMilliseconds,
            stopwatchFrequency = Stopwatch.Frequency,
            paginationDecisionInput = false,
            frames = _frames.Select(frame => new
            {
                file = $"{frame.Name}.png",
                frame.CapturedAtUtc,
                frame.ElapsedTicks,
                elapsedMilliseconds = frame.ElapsedTicks * 1000d /
                                      Stopwatch.Frequency,
                width = frame.Image.Width,
                height = frame.Image.Height,
                textureBands = frame.BandRects.Select(RectMetadata.From)
            })
        };
        File.WriteAllText(
            Path.Combine(_traceDirectory, "metadata.json"),
            JsonSerializer.Serialize(metadata,
                new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed record CapturedTextureBands(Mat Image, Rect[] BandRects);

    private sealed record CapturedFrame(
        string Name,
        DateTimeOffset CapturedAtUtc,
        long ElapsedTicks,
        Mat Image,
        Rect[] BandRects);

    private sealed record RectMetadata(int X, int Y, int Width, int Height)
    {
        internal static RectMetadata From(Rect rect) =>
            new(rect.X, rect.Y, rect.Width, rect.Height);
    }
}
