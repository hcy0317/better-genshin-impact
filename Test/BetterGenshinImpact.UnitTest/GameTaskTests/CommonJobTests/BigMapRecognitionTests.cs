using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Model.Area;
using Microsoft.Extensions.Time.Testing;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class BigMapRecognitionTests
{
    [Fact]
    public void MapScaleControlRecognizesCandidateListWithoutCloseButton()
    {
        using var image = new ImageRegion(new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black), 0, 0);
        PaintTemplate(image, "MapScaleButton.png", 30, 440);

        Assert.True(Bv.IsInBigMapUi(image));
    }

    [Fact]
    public async Task RecoveryCanExitCandidateListWithoutCloseButtonAndVerifyOverworld()
    {
        using var image = new ImageRegion(new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black), 0, 0);
        PaintTemplate(image, "MapScaleButton.png", 30, 440);
        var clock = new FakeTimeProvider();
        var driver = new MapRecoveryDriver(image, clock);

        var recovered = await UiRecovery.ToMainAsync(driver, default, requireOverworld: true, clock: clock);

        Assert.True(recovered.Matches(UiTarget.Overworld));
        Assert.True(driver.Escaped);
    }

    [Fact]
    public void SettingsIconWithoutMapCloseControlIsNotABigMap()
    {
        using var image = new ImageRegion(new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black), 0, 0);
        PaintTemplate(image, "MapSettingsButton.png", 25, 990);

        Assert.False(Bv.IsInBigMapUi(image));
    }

    [Fact]
    public void PaimonMenuBackControlRejectsSpuriousMapScaleMatch()
    {
        using var image = new ImageRegion(new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black), 0, 0);
        PaintTemplate(image, "MapScaleButton.png", 30, 440);
        PaintTemplate(image, "esc_return_button.png", 0, 0, "UseRedeemCode");

        Assert.False(Bv.IsInBigMapUi(image));
    }

    [Theory]
    [InlineData("MapSettingsButton.png", 25, 990)]
    [InlineData("MapScaleButton.png", 30, 440)]
    public void IndependentMapControlsStillRecognizeTheMap(string name, int x, int y)
    {
        using var image = new ImageRegion(new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black), 0, 0);
        PaintTemplate(image, name, x, y);
        PaintTemplate(image, "MapCloseButton.png", 1813, 19);

        Assert.True(Bv.IsInBigMapUi(image));
    }

    [Fact]
    public void GenericCloseControlAloneIsNotABigMap()
    {
        using var image = new ImageRegion(new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black), 0, 0);
        PaintTemplate(image, "MapCloseButton.png", 1813, 19);

        Assert.False(Bv.IsInBigMapUi(image));
    }

    private static void PaintTemplate(ImageRegion image, string name, int x, int y, string assetGroup = "QuickTeleport")
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "BetterGenshinImpact.sln"))) root = root.Parent;
        Assert.NotNull(root);
        using var template = Cv2.ImRead(Path.Combine(root!.FullName,
            "BetterGenshinImpact", "GameTask", assetGroup, "Assets", "1920x1080", name));
        Assert.False(template.Empty());
        using var target = new Mat(image.SrcMat, new Rect(x, y, template.Width, template.Height));
        template.CopyTo(target);
    }

    private sealed class MapRecoveryDriver(ImageRegion image, FakeTimeProvider clock) : IUiDriver
    {
        private long _frame;
        public bool Escaped { get; private set; }
        public UiSnapshot Capture() => new(++_frame)
        {
            BigMap = !Escaped && Bv.IsInBigMapUi(image),
            MainHud = Escaped
        };
        public Task DelayAsync(int milliseconds, CancellationToken ct)
        {
            clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
        public Task<bool> ActAsync(UiAction action, UiSnapshot observed, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Escaped = action == UiAction.Escape && observed.CanEscape;
            return Task.FromResult(Escaped);
        }
    }
}
