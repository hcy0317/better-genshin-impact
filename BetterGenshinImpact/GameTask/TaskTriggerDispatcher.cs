using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Mask;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.Helpers;
using BetterGenshinImpact.View;
using Fischless.GameCapture;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BetterGenshinImpact.GameTask.AutoEat;
using BetterGenshinImpact.GameTask.AutoPick;
using BetterGenshinImpact.GameTask.AutoSkip;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Runtime;
using Fischless.GameCapture.Graphics;
using BetterGenshinImpact.Service;
using BetterGenshinImpact.Service.Model;
using BetterGenshinImpact.Service.Model.OverlayMetric;
using Vanara.PInvoke;
using Rect = OpenCvSharp.Rect;

namespace BetterGenshinImpact.GameTask
{
    public class TaskTriggerDispatcher : IDisposable, IAsyncDisposable
    {
        private readonly ILogger<TaskTriggerDispatcher> _logger;
        private readonly OverlayMetricsService _metricsService;
        private readonly IMaskWindowHost _maskWindowHost;

        private readonly System.Timers.Timer _timer = new();
        private readonly FailureScreenshotFrameCache _failureScreenshotFrameCache = new(TimeSpan.FromSeconds(1));
        private static TaskTriggerDispatcher? _instance;
        private readonly DispatcherDrainController _lifetime;
        private readonly object _lifecycleGate = new();
        private Task? _disposeTask;
        private bool _disposed;

        /// <summary>
        /// 脚本可以通过 AddTrigger 启用的触发器
        /// </summary>
        private static readonly Dictionary<string, Type> ScriptTriggerTypes = new()
        {
            ["AutoPick"] = typeof(AutoPickTrigger),
            ["AutoSkip"] = typeof(AutoSkipTrigger),
            ["AutoEat"] = typeof(AutoEatTrigger),
        };

        /// <summary>
        /// 本次截图会话的触发器，按优先级从高到低排列。Start 时整体替换，之后不再修改；启停状态只在 Tick 中读写
        /// </summary>
        private volatile List<TriggerSlot> _slots = [];

        /// <summary>
        /// 保护 _inTask 与 _leases。任意线程修改，Tick 在锁内拷贝一份后使用，锁内不做其他事
        /// </summary>
        private readonly object _leaseLock = new();

        /// <summary>
        /// 是否正在执行独立任务。任务中不看用户配置，只运行启用名单里的触发器
        /// </summary>
        private bool _inTask;

        /// <summary>
        /// 任务期间的启用名单，按加入顺序排列
        /// </summary>
        private readonly List<TriggerLease> _leases = [];

        /// <summary>
        /// 上一次截图会话留下的触发器，下一帧对其中仍处于启用状态的调用 OnDisabled。由 _leaseLock 保护
        /// </summary>
        private readonly List<TriggerSlot> _retiredSlots = [];

        /// <summary>
        /// 当前绑定的运行环境，由 GameRuntimeService 通过 Start / Stop 设置。Tick 线程只读
        /// </summary>
        private volatile GameRuntime? _runtime;

        /// <summary>
        /// 仅供本类保存截图使用；当前运行环境的截图器，未启动时为 null
        /// </summary>
        private IGameCapture? GameCapture => _runtime?.Capture;

        private static readonly object _locker = new();
        private int _frameIndex = 0;

        private RECT _gameRect = RECT.Empty;
        private bool _prevGameActive;


        /// <summary>
        /// 截图器停止或游戏已退出。由 GameRuntimeService 订阅并停止运行环境
        /// </summary>
        public event EventHandler? UiTaskStopTickEvent;

        private GameUiCategory PrevGameUiCategory = GameUiCategory.Unknown; // 上一个UI类别
        private DateTime PrevGameUiChangeTime = DateTime.Now; // 上一次UI变化时间


        public TaskTriggerDispatcher(
            ILogger<TaskTriggerDispatcher> logger,
            OverlayMetricsService metricsService,
            IMaskWindowHost maskWindowHost)
        {
            _logger = logger;
            _metricsService = metricsService;
            _maskWindowHost = maskWindowHost;
            _lifetime = new(() => _timer.Stop(), ReleaseStoppedCaptureAsync);
            _instance = this;
            _timer.Elapsed += Tick;
            //_timer.Tick += Tick;
        }

