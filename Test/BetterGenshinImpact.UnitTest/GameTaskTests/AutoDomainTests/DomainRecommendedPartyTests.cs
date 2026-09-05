using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.AutoDomain;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoDomainTests;

public class DomainRecommendedPartyTests
{
    [Fact]
    public void AutomaticSelectionIsOptInAndPreservesTheConfiguredFallback()
    {
        var oldConfig = Newtonsoft.Json.JsonConvert.DeserializeObject<AutoDomainConfig>("{\"PartyName\":\"水\"}")!;
        Assert.False(oldConfig.AutoSelectPartyByRecommendedElements);
        oldConfig.AutoSelectPartyByRecommendedElements = true;
        var restored = Newtonsoft.Json.JsonConvert.DeserializeObject<AutoDomainConfig>(
            Newtonsoft.Json.JsonConvert.SerializeObject(oldConfig))!;
        Assert.True(restored.AutoSelectPartyByRecommendedElements);
        Assert.Equal("水", restored.PartyName);
    }

    [Fact]
    public async Task FailedRecognitionUsesTheConfiguredDefaultParty()
    {
        var activeParty = "草";
        var success = await DomainRecommendedParty.SwitchAsync([], "水",
            (_, _) => throw new InvalidOperationException("No recommendation should be sent to the game"),
            (name, _) => { activeParty = name; return Task.FromResult(true); }, CancellationToken.None);

        Assert.True(success);
        Assert.Equal("水", activeParty);
    }

    [Fact]
    public async Task MissingRecommendedPartyAlsoUsesTheConfiguredDefault()
    {
        var activeParty = "草";
        var success = await DomainRecommendedParty.SwitchAsync(["火", "冰"], "水",
            (_, _) => Task.FromResult(false),
            (name, _) => { activeParty = name; return Task.FromResult(true); }, CancellationToken.None);
        Assert.True(success);
        Assert.Equal("水", activeParty);
    }

    [Fact]
    public async Task AvailableRecommendedPartyDoesNotGetReplacedByTheDefault()
    {
        var activeParty = "水";
        var success = await DomainRecommendedParty.SwitchAsync(["火", "冰"], "水",
            (names, _) => { activeParty = names.Single(name => name == "冰"); return Task.FromResult(true); },
            (_, _) => throw new InvalidOperationException("Do not replace a matching team"), CancellationToken.None);
        Assert.True(success);
        Assert.Equal("冰", activeParty);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task EmptyDefaultPreservesCurrentParty(string? defaultParty)
    {
        Assert.True(await DomainRecommendedParty.SwitchAsync([], defaultParty,
            (_, _) => throw new InvalidOperationException(),
            (_, _) => throw new InvalidOperationException(), CancellationToken.None));
    }

    [Fact]
    public async Task FailureToSwitchTheDefaultIsNotReportedAsSuccess()
    {
        Assert.False(await DomainRecommendedParty.SwitchAsync(["雷"], "水",
            (_, _) => Task.FromResult(false), (_, _) => Task.FromResult(false), CancellationToken.None));
    }

    [Fact]
    public async Task CancellationDoesNotSwitchTheDefaultParty()
    {
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DomainRecommendedParty.SwitchAsync(["火"], "水",
            (_, _) => { cts.Cancel(); return Task.FromResult(false); },
            (_, _) => throw new InvalidOperationException("No game input after cancellation"), cts.Token));
    }

