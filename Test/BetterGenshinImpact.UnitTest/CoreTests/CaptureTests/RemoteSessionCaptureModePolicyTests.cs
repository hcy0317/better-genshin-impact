using Fischless.GameCapture;

namespace BetterGenshinImpact.UnitTest.CoreTests.CaptureTests;

public class RemoteSessionCaptureModePolicyTests
{
    [Fact]
    public void RemotePrimaryBitBltStartsWithForegroundDesktopPixels()
    {
        var sourceSettings = new Dictionary<string, object>
        {
            ["autoFixWin11BitBlt"] = true,
            ["MinUpdateIntervalMs"] = 17,
            ["UseCpuConvert"] = true
        };
        var capture = new RecordingCapture();
        CaptureModes? createdMode = null;
        using var started = RemoteSessionCaptureModePolicy.Start(CaptureModes.BitBlt, true, false,
            (nint)1234, sourceSettings, mode => { createdMode = mode; return capture; });
        Assert.Same(capture, started);
        Assert.Equal(CaptureModes.BitBlt, createdMode);
        Assert.Equal((nint)1234, capture.Window);
        Assert.Equal(1, capture.Starts);
        Assert.True(capture.Settings!.TryGetValue("captureFromDesktop", out var desktop) && desktop is true);
        Assert.Equal(true, capture.Settings["autoFixWin11BitBlt"]);
        Assert.Equal(17, capture.Settings["MinUpdateIntervalMs"]);
        Assert.Equal(true, capture.Settings["UseCpuConvert"]);
        Assert.False(sourceSettings.ContainsKey("captureFromDesktop"));
        Assert.Equal(0, capture.Stops);
    }

    [Theory]
    [InlineData(CaptureModes.BitBlt, false, false, CaptureModes.BitBlt, false)]
    [InlineData(CaptureModes.BitBlt, true, false, CaptureModes.BitBlt, true)]
    [InlineData(CaptureModes.BitBlt, null, false, CaptureModes.BitBlt, false)]
    [InlineData(CaptureModes.BitBlt, false, true, CaptureModes.BitBlt, true)]
    [InlineData(CaptureModes.BitBlt, true, true, CaptureModes.BitBlt, true)]
    [InlineData(CaptureModes.BitBlt, null, true, CaptureModes.BitBlt, true)]
    [InlineData(CaptureModes.WindowsGraphicsCapture, true, false, CaptureModes.BitBlt, true)]
    [InlineData(CaptureModes.WindowsGraphicsCaptureHdr, true, false, CaptureModes.BitBlt, true)]
    [InlineData(CaptureModes.WindowsGraphicsCaptureV2, true, false, CaptureModes.BitBlt, true)]
    [InlineData(CaptureModes.WindowsGraphicsCapture, false, false, CaptureModes.WindowsGraphicsCapture, false)]
    [InlineData(CaptureModes.WindowsGraphicsCaptureHdr, false, false, CaptureModes.WindowsGraphicsCaptureHdr, false)]
    [InlineData(CaptureModes.WindowsGraphicsCaptureV2, false, false, CaptureModes.WindowsGraphicsCaptureV2, false)]
    [InlineData(CaptureModes.WindowsGraphicsCapture, null, false, CaptureModes.WindowsGraphicsCapture, false)]
    [InlineData(CaptureModes.WindowsGraphicsCaptureHdr, null, false, CaptureModes.WindowsGraphicsCaptureHdr, false)]
    [InlineData(CaptureModes.WindowsGraphicsCaptureV2, null, false, CaptureModes.WindowsGraphicsCaptureV2, false)]
    [InlineData(CaptureModes.WindowsGraphicsCapture, false, true, CaptureModes.WindowsGraphicsCapture, false)]
    [InlineData(CaptureModes.WindowsGraphicsCaptureHdr, false, true, CaptureModes.WindowsGraphicsCaptureHdr, false)]
    [InlineData(CaptureModes.WindowsGraphicsCaptureV2, false, true, CaptureModes.WindowsGraphicsCaptureV2, false)]
    [InlineData(CaptureModes.WindowsGraphicsCapture, null, true, CaptureModes.WindowsGraphicsCapture, false)]
    [InlineData(CaptureModes.DwmGetDxSharedSurface, true, false, CaptureModes.DwmGetDxSharedSurface, false)]
    [InlineData(CaptureModes.DwmGetDxSharedSurface, true, true, CaptureModes.DwmGetDxSharedSurface, false)]
    [InlineData(CaptureModes.DwmGetDxSharedSurface, false, true, CaptureModes.DwmGetDxSharedSurface, false)]
    public void StartPreservesRuntimeModesSettingsAndDesktopBoundaries(CaptureModes requested, bool? remote,
        bool child, CaptureModes expected, bool desktopExpected)
    {
        var original = new Dictionary<string, object>
        {
            ["autoFixWin11BitBlt"] = true,
            ["MinUpdateIntervalMs"] = 19,
            ["UseCpuConvert"] = false
        };
        var capture = new RecordingCapture();
        var creates = new List<CaptureModes>();
        using var started = RemoteSessionCaptureModePolicy.Start(requested, remote, child, (nint)987,
            original, mode => { creates.Add(mode); return capture; });
        Assert.Equal(new[] { expected }, creates.ToArray());
        Assert.Same(capture, started);
        Assert.Equal(1, capture.Starts);
        Assert.Equal((nint)987, capture.Window);
        Assert.NotSame(original, capture.Settings);
        Assert.Equal(desktopExpected, capture.Settings!.TryGetValue("captureFromDesktop", out var desktop) && desktop is true);
        Assert.Equal(true, capture.Settings["autoFixWin11BitBlt"]);
        Assert.Equal(19, capture.Settings["MinUpdateIntervalMs"]);
        Assert.Equal(false, capture.Settings["UseCpuConvert"]);
        Assert.Equal(3, original.Count);
        Assert.False(original.ContainsKey("captureFromDesktop"));
        Assert.Equal(true, original["autoFixWin11BitBlt"]);
        Assert.Equal(19, original["MinUpdateIntervalMs"]);
        Assert.Equal(false, original["UseCpuConvert"]);
        Assert.Equal(0, capture.Stops);
        Assert.Equal(0, capture.Disposals);
    }