        public static TaskTriggerDispatcher Instance()
        {
            return App.GetService<TaskTriggerDispatcher>()
                   ?? throw new InvalidOperationException("调度器未注册");
        }

        internal static TaskTriggerDispatcher? Existing => _instance;
        public static IGameCapture GlobalGameCapture => TaskContext.Instance().Runtime?.Capture
            ?? throw new InvalidOperationException("截图器未初始化!");

        /// <summary>
        /// 兼容旧调用方，从 DI 容器获取调度器；截图器是否已启动应查看当前 GameRuntime
        /// </summary>
        public static TaskTriggerDispatcher? InstanceNullable() => App.GetService<TaskTriggerDispatcher>();

        /// <summary>
        /// 任务开始：进入任务模式并清空启用名单。用户开启的触发器在下一帧停用
        /// </summary>
        public void BeginTask()
        {
            lock (_leaseLock)
            {
                _inTask = true;
                _leases.Clear();
            }
        }

        /// <summary>
        /// 任务结束：退出任务模式并清空启用名单，下一帧按用户配置恢复。重复调用无副作用
        /// </summary>
        public void EndTask()
        {
            lock (_leaseLock)
            {
                _inTask = false;
                _leases.Clear();
            }
        }

        /// <summary>
        /// 任务期间启用一个触发器（加入启用名单）。只接受 "AutoPick"、"AutoSkip"、"AutoEat"。
        /// 同一个触发器在名单里有多条时，使用最后加入那一条的参数。不在任务中时名单不生效
        /// </summary>
        /// <param name="name">触发器名称</param>
        /// <param name="options">脚本传入的参数，AutoPick 为 AutoPickExternalConfig，AutoSkip 为 AutoSkipConfig</param>
        /// <returns>租约，Dispose 时撤销这一条；名称不支持时返回 null。不需要中途撤销的调用方可以忽略返回值</returns>
        public IDisposable? AddTrigger(string name, object? options = null)
        {
            if (!ScriptTriggerTypes.TryGetValue(name, out var triggerType))
            {
                return null;
            }

            var lease = new TriggerLease(this, triggerType, options);
            lock (_leaseLock)
            {
                _leases.Add(lease);
            }

            return lease;
        }

        /// <summary>
        /// 清空启用名单，不销毁任何触发器实例
        /// </summary>
        public void ClearTriggers()
        {
            lock (_leaseLock)
            {
                _leases.Clear();
            }
        }

        /// <summary>
        /// 取当前截图会话中的触发器实例，供诊断等只读场景使用。截图器未启动时返回 null
        /// </summary>
        public T? GetTrigger<T>() where T : class, ITaskTrigger
        {
            foreach (var slot in _slots)
            {
                if (slot.Trigger is T trigger)
                {
                    return trigger;
                }
            }

            return null;
        }

        private void RemoveLease(TriggerLease lease)
        {
            lock (_leaseLock)
            {
                _leases.Remove(lease);
            }
        }

        /// <summary>
        /// 开始调度。运行环境的截图器、输入和 TaskContext 已由 GameRuntimeService 准备好
        /// </summary>
        internal bool RearmGameLoadingTriggerForStartupRetry()
        {
            lock (_locker) return GetTrigger<GameLoading.GameLoadingTrigger>()?.RearmForStartupRetry() == true;
        }

        public void Start(GameRuntime runtime, int interval = 50)
        {
            ArgumentNullException.ThrowIfNull(runtime);
            lock (_lifecycleGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _lifetime.PrepareStart();
                try
                {
                    ChatUiHotkeyGuard.Reset();
                    _failureScreenshotFrameCache.Clear();
                    _runtime = runtime;
                    _slots = GameTaskManager.CreateTriggers().Select(t => new TriggerSlot(t)).ToList();
                    runtime.Window.ViewportChanged += OnViewportChanged;
                    _frameIndex = 0;
                    _timer.Interval = interval;
                    _lifetime.Activate(() => { if (!_timer.Enabled) _timer.Start(); });
                }
                catch
                {
                    ObserveStop(_lifetime.StopAsync());
                    throw;
                }
            }
        }