    [Fact]
    public async Task UiFailureIsNotMistakenForAMissingParty()
    {
        var failure = new InvalidOperationException("Lost the party selection page");
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => DomainRecommendedParty.SwitchAsync(["火"], "水",
            (_, _) => throw failure, (_, _) => throw new Exception("Do not operate an unknown page"), CancellationToken.None));
        Assert.Same(failure, actual);
    }

    [Theory]
    [InlineData("火", 255, 102, 64)]
    [InlineData("水", 0, 192, 255)]
    [InlineData("冰", 153, 255, 255)]
    [InlineData("草", 165, 200, 59)]
    [InlineData("风", 128, 255, 215)]
    [InlineData("岩", 255, 204, 0)]
    [InlineData("雷", 204, 128, 255)]
    public void ReadsEachElementColor(string element, int r, int g, int b)
    {
        using var screen = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(40, 40, 40));
        Cv2.Circle(screen, new Point(322, 110), 12, new Scalar(b, g, r), 4);
        Assert.Equal(new[] { element }, DomainRecommendedParty.Recognize(screen, Label()));
    }

    [Theory]
    [InlineData(720)]
    [InlineData(1080)]
    [InlineData(1440)]
    [InlineData(2160)]
    public void SupportsScaledBgraScreens(int height)
    {
        var scale = height / 1080d;
        using var screen = new Mat(height, (int)(1920 * scale), MatType.CV_8UC4, new Scalar(40, 40, 40, 255));
        Cv2.Circle(screen, new Point((int)(322 * scale), (int)(110 * scale)), (int)(12 * scale),
            new Scalar(64, 102, 255, 255), Math.Max(1, (int)(4 * scale)));
        Assert.Equal(new[] { "火" }, DomainRecommendedParty.Recognize(screen, Label(scale: scale)));
    }

    [Fact]
    public void IgnoresColoredCharactersWithoutAConfidentRecommendationLabel()
    {
        using var screen = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(40, 40, 40));
        Cv2.Circle(screen, new Point(322, 110), 12, new Scalar(64, 102, 255), 4);
        Assert.Empty(DomainRecommendedParty.Recognize(screen, Label("推荐队伍等级90")));
        Assert.Empty(DomainRecommendedParty.Recognize(screen, Label(score: 0.3f)));
        Assert.Empty(DomainRecommendedParty.Recognize(screen, new OcrResult([])));
    }

    [Fact]
    public void IgnoresTextAndColoredBackgroundsInTheRecommendationStrip()
    {
        using var screen = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(40, 40, 40));
        Cv2.Circle(screen, new Point(322, 110), 12, Scalar.White, 4);
        Cv2.Rectangle(screen, new Rect(360, 86, 100, 48), new Scalar(64, 102, 255), -1);
        Assert.Empty(DomainRecommendedParty.Recognize(screen, Label()));
    }

    [Fact]
    public void DoesNotReadElementColorsElsewhereInTheScreen()
    {
        using var screen = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(40, 40, 40));
        Cv2.Circle(screen, new Point(322, 300), 12, new Scalar(64, 102, 255), 4);
        Cv2.Circle(screen, new Point(900, 110), 12, new Scalar(255, 192, 0), 4);
        Assert.Empty(DomainRecommendedParty.Recognize(screen, Label()));
    }

    private static OcrResult Label(string text = "推荐元素：", float score = 0.99f, double scale = 1)
        => new([new OcrResultRegion(new RotatedRect(new Point2f((float)(240 * scale), (float)(110 * scale)),
            new Size2f((float)(120 * scale), (float)(22 * scale)), 0), text, score)]);

    [Fact]
    public void ReadsColoredIconsToTheRightOfRecommendedElementsLabelInDisplayOrder()
    {
        using var screen = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(40, 40, 40));
        // Synthetic screen: OCR is an external boundary; rings stand in for the colored icon pixels.
        Cv2.Circle(screen, new Point(322, 110), 12, new Scalar(64, 102, 255), 4);
        Cv2.Circle(screen, new Point(367, 110), 12, new Scalar(255, 192, 0), 4);
        var text = new OcrResult([new OcrResultRegion(
            new RotatedRect(new Point2f(240, 110), new Size2f(120, 22), 0), "推荐元素：", 0.99f)]);

        Assert.Equal(new[] { "火", "水" }, DomainRecommendedParty.Recognize(screen, text));
    }
}
