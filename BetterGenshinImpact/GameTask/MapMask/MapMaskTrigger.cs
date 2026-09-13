using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Recognition.OpenCv;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Common.Map.Maps;
using BetterGenshinImpact.GameTask.Common.Map.Maps.Base;
using BetterGenshinImpact.GameTask.Common.Map.Maps.Layer;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Helpers;
using BetterGenshinImpact.View;
using BetterGenshinImpact.ViewModel;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using Rect = System.Windows.Rect;

namespace BetterGenshinImpact.GameTask.MapMask;

/// <summary>
/// 地图遮罩触发器
/// </summary>
public class MapMaskTrigger : ITaskTrigger, IAsyncDisposable
{
    private readonly ILogger<MapMaskTrigger> _logger = App.GetLogger<MapMaskTrigger>();

    public string Name => "地图遮罩";
    public bool IsEnabled { get; set; }
    public int Priority => 1; // 低优先级
    public bool IsExclusive => false;

    public GameUiCategory SupportedGameUiCategory => GameUiCategory.Unknown;

    private readonly MapMaskConfig _config = TaskContext.Instance().Config.MapMaskConfig;
    private readonly string _mapMatchingMethod = TaskContext.Instance().Config.PathingConditionConfig.MapMatchingMethod;

    private readonly TemplateMatchStabilityDetector _detector = new();

    private DateTime _prevExecute = DateTime.MinValue;

    // 图像连续稳定次数
    private int _stableCount = 0;

    private ISceneMap _teyvatMap => MapManager.GetMap(MapTypes.Teyvat, _mapMatchingMethod);
    private OpenCvSharp.Rect _prevRect = default;
    private readonly object _prevRectLock = new();

    private const int RectDebounceThreshold = 3;

    private readonly NavigationInstance _navigationInstance = new();

    private sealed class PendingUiUpdate
    {
        public bool? IsInBigMapUi { get; init; }
        public Rect? BigMapViewport { get; init; }
        public Rect? MiniMapViewport { get; init; }
    }

    private PendingUiUpdate? _pendingUiUpdate;
    private int _uiApplyScheduled;

    private sealed class ComputeWorkItem : IDisposable
    {
        public required string MapMatchingMethod { get; init; }
        public Mat? Mat { get; set; }

        public void Dispose()
        {
            Mat?.Dispose();
            Mat = null;
        }
    }

    private readonly LatestOwnedWork<ComputeWorkItem> _bigMapWork;
    private readonly LatestOwnedWork<ComputeWorkItem> _miniMapWork;
    private int _stopping;

