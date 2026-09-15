using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Recognition.OCR.Paddle;
using BetterGenshinImpact.Core.Recognition.ONNX;
using BetterGenshinImpact.GameTask;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.Core.Recognition.OCR;

public class OcrFactory : IDisposable, IAsyncDisposable
{
    // public static IOcrService Media = Create(OcrEngineTypes.Media);


    public static IOcrService Paddle => App.ServiceProvider.GetRequiredService<OcrFactory>().PaddleOcr;
    internal IOcrService PaddleOcr => _lifetime.Service;

    private readonly OcrServiceLifetime _lifetime;
    private readonly ILogger<BgiOnnxFactory> _logger;
    private readonly OtherConfig.Ocr _config;
    private readonly Func<IOcrService>? _nativeFactory;

    /// <summary>
    ///  OCR 工厂,不可以直接实例化,请使用 App.ServiceProvider获取实例
    /// </summary>
    /// <param name="logger"></param>
    public OcrFactory(ILogger<BgiOnnxFactory> logger)
    {
        _logger = logger;
        _config = GetConfig();
        _lifetime = CreateLifetime();
    }

    internal OcrFactory(ILogger<BgiOnnxFactory> logger, Func<IOcrService> nativeFactory)
    {
        _logger = logger;
        _config = new OtherConfig.Ocr();
        _nativeFactory = nativeFactory;
        _lifetime = CreateLifetime();
    }

    private OcrServiceLifetime CreateLifetime() => new(() => Create(OcrEngineTypes.Paddle), observation =>
        _logger.LogDebug("OCR_LIFECYCLE generation={Generation} state={State} ms={Milliseconds:F1} borrowers={Borrowers} suppressed={Suppressed}",
            observation.Generation, observation.State, observation.Milliseconds, observation.Borrowers, observation.Suppressed));

    public Task PrepareAsync(CancellationToken ct = default) => _lifetime.PrepareAsync(ct);
    public static Task PreparePaddleAsync(CancellationToken ct = default) =>
        App.ServiceProvider.GetRequiredService<OcrFactory>().PrepareAsync(ct);

    /// <summary>
    /// 创建
    /// </summary>
    private IOcrService Create(OcrEngineTypes type)
    {
        var result = type switch
        {
            OcrEngineTypes.Paddle => _nativeFactory == null ? CreatePaddleOcrInstance() : _nativeFactory(),
            _ => throw new ArgumentOutOfRangeException(Enum.GetName(type), type, "不支持的 OCR 引擎类型")
        };
        try { _logger.LogDebug("创建了类型为 {Type} 的 OCR服务", Enum.GetName(type)); }
        catch { /* 已创建的native实例不能因日志失败而遗失。 */ }
        return result;
    }

    /// <summary>
    /// 获取 OCR 配置
    /// 为了单元测试
    /// </summary>
    /// <returns></returns>
    private OtherConfig.Ocr GetConfig()
    {
        try
        {
            // 直接使用配置
            return TaskContext.Instance().Config.OtherConfig.OcrConfig;
        }
        catch (Exception e)
        {
            // 如果配置获取失败，使用默认配置
            try { _logger.LogWarning(e, "获取 OCR 配置失败，使用默认配置"); } catch { }
            return new OtherConfig.Ocr();
        }
    }

    /// <summary>
    /// 若果配置中没有设置文化信息，则使用默认的文化信息
    /// 为了单元测试
    /// </summary>
    /// <returns></returns>
    private CultureInfo GetCultureInfo()
    {
        try
        {
            return new CultureInfo(TaskContext.Instance().Config.OtherConfig.GameCultureInfoName);
        }
        catch (Exception e)
        {
            var result = new CultureInfo(new OtherConfig().GameCultureInfoName);
            try { _logger.LogInformation("获取游戏文化信息失败，使用默认文化信息: {CultureInfo}", result.Name); } catch { }
            return result;
        }
    }

    private PaddleOcrService CreatePaddleOcrInstance()
    {
        return _config.PaddleOcrModelConfig switch
        {
            PaddleOcrModelConfig.V4Auto =>
                new PaddleOcrService(App.ServiceProvider.GetRequiredService<BgiOnnxFactory>(),
                    PaddleOcrService.PaddleOcrModelType.FromCultureInfoV4(GetCultureInfo()) ??
                    PaddleOcrService.PaddleOcrModelType.V4),
            PaddleOcrModelConfig.V5Auto =>
                new PaddleOcrService(App.ServiceProvider.GetRequiredService<BgiOnnxFactory>(),
                    PaddleOcrService.PaddleOcrModelType.FromCultureInfo(GetCultureInfo()) ??
                    PaddleOcrService.PaddleOcrModelType.V5),
            PaddleOcrModelConfig.V5 =>
                new PaddleOcrService(App.ServiceProvider.GetRequiredService<BgiOnnxFactory>(),
                    PaddleOcrService.PaddleOcrModelType.V5),
            PaddleOcrModelConfig.V6 =>
                new PaddleOcrService(App.ServiceProvider.GetRequiredService<BgiOnnxFactory>(),
                    PaddleOcrService.PaddleOcrModelType.V6),
            PaddleOcrModelConfig.V4 =>
                new PaddleOcrService(App.ServiceProvider.GetRequiredService<BgiOnnxFactory>(),
                    PaddleOcrService.PaddleOcrModelType.V4),
            PaddleOcrModelConfig.V4En =>
                new PaddleOcrService(App.ServiceProvider.GetRequiredService<BgiOnnxFactory>(),
                    PaddleOcrService.PaddleOcrModelType.V4En),
            PaddleOcrModelConfig.V5Korean =>
                new PaddleOcrService(App.ServiceProvider.GetRequiredService<BgiOnnxFactory>(),
                    PaddleOcrService.PaddleOcrModelType.V5Korean),
            PaddleOcrModelConfig.V5Latin =>
                new PaddleOcrService(App.ServiceProvider.GetRequiredService<BgiOnnxFactory>(),
                    PaddleOcrService.PaddleOcrModelType.V5Latin),
            PaddleOcrModelConfig.V5Eslav =>
                new PaddleOcrService(App.ServiceProvider.GetRequiredService<BgiOnnxFactory>(),
                    PaddleOcrService.PaddleOcrModelType.V5Eslav),
            _ => throw new ArgumentOutOfRangeException(nameof(_config.PaddleOcrModelConfig),
                _config.PaddleOcrModelConfig, "不支持的 Paddle OCR 模型配置")
        };
    }

    public Task Unload() => _lifetime.UnloadAsync();

    public void Dispose()
    {
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }

    public ValueTask DisposeAsync() => _lifetime.DisposeAsync();
}
