using BetterGenshinImpact.GameTask.Common.Job;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Model.Area;
using Microsoft.Extensions.Time.Testing;
using Fischless.GameCapture;
using Microsoft.Extensions.Logging.Abstractions;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.Core.Recognition.OCR;
using System.Reflection;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class CurrentPlayerUidTaskTests
{
    [Fact]
    public async Task StartReadsCurrentOwnMenuWhenFooterIsUnavailableAndPublishesOnlyAfterWorld()
    {
        var io = new FakeIo();
        var result = await new CurrentPlayerUidTask(io, io.Clock).Start(CancellationToken.None);
        Assert.Equal(123456789, result);
        Assert.Equal(1, io.MenuInputs);
        Assert.True(io.WorldCapturesAfterReturn >= 2);
        Assert.False(io.Menu);
    }

    [Theory]
    [InlineData("UID 123456789", 123456789)]
    [InlineData("UID 1234567890", 1234567890)]
    public async Task LegalFooterKeepsFastPathWithoutMenu(string text, int expected)
    {
        var io = new FakeIo { Footer = text };
        Assert.Equal(expected, await new CurrentPlayerUidTask(io, io.Clock).Start(CancellationToken.None));
        Assert.Equal(0, io.MenuInputs);
        Assert.Equal(0, io.EscapeInputs);
    }

    [Theory]
    [InlineData("wrong-page")]
    [InlineData("missing-card")]
    [InlineData("missing-label")]
    [InlineData("conflicting")]
    [InlineData("overflow")]
    [InlineData("no-uid")]
    [InlineData("unrelated-digits")]
    [InlineData("wrong-session")]
    public async Task MenuIdentityMustBeCurrentUniqueAndOwned(string defect)
    {
        var io = new FakeIo { Defect = defect };
        Assert.Equal(0, await new CurrentPlayerUidTask(io, io.Clock).Start(CancellationToken.None));
        Assert.Equal(1, io.MenuInputs);
        Assert.False(io.Menu);
    }

    [Fact]
    public async Task SplitLabelAndDigitsUseSameFrameGeometry()
    {
        var io = new FakeIo { Defect = "split" };
        Assert.Equal(123456789, await new CurrentPlayerUidTask(io, io.Clock).Start(CancellationToken.None));
        Assert.True(io.WorldCapturesAfterReturn >= 2);
    }

    [Fact]
    public async Task FailedWorldReturnNeverPublishesAnAlreadyReadCandidate()
    {
        var io = new FakeIo { BlockReturn = true };
        Assert.Equal(0, await new CurrentPlayerUidTask(io, io.Clock).Start(CancellationToken.None));
        Assert.Equal(1, io.MenuInputs);
        Assert.True(io.Clock.GetUtcNow() - io.Started <= TimeSpan.FromSeconds(5.3));
    }

    [Fact]
    public async Task MenuNeverReadyUsesOneOpenAndTheSingleTotalBudget()
    {
        var io = new FakeIo { BlockMenu = true };
        Assert.Equal(0, await new CurrentPlayerUidTask(io, io.Clock).Start(CancellationToken.None));
        Assert.Equal(1, io.MenuInputs);
        Assert.True(io.Clock.GetUtcNow() - io.Started <= TimeSpan.FromSeconds(5.3));
    }

    [Fact]
    public async Task CancellationAfterReadPropagatesAndDoesNotInputForCleanup()
    {
        using var cancelled = new CancellationTokenSource();
        var io = new FakeIo { AfterMenuRead = cancelled.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CurrentPlayerUidTask(io, io.Clock).Start(cancelled.Token));
        Assert.Equal(1, io.MenuInputs);
        Assert.Equal(0, io.EscapeInputs);
    }

    [Fact]
    public async Task CancelledParentNeverCapturesOrOpensMenu()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var io = new FakeIo();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CurrentPlayerUidTask(io, io.Clock).Start(cancelled.Token));
        Assert.Equal(0, io.Captures);
        Assert.Equal(0, io.MenuInputs);
    }

    [Fact]
    public async Task ParentRemainingBudgetIsNotRestartedAtFiveSeconds()
    {
        var io = new FakeIo();
        using var parent = UiOperation.Begin("uid-parent", TimeSpan.FromMilliseconds(300), logger: NullLogger.Instance, clock: io.Clock);
        try { Assert.Equal(0, await new CurrentPlayerUidTask(io, io.Clock).Start(parent.Token)); }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { }
        Assert.Equal(0, io.MenuInputs);
        Assert.True(io.Clock.GetUtcNow() - io.Started <= TimeSpan.FromMilliseconds(300));
        Assert.Equal(0, io.EscapeInputs);
    }

    [Fact]
    public async Task AlreadyOwnMenuDoesNotOpenItAgainButStillReturnsWorld()
    {
        var io = new FakeIo { Menu = true };
        Assert.Equal(123456789, await new CurrentPlayerUidTask(io, io.Clock).Start(CancellationToken.None));
        Assert.Equal(0, io.MenuInputs);
        Assert.True(io.WorldCapturesAfterReturn >= 2);
    }

    [Fact]
    public async Task UnavailableCapturePreservesFailureZeroWithoutUiInput()
    {
        var io = new FakeIo { Defect = "capture-failed" };
        Assert.Equal(0, await new CurrentPlayerUidTask(io, io.Clock).Start(CancellationToken.None));
        Assert.Equal(0, io.MenuInputs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NormalTerminalEndPropagatesEvenWithoutCancelledToken(bool afterMenu)
    {
        var io = new FakeIo();
        if (afterMenu) io.AfterMenuRead = () => throw new NormalEndException("synthetic normal end");
        else io.Defect = "normal-end";
        await Assert.ThrowsAsync<NormalEndException>(() => new CurrentPlayerUidTask(io, io.Clock).Start(CancellationToken.None));
        Assert.Equal(afterMenu ? 1 : 0, io.MenuInputs);
        Assert.Equal(0, io.EscapeInputs);
    }

    [Fact]
    public async Task PreparedServicesResolveOnceAtCompositionAndNeverDuringStart()
    {
        var lookups = 0;
        var textLookups = 0;
        var sessions = 0;
        var service = DispatchProxy.Create<IOcrService, ForbiddenOcrProxy>();
        var io = new FakeIo { Footer = "UID 123456789" };
        var boundaries = new NativeUiDriverIo
        {
            Clock = io.Clock,
            Ocr = () => throw new InvalidOperationException("Uncaptured OCR lookup"),
            Texts = () => { textLookups++; return new("synthetic domain", "synthetic close"); }
        };
        var task = CurrentPlayerUidTask.CreatePrepared(boundaries, () => { lookups++; return service; }, NullLogger.Instance,
            (prepared, ocr) =>
            {
                sessions++;
                Assert.Same(service, ocr);
                Assert.Same(service, prepared.Ocr());
                Assert.Same(service, prepared.Ocr());
                prepared.Texts();
                prepared.Texts();
                return io;
            });
        Assert.Equal(1, lookups);
        Assert.Equal(1, textLookups);
        Assert.Equal(0, sessions);
        Assert.Equal(123456789, await task.Start(CancellationToken.None));
        Assert.Equal(1, lookups);
        Assert.Equal(1, textLookups);
        Assert.Equal(1, sessions);
    }

    public class ForbiddenOcrProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            throw new InvalidOperationException("The factory contract test must not execute OCR");
    }

    private sealed class FakeIo : ICurrentPlayerUidIo
    {
        internal FakeTimeProvider Clock { get; } = new();
        internal DateTimeOffset Started;
        private readonly Guid _session = Guid.NewGuid();
        private long _sequence;
        internal bool Menu, BlockReturn, BlockMenu;
        internal int MenuInputs, EscapeInputs, WorldCapturesAfterReturn, Captures;
        internal string? Footer;
        internal string Defect = string.Empty;
        internal Action? AfterMenuRead;
        private bool _returned;
        internal FakeIo() { Started = Clock.GetUtcNow(); }
        public UiSnapshot Capture()
        {
            Clock.Advance(TimeSpan.FromMilliseconds(20));
            Captures++;
            if (_returned && !Menu) WorldCapturesAfterReturn++;
            var source = new CaptureFrameStamp(_session, ++_sequence, Clock.GetTimestamp(), Clock.TimestampFrequency, Clock.GetUtcNow());
            return new UiSnapshot(_sequence) { MainHud = !Menu, MenuBack = Menu }.WithSource(source, Clock, UiSnapshot.RecoveryMaximumAge);
        }
        public CurrentPlayerUidFrame ReadUidFrame()
        {
            if (Defect == "normal-end") throw new NormalEndException("synthetic normal end");
            if (Defect == "capture-failed") throw new InvalidOperationException("synthetic capture unavailable");
            var scene = Capture();
            var text = new List<CurrentPlayerUidText>();
            if (!Menu && Footer != null) text.Add(new(Footer, 1685, 1053, 220, 23));
            if (Menu)
            {
                if (Defect != "missing-card") text.AddRange(new[] {
                    new CurrentPlayerUidText("冒险等阶", 315, 170, 100, 24),
                    new CurrentPlayerUidText("世界等级", 315, 250, 100, 24),
                    new CurrentPlayerUidText("生日", 315, 290, 50, 24),
                    new CurrentPlayerUidText("队伍配置", 300, 440, 100, 24) });
                if (Defect != "no-uid") text.Add(new(Defect switch {
                    "missing-label" => "123456789", "overflow" => "UID 9999999999",
                    "unrelated-digits" => "UID: 等级60 123456789", "split" => "UID:", _ => "UID 123456789"
                }, 115, 195, Defect == "split" ? 40 : 182, 30));
                if (Defect == "split") text.Add(new("123456789", 159, 195, 125, 30));
                if (Defect == "conflicting") text.Add(new("UID 234567891", 115, 200, 182, 30));
                if (Defect == "wrong-page") scene = scene with { MenuBack = false };
                if (Defect == "wrong-session") scene = scene.WithSource(new(Guid.NewGuid(), _sequence, Clock.GetTimestamp(), Clock.TimestampFrequency, Clock.GetUtcNow()), Clock, UiSnapshot.RecoveryMaximumAge);
                AfterMenuRead?.Invoke();
            }
            return new(scene, 1920, 1080, text);
        }
        public Task<bool> OpenMenuAsync(UiSnapshot observed, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); MenuInputs++; Menu = !BlockMenu; Clock.Advance(TimeSpan.FromMilliseconds(60)); return Task.FromResult(true); }
        public Task<bool> ActAsync(UiAction action, UiSnapshot observed, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); EscapeInputs++; if (!BlockReturn) { Menu = false; _returned = true; } Clock.Advance(TimeSpan.FromMilliseconds(60)); return Task.FromResult(!BlockReturn); }
        public Task DelayAsync(int milliseconds, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Clock.Advance(TimeSpan.FromMilliseconds(milliseconds)); return Task.CompletedTask; }
    }
}
