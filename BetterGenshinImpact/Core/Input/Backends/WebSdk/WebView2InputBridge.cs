using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace BetterGenshinImpact.Core.Input.Backends.WebSdk;

/// <summary>
/// 基于 WebView2 的 SDK 调用桥：负责把 SDK 注入页面、把调用投递到页面、查询 SDK 状态。
/// <para>
/// 宿主只需调用 <see cref="InstallAsync"/>。本类不持有 WebView2 的生命周期，由宿主创建和释放。
/// </para>
/// </summary>
public sealed class WebView2InputBridge : IWebInputBridge, IDisposable
{
    private const string RequestChannel = "bgi.input";
    private const string ErrorChannel = "bgi.input.error";
    private const string ResultChannel = "bgi.input.result";
    private const string SdkResourceName = "BetterGenshinImpact.Resources.JavaScript.ys-input-inject.js";

    /// <summary>
    /// 同类 warn 的最小间隔，避免每次按键都刷日志
    /// </summary>
    private const long WarnIntervalMs = 10_000;

    /// <summary>
    /// 页面侧分发脚本：按到达顺序串行调用 window.__ysInputInject 上的函数，只在失败时回报。
    /// 只在顶层文档生效：注入脚本也会在子 frame 中执行，而子 frame 没有 chrome.webview
    /// </summary>
    private const string BootstrapScript = """
        (() => {
          if (window.top !== window || !window.chrome?.webview || window.__bgiInputBridge) return;
          window.__bgiInputBridge = true;
          let queue = Promise.resolve();
          window.chrome.webview.addEventListener("message", ({ data }) => {
            if (!data || data.channel !== "bgi.input") return;
            const { method, args = [], id } = data;
            // 串行执行：tapKey、click 等是异步函数，并发执行会打乱按键顺序
            queue = queue
              .then(async () => {
                const sdk = window.__ysInputInject;
                if (!sdk || !Object.hasOwn(sdk, method) || typeof sdk[method] !== "function") {
                  throw new Error(`unknown sdk method: ${method}`);
                }
                await sdk[method](...args);
                if (id) window.chrome.webview.postMessage({ channel: "bgi.input.result", id, ok: true });
              })
              .catch(e => window.chrome.webview.postMessage({
                channel: id ? "bgi.input.result" : "bgi.input.error", id, ok: false, method, error: String(e?.message ?? e)
              }));
          });
        })();
        """;

    /// <summary>
    /// 状态查询脚本：SDK 未注入或页面尚未加载到游戏时 status() 会抛错，统一转成 error 字段
    /// </summary>
    private const string StatusScript = """
        (() => {
          try {
            const s = window.__ysInputInject?.status();
            if (!s) return { error: "sdk not installed" };
            return { rtcDataChannelState: s.rtcDataChannelState ?? null, gameDataStarted: s.gameDataStarted ?? null };
          } catch (e) {
            return { error: String(e?.message ?? e) };
          }
        })()
        """;

    private static ILogger? _logger;
    private static string? _sdkScript;

    private readonly CoreWebView2 _core;
    private readonly Dispatcher _dispatcher;
    private long _lastPostWarnTicks = long.MinValue;
    private long _lastErrorWarnTicks = long.MinValue;
    private volatile bool _disposed;
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _pending = new();

    private WebView2InputBridge(CoreWebView2 core, Dispatcher dispatcher)
    {
        _core = core;
        _dispatcher = dispatcher;
        _core.WebMessageReceived += OnWebMessageReceived;
    }

    private static ILogger Logger => _logger ??= App.GetLogger<WebView2InputBridge>();

    /// <summary>
    /// 注入 SDK 与分发脚本（对之后的每次导航生效），然后返回桥。
    /// 必须在 CoreWebView2 所在的 UI 线程上、首次导航之前调用
    /// </summary>
    public static async Task<WebView2InputBridge> InstallAsync(CoreWebView2 core)
    {
        ArgumentNullException.ThrowIfNull(core);
        var dispatcher = Dispatcher.CurrentDispatcher;

        // SDK 本身不改动，外面包一层只在顶层文档执行
        await core.AddScriptToExecuteOnDocumentCreatedAsync($"if (window.top === window) {{\n{GetSdkScript()}\n}}");
        await core.AddScriptToExecuteOnDocumentCreatedAsync(BootstrapScript);
        return new WebView2InputBridge(core, dispatcher);
    }

    /// <summary>
    /// 投递一次 SDK 调用，不等待页面执行。可在任意线程调用：
    /// 其他线程经 Dispatcher.BeginInvoke 投递到 UI 线程，同优先级按先后顺序执行，任务线程不阻塞，也不会与等待任务的 UI 线程互相死锁；
    /// UI 线程上直接投递。桥已释放时抛出 <see cref="InvalidOperationException"/>
    /// </summary>
    public void Invoke(string method, params object[] args)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        if (_disposed)
        {
            throw new InvalidOperationException("云原神 WebView2 输入桥已释放");
        }