    public MapMaskTrigger()
    {
        _bigMapWork = new(ProcessBigMapCompute, error => _logger.LogDebug(error, "地图遮罩异步计算时发生异常"));
        _miniMapWork = new(ProcessMiniMapCompute, error => _logger.LogDebug(error, "地图遮罩异步计算时发生异常"));
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _stopping, 1);
        Interlocked.Exchange(ref _pendingUiUpdate, null);
        await Task.WhenAll(_bigMapWork.DisposeAsync().AsTask(), _miniMapWork.DisposeAsync().AsTask()).ConfigureAwait(false);
    }

    /// <summary>
    /// 初始化触发器状态，并在关闭时同步隐藏遮罩UI
    /// </summary>
    public void Init()
    {
        IsEnabled = _config.Enabled;

        // 关闭时隐藏UI
        if (!IsEnabled)
        {
            _bigMapWork.DiscardPending();
            _miniMapWork.DiscardPending();

            Interlocked.Exchange(ref _pendingUiUpdate, null);

            UIDispatcherHelper.BeginInvoke(() =>
            {
                if (Volatile.Read(ref _stopping) != 0) return;
                if (MaskWindow.InstanceNullable() != null)
                {
                    var window = MaskWindow.Instance();
                    if (window.DataContext is MaskWindowViewModel vm)
                    {
                        vm.IsInBigMapUi = false;
                    }

                    window.PointsCanvasControl.UpdateViewport(0, 0, 0, 0);
                    window.MiniMapPointsCanvasControl.UpdateViewport(0, 0, 0, 0);
                }
            });
        }
    }

    /// <summary>
    /// 接收每帧截图内容并驱动大地图/小地图的异步定位与UI更新
    /// </summary>
    /// <param name="content">捕获到的画面内容</param>
    public void OnCapture(CaptureContent content)
    {
        if (Volatile.Read(ref _stopping) != 0) return;
        if ((DateTime.Now - _prevExecute).TotalMilliseconds <= 50)
        {
            return;
        }

        _prevExecute = DateTime.Now;

        try
        {
            var region = content.CaptureRectArea;
            var inBigMapUi = content.CurrentGameUiCategory == GameUiCategory.BigMap || Bv.IsInBigMapUi(region);
            var mapMatchingMethod = TaskContext.Instance().Config.PathingConditionConfig.MapMatchingMethod;
            PendingUiUpdate? update = null;

            if (inBigMapUi)
            {
                if (_detector.IsStable(region.CacheGreyMat))
                {
                    _stableCount++;
                    if (_stableCount >= 20)
                    {
                        _stableCount = 0;
                    }
                }
                else
                {
                    _stableCount = 0;
                }

                if (_stableCount == 0)
                {
                    var greyMat = region.CacheGreyMat.Clone();
                    EnqueueBigMapCompute(new ComputeWorkItem
                    {
                        MapMatchingMethod = mapMatchingMethod,
                        Mat = greyMat
                    });
                }
            }
            else
            {
                // 主界面上展示小地图
                if (_config.MiniMapMaskEnabled)
                {
                    if (Bv.IsInMainUi(region))
                    {
                        var srcMat = region.SrcMat.Clone();
                        EnqueueMiniMapCompute(new ComputeWorkItem
                        {
                            MapMatchingMethod = mapMatchingMethod,
                            Mat = srcMat
                        });

                        // 自动记录路径
                        if (_config.PathAutoRecordEnabled)
                        {
                            // ...
                        }
                    }
                    else
                    {
                        update = new PendingUiUpdate { MiniMapViewport = new Rect(0, 0, 0, 0) };
                    }
                }

                lock (_prevRectLock)
                {
                    _prevRect = default;
                }
            }

            update = update == null
                ? new PendingUiUpdate { IsInBigMapUi = inBigMapUi }
                : new PendingUiUpdate
                {
                    IsInBigMapUi = inBigMapUi,
                    BigMapViewport = update.BigMapViewport,
                    MiniMapViewport = update.MiniMapViewport
                };

            QueueUiUpdate(update);
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "实时地图定位时发生异常");
        }
    }

    /// <summary>
    /// 入队大地图定位计算，仅保留正在执行与最新任务
    /// </summary>
    /// <param name="workItem">计算任务</param>
    private void EnqueueBigMapCompute(ComputeWorkItem workItem)
    {
        _bigMapWork.Enqueue(workItem);
    }

    /// <summary>
    /// 入队小地图定位计算，仅保留正在执行与最新任务
    /// </summary>
    /// <param name="workItem">计算任务</param>
    private void EnqueueMiniMapCompute(ComputeWorkItem workItem)
    {
        _miniMapWork.Enqueue(workItem);
    }

    /// <summary>
    /// 执行大地图定位计算并产出UI更新
    /// </summary>
    /// <param name="workItem">计算任务</param>
    private void ProcessBigMapCompute(ComputeWorkItem workItem)
    {
        if (workItem.Mat == null)
        {
            return;
        }

        OpenCvSharp.Rect prevRect;
        lock (_prevRectLock)
        {
            prevRect = _prevRect;
        }

        var sceneMap = (SceneBaseMap)MapManager.GetMap(MapTypes.Teyvat, workItem.MapMatchingMethod);
        var rect256 = BigMapTeyvat256Layer.GetInstance(sceneMap).GetBigMapRect(workItem.Mat, prevRect);
        if (rect256 != default)
        {
            if (rect256 is { Width: < 50, Height: < 40 } || rect256 is { Width: > 3000, Height: > 1800 })
            {
                lock (_prevRectLock)
                {
                    _prevRect = default;
                }
                return;
            }

            lock (_prevRectLock)
            {
                _prevRect = rect256;
            }
        }

        const int s = TeyvatMap.BigMap256ScaleTo2048;
        var rect2048 = new Rect(rect256.X * s, rect256.Y * s, rect256.Width * s, rect256.Height * s);
        QueueUiUpdate(new PendingUiUpdate { BigMapViewport = rect2048 });
    }

    /// <summary>
    /// 执行小地图定位计算并产出UI更新
    /// </summary>
    /// <param name="workItem">计算任务</param>
    private void ProcessMiniMapCompute(ComputeWorkItem workItem)
    {
        if (workItem.Mat == null)
        {
            return;
        }

        using var imageRegion = new ImageRegion(workItem.Mat, 0, 0);
        workItem.Mat = null;

        var miniPoint = _navigationInstance.GetPositionStable(imageRegion, nameof(MapTypes.Teyvat), workItem.MapMatchingMethod);
        if (miniPoint != default)
        {
            double viewportSize = MapAssets.MimiMapRect1080P.Width / 3.0 * 10;
            QueueUiUpdate(new PendingUiUpdate
            {
                MiniMapViewport = new Rect(
                    miniPoint.X - viewportSize / 2.0,
                    miniPoint.Y - viewportSize / 2.0,
                    viewportSize,
                    viewportSize)
            });
        }
        else
        {
            QueueUiUpdate(new PendingUiUpdate { MiniMapViewport = new Rect(0, 0, 0, 0) });
        }
    }

    /// <summary>
    /// 合并并异步投递UI更新
    /// </summary>
    /// <param name="update">待应用的UI更新</param>
    private void QueueUiUpdate(PendingUiUpdate update)
    {
        if (Volatile.Read(ref _stopping) != 0) return;
        Interlocked.Exchange(ref _pendingUiUpdate, update);
        TryScheduleUiApply();
    }

    /// <summary>
    /// 确保仅有一个UI更新调度在队列中
    /// </summary>
    private void TryScheduleUiApply()
    {
        if (Interlocked.Exchange(ref _uiApplyScheduled, 1) == 0)
        {
            UIDispatcherHelper.BeginInvoke(ApplyPendingUiUpdate);
        }
    }

    /// <summary>
    /// 在UI线程应用合并后的更新
    /// </summary>
    private void ApplyPendingUiUpdate()
    {
        // 已投递但尚未执行的UI回调必须在访问MaskWindow/DI前拒绝。
        if (Volatile.Read(ref _stopping) != 0) return;
        var update = Interlocked.Exchange(ref _pendingUiUpdate, null);
        if (update != null)
        {
            var window = MaskWindow.Instance();
            if (!_config.Enabled)
            {
                if (window.DataContext is MaskWindowViewModel vmWhenDisabled)
                {
                    vmWhenDisabled.IsInBigMapUi = false;
                }

                window.PointsCanvasControl.UpdateViewport(0, 0, 0, 0);
                window.MiniMapPointsCanvasControl.UpdateViewport(0, 0, 0, 0);
                Interlocked.Exchange(ref _uiApplyScheduled, 0);
                return;
            }

            if (update.IsInBigMapUi is { } isInBigMapUi && window.DataContext is MaskWindowViewModel vm)
            {
                vm.IsInBigMapUi = isInBigMapUi;
            }

            if (update.BigMapViewport is { } bigMapViewport)
            {
                window.PointsCanvasControl.UpdateViewport(bigMapViewport.X, bigMapViewport.Y, bigMapViewport.Width, bigMapViewport.Height);
            }

            if (update.MiniMapViewport is { } miniMapViewport)
            {
                window.MiniMapPointsCanvasControl.UpdateViewport(miniMapViewport.X, miniMapViewport.Y, miniMapViewport.Width, miniMapViewport.Height);
            }
        }

        Interlocked.Exchange(ref _uiApplyScheduled, 0);
        if (Volatile.Read(ref _pendingUiUpdate) != null)
        {
            TryScheduleUiApply();
        }
    }
}