        /// <summary>
        /// 停止调度。截图器和窗口监听随运行环境一起由 GameRuntimeService 释放
        /// </summary>
        public void Stop()
        {
            var stopped = StopAsync();
            if (_lifetime.IsCurrentCallback || Application.Current?.Dispatcher.CheckAccess() == true)
                ObserveStop(stopped);
            else stopped.GetAwaiter().GetResult();
        }

        public Task StopAsync()
        {
            lock (_lifecycleGate) return _lifetime.StopAsync();
        }

        internal bool IsCurrentStopRequest(long generation) => _lifetime.IsCurrentStopRequest(generation);

        private void RequestUiStop(bool gameExited)
        {
            // 先停止接收新tick，再交UI取消任务并排空；不能等待UI接单期间继续反复投递。
            if (!_lifetime.RequestStop(out var generation)) return;
            try
            {
                if (gameExited) _logger.LogInformation("游戏已退出，BetterGI 请求停止截图器，captureGeneration={Generation}", generation);
                else _logger.LogError("截图器未初始化，请求停止，captureGeneration={Generation}", generation);
            }
            catch { /* 记录故障不能阻止已经取得所有权的停止通知。 */ }
            UiTaskStopTickEvent?.Invoke(this, new CaptureStopRequestedEventArgs(generation));
        }

