using System.Diagnostics;
using Fischless.GameCapture.Graphics.Helpers;
using SharpDX.Direct3D11;
using Vanara.PInvoke;
using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using OpenCvSharp;
using SharpDX;
using SharpDX.D3DCompiler;

namespace Fischless.GameCapture.Graphics;

public class GraphicsCapture(bool captureHdr = false) : IGameCapture
{
    private nint _hWnd;

    private Direct3D11CaptureFramePool? _captureFramePool;
    private GraphicsCaptureItem? _captureItem;

    private GraphicsCaptureSession? _captureSession;

    private IDirect3DDevice? _d3dDevice;

    public bool IsCapturing { get; private set; }

    private ResourceRegion? _region;
    private RECT? _captureRect;

    // HDR相关
    private bool _isHdrEnabled = captureHdr;
    private DirectXPixelFormat _pixelFormat;
    private Texture2D? _hdrOutputTexture;
    private ComputeShader? _hdrComputeShader;

    // 最新帧的存储
    private Mat? _latestFrame;
    private readonly ReaderWriterLockSlim _frameAccessLock = new();
    private readonly FrameCallbackLifetime _frameCallbackLifetime = new();
    private readonly object _captureLifecycleSync = new();

    // 用于获取帧数据的临时纹理和暂存资源
    private Texture2D? _stagingTexture;

    // Surface 大小
    private int _surfaceWidth;
    private int _surfaceHeight;

    private long _lastFrameTime;

    private readonly Stopwatch _frameTimer = new();

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    public void Start(nint hWnd, Dictionary<string, object>? settings = null)
    {
        lock (_captureLifecycleSync)
        {
            Stop();
            _frameCallbackLifetime.Reset();
            try
            {
                _hWnd = hWnd;
                (_region, _captureRect) = GetGameScreenInfo(hWnd);

                _captureItem = CaptureHelper.CreateItemForWindow(_hWnd);

                if (_captureItem == null)
                {
                    throw new InvalidOperationException("Failed to create capture item.");
                }

                _surfaceWidth = _captureItem.Size.Width;
                _surfaceHeight = _captureItem.Size.Height;

                // 创建D3D设备
                _d3dDevice = Direct3D11Helper.CreateDevice();

                // 使用创建截图器的 DispatcherQueue 交付帧事件。BetterGI 的 WPF 主线程负责泵送该队列；
                // 在 loopback RDP 桌面中，FreeThreaded 帧池可能成功启动却始终不交付首帧。
                // Stop() 会先退订并停止生产者，再等待已进入的回调，因此无需依赖 FreeThreaded 规避晚到回调。
                try
                {
                    if (!_isHdrEnabled)
                    {
                        // 不处理 HDR，直接抛异常走 SDR 分支
                        throw new Exception();
                    }

                    _pixelFormat = DirectXPixelFormat.R16G16B16A16Float;
                    _captureFramePool = Direct3D11CaptureFramePool.Create(
                        _d3dDevice,
                        _pixelFormat,
                        2,
                        _captureItem.Size);
                }
                catch (Exception)
                {
                    // Fallback
                    _pixelFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;
                    _captureFramePool = Direct3D11CaptureFramePool.Create(
                        _d3dDevice,
                        _pixelFormat,
                        2,
                        _captureItem.Size);
                    _isHdrEnabled = false;
                }

                _captureItem.Closed += CaptureItemOnClosed;
                _captureFramePool.FrameArrived += OnFrameArrived;

                _captureSession = _captureFramePool.CreateCaptureSession(_captureItem);
                if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession",
                        nameof(GraphicsCaptureSession.IsCursorCaptureEnabled)))
                {
                    _captureSession.IsCursorCaptureEnabled = false;
                }

                if (ApiInformation.IsWriteablePropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession",
                        nameof(GraphicsCaptureSession.IsBorderRequired)))
                {
                    _captureSession.IsBorderRequired = false;
                }

