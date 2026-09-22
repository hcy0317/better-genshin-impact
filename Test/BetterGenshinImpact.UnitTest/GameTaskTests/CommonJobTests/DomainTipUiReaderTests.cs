using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.View.Drawable;
using BetterGenshinImpact.Helpers;
using Fischless.GameCapture;
using Microsoft.Extensions.Time.Testing;
using OpenCvSharp;
using BetterGenshinImpact.Core.Recognition.ONNX;
using BetterGenshinImpact.Core.Recognition.OCR.Paddle;
using BetterGenshinImpact.Service;
using Microsoft.Extensions.Logging.Abstractions;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class DomainTipUiReaderTests
{
    internal static readonly DomainTipTexts Texts = new("地脉异常", "点击任意位置关闭");

    [Fact]
    public void RecordedTitleAndFooterProduceOneSourceBoundClientRectangle()
    {
        Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
        var clock = new FakeTimeProvider();
        using var image = RecordedFrame(new CaptureFrameSource(clock).Next());
        var ocr = new BoxesOcr();

        var observed = DomainTipUiReader.Read(image, ocr, Texts);

        Assert.True(observed.IsCandidate);
        Assert.True(observed.CanDismiss);
        Assert.True(observed.IsFor(image.FrameStamp));
        Assert.InRange(observed.CloseBounds.X, 877, 879);
        Assert.InRange(observed.CloseBounds.Y, 686, 688);
        Assert.InRange(observed.CloseBounds.Width, 165, 170);
        Assert.Equal(2, ocr.Calls);
    }

    [Fact]
    public void RecordedCropsAreRecognizedByExplicitCpuPaddleV6()
    {
        Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
        Assert.Null(ConfigService.Config);
        var model = PaddleOcrService.PaddleOcrModelType.V6;
        Assert.True(File.Exists(model.DetectionModel.ModalPath));
        Assert.True(File.Exists(model.RecognitionModel.ModalPath));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(model.RecognitionModel.ModalPath)!, "inference.yml")));
        Assert.True(File.Exists(model.PreHeatImagePath));
        var originalPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process);
        try
        {
            using var factory = new BgiOnnxFactory(NullLogger<BgiOnnxFactory>.Instance, forceCpuOcr: true);
            using var ocr = new PaddleOcrService(factory, model);
            using var image = RecordedFrame(new CaptureFrameSource().Next());
            var observed = DomainTipUiReader.Read(image, ocr, Texts);
            Assert.True(observed.CanDismiss, $"Actual CPU OCR: {observed}");
            Assert.InRange(observed.CloseBounds.X, 870, 890);
            Assert.InRange(observed.CloseBounds.Y, 680, 700);
            Assert.Null(ConfigService.Config);
        }
        finally { Environment.SetEnvironmentVariable("PATH", originalPath, EnvironmentVariableTarget.Process); }
    }

    [Theory]
    [InlineData("title-only", true)]
    [InlineData("footer-only", true)]
    [InlineData("swapped", false)]
    [InlineData("outside", true)]
    [InlineData("tiny", true)]
    [InlineData("ambiguous", true)]
    [InlineData("vague", false)]
    [InlineData("none", false)]
    public void IncompleteOrInvalidOcrNeverCreatesClickPermission(string fault, bool candidate)
    {
        using var image = RecordedFrame(new CaptureFrameSource().Next());
        var ocr = new BoxesOcr { Read = mat =>
        {
            var title = mat.Height == 85;
            if (fault == "none" || fault == "title-only" && !title || fault == "footer-only" && title) return new([]);
            var text = fault == "vague" ? "关闭" : title ^ fault == "swapped" ? Texts.Title : Texts.Close;
            var box = title ? new RotatedRect(new Point2f(114, 45), new Size2f(132, 34), 0)
                : new RotatedRect(new Point2f(412, 25), new Size2f(168, 22), 0);
            if (!title && fault == "outside") box = new(new Point2f(-20, 25), new Size2f(168, 22), 0);
            if (!title && fault == "tiny") box = new(new Point2f(412, 25), new Size2f(1, 1), 0);
            var region = new OcrResultRegion(box, text, 1);
            return fault == "ambiguous" && !title ? new([region, region]) : new([region]);
        } };

        var result = DomainTipUiReader.Read(image, ocr, Texts);

        Assert.Equal(candidate, result.IsCandidate);
        Assert.False(result.CanDismiss);
    }

    [Fact]
    public void RecognitionUsesTheExplicitGameLanguageAndOriginalSource()
    {
        using var image = RecordedFrame(new CaptureFrameSource().Next());
        var words = new DomainTipTexts("Ley Line Disorder", "Click anywhere to close");
        var ocr = new BoxesOcr { Read = mat => new([new(new(new Point2f(200, 25), new Size2f(200, 20), 0),
            mat.Height == 85 ? "Ley Line\nDisorder" : "Click anywhere to close", 1)]) };
        var result = DomainTipUiReader.Read(image, ocr, words);
        Assert.True(result.CanDismiss);
        Assert.Equal(image.FrameStamp, result.Source);
        Assert.False(DomainTipUiReader.Read(image, ocr, Texts).IsCandidate);
        Assert.False((result with { Source = default }).CanDismiss);
    }

    internal static ImageRegion RecordedFrame(CaptureFrameStamp source)
    {
        var full = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black);
        try
        {
            foreach (var (file, bounds) in new[] {
                ("domain-tip-title-20260920.png", new Rect(800, 390, 1050, 85)),
                ("domain-tip-footer-20260920.png", new Rect(550, 673, 820, 47)) })
            {
                using var pixels = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ui", file));
                Assert.False(pixels.Empty());
                Assert.Equal(bounds.Size, pixels.Size());
                using var target = new Mat(full, bounds);
                pixels.CopyTo(target);
            }
            return new ImageRegion(full, 0, 0, drawContent: new DrawContent()) { FrameStamp = source };
        }
        catch { full.Dispose(); throw; }
    }

    internal sealed class BoxesOcr : IOcrService
    {
        internal int Calls;
        internal Func<Mat, OcrResult>? Read;
        public string Ocr(Mat mat) => throw new InvalidOperationException("Only explicit detection is part of this fixture.");
        public string OcrWithoutDetector(Mat mat) => throw new InvalidOperationException("Only explicit detection is part of this fixture.");
        public OcrResult OcrResult(Mat mat)
        {
            Calls++;
            if (Read != null) return Read(mat);
            return mat.Height == 85
                ? new([new(new(new Point2f(114, 45), new Size2f(132, 34), 0), Texts.Title, 1)])
                : new([new(new(new Point2f(412, 25), new Size2f(168, 22), 0), Texts.Close, 1)]);
        }
    }
}