        private void ObserveStop(Task task)
        {
            _ = task.ContinueWith(failed => _logger.LogError(failed.Exception, "停止截图调度器失败"),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }

        private async Task ReleaseStoppedCaptureAsync()
        {
            ChatUiHotkeyGuard.Reset();
            // Tick/视口回调已排空，接着排空拥有Mat的触发器，再由Runtime owner释放截图器。
            var slots = _slots;
            var failures = new List<Exception>();
            foreach (var slot in slots)
            {
                try
                {
                    if (slot.IsActive) { slot.Trigger.OnDisabled(); slot.IsActive = false; }
                }
                catch (Exception error) { failures.Add(error); }
            }
            foreach (var trigger in slots.Select(slot => slot.Trigger).OfType<IAsyncDisposable>())
            {
                try { await trigger.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) { failures.Add(error); }
            }
            if (failures.Count != 0) throw new AggregateException("触发器停止或排空失败", failures);
            _slots = [];
            EndTask();
            var runtime = _runtime;
            if (runtime != null)
            {
                runtime.Window.ViewportChanged -= OnViewportChanged;
                _runtime = null;
            }

            _gameRect = RECT.Empty;
            _prevGameActive = false;
            PictureInPictureService.Hide(resetManual: true);
            HtmlMaskWindow.CloseAll();
        }

        public void StartTimer()
        {
            lock (_lifecycleGate)
            {
                if (_disposed || _lifetime.IsStopping) return;
                _lifetime.ScheduleTimer(() => { if (!_timer.Enabled) _timer.Start(); });
            }
        }

        public void StopTimer()
        {
            lock (_lifecycleGate)
            {
                if (!_disposed && _timer.Enabled) _timer.Stop();
            }

            ChatUiHotkeyGuard.Reset();
        }

        internal Mat? TryCloneLatestFrame(out TimeSpan age)
        {
            return _failureScreenshotFrameCache.TryClone(DateTimeOffset.UtcNow, out age);
        }

        internal GameCaptureFrame? TryCloneLatestCaptureFrame(out TimeSpan age)
        {
            var pixels = _failureScreenshotFrameCache.TryClone(DateTimeOffset.UtcNow, out age, out var stamp);
            return pixels == null ? null : new GameCaptureFrame(pixels, stamp);
        }

        public void Dispose()
        {
            var disposed = DisposeAsync().AsTask();
            if (_lifetime.IsCurrentCallback || Application.Current?.Dispatcher.CheckAccess() == true) ObserveStop(disposed);
            else disposed.GetAwaiter().GetResult();
        }

        public ValueTask DisposeAsync()
        {
            lock (_lifecycleGate)
            {
                _disposed = true;
                if (_disposeTask?.IsFaulted == true) _disposeTask = null;
                return new(_disposeTask ??= DisposeCoreAsync());
            }
        }
        private async Task DisposeCoreAsync()
        {
            await StopAsync().ConfigureAwait(false);
            _timer.Elapsed -= Tick;
            _timer.Dispose();
            _failureScreenshotFrameCache.Dispose();
        }

        public void Tick(object? sender, EventArgs e)
        {
            if (!_lifetime.TryEnter()) return;
            var hasLock = false;
            DispatcherTickMetrics? tickMetrics = null;
            try
            {
                tickMetrics = new DispatcherTickMetrics();
                // 上一帧还没处理完时只记录跳过次数，不等待锁；等待时间不应混入本轮处理耗时。
                Monitor.TryEnter(_locker, ref hasLock);
                if (!hasLock)
                {
                    _metricsService?.RecordSkippedTick();
                    // 正在执行时跳过
                    return;
                }

                var runtime = _runtime;
                if (runtime == null)
                {
                    // Stop 之后残留的一次调度
                    return;
                }

                // 先同步触发器启停状态，再做最小化、前台等判断：游戏在后台时，停用也能及时收尾
                var runnableTriggers = SyncTriggerStates();

                var window = runtime.Window;
                var gameCapture = runtime.Capture;

                // 检查截图器是否在运行、游戏是否已退出
                var alive = window.IsAlive;
                if (!gameCapture.IsCapturing || !alive)
                {
                    if (!ReferenceEquals(_runtime, runtime))
                    {
                        // 本轮调度期间运行环境已被主动停止并释放，不是游戏退出
                        return;
                    }

                    ChatUiHotkeyGuard.Reset();
                    _maskWindowHost.ReportGameWindow(new GameWindowState(false, false, false, false, default));
                    RequestUiStop(gameExited: !alive);
                    return;
                }

                // 如果是最小化状态，直接不进行截图
                if (window.IsMinimized)
                {
                    ChatUiHotkeyGuard.Reset();
                    PictureInPictureService.Hide();
                    _maskWindowHost.ReportGameWindow(new GameWindowState(true, false, true, false, default));
                    return;
                }

                // 检查游戏是否在前台
                var hasBackgroundTriggerToRun = false;
                var autoSkipConfig = TaskContext.Instance().Config.AutoSkipConfig;
                var shouldShowPictureInPicture = autoSkipConfig.Enabled
                                                 && autoSkipConfig.PictureInPictureEnabled
                                                 && !PictureInPictureService.IsManuallyClosed
                                                 && TaskControl.TaskSemaphore.CurrentCount == 1; // 没有任务持有锁（也就是没有任务正在运行）
                var active = window.IsForeground;
                if (!active)
                {
                    ChatUiHotkeyGuard.Reset();

                    if (_prevGameActive)
                    {
                        Debug.WriteLine("游戏窗口不在前台, 不再进行截屏");
                    }

                    // 只上报事实，前台是其他进程时是否隐藏遮罩由遮罩宿主统一判断
                    _maskWindowHost.ReportGameWindow(new GameWindowState(true, false, false,
                        IsForegroundOwnedByBetterGiOrGame(window), default));

                    _prevGameActive = active;

                    // 输入依赖前台时，失焦后只执行后台触发器；
                    // 输入不依赖前台的运行环境（网页版）失焦后照常执行全部触发器
                    if (window.RequiresForeground)
                    {
                        var exclusive = runnableTriggers.FirstOrDefault(t => t.IsExclusive);
                        if (exclusive != null)
                        {
                            hasBackgroundTriggerToRun = exclusive.IsBackgroundRunning;
                        }
                        else
                        {
                            hasBackgroundTriggerToRun = runnableTriggers.Any(t => t.IsBackgroundRunning);
                        }

                        if (!hasBackgroundTriggerToRun && shouldShowPictureInPicture)
                        {
                            hasBackgroundTriggerToRun = true;
                        }

                        if (!hasBackgroundTriggerToRun)
                        {
                            // 没有后台运行的触发器，这次不再进行截图
                            PictureInPictureService.Hide();
                            return;
                        }
                    }
                }
                else
                {
                    PictureInPictureService.Hide(resetManual: true);
                    // 只上报事实，显示、置顶、跟随位置由遮罩宿主去重后决定，不会每帧切 UI 线程
                    _maskWindowHost.ReportGameWindow(new GameWindowState(true, true, false, true,
                        window.Viewport.ScreenRect));

                    _prevGameActive = active;
                    // // 移动游戏窗口的时候同步遮罩窗口的位置,此时不进行捕获
                    if (SyncMaskWindowPosition())
                    {
                        return;
                    }
                }

                var hasEnabledTriggers = runnableTriggers.Count > 0;
                if (TaskTriggerCapturePolicy.ShouldSkip(
                        active,
                        TaskControl.TaskSemaphore.CurrentCount == 0,
                        hasEnabledTriggers))
                {
                    return;
                }
                if (!hasEnabledTriggers && !active)
                {
                    // Debug.WriteLine("没有可用的触发器且不处于仅截屏状态, 不再进行截屏");
                    return;
                }

                // 帧序号自增 1分钟后归零(MaxFrameIndexSecond)
                _frameIndex = (_frameIndex + 1) % (int)(CaptureContent.MaxFrameIndexSecond * 1000d / _timer.Interval);

                var speedTimer = new SpeedTimer();
                // 从真正开始截图处计时，前面的窗口状态检查不计入 BetterGI 本轮处理耗时。
                // 仅在遮罩指标开启时启动采样：未 Begin 时 EndCapture/AddTriggerCost/Publish 均按设计空转。
                if (_metricsService is { IsEnabled: true })
                {
                    tickMetrics.Begin();
                }
                // 捕获游戏画面
                var captureFrame = gameCapture.Capture();
                var bitmap = captureFrame?.Frame;
                tickMetrics.EndCapture();
                speedTimer.Record("截图");

                if (bitmap == null)
                {
                    _logger.LogWarning("截图失败!");
                    return;
                }

                // 从这里接管帧所有权：画中画或诊断发布抛错也不能泄漏本帧Mat。
                using var content = new CaptureContent(captureFrame!, _frameIndex, _timer.Interval);

                try
                {
                    _failureScreenshotFrameCache.TryUpdate(bitmap, DateTimeOffset.UtcNow, captureFrame!.Stamp);
                }
                catch (Exception cacheException)
                {
                    // 诊断缓存不能影响实时任务处理。
                    _logger.LogDebug(cacheException, "更新错误截图缓存帧失败");
                }

                if (shouldShowPictureInPicture && !active)
                {
                    PictureInPictureService.Update(bitmap);
                }
                else
                {
                    PictureInPictureService.Hide();
                }

                // 循环执行所有触发器 有独占状态的触发器的时候只执行独占触发器
                ChatUiHotkeyGuard.UpdateVisualState(Bv.DetectChatUi(content.CaptureRectArea));

                if (!hasEnabledTriggers)
                {
                    return;
                }

                var needRunTriggers = new List<ITaskTrigger>(); // 最终要执行的触发器列表
                var exclusiveTrigger = runnableTriggers.FirstOrDefault(t => t.IsExclusive);
                if (exclusiveTrigger != null)
                {
                    needRunTriggers.Add(exclusiveTrigger);
                }
                else
                {
                    IEnumerable<ITaskTrigger> runningTriggers = runnableTriggers;
                    if (hasBackgroundTriggerToRun)
                    {
                        runningTriggers = runningTriggers.Where(t => t.IsBackgroundRunning);
                    }

                    needRunTriggers.AddRange(runningTriggers);
                }

                if (needRunTriggers.Count > 0)
                {
                    // 判断当前UI
                    content.CurrentGameUiCategory = Bv.WhichGameUiForTriggers(content.CaptureRectArea);

                    if (content.CurrentGameUiCategory != PrevGameUiCategory)
                    {
                        PrevGameUiChangeTime = DateTime.Now;
                    }

                    foreach (var trigger in needRunTriggers)
                    {
                        if ((PrevGameUiCategory != content.CurrentGameUiCategory || (DateTime.Now - PrevGameUiChangeTime).TotalSeconds <= 30) // UI变化了后的30s内则所有触发器执行一遍
                            || trigger.SupportedGameUiCategory == content.CurrentGameUiCategory)
                        {
                            // 触发器耗时只累计触发器执行本体，便于和截图耗时、总处理耗时拆开观察。
                            var triggerStart = Stopwatch.GetTimestamp();
                            trigger.OnCapture(content);
                            tickMetrics.AddTriggerCost(triggerStart);
                            speedTimer.Record(trigger.Name);
                        }
                    }

                    PrevGameUiCategory = content.CurrentGameUiCategory;
                }

                speedTimer.DebugPrint();
            }
            finally
            {
                try
                {
                    try { tickMetrics?.EndProcessing(); }
                    finally { if (hasLock) Monitor.Exit(_locker); }

                    if (tickMetrics?.IsEnabled == true)
                    {
                        // 释放调度锁后再发布指标，避免 UI 订阅回调参与实时触发器锁竞争。
                        tickMetrics.Publish(_metricsService);
                    }
                }
                finally { _lifetime.Exit(); }
            }
        }

        /// <summary>
        /// 算出每个触发器本帧的状态，与上一帧不同时调用 OnEnabled / OnDisabled。只在 Tick 中调用（持有 _locker）。
        /// <para>规则：任务中只看启用名单，同一个触发器取最后加入那一条的参数；不在任务中只看用户配置。</para>
        /// </summary>
        /// <returns>本帧可以调用 OnCapture 的触发器，按优先级排列。暂停计数大于 0 时为空</returns>
        private List<ITaskTrigger> SyncTriggerStates()
        {
            bool inTask;
            TriggerLease[] leases;
            TriggerSlot[] retiredSlots;
            lock (_leaseLock)
            {
                inTask = _inTask;
                leases = [.. _leases];
                retiredSlots = [.. _retiredSlots];
                _retiredSlots.Clear();
            }

            // 上一次截图会话留下的触发器：只做收尾，不再运行
            foreach (var slot in retiredSlots)
            {
                if (slot.IsActive)
                {
                    slot.IsActive = false;
                    slot.Options = null;
                    InvokeSafely(slot.Trigger, nameof(ITaskTrigger.OnDisabled), static t => t.OnDisabled());
                }
            }

            var slots = _slots;
            var disabledByUser = false;
            var activeTriggers = new List<ITaskTrigger>(slots.Count);
            foreach (var slot in slots)
            {
                var trigger = slot.Trigger;
                bool shouldBeActive;
                object? options = null;
                if (inTask)
                {
                    var lease = FindLastLease(leases, trigger.GetType());
                    shouldBeActive = lease != null;
                    options = lease?.Options;
                }
                else
                {
                    shouldBeActive = trigger.IsEnabledByConfig;
                }

                // 参数按引用比较；参数变了等同于重新启用，运行状态随之重置
                if (shouldBeActive != slot.IsActive || !ReferenceEquals(options, slot.Options))
                {
                    if (slot.IsActive)
                    {
                        InvokeSafely(trigger, nameof(ITaskTrigger.OnDisabled), static t => t.OnDisabled());
                        disabledByUser |= !shouldBeActive && !inTask;
                    }

                    slot.IsActive = shouldBeActive;
                    slot.Options = options;
                    if (shouldBeActive)
                    {
                        InvokeSafely(trigger, nameof(ITaskTrigger.OnEnabled), t => t.OnEnabled(options));
                    }

                    _logger.LogDebug("实时触发器 {Name} {State}", trigger.Name, shouldBeActive ? "启用" : "停用");
                }

                if (slot.IsActive)
                {
                    activeTriggers.Add(trigger);
                }
            }

            if (disabledByUser)
            {
                // 用户关闭了触发器：擦掉留在遮罩上的识别结果。任务开始、结束时的清理由 TaskRunner 负责
                _runtime?.MaskWindowDrawingBoard.ClearAll();
            }

            // 暂停（热键、战斗、传送、选 F 选项等）：本帧不调用任何 OnCapture，触发器状态保留
            if (RunnerContext.Instance.AutoPickTriggerStopCount > 0)
            {
                return [];
            }

            return activeTriggers;
        }

        private static TriggerLease? FindLastLease(TriggerLease[] leases, Type triggerType)
        {
            for (var i = leases.Length - 1; i >= 0; i--)
            {
                if (leases[i].TriggerType == triggerType)
                {
                    return leases[i];
                }
            }

            return null;
        }

        /// <summary>
        /// 回调抛出的异常只记日志，不影响其他触发器，也不影响状态迁移
        /// </summary>
        private void InvokeSafely(ITaskTrigger trigger, string callbackName, Action<ITaskTrigger> callback)
        {
            try
            {
                callback(trigger);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "实时触发器 {Name} 执行 {Callback} 失败", trigger.Name, callbackName);
            }
        }

