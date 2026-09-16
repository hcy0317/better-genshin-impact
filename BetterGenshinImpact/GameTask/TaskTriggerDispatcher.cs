using BetterGenshinImpact.Core.Config;
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
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.GameLoading;
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
        private readonly ILogger<TaskTriggerDispatcher> _logger = App.GetLogger<TaskTriggerDispatcher>();
        private readonly OverlayMetricsService? _metricsService = App.GetService<OverlayMetricsService>();
        private readonly CustomHtmlMaskService? _customHtmlMaskService = App.GetService<CustomHtmlMaskService>();
        private readonly FailureScreenshotFrameCache _failureScreenshotFrameCache = new(TimeSpan.FromSeconds(1));

        private static TaskTriggerDispatcher? _instance;

        private readonly System.Timers.Timer _timer = new();
        private readonly DispatcherDrainController _lifetime;
        private readonly object _lifecycleGate = new();
        private Task? _disposeTask;
        private bool _disposed;
        private List<ITaskTrigger>? _triggers;

        public IGameCapture? GameCapture { get; private set; }

        private static readonly object _locker = new();
        private int _frameIndex = 0;

        private RECT _gameRect = RECT.Empty;
        private bool _prevGameActive;


        private static readonly object _triggerListLocker = new();

        private WinEventHookOwner? _winEventHooks;

        public event EventHandler? UiTaskStopTickEvent;

        public event EventHandler? UiTaskStartTickEvent;

        private GameUiCategory PrevGameUiCategory = GameUiCategory.Unknown; // 上一个UI类别
        private DateTime PrevGameUiChangeTime = DateTime.Now; // 上一次UI变化时间


        public TaskTriggerDispatcher()
        {
            _lifetime = new(() => _timer.Stop(), async () =>
            {
                ClearTriggers();
                await GameTaskManager.DrainRetiredTriggersAsync().ConfigureAwait(false);
                await ReleaseStoppedCaptureAsync().ConfigureAwait(false);
            });
            _instance = this;
            _timer.Elapsed += Tick;
            //_timer.Tick += Tick;
        }

        public static TaskTriggerDispatcher Instance()
        {
            if (_instance == null)
            {
                throw new Exception("请先在启动页启动BetterGI，如果已经启动请重启");
            }

            return _instance;
        }

        internal static TaskTriggerDispatcher? Existing => _instance;

        public static IGameCapture GlobalGameCapture
        {
            get
            {
                _instance = Instance();

                if (_instance.GameCapture == null)
                {
                    throw new Exception("截图器未初始化!");
                }

                return _instance.GameCapture;
            }
        }

        public void ClearTriggers()
        {
            lock (_triggerListLocker)
            {
                GameTaskManager.ClearTriggers();
                _triggers?.Clear();
            }
        }

        public void SetTriggers(List<ITaskTrigger> list)
        {
            lock (_triggerListLocker)
            {
                _triggers = list;
            }
        }

        public bool AddTrigger(string name, object? externalConfig)
        {
            lock (_triggerListLocker)
            {
                if (GameTaskManager.AddTrigger(name, externalConfig))
                {
                    SetTriggers(GameTaskManager.ConvertToTriggerList(true));
                    return true;
                }

                return false;
            }
        }

        public void Start(IntPtr hWnd, CaptureModes mode, int interval = 50)
        {
            lock (_lifecycleGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _lifetime.PrepareStart();
                try
                {
                    // 初始化截图器
                    ChatUiHotkeyGuard.Reset();
                    _failureScreenshotFrameCache.Clear();
                    GameCapture = GameCaptureFactory.Create(mode);
                    // 激活窗口 保证后面能够正常获取窗口信息
                    SystemControl.ActivateWindow(hWnd);

                    // 初始化任务上下文(一定要在初始化触发器前完成)
                    TaskContext.Instance().Init(hWnd);

                    // 初始化触发器(一定要在任务上下文初始化完毕后使用)
                    _triggers = GameTaskManager.LoadInitialTriggers();
                    GameLoadingTrigger.GlobalEnabled = TaskContext.Instance().Config.GenshinStartConfig.AutoEnterGameEnabled;

                    // if (GraphicsCapture.IsHdrEnabled(hWnd))
                    // {
                    //     _logger.LogError("游戏窗口在HDR模式下无法获取正常颜色的截图，请关闭HDR模式！");
                    // }

                    // 启动截图
                    GameCapture.Start(hWnd,
                        new Dictionary<string, object>()
                        {
                    { "autoFixWin11BitBlt", OsVersionHelper.IsWindows11_OrGreater && TaskContext.Instance().Config.AutoFixWin11BitBlt }
                        }
                    );

                    // 使用 SetWinEventHook 监听窗口移动和大小变化事件
                    _winEventHooks = new WinEventHookOwner(Application.Current.Dispatcher, WinEventCallback,
                        observation => _logger.LogDebug(
                            "CAPTURE_HOOK id={Registration} phase={Phase} hook={Hook} ownerThread={OwnerThread} currentThread={CurrentThread} ownerManaged={OwnerManaged} currentManaged={CurrentManaged} success={Success} win32={Win32} ms={Milliseconds:F1} suppressed={Suppressed}",
                            observation.RegistrationId, observation.Phase, observation.Hook,
                            observation.OwnerThreadId, observation.CurrentThreadId, observation.OwnerManagedThreadId,
                            observation.CurrentManagedThreadId, observation.Succeeded, observation.Win32Error,
                            observation.Milliseconds, observation.Suppressed));
                    _winEventHooks.Register();

                    // 启动定时器
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
            _gameRect = RECT.Empty;
            _prevGameActive = false;
            List<Exception> failures = [];
            if (_winEventHooks != null)
            {
                try
                {
                    await _winEventHooks.ReleaseAsync().ConfigureAwait(false);
                    _winEventHooks = null;
                }
                catch (Exception error)
                {
                    failures.Add(error);
                    try { _logger.LogError(error, "调度器窗口钩子清理失败，保留原owner以便重试"); }
                    catch { /* 后续资源仍须尽力清理。 */ }
                }
            }
            failures.AddRange(TaskRunnerCleanup.RunAll(
            [
                ("截图器", () => { GameCapture?.Dispose(); GameCapture = null; }),
                ("画中画", () => PictureInPictureService.Hide(resetManual: true)),
                ("HTML遮罩", HtmlMaskWindow.CloseAll)
            ], (step, error) => _logger.LogError(error, "调度器清理失败: {Step}", step)));
            TaskRunnerFailurePolicy.ThrowCleanupFailures(failures);
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
            GameCapture?.Dispose();
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

                // 检查截图器是否初始化
                var maskWindow = MaskWindow.Instance();
                if (GameCapture == null || !GameCapture.IsCapturing)
                {
                    ChatUiHotkeyGuard.Reset();
                    RequestUiStop(TaskContext.Instance().SystemInfo.GameProcess.HasExited);
                    return;
                }

                // 如果是最小化状态，直接不进行截图
                if (SystemControl.IsGenshinImpactMinimized())
                {
                    ChatUiHotkeyGuard.Reset();
                    PictureInPictureService.Hide();
                    return;
                }

                // 检查游戏是否在前台
                var hasBackgroundTriggerToRun = false;
                var autoSkipConfig = TaskContext.Instance().Config.AutoSkipConfig;
                var shouldShowPictureInPicture = autoSkipConfig.Enabled
                                                 && autoSkipConfig.PictureInPictureEnabled
                                                 && !PictureInPictureService.IsManuallyClosed
                                                 && TaskControl.TaskSemaphore.CurrentCount == 1; // 没有任务持有锁（也就是没有任务正在运行）
                var active = SystemControl.IsGenshinImpactActive();
                if (!active)
                {
                    ChatUiHotkeyGuard.Reset();
                    // 检查游戏是否已结束
                    if (TaskContext.Instance().SystemInfo.GameProcess.HasExited)
                    {
                        RequestUiStop(gameExited: true);
                        return;
                    }

                    if (_prevGameActive)
                    {
                        Debug.WriteLine("游戏窗口不在前台, 不再进行截屏");
                    }

                    var pName = SystemControl.GetActiveProcessName();
                    if (pName != "Idle" && pName != "BetterGI" && pName != "YuanShen" && pName != "GenshinImpact" && pName != "Genshin Impact Cloud Game")
                    {
                        // Debug.WriteLine(pName + "：hide mask window");
                        maskWindow.Invoke(() => { maskWindow.HideSelf(); });
                        HtmlMaskWindow.HideAll();
                    }

                    _prevGameActive = active;

                    if (_triggers != null)
                    {
                        lock (_triggerListLocker)
                        {
                            var exclusive = _triggers.FirstOrDefault(t => t is { IsEnabled: true, IsExclusive: true });
                            if (exclusive != null)
                            {
                                hasBackgroundTriggerToRun = exclusive.IsBackgroundRunning;
                            }
                            else
                            {
                                hasBackgroundTriggerToRun = _triggers.Any(t => t is { IsEnabled: true, IsBackgroundRunning: true });
                            }
                        }
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
                else
                {
                    PictureInPictureService.Hide(resetManual: true);
                    // if (!_prevGameActive)
                    // {
                    maskWindow.BeginInvoke(() =>
                    {
                        if (_lifetime.IsStopping) return;
                        if (maskWindow.IsExist())
                        {
                            maskWindow.Show();
                            if (!_prevGameActive)
                            {
                                maskWindow.BringToTop();
                            }
                        }
                    });
                    _customHtmlMaskService?.ShowIfEnabled();
                    HtmlMaskWindow.ShowAll();
                    // }

                    _prevGameActive = active;
                    // // 移动游戏窗口的时候同步遮罩窗口的位置,此时不进行捕获
                    if (SyncMaskWindowPosition())
                    {
                        return;
                    }
                }

                var hasEnabledTriggers = _triggers != null && _triggers.Exists(t => t.IsEnabled);
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
                var captureFrame = GameCapture.Capture();
                var bitmap = captureFrame?.Frame;
                tickMetrics.EndCapture();
                speedTimer.Record("截图");

                if (bitmap == null)
                {
                    _logger.LogWarning("截图失败!");
                    return;
                }

                try
                {
                    _failureScreenshotFrameCache.TryUpdate(bitmap, DateTimeOffset.UtcNow);
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
                using var content = new CaptureContent(captureFrame!, _frameIndex, _timer.Interval);
                ChatUiHotkeyGuard.UpdateVisualState(Bv.DetectChatUi(content.CaptureRectArea));

                if (!hasEnabledTriggers)
                {
                    return;
                }

                lock (_triggerListLocker)
                {
                    var needRunTriggers = new List<ITaskTrigger>(); // 最终要执行的触发器列表
                    var exclusiveTrigger = _triggers!.FirstOrDefault(t => t is { IsEnabled: true, IsExclusive: true });
                    if (exclusiveTrigger != null)
                    {
                        needRunTriggers.Add(exclusiveTrigger);
                    }
                    else
                    {
                        var runningTriggers = _triggers!.Where(t => t.IsEnabled);
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
        /// / 移动游戏窗口的时候同步遮罩窗口的位置
        /// </summary>
        /// <returns></returns>
        private bool SyncMaskWindowPosition()
        {
            var hWnd = TaskContext.Instance().GameHandle;
            var currentRect = SystemControl.GetCaptureRect(hWnd);
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
                MaskWindow.Instance().RefreshPosition();
                HtmlMaskWindow.UpdateAllPositions();
                return true;
            }

            return false;
        }

        private bool SizeIsZero(RECT rect)
        {
            return rect.Width == 0 || rect.Height == 0;
        }

        private void WinEventCallback(User32.HWINEVENTHOOK hWinEventHook, uint @event, HWND hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (!_lifetime.TryEnter()) return;
            try
            {
                var target = TaskContext.Instance().GameHandle;
                if (target == IntPtr.Zero)
                {
                    return;
                }

                if (idObject != 0)
                {
                    return;
                }

                var hwndPtr = hwnd.DangerousGetHandle();
                if (hwndPtr == target)
                {
                    SyncMaskWindowPosition();
                }
            }
            finally { _lifetime.Exit(); }
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
