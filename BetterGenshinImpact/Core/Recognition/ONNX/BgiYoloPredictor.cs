using System;
using BetterGenshinImpact.GameTask.Model.Area;
using OpenCvSharp;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using BetterGenshinImpact.View.Drawable;
using Compunet.YoloSharp;
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
    private readonly object _predictionLock = new();
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
        ILogger? logger = null)
    {
        _model = onnxModel;
        _predictorInitialization = new OnnxInitializationTask<YoloPredictor>(
            onnxModel.Name,
            () => new YoloPredictor(modelPath,
                new YoloPredictorOptions
                {
                    SessionOptions = sessionOptions
                }),
            logger ?? NullLogger.Instance);
    }

    public YoloPredictor Predictor => _predictorInitialization.Value;

    public TResult UsePredictor<TResult>(Func<YoloPredictor, TResult> action)
    {
        lock (_predictionLock)
        {
            return action(Predictor);
        }
    }

    public async Task WarmUpAsync(ILogger logger, CancellationToken ct)
    {
        if (_predictorInitialization.IsValueCreated)
        {
            logger.LogDebug("[ONNX]模型 {Model} 预测器已初始化，复用现有会话。", _model.Name);
            return;
        }

        await _predictorInitialization.GetValueAsync(ct);
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

        if (_predictorInitialization.TryGetValue(out var predictor))
        {
            predictor.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    ~BgiYoloPredictor()
    {
        Dispose();
    }
}