        var json = JsonConvert.SerializeObject(new { channel = RequestChannel, method, args });
        if (_dispatcher.CheckAccess())
        {
            // UI 线程直接投递：保证宿主关闭前（Closing 中）发出的 releaseAll 能在页面销毁前送达
            Post(method, json);
        }
        else
        {
            _dispatcher.BeginInvoke(() => Post(method, json));
        }
    }

    public void InvokeAcknowledged(string method, Action beforePost, params object[] args)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        ArgumentNullException.ThrowIfNull(beforePost);
        if (_disposed) throw new InvalidOperationException("云原神 WebView2 输入桥已释放");
        // 不能阻塞接收页面回执的UI线程。实时触发器/战斗任务本来就在工作线程运行。
        if (_dispatcher.CheckAccess()) throw new InvalidOperationException("确认式SDK输入必须在任务线程调用");
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var json = JsonConvert.SerializeObject(new { channel = RequestChannel, method, args, id });
        var gate = new object();
        var expired = false;
        var execution = ExecutionContext.Capture();
        _pending[id] = completion;
        try
        {
            _dispatcher.BeginInvoke(() =>
            {
                void PostAcknowledged()
                {
                    lock (gate)
                    {
                        if (expired) return;
                        try
                        {
                            if (_disposed) throw new InvalidOperationException("SDK桥已关闭");
                            beforePost();
                            _core.PostWebMessageAsJson(json);
                        }
                        catch (Exception error) { completion.TrySetException(error); }
                    }
                }
                if (execution == null) PostAcknowledged();
                else ExecutionContext.Run(execution, _ => PostAcknowledged(), null);
            });
            completion.Task.WaitAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        }
        finally
        {
            // 超时但尚未出队的请求不允许迟到投递；已投递但未确认则由调用方保留Unknown。
            lock (gate) expired = true;
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// 查询 SDK 状态。在 UI 线程上调用（宿主的 DispatcherTimer），其他线程会被切到 UI 线程执行
    /// </summary>
    public async Task<WebSdkStatus> GetStatusAsync()
    {
        if (_disposed)
        {
            return new WebSdkStatus(null, null, "bridge disposed");
        }

        try
        {
            var json = _dispatcher.CheckAccess()
                ? await _core.ExecuteScriptAsync(StatusScript)
                : await _dispatcher.InvokeAsync(() => _core.ExecuteScriptAsync(StatusScript)).Task.Unwrap();
            var result = JToken.Parse(json) as JObject;
            return new WebSdkStatus(
                result?.Value<string>("rtcDataChannelState"),
                result?.Value<bool?>("gameDataStarted"),
                result?.Value<string>("error"));
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException or JsonException
                                      or System.Runtime.InteropServices.COMException)
        {
            // 页面正在导航或 WebView2 已关闭
            return new WebSdkStatus(null, null, e.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var pending in _pending.Values)
            pending.TrySetException(new ObjectDisposedException(nameof(WebView2InputBridge)));
        _core.WebMessageReceived -= OnWebMessageReceived;
    }

    private void Post(string method, string json)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _core.PostWebMessageAsJson(json);
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException
                                      or System.Runtime.InteropServices.COMException)
        {
            // 页面已关闭或正在导航。调用方已经返回，只能记录
            if (ShouldWarn(ref _lastPostWarnTicks))
            {
                Logger.LogWarning("云原神 SDK 调用投递失败：{Method}，{Error}", method, e.Message);
            }
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JObject message;
        try
        {
            message = JObject.Parse(e.WebMessageAsJson);
        }
        catch (JsonException)
        {
            // 不是 JSON 对象的消息与本桥无关
            return;
        }

        if (string.Equals(message.Value<string>("channel"), ResultChannel, StringComparison.Ordinal))
        {
            if (message.Value<string>("id") is { } id && _pending.TryGetValue(id, out var pending))
            {
                if (message.Value<bool?>("ok") == true) pending.TrySetResult();
                else pending.TrySetException(new InvalidOperationException("页面SDK未确认输入执行"));
            }
            return;
        }
        if (!string.Equals(message.Value<string>("channel"), ErrorChannel, StringComparison.Ordinal))
        {
            return;
        }

        // 页面断开由宿主的状态轮询发现，这里只记录调用错误
        if (ShouldWarn(ref _lastErrorWarnTicks))
        {
            Logger.LogWarning("云原神 SDK 调用失败：{Method}，{Error}",
                message.Value<string>("method") ?? string.Empty,
                message.Value<string>("error") ?? "未知错误");
        }
    }

    private static bool ShouldWarn(ref long lastTicks)
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref lastTicks);
        if (last != long.MinValue && now - last < WarnIntervalMs)
        {
            return false;
        }

        Interlocked.Exchange(ref lastTicks, now);
        return true;
    }

    private static string GetSdkScript()
    {
        if (_sdkScript != null)
        {
            return _sdkScript;
        }

        using var stream = typeof(WebView2InputBridge).Assembly.GetManifestResourceStream(SdkResourceName)
                           ?? throw new InvalidOperationException($"找不到嵌入资源 {SdkResourceName}");
        using var reader = new StreamReader(stream);
        return _sdkScript = reader.ReadToEnd();
    }
}

/// <summary>
/// 页面 SDK 的状态快照
/// </summary>
/// <param name="RtcDataChannelState">RTC 数据通道状态，open 时输入才能发往云端</param>
/// <param name="GameDataStarted">游戏数据通道是否已开始，即已进入游戏画面</param>
/// <param name="Error">SDK 未注入、页面未加载到游戏或查询失败时的原因</param>
public sealed record WebSdkStatus(string? RtcDataChannelState, bool? GameDataStarted, string? Error)
{
    public bool IsReady => RtcDataChannelState == "open" && GameDataStarted == true;
}
