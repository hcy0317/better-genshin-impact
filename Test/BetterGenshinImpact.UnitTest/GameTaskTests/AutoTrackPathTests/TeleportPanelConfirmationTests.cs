using BetterGenshinImpact.GameTask.AutoTrackPath;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Model.Area;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoTrackPathTests;

public class TeleportPanelConfirmationTests
{
    [Fact]
    public async Task UnknownScreenCannotConfirmTeleportWithoutSendingInput()
    {
        using var image = new ImageRegion(new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black), 0, 0);
        var inputs = 0;

        var confirmed = await TeleportPanelConfirmation.TryConfirmAsync(image,
            _ => { inputs++; return Task.CompletedTask; }, default);

        Assert.False(confirmed);
        Assert.Equal(0, inputs);
    }

    [Fact]
    public async Task MapWithoutTeleportButtonDoesNotSendConfirmation()
    {
        using var image = CreateImage("MapScaleButton.png", 30, 440);
        var inputs = 0;
        Assert.False(await TeleportPanelConfirmation.TryConfirmAsync(image,
            _ => { inputs++; return Task.CompletedTask; }, default));
        Assert.Equal(0, inputs);
    }

    [Fact]
    public async Task VisibleTeleportButtonCanConfirmEvenWithoutOtherMapControls()
    {
        using var image = CreateImage("GoTeleport.png", 1440, 960);
        var inputs = 0;
        Assert.True(await TeleportPanelConfirmation.TryConfirmAsync(image,
            _ => { inputs++; return Task.CompletedTask; }, default));
        Assert.Equal(1, inputs);
    }

    [Fact]
    public async Task CancelledConfirmationDoesNotSendInput()
    {
        using var image = CreateImage("GoTeleport.png", 1440, 960);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var inputs = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TeleportPanelConfirmation.TryConfirmAsync(image,
            _ => { inputs++; return Task.CompletedTask; }, cancellation.Token));
        Assert.Equal(0, inputs);
    }

    [Fact]
    public async Task FailedInputCannotBeReportedAsConfirmed()
    {
        using var image = CreateImage("GoTeleport.png", 1440, 960);
        var failure = new IOException("input failed");
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => TeleportPanelConfirmation.TryConfirmAsync(image,
            _ => Task.FromException(failure), default)));
    }

    [Fact]
    public async Task PanelObservationReplacesThePreTeleportHudInTimeoutDiagnostics()
    {
        using var operation = UiOperation.Begin("teleport", TimeSpan.FromSeconds(5));
        operation.Observe(new UiSnapshot(1) { MainHud = true }, UiTarget.Overworld);
        using var image = new ImageRegion(new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black), 0, 0);
        Assert.False(await TeleportPanelConfirmation.TryConfirmAsync(image, _ => Task.CompletedTask, default));

        Assert.Contains("phase=teleport-panel,map=False,teleportButton=False", operation.Timeout().Message);
        Assert.DoesNotContain("hud=True", operation.Timeout().Message);
    }

    private static ImageRegion CreateImage(string templateName, int x, int y)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "BetterGenshinImpact.sln"))) root = root.Parent;
        Assert.NotNull(root);
        using var template = Cv2.ImRead(Path.Combine(root!.FullName,
            "BetterGenshinImpact", "GameTask", "QuickTeleport", "Assets", "1920x1080", templateName));
        Assert.False(template.Empty());
        var image = new ImageRegion(new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black), 0, 0);
        using var target = new Mat(image.SrcMat, new Rect(x, y, template.Width, template.Height));
        template.CopyTo(target);
        return image;
    }
}