        /// <summary>
        /// 一个触发器实例，以及它上一帧的启停状态和参数。只在 Tick 中读写
        /// </summary>
        private sealed class TriggerSlot(ITaskTrigger trigger)
        {
            public ITaskTrigger Trigger { get; } = trigger;

            public bool IsActive { get; set; }

            public object? Options { get; set; }
        }

        /// <summary>
        /// 启用名单中的一条。Dispose 时从名单中删除，重复调用无副作用
        /// </summary>
        private sealed class TriggerLease(TaskTriggerDispatcher owner, Type triggerType, object? options) : IDisposable
        {
            public Type TriggerType { get; } = triggerType;

            public object? Options { get; } = options;

            public void Dispose() => owner.RemoveLease(this);
        }

        /// <summary>
        /// / 移动游戏窗口的时候同步遮罩窗口的位置
        /// </summary>
        /// <returns></returns>
        private bool SyncMaskWindowPosition()
        {
            var runtime = _runtime;
            if (runtime == null)
            {
                return false;
            }

            var currentRect = runtime.Window.Viewport.ScreenRect;
            if (_gameRect == RECT.Empty)
            {
                _gameRect = new RECT(currentRect);
            }
            else if (_gameRect != currentRect)
            {
                // // 后面大概可以取消掉这个判断，支持随意移动变化窗口 —— 现在已经可以取消了，但是一些Assets要重新加载
                // if ((_gameRect.Width != currentRect.Width || _gameRect.Height != currentRect.Height)
                //     && !SizeIsZero(_gameRect) && !SizeIsZero(currentRect))
                // {
                //     _logger.LogError("► 游戏窗口大小发生变化 {W}x{H}->{CW}x{CH}, 自动重启截图器中...", _gameRect.Width, _gameRect.Height, currentRect.Width, currentRect.Height);
                //     UiTaskStopTickEvent?.Invoke(null, EventArgs.Empty);
                //     UiTaskStartTickEvent?.Invoke(null, EventArgs.Empty);
                //     _logger.LogInformation("► 游戏窗口大小发生变化，截图器重启完成！");
                // }

                if ((_gameRect.Width != currentRect.Width || _gameRect.Height != currentRect.Height) && !SizeIsZero(_gameRect) && !SizeIsZero(currentRect))
                {
                    _logger.LogError("► 游戏窗口大小发生变化 {W}x{H}->{CW}x{CH}, 无需重新启动截图器。", _gameRect.Width, _gameRect.Height, currentRect.Width, currentRect.Height);
                }

                _gameRect = new RECT(currentRect);
                TaskContext.Instance().SystemInfo.CaptureAreaRect = currentRect;
                // 遮罩和 HTML 遮罩的位置由遮罩宿主统一跟随
                var window = runtime.Window;
                var active = window.IsForeground;
                _maskWindowHost.ReportGameWindow(new GameWindowState(
                    runtime.Capture.IsCapturing,
                    active,
                    window.IsMinimized,
                    active || IsForegroundOwnedByBetterGiOrGame(window),
                    currentRect));
                return true;
            }

            return false;
        }

