using BetterGenshinImpact.Core.Recognition.OCR.Paddle;
using BetterGenshinImpact.Core.Recognition.ONNX;
using OpenCvSharp;
using System;
using System.Globalization;

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
    private readonly BgiOnnxFactory _factory;
    private readonly Rec _recognizer;

    internal ArtifactRecognitionOnlyOcrSession(bool? forceCpuOcr = null)
    {
        var model = ArtifactOcrProviderPolicy.ResolveCurrentModel();
        var factory = ArtifactOcrProviderPolicy.CreateFactory(forceCpuOcr);
        try
        {
            _recognizer = new Rec(
                model.RecognitionModel,
                model.RecLabel(),
                model.RecognitionVersion,
                factory);
            _factory = factory;
        }
        catch
        {
            factory.Dispose();
            throw;
        }
    }

    internal string RecognizeWithoutDetector(Mat region) =>
        _recognizer.Run(region).Text.Trim();

    public void Dispose()
    {
        try
        {
            _recognizer.Dispose();
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