    [Fact]
    public void StartFailureDisposesCreatedCaptureExactlyOnceWithoutStopOrRestart()
    {
        var failure = new InvalidOperationException("original capture Start failed");
        var capture = new RecordingCapture { StartError = failure };
        var creates = 0;
        var settings = new Dictionary<string, object> { ["autoFixWin11BitBlt"] = true };
        var actual = Assert.Throws<InvalidOperationException>(() => RemoteSessionCaptureModePolicy.Start(
            CaptureModes.BitBlt, true, false, (nint)1234, settings, _ => { creates++; return capture; }));
        Assert.Same(failure, actual);
        Assert.Equal(1, creates);
        Assert.Equal(1, capture.Starts);
        Assert.Equal(1, capture.Disposals);
        Assert.Equal(0, capture.Stops);
        Assert.False(settings.ContainsKey("captureFromDesktop"));
    }

    [Fact]
    public void FactoryFailureDoesNotStartCaptureOrRewriteSettings()
    {
        var failure = new InvalidOperationException("original capture factory failed");
        var capture = new RecordingCapture();
        var creates = 0;
        var settings = new Dictionary<string, object> { ["MinUpdateIntervalMs"] = 17 };
        var actual = Assert.Throws<InvalidOperationException>(() => RemoteSessionCaptureModePolicy.Start(
            CaptureModes.BitBlt, true, false, (nint)1234, settings, _ => { creates++; throw failure; }));
        Assert.Same(failure, actual);
        Assert.Equal(1, creates);
        Assert.Equal(0, capture.Starts);
        Assert.Equal(0, capture.Stops);
        Assert.Equal(0, capture.Disposals);
        Assert.Single(settings);
        Assert.Equal(17, settings["MinUpdateIntervalMs"]);
    }

    private sealed class RecordingCapture : IGameCapture
    {
        public bool IsCapturing { get; private set; }
        public nint Window { get; private set; }
        public Dictionary<string, object>? Settings { get; private set; }
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public int Disposals { get; private set; }
        public Exception? StartError { get; init; }
        public void Start(nint hWnd, Dictionary<string, object>? settings = null)
        {
            Window = hWnd;
            Settings = settings;
            Starts++;
            if (StartError != null) throw StartError;
            IsCapturing = true;
        }
        public GameCaptureFrame? Capture() => throw new Exception("No native capture in policy tests");
        public void Stop() { Stops++; IsCapturing = false; }
        public void Dispose() { Disposals++; IsCapturing = false; }
    }

    [Theory]
    [InlineData(CaptureModes.WindowsGraphicsCapture)]
    [InlineData(CaptureModes.WindowsGraphicsCaptureHdr)]
    [InlineData(CaptureModes.WindowsGraphicsCaptureV2)]
    public void Resolve_ShouldUseBitBltForWindowsGraphicsCaptureInsideRemoteSession(CaptureModes requestedMode)
    {
        var resolvedMode = RemoteSessionCaptureModePolicy.Resolve(requestedMode, isRemoteSession: true);

        Assert.Equal(CaptureModes.BitBlt, resolvedMode);
    }

    [Theory]
    [InlineData(CaptureModes.BitBlt, true)]
    [InlineData(CaptureModes.DwmGetDxSharedSurface, true)]
    [InlineData(CaptureModes.WindowsGraphicsCapture, false)]
    [InlineData(CaptureModes.WindowsGraphicsCaptureHdr, false)]
    [InlineData(CaptureModes.WindowsGraphicsCaptureV2, false)]
    public void Resolve_ShouldPreserveModesThatDoNotNeedRemoteFallback(
        CaptureModes requestedMode,
        bool isRemoteSession)
    {
        var resolvedMode = RemoteSessionCaptureModePolicy.Resolve(requestedMode, isRemoteSession);

        Assert.Equal(requestedMode, resolvedMode);
    }
}
