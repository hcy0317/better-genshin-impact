using System;
using BetterGenshinImpact.GameTask.Model.Area;
using OpenCvSharp;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using BetterGenshinImpact.View.Drawable;
using Compunet.YoloSharp;
using Compunet.YoloSharp.Data;
using Microsoft.ML.OnnxRuntime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.Core.Recognition.ONNX;

public class BgiYoloPredictor : IDisposable
{
    private readonly BgiOnnxModel _model;


    private readonly OnnxInitializationTask<YoloPredictor> _predictorInitialization;
    private readonly Action<BgiYoloPredictor>? _initializationFailed;
    private readonly object _predictionLock = new();
    private readonly HashSet<(int Width, int Height)> _preparedClassificationSizes = [];
    private int _failureReported;
    private int _disposed;

    /// <summary>
    /// 使用 BgiOnnxFactory 创建这个类的实例
    /// </summary>
    /// <param name="onnxModel">模型</param>
    /// <param name="modelPath">实际要加载的模型文件的绝对路径，在使用模型缓存的场景下可能有差别</param>
    /// <param name="sessionOptions">sessionOptions</param>
    protected internal BgiYoloPredictor(
        BgiOnnxModel onnxModel,
        string modelPath,
        SessionOptions sessionOptions,
        ILogger? logger = null,
        Action<BgiYoloPredictor>? initializationFailed = null)
    {
        _model = onnxModel;
        _initializationFailed = initializationFailed;
        _predictorInitialization = new OnnxInitializationTask<YoloPredictor>(
            onnxModel.Name,
            () =>
            {
                try
                {
                    return new YoloPredictor(modelPath,
                        new YoloPredictorOptions
                        {
                            SessionOptions = sessionOptions
                        });
                }
                finally
                {
                    sessionOptions.Dispose();
                }
            },
            logger ?? NullLogger.Instance,
            disposeValue: predictor => predictor.Dispose(),
            disposeUnstarted: sessionOptions.Dispose);
    }

    public YoloPredictor Predictor
    {
        get
        {
            ThrowIfDisposed();
            try
            {
                var predictor = _predictorInitialization.Value;
                ThrowIfDisposed();
                return predictor;
            }
            catch
            {
                ReportInitializationFailure();
                throw;
            }
        }
    }

    public TResult UsePredictor<TResult>(Func<YoloPredictor, TResult> action)
    {
        if (RecognitionReadinessScope.IsNonBlocking)
        {
            if (!System.Threading.Monitor.TryEnter(_predictionLock))
                throw new RecognitionNotReadyException("预测器正在准备/被借用，当前实时观察不等待");
            try
            {
                ThrowIfDisposed();
                if (!_predictorInitialization.TryGetValue(out var ready))
                    throw new RecognitionNotReadyException("预测器未完成初始化");
                return action(ready);
            }
            finally { System.Threading.Monitor.Exit(_predictionLock); }
        }
        lock (_predictionLock)
        {
            ThrowIfDisposed();
            return action(Predictor);
        }
    }

    internal bool IsClassificationPrepared(int width, int height)
    {
        if (!System.Threading.Monitor.TryEnter(_predictionLock)) return false;
        try { return Volatile.Read(ref _disposed) == 0 && _preparedClassificationSizes.Contains((width, height)); }
        finally { System.Threading.Monitor.Exit(_predictionLock); }
    }

    public async Task WarmUpAsync(ILogger logger, CancellationToken ct)
    {
        if (_predictorInitialization.IsValueCreated)
        {
            logger.LogDebug("[ONNX]模型 {Model} 预测器已初始化，复用现有会话。", _model.Name);
            return;
        }

        try
        {
            await _predictorInitialization.GetValueAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            ReportInitializationFailure();
            throw;
        }
    }

    /// <summary>当前调用方所需分类尺寸的真实首推理；结果只用于准备，不能当作实时画面证据。</summary>
    internal async Task<bool> PrepareClassificationAsync(
        SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24> sample, ILogger logger, CancellationToken ct)
    {
        await WarmUpAsync(logger, ct);
        ct.ThrowIfCancellationRequested();
        var started = Stopwatch.GetTimestamp();
        lock (_predictionLock)
        {
            ThrowIfDisposed();
            if (_preparedClassificationSizes.Contains((sample.Width, sample.Height))) return false;
            ct.ThrowIfCancellationRequested();
            _ = Predictor.Classify(sample).GetTopClass();
            ct.ThrowIfCancellationRequested();
            _preparedClassificationSizes.Add((sample.Width, sample.Height));
        }
        try { logger.LogDebug("ONNX_CLASSIFICATION_PREPARED model={Model} width={Width} height={Height} ms={Milliseconds:F2}; result-not-gameplay-evidence",
            _model.Name, sample.Width, sample.Height, Stopwatch.GetElapsedTime(started).TotalMilliseconds); } catch { }
        return true;
    }

    private void ReportInitializationFailure()
    {
        if (Interlocked.Exchange(ref _failureReported, 1) == 0)
        {
            _initializationFailed?.Invoke(this);
        }
    }

    /// <summary>
    /// 检测
    /// </summary>
    /// <param name="region">图像</param>
    /// <returns>类别-矩形框</returns>
    public Dictionary<string, List<Rect>> Detect(ImageRegion region)
    {
        var result = UsePredictor(predictor => predictor.Detect(region.CacheImage));


        var dict = new Dictionary<string, List<Rect>>();
        foreach (var box in result)
        {
            if (!dict.TryGetValue(box.Name.Name, out var value))
            {
                dict[box.Name.Name] = [new Rect(box.Bounds.X, box.Bounds.Y, box.Bounds.Width, box.Bounds.Height)];
            }
            else
            {
                value.Add(new Rect(box.Bounds.X, box.Bounds.Y, box.Bounds.Width, box.Bounds.Height));
            }
        }

        Debug.WriteLine("YOLO识别结果:" + JsonSerializer.Serialize(dict));

        var list = result
            .Select(box => new Rect(box.Bounds.X, box.Bounds.Y, box.Bounds.Width, box.Bounds.Height))
            .Select(rect => region.ToRectDrawable(rect, _model.Name)).ToList();

        VisionContext.Instance().DrawContent.PutOrRemoveRectList(_model.Name, list);

        return dict;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_predictionLock)
        {
            _predictorInitialization.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    ~BgiYoloPredictor()
    {
        Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(BgiYoloPredictor));
        }
    }
}
