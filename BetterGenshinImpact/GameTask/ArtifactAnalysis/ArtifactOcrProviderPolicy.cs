using BetterGenshinImpact.Core.Recognition.OCR.Paddle;
using BetterGenshinImpact.Core.Recognition.ONNX;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace BetterGenshinImpact.GameTask.ArtifactAnalysis;

internal static class ArtifactOcrProviderPolicy
{
    internal const bool ExcludeTensorRt = true;

    internal static BgiOnnxFactory CreateFactory(bool? forceCpuOcr = null)
    {
        return new BgiOnnxFactory(
            App.GetLogger<BgiOnnxFactory>(),
            forceCpuOcr,
            excludeTensorRtForOcr: ExcludeTensorRt);
    }

    internal static PaddleOcrService.PaddleOcrModelType ResolveCurrentModel()
    {
        var cultureName = TaskContext.Instance().Config.OtherConfig.GameCultureInfoName;
        return ArtifactInventoryUi.SelectOcrModel(new CultureInfo(cultureName));
    }
}

internal sealed class ArtifactRetryableLazy<T> where T : class
{
    private readonly object _gate = new();
    private T? _value;

    internal T GetOrCreate(Func<T> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        lock (_gate)
        {
            return _value ??= factory();
        }
    }

    internal T? Take()
    {
        lock (_gate)
        {
            var value = _value;
            _value = null;
            return value;
        }
    }
}

internal sealed class ArtifactRecognitionOnlyOcrSession : IDisposable
{
    internal const int InventoryParallelRecognizerCount = 6;
    internal const int CpuParallelRecognizerLimit = 2;
    private readonly BgiOnnxFactory _factory;
    private readonly Rec[] _recognizers;

    internal ArtifactRecognitionOnlyOcrSession(
        bool? forceCpuOcr = null,
        int parallelRecognizerCount = 1)
    {
        if (parallelRecognizerCount < 1)
            throw new ArgumentOutOfRangeException(nameof(parallelRecognizerCount));
        var model = ArtifactOcrProviderPolicy.ResolveCurrentModel();
        var factory = ArtifactOcrProviderPolicy.CreateFactory(forceCpuOcr);
        parallelRecognizerCount = ResolveParallelRecognizerCount(
            parallelRecognizerCount,
            BgiOnnxFactory.ResolveOcrProviderTypes(
                factory.CpuOcr,
                ArtifactOcrProviderPolicy.ExcludeTensorRt,
                factory.ProviderTypes));
        List<Rec> recognizers = [];
        try
        {
            for (var index = 0; index < parallelRecognizerCount; index++)
            {
                recognizers.Add(new Rec(
                    model.RecognitionModel,
                    model.RecLabel(),
                    model.RecognitionVersion,
                    factory));
            }
            _recognizers = recognizers.ToArray();
            _factory = factory;
        }
        catch
        {
            foreach (var recognizer in recognizers) recognizer.Dispose();
            factory.Dispose();
            throw;
        }
    }

    internal string RecognizeWithoutDetector(Mat region) =>
        _recognizers[0].Run(region).Text.Trim();

    internal string[] RecognizeBatch(Mat[] regions)
    {
        var results = new string[regions.Length];
        var workers = Math.Min(_recognizers.Length, regions.Length);
        Parallel.For(0, workers, worker =>
        {
            for (var index = worker; index < regions.Length; index += workers)
            {
                results[index] = _recognizers[worker]
                    .Run(regions[index])
                    .Text
                    .Trim();
            }
        });
        return results;
    }

    internal static int ResolveParallelRecognizerCount(
        int requested,
        IReadOnlyList<ProviderType> providers)
    {
        if (requested < 1)
            throw new ArgumentOutOfRangeException(nameof(requested));
        ArgumentNullException.ThrowIfNull(providers);
        var primary = providers.FirstOrDefault();
        return primary is ProviderType.Cuda or ProviderType.Dml
            ? requested
            : Math.Min(requested, CpuParallelRecognizerLimit);
    }

    public void Dispose()
    {
        try
        {
            foreach (var recognizer in _recognizers) recognizer.Dispose();
        }
        finally
        {
            _factory.Dispose();
        }
    }
}

internal sealed class ArtifactPaddleOcrSession : IDisposable
{
    private readonly BgiOnnxFactory _factory;

    internal ArtifactPaddleOcrSession(bool? forceCpuOcr = null)
    {
        var factory = ArtifactOcrProviderPolicy.CreateFactory(forceCpuOcr);
        try
        {
            Service = new PaddleOcrService(
                factory,
                ArtifactOcrProviderPolicy.ResolveCurrentModel());
            _factory = factory;
        }
        catch
        {
            factory.Dispose();
            throw;
        }
    }

    internal PaddleOcrService Service { get; }

    public void Dispose()
    {
        try
        {
            Service.Dispose();
        }
        finally
        {
            _factory.Dispose();
        }
    }
}