                _lastFrameTime = 0;
                _frameTimer.Restart();
                _captureSession.StartCapture();
                IsCapturing = true;
            }
            catch
            {
                Stop();
                throw;
            }
        }
    }

    /// <summary>
    /// 从 DwmGetWindowAttribute 的矩形 截取出 GetClientRect的矩形（游戏区域）
    /// </summary>
    /// <param name="hWnd"></param>
    /// <returns></returns>
    private static (ResourceRegion? Region, RECT? CaptureRect) GetGameScreenInfo(nint hWnd)
    {
        var exStyle = User32.GetWindowLong(hWnd, User32.WindowLongFlags.GWL_EXSTYLE);
        if ((exStyle & (int)User32.WindowStylesEx.WS_EX_TOPMOST) != 0)
        {
            return (null, null);
        }

        ResourceRegion region = new();
        DwmApi.DwmGetWindowAttribute<RECT>(hWnd, DwmApi.DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS,
            out var windowRect);
        User32.GetClientRect(hWnd, out var clientRect);
        POINT point = default;
        User32.ClientToScreen(hWnd, ref point);

        region.Left = point.X > windowRect.Left ? point.X - windowRect.Left : 0;
        region.Top = point.Y > windowRect.Top ? point.Y - windowRect.Top : 0;
        region.Right = region.Left + clientRect.Width;
        region.Bottom = region.Top + clientRect.Height;
        region.Front = 0;
        region.Back = 1;

        var left = windowRect.Left;
        var top = windowRect.Top + windowRect.Height - clientRect.Height;
        var right = left + clientRect.Width;
        var bottom = top + clientRect.Height;

        return (region, new RECT(left, top, right, bottom));
    }

    private Texture2D ProcessHdrTexture(Texture2D hdrTexture)
    {
        var device = hdrTexture.Device;
        var context = device.ImmediateContext;

        var width = hdrTexture.Description.Width;
        var height = hdrTexture.Description.Height;

        _hdrOutputTexture ??= Direct3D11Helper.CreateOutputTexture(device, width, height);
        _hdrComputeShader ??= new ComputeShader(device, ShaderBytecode.Compile(HdrToSdrShader.Content, "CS_HDRtoSDR", "cs_5_0"));

        using var inputSrv = new ShaderResourceView(device, hdrTexture);
        using var outputUav = new UnorderedAccessView(device, _hdrOutputTexture);

        context.ComputeShader.Set(_hdrComputeShader);
        context.ComputeShader.SetShaderResource(0, inputSrv);
        context.ComputeShader.SetUnorderedAccessView(0, outputUav);

        var threadGroupCountX = (int)Math.Ceiling(width / 16.0);
        var threadGroupCountY = (int)Math.Ceiling(height / 16.0);

        context.Dispatch(threadGroupCountX, threadGroupCountY, 1);

        return _hdrOutputTexture;
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (!_frameCallbackLifetime.TryEnter())
        {
            return;
        }

        try
        {
            _frameAccessLock.EnterWriteLock();
            try
            {
                if (_hWnd == 0)
                {
                    return;
                }

                using var frame = sender.TryGetNextFrame();
                if (frame == null)
                {
                    return;
                }

                // 限制最高处理帧率为62fps
                if (_frameTimer.ElapsedMilliseconds - _lastFrameTime < 16)
                {
                    return;
                }
                _lastFrameTime = _frameTimer.ElapsedMilliseconds;

                var captureSize = _captureItem!.Size;

                // 检查帧大小是否变化
                if (captureSize.Width != _surfaceWidth || captureSize.Height != _surfaceHeight)
                {
                    if (User32.IsIconic(_hWnd))
                        return;

                    _captureFramePool!.Recreate(
                        _d3dDevice,
                        _pixelFormat,
                        2,
                        captureSize
                    );
                    _stagingTexture?.Dispose();
                    _stagingTexture = null;
                    _hdrOutputTexture?.Dispose();
                    _hdrOutputTexture = null;
                    _surfaceWidth = captureSize.Width;
                    _surfaceHeight = captureSize.Height;
                    (_region, _captureRect) = GetGameScreenInfo(_hWnd);
                    return;
                }

                try
                {
                    // 从捕获的帧创建一个可以被访问的纹理
                    using var surfaceTexture = Direct3D11Helper.CreateSharpDXTexture2D(frame.Surface);
                    var sourceTexture = _isHdrEnabled ? ProcessHdrTexture(surfaceTexture) : surfaceTexture;
                    var d3dDevice = surfaceTexture.Device;

                    _stagingTexture ??= Direct3D11Helper.CreateStagingTexture(d3dDevice, frame.ContentSize.Width, frame.ContentSize.Height, _region);
                    var newFrame = _stagingTexture.CreateMat(d3dDevice, sourceTexture, _region);

                    // 新帧构造成功后再替换，异常时保留上一帧
                    var oldFrame = _latestFrame;
                    _latestFrame = newFrame;
                    oldFrame?.Dispose();
                }
                catch (SharpDXException e)
                {
                    Debug.WriteLine($"SharpDXException: {e.Descriptor}");
                }
            }
            finally
            {
                _frameAccessLock.ExitWriteLock();
            }
        }
        finally
        {
            _frameCallbackLifetime.Exit();
        }
    }

    public GameCaptureFrame? Capture()
    {
        // 使用读锁获取最新帧
        _frameAccessLock.EnterReadLock();
        try
        {
            // 返回最新帧的副本（这里我们必须克隆，因为Mat是不线程安全的）
            var frame = _latestFrame?.Clone();
            return frame == null
                ? null
                : new GameCaptureFrame(frame, _captureRect);
        }
        finally
        {
            _frameAccessLock.ExitReadLock();
        }
    }

    public void Stop()
    {
        lock (_captureLifecycleSync)
        {
            IsCapturing = false;
            _hWnd = 0;
            _frameTimer.Reset();

            CaptureShutdownCoordinator.Stop(
                _frameCallbackLifetime,
                [
                    ("capture item event", () =>
                    {
                        if (_captureItem != null)
                        {
                            _captureItem.Closed -= CaptureItemOnClosed;
                        }
                    }),
                    ("frame arrived event", () =>
                    {
                        if (_captureFramePool != null)
                        {
                            _captureFramePool.FrameArrived -= OnFrameArrived;
                        }
                    }),
                    ("capture session", () => DisposeAndClear(ref _captureSession))
                ],
                [
                    ("captured frames", ReleaseCapturedFrames),
                    ("capture frame pool", () => DisposeAndClear(ref _captureFramePool)),
                    ("staging texture", () => DisposeAndClear(ref _stagingTexture)),
                    ("HDR output texture", () => DisposeAndClear(ref _hdrOutputTexture)),
                    ("HDR compute shader", () => DisposeAndClear(ref _hdrComputeShader)),
                    ("D3D device", () => DisposeAndClear(ref _d3dDevice)),
                    ("capture item", () => _captureItem = null)
                ]);
        }
    }

    private void ReleaseCapturedFrames()
    {
        _frameAccessLock.EnterWriteLock();
        try
        {
            DisposeAndClear(ref _latestFrame);
            _captureRect = null;
            _region = null;
        }
        finally
        {
            _frameAccessLock.ExitWriteLock();
        }
    }

    private static void DisposeAndClear<T>(ref T? resource) where T : class, IDisposable
    {
        var current = resource;
        resource = null;
        current?.Dispose();
    }

    private void CaptureItemOnClosed(GraphicsCaptureItem sender, object args)
    {
        try
        {
            Stop();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Failed to stop capture after the target window closed: {exception}");
        }
    }
}
