using BetterGenshinImpact.Core.Recognition.OCR.Paddle;
using BetterGenshinImpact.Core.Recognition.ONNX;
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