        private bool SizeIsZero(RECT rect)
        {
            return rect.Width == 0 || rect.Height == 0;
        }

        /// <summary>
        /// 游戏窗口移动或缩放（由 IGameWindow 在 UI 线程上通知）
        /// </summary>
        private void OnViewportChanged(object? sender, EventArgs e)
        {
            if (!_lifetime.TryEnter()) return;
            try { SyncMaskWindowPosition(); }
            finally { _lifetime.Exit(); }
        }

        /// <summary>
        /// 游戏不在前台时，前台窗口属于 BetterGI 自身或游戏进程（或者没有前台窗口）就保留遮罩。
        /// 按进程 ID 判断，另一个 BetterGI 实例在前台时本实例的遮罩会隐藏，避免多个置顶遮罩叠在一起
        /// </summary>
        private static bool IsForegroundOwnedByBetterGiOrGame(IGameWindow window)
        {
            var foreground = User32.GetForegroundWindow();
            if (foreground.IsNull)
            {
                return true;
            }

            _ = User32.GetWindowThreadProcessId(foreground, out var pid);
            return pid == 0 || pid == (uint)Environment.ProcessId || pid == (uint)window.ProcessId;
        }

        public void TakeScreenshot()
        {
            SaveScreenshot("手动截图", string.Empty);
        }

