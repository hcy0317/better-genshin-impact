using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using BetterGenshinImpact.Core.Input;
using BetterGenshinImpact.Core.Mask;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.GameTask.Runtime;
using BetterGenshinImpact.Helpers;
using Fischless.GameCapture;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace BetterGenshinImpact.UnitTest.CoreTests.MaskTests;

public class DesktopRecognitionIsolationTests(ITestOutputHelper output)
{
    private static readonly string FixtureDirectory = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Mask", "DesktopRecognition");
    private static string Fixture(string name) => System.IO.Path.Combine(FixtureDirectory, name);
    private static string Template => Fixture("disabled-ui.png");

    [Fact]
    public void DesktopRuntimeBreaksActualFindBoardRendererFeedbackWhileKeepingFeatureDrawingAndPreference()
    {
        // These hashes identify derived ROI fixtures; original full-frame hashes remain in provenance.json.
        Assert.Equal("BD9F7D242456B74A09925693FECB99920E1B3D1CE3E6C1BA0C02B95BD2C1A83C", Hash(Fixture("talk-red-roi.png")));
        Assert.Equal("89E1753D6256AA003E27625B0D004461A7C8ABEC85EB67490E5B8D211DD5F64A", Hash(Fixture("talk-clean-roi.png")));
        Assert.Equal("96759FB9B99F85D274C888718FF102EF47F31BD17CF939BA813FFDA556F0DD5F", Hash(Template));
        var board = new MaskWindowDrawingBoard();
        var feature = new MaskWindowDrawingGroup("script-marker", MaskWindowDrawingKind.Feature);
        board.Set(feature, [new MaskWindowDrawingText("keep-script", new System.Windows.Point(900, 900))]);
        const bool userPreference = true;
        using var runtime = Runtime(board, true);
        using var template = Cv2.ImRead(Template, ImreadModes.Color);
        var ro = RecognitionObject.TemplateMatch(template);
        ro.Name = "AutoSkip.DisabledUiButton"; ro.DrawOnWindow = true;
        ro.RegionOfInterest = new Rect(0, 0, 640, 135);
        var scores = new List<double>(); var exists = new List<bool>(); var visibleRecognition = new List<bool>();
        var painted = false;
        try
        {
        using (var red = CaptureCanvas(true))
        { var recorded = Score(red, ro); Assert.Equal(0.6294117569923401, recorded.Score, 6); Assert.Equal(new Point(266, 38), recorded.Position); }
        using (var clean = CaptureCanvas(false))
        { var recorded = Score(clean, ro); Assert.Equal(0.9934465289115906, recorded.Score, 6); Assert.Equal(new Point(266, 38), recorded.Position); }
            for (var frame = 0; frame < 3; frame++)
            {
                // Only the lossless recorded ROI is replayed on a 1920x1080 black capture canvas.
                using var image = new GameCaptureRegion(CaptureCanvas(painted), 0, 0, drawingBoard: board);
                var (score, _) = Score(image.SrcMat, ro);
                scores.Add(score);
                using var found = image.Find(ro);
                exists.Add(found.IsExist());
                var submitted = MaskWindowDrawingVisibility.GetEntries(board.Current, userPreference).ToArray();
                Assert.Contains(submitted, x => x.Group == feature);
                painted = submitted.Any(x => x.Group.Kind == MaskWindowDrawingKind.Recognition);
                visibleRecognition.Add(painted);
            }
            output.WriteLine($"producer={typeof(MaskWindowDrawingBoard).Assembly.GetName().Name} mvid={typeof(MaskWindowDrawingBoard).Assembly.ManifestModule.ModuleVersionId} scores={string.Join(",", scores)} matched={string.Join(",", exists)} renderedRecognition={string.Join(",", visibleRecognition)} nativeInputs=0");
            Assert.All(exists, Assert.True);
            Assert.All(scores, x => Assert.True(x >= .8));
            Assert.DoesNotContain(true, visibleRecognition);
            Assert.True(userPreference);
            Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
            Assert.Null(System.Windows.Application.Current);
        }
        finally { ro.TemplateImageGreyMat?.Dispose(); }
    }

#if !OLD_DESKTOP_CAPTURE
    [Theory]
    [InlineData("self")]
    [InlineData("rect-values")]
    [InlineData("rect")]
    [InlineData("line")]
    public void ExplicitRegionPublicDrawingRemainsVisibleInDesktopScopeAndRespectsPreference(string api)
    {
        var board = new MaskWindowDrawingBoard();
        using var runtime = Runtime(board, true);
        using var image = new GameCaptureRegion(new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black), 0, 0, drawingBoard: board);
        var name = "explicit-" + api;
        switch (api)
        {
            case "self": image.DrawSelf(name); break;
            case "rect-values": image.DrawRect(800, 800, 28, 20, name); break;
            case "rect": image.DrawRect(new Rect(800, 800, 28, 20), name); break;
            case "line": image.DrawLine(800, 800, 900, 900, name); break;
        }
        // A failed automatic Find sharing the name must not erase an explicit public drawing.
        using var template = Cv2.ImRead(Template, ImreadModes.Color);
        var automatic = RecognitionObject.TemplateMatch(template); automatic.Name = name; automatic.DrawOnWindow = true;
        automatic.RegionOfInterest = new Rect(0, 0, 640, 135);
        try { using var absent = image.Find(automatic); Assert.False(absent.IsExist()); }
        finally { automatic.TemplateImageGreyMat?.Dispose(); }
        Assert.Contains(MaskWindowDrawingVisibility.GetEntries(board.Current, true), x => x.Group.Name == name);
        Assert.DoesNotContain(MaskWindowDrawingVisibility.GetEntries(board.Current, false), x => x.Group.Name == name);
        Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
        Assert.Null(System.Windows.Application.Current);
    }

    [Fact]
    public void DesktopScopeClearsOldRecognitionButKeepsFeatureAndRestoresFreshLocalRecognition()
    {
        var board = new MaskWindowDrawingBoard();
        SetRecognition(board); SetFeature(board);
        using (var runtime = Runtime(board, true))
        {
            Assert.DoesNotContain(board.Current.Entries, x => x.Group.Kind == MaskWindowDrawingKind.Recognition);
            Assert.Contains(board.Current.Entries, x => x.Group.Kind == MaskWindowDrawingKind.Feature);
            SetRecognition(board);
            Assert.DoesNotContain(board.Current.Entries, x => x.Group.Kind == MaskWindowDrawingKind.Recognition);
        }
        SetRecognition(board);
        Assert.Contains(MaskWindowDrawingVisibility.GetEntries(board.Current, true), x => x.Group.Kind == MaskWindowDrawingKind.Recognition);
        Assert.DoesNotContain(MaskWindowDrawingVisibility.GetEntries(board.Current, false), x => x.Group.Kind == MaskWindowDrawingKind.Recognition);
        Assert.Contains(MaskWindowDrawingVisibility.GetEntries(board.Current, false), x => x.Group.Kind == MaskWindowDrawingKind.Feature);
    }

    [Fact]
    public void NestedScopesAndRepeatedDisposeDoNotResumeTheOuterDesktopScope()
    {
        var board = new MaskWindowDrawingBoard(); SetFeature(board);
        using var first = Runtime(board, true); var second = Runtime(board, true);
        second.Dispose(); second.Dispose(); SetRecognition(board);
        Assert.DoesNotContain(board.Current.Entries, x => x.Group.Kind == MaskWindowDrawingKind.Recognition);
        first.Dispose(); first.Dispose(); SetRecognition(board);
        Assert.Contains(board.Current.Entries, x => x.Group.Kind == MaskWindowDrawingKind.Recognition);
        Assert.Contains(board.Current.Entries, x => x.Group.Kind == MaskWindowDrawingKind.Feature);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void WindowUnknownAndStoppedCaptureFactsDoNotSuppressRecognition(bool desktop, bool capturing)
    {
        var board = new MaskWindowDrawingBoard(); var capture = Fake<IGameCapture>();
        ((NoNativeEndpoint)(object)capture).Capturing = capturing;
        using var runtime = Runtime(board, desktop, capture);
        SetRecognition(board);
        Assert.Contains(board.Current.Entries, x => x.Group.Kind == MaskWindowDrawingKind.Recognition);
    }

    [Fact]
    public void CaptureDisposeFailureStillReleasesRecognitionAndWindowExactlyOnce()
    {
        var board = new MaskWindowDrawingBoard(); var capture = Fake<IGameCapture>(); var window = Fake<IGameWindow>();
        var endpoint = (NoNativeEndpoint)(object)capture; endpoint.ThrowOnDispose = true;
        var runtime = Runtime(board, true, capture, window);
        Assert.Throws<InvalidOperationException>(runtime.Dispose);
        Assert.Equal(1, ((NoNativeEndpoint)(object)window).DisposeCalls);
        runtime.Dispose(); Assert.Equal(1, endpoint.DisposeCalls);
        SetRecognition(board);
        Assert.Contains(board.Current.Entries, x => x.Group.Kind == MaskWindowDrawingKind.Recognition);
    }

    [Fact]
    public void ConstructorObserverFailureCannotLeaveGlobalSuppressionBehind()
    {
        var board = new MaskWindowDrawingBoard(); SetRecognition(board); SetFeature(board);
        Action fail = () => throw new InvalidOperationException("observer failed");
        board.Changed += fail;
        Assert.Throws<InvalidOperationException>(() => Runtime(board, true));
        board.Changed -= fail; SetRecognition(board);
        Assert.Contains(board.Current.Entries, x => x.Group.Kind == MaskWindowDrawingKind.Recognition);
        Assert.Contains(board.Current.Entries, x => x.Group.Kind == MaskWindowDrawingKind.Feature);
    }

    [Fact]
    public void LeaseSnapshotNotificationRunsOutsideTheProducerLock()
    {
        var board = new MaskWindowDrawingBoard(); SetRecognition(board); var once = 0;
        board.Changed += () =>
        {
            if (System.Threading.Interlocked.Increment(ref once) == 1)
                Assert.True(System.Threading.Tasks.Task.Run(() => SetFeature(board)).Wait(TimeSpan.FromSeconds(1)));
        };
        using var runtime = Runtime(board, true);
        Assert.Contains(board.Current.Entries, x => x.Group.Kind == MaskWindowDrawingKind.Feature);
    }

    private static void SetRecognition(MaskWindowDrawingBoard board) => board.Set(MaskWindowDrawingGroup.Automatic("recognition"), [new MaskWindowDrawingRect(new System.Windows.Rect(266, 38, 28, 20))]);
    private static void SetFeature(MaskWindowDrawingBoard board) => board.Set(new("script", MaskWindowDrawingKind.Feature), [new MaskWindowDrawingText("script", new System.Windows.Point(800, 800))]);
#endif

    private static GameRuntime Runtime(MaskWindowDrawingBoard board, bool desktop, IGameCapture? capture = null, IGameWindow? window = null)
    {
#if OLD_DESKTOP_CAPTURE
        return new GameRuntime(GameRuntimeKind.Win32Window, Fake<IGameWindow>(), Fake<IGameCapture>(), Fake<IInputBackend>(), board, Fake<IMaskWindowMapState>());
#else
        return new GameRuntime(GameRuntimeKind.Win32Window, window ?? Fake<IGameWindow>(), capture ?? Fake<IGameCapture>(), Fake<IInputBackend>(), board, Fake<IMaskWindowMapState>(), desktop);
#endif
    }
    private static Mat CaptureCanvas(bool painted)
    {
        using var crop = Cv2.ImRead(Fixture(painted ? "talk-red-roi.png" : "talk-clean-roi.png"), ImreadModes.Color);
        Assert.Equal(new Size(640, 135), crop.Size());
        var canvas = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black);
        try
        {
            using var destination = new Mat(canvas, new Rect(0, 0, 640, 135));
            crop.CopyTo(destination);
            return canvas;
        }
        catch { canvas.Dispose(); throw; }
    }

    private static (double Score, Point Position) Score(Mat frame, RecognitionObject ro)
    {
        using var gray = new Mat(); Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
        using var source = new Mat(gray, ro.RegionOfInterest); using var result = new Mat();
        Cv2.MatchTemplate(source, ro.TemplateImageGreyMat!, result, ro.TemplateMatchMode);
        Cv2.MinMaxLoc(result, out _, out var score, out _, out var position);
        return (score, position);
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(System.IO.File.ReadAllBytes(path)));
    private static T Fake<T>() where T : class => DispatchProxy.Create<T, NoNativeEndpoint>();
    public class NoNativeEndpoint : DispatchProxy
    {
        public bool Capturing = true, ThrowOnDispose;
        public int DisposeCalls;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == "Dispose")
            { DisposeCalls++; if (ThrowOnDispose) throw new InvalidOperationException("fake dispose failure"); return null; }
            if (method?.Name == "get_IsCapturing") return Capturing;
            return method?.ReturnType == typeof(void) ? null : method?.ReturnType == typeof(bool) ? false :
                method?.ReturnType.IsValueType == true ? Activator.CreateInstance(method.ReturnType) : null;
        }
    }
}