        internal void TakeFailureScreenshot(string context)
        {
            SaveScreenshot(context, "error-");
        }

        private void SaveScreenshot(string context, string fileNamePrefix)
        {
            if (!_lifetime.TryEnter()) return;
            try
            {
                var path = Global.Absolute($@"log\screenshot\");
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                }

                Mat? mat;
                try
                {
                    mat = TaskControl.CaptureGameImage(GameCapture);
                }
                catch (Exception captureException)
                {
                    mat = _failureScreenshotFrameCache.TryClone(DateTimeOffset.UtcNow, out var cachedFrameAge);
                    if (mat == null)
                    {
                        _logger.LogInformation("截图失败，未获取到实时图像且没有可用的缓存帧");
                        _logger.LogDebug(captureException, "实时截图失败且缓存帧不可用");
                        return;
                    }

                    _logger.LogWarning(
                        captureException,
                        "实时截图失败，改用最近有效缓存帧；缓存帧年龄 {AgeSeconds:F1} 秒",
                        cachedFrameAge.TotalSeconds);
                }

                using (mat)
                {
                    var name = $@"{fileNamePrefix}{DateTime.Now:yyyyMMddHHmmssffff}.png";
                    var savePath = Global.Absolute($@"log\screenshot\{name}");
                    if (TaskContext.Instance().Config.CommonConfig.ScreenshotUidCoverEnabled)
                    {
                        var assetScale = TaskContext.Instance().SystemInfo.ScaleTo1080PRatio;
                        var rect = new Rect((int)(mat.Width - MaskWindowConfig.UidCoverRightBottomRect.X * assetScale),
                            (int)(mat.Height - MaskWindowConfig.UidCoverRightBottomRect.Y * assetScale),
                            (int)(MaskWindowConfig.UidCoverRightBottomRect.Width * assetScale),
                            (int)(MaskWindowConfig.UidCoverRightBottomRect.Height * assetScale));
                        mat.Rectangle(rect, Scalar.White, -1);
                        if (!Cv2.ImWrite(savePath, mat))
                        {
                            throw new IOException($"OpenCV failed to write screenshot: {savePath}");
                        }
                    }
                    else
                    {
                        if (!Cv2.ImWrite(savePath, mat))
                        {
                            throw new IOException($"OpenCV failed to write screenshot: {savePath}");
                        }
                    }

                    _logger.LogInformation("截图已保存: {Name}；上下文: {Context}", name, context);
                }
            }
            catch (Exception e)
            {
                _logger.LogError("截图保存失败: {Message}", e.Message);
                _logger.LogDebug("截图保存失败: {StackTrace}", e.StackTrace);
            }
            finally { _lifetime.Exit(); }
        }
    }
}
