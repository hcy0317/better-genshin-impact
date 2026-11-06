using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.Common.Job;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Helpers;
using Fischless.GameCapture;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;
public class SwitchPartyTraversalTests(ITestOutputHelper output)
{
    [Fact]
    public async Task PublicStartTraversesBeyondPartiallyReadFirstPageAndConfirmsTheRequestedParty()
    {
        var replay = new Replay();
        bool? found = null;
        var error = await Record.ExceptionAsync(async () => found = await new SwitchPartyTask(replay.Io()).Start("矿物", default));
        output.WriteLine($"publicStart result={found} error={error?.GetType().Name} pageActions={replay.PageActions} selected={replay.Selected} scene={replay.Scene} nativeInputs=0");
        Assert.Null(error);
        Assert.True(found);
        Assert.Equal(1, replay.PageActions);
        Assert.True(replay.Selected);
        Assert.Equal("world", replay.Scene);
        Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
        Assert.Null(System.Windows.Application.Current);
    }

    [Fact]
    public async Task PublicStartRejectsMovingThumbWhenOcrNamesAndPositionsStillDescribeTheOldPage()
    {
        var replay = new Replay { Fault = "thumb-only", MissingTarget = true, NeverAtEnd = true };
        bool? result = null;
        var error = await Record.ExceptionAsync(async () => result = await new SwitchPartyTask(replay.Io()).Start("矿物", default));
        output.WriteLine($"thumb-only publicStart result={result} error={error?.GetType().Name} pageActions={replay.PageActions} selected={replay.Selected} nativeInputs=0");
        Assert.NotNull(error);
        Assert.Equal(1, replay.PageActions);
        Assert.False(replay.Selected);
    }

    [Theory]
    [InlineData("party-list-scrollbar-1742.png")]
    [InlineData("party-list-scrollbar-1818.png")]
    public void ActualS25ScrollbarPixelsStillShowMorePages(string fixture)
    {
        using var crop = Cv2.ImRead(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ui", fixture));
        Assert.Equal(new Size(16, 850), crop.Size());
        using var canvas = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(35, 45, 55));
        using (var destination = new Mat(canvas, new Rect(694, 105, 16, 850))) crop.CopyTo(destination);
        var clock = new FakeTimeProvider(); clock.Advance(TimeSpan.FromMilliseconds(2));
        var page = PartyListPageObservation.Read(canvas, new CaptureFrameSource(clock).Next(), 1, Array.Empty<(string, int)>());
        output.WriteLine($"actual={fixture} kind={page.End} thumb={page.Thumb} nativeInputs=0");
        Assert.Equal(PartyListEnd.More, page.End);
    }

    [Fact]
    public async Task PublicStartReturnsFalseAtTheActuallyObservedEndWithoutChoosingAnotherParty()
    {
        var replay = new Replay { MissingTarget = true };
        Assert.False(await new SwitchPartyTask(replay.Io()).Start("矿物", default));
        Assert.Equal(1, replay.PageActions); Assert.False(replay.Selected); Assert.Equal("world", replay.Scene);
    }

    [Theory]
    [InlineData("no-thumb", 0)]
    [InlineData("stuck", 1)]
    [InlineData("foreign-window", 0)]
    [InlineData("stale-ocr", 0)]
    [InlineData("foreign-session", 1)]
    [InlineData("not-list", 0)]
    public async Task PublicStartRejectsUnknownOrUnprovedPagingBeforeAnotherPageInput(string fault, int allowedPageActions)
    {
        var replay = new Replay { Fault = fault };
        var error = await Record.ExceptionAsync(() => new SwitchPartyTask(replay.Io()).Start("矿物", default));
        Assert.NotNull(error); Assert.Equal(allowedPageActions, replay.PageActions); Assert.False(replay.Selected);
    }

    [Fact]
    public async Task PublicStartKeepsTheOriginalSixteenPageBoundWithoutInventingCompletion()
    {
        var replay = new Replay { MissingTarget = true, NeverAtEnd = true };
        var started = replay.Time.GetTimestamp();
        Assert.False(await new SwitchPartyTask(replay.Io()).Start("矿物", default));
        Assert.Equal(15, replay.PageActions); Assert.False(replay.Selected);
        Assert.True(replay.Time.GetElapsedTime(started) < TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task PublicStartHonorsCancellationBeforeAnyPageInput()
    {
        var replay = new Replay(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SwitchPartyTask(replay.Io()).Start("矿物", cancel.Token));
        Assert.Equal(0, replay.PageActions); Assert.False(replay.Selected);
    }

    internal sealed class Replay : IUiDriver
    {
        internal readonly FakeTimeProvider Time = new();
        private readonly CaptureFrameSource _source;
        private readonly CaptureFrameSource _foreignSource;
        internal string Fault = "";
        internal bool MissingTarget, NeverAtEnd;
        internal string Scene = "party";
        internal int PageActions, Page;
        internal bool Selected;
        internal Replay() { _source = new(Time); _foreignSource = new(Time); }
        internal SwitchPartyTaskIo Io() => new()
        {
            Scale = 1, Clock = Time, Logger = NullLogger.Instance,
            Driver = () => this, Capture = Image, Read = Read,
            Element = (image, name) => name == "PartyBtnDelete" ? image.Derive(620, 990, 52, 50) : image.Derive(20, 30, 50, 30),
            OcrOne = (_, _) => "水", OcrMany = (image, _) => Names(image),
            Click = (_, _, y) => { if (Scene == "party") Scene = "list"; else if (Page == 1) Selected = true; else { PageActions++; Page = 1; } },
            ScrollDown = (_, _) => { PageActions++; if (Fault != "stuck") Page++; },
            ResetTop = _ => { Page = 0; return Task.CompletedTask; }, Delay = DelayAsync,
            ReturnMain = token => UiRecovery.ToMainAsync(this, token, false, NullLogger.Instance),
            Confirm = (_, _) => { if (Scene == "list" && Selected) { Scene = "party"; return true; } if (Scene == "party" && Selected) { Scene = "world"; return true; } return false; },
            Prepare = () => { }, SameTarget = () => Fault != "foreign-window", ClearDrawings = () => { }, ClearCombatScenes = () => { }
        };
        internal ImageRegion Image()
        {
            Time.Advance(TimeSpan.FromMilliseconds(2));
            var mat = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(35, 45, 55));
            if (Scene == "list" && Fault != "no-thumb")
                Cv2.Rectangle(mat, new Rect(698, NeverAtEnd ? 112 + Page * 40 : Page == 0 ? 112 : 730, 6, NeverAtEnd ? 200 : 220), new Scalar(205, 205, 205), -1);
            return new GameCaptureRegion(mat, 0, 0) { FrameStamp = Fault == "foreign-session" && Page > 0 ? _foreignSource.Next() : _source.Next() };
        }
        internal UiSnapshot Read(ImageRegion image) => new UiSnapshot(image.FrameStamp.Sequence)
        { Party = Scene == "party", PartyList = Scene == "list" && Fault != "not-list", MainHud = Scene == "world", Closable = Scene != "world" }
            .WithSource(image.FrameStamp, Time, UiSnapshot.RecoveryMaximumAge);
        private List<Region> Names(ImageRegion image)
        {
            if (Fault == "stale-ocr") Time.Advance(TimeSpan.FromSeconds(3));
            var names = Page == 0 || Fault == "thumb-only" ? new[] { "采集", "水", "火", "雷" } :
                new[] { "雷", "草", MissingTarget ? NeverAtEnd ? "风" + Page : "风" : "矿物" };
            var result = new List<Region>();
            for (var i = 0; i < names.Length; i++) { var region = image.Derive(70, 135 + i * 174, 50, 25); region.Text = names[i]; result.Add(region); }
            return result;
        }
        public UiSnapshot Capture() { using var image = Image(); return Read(image); }
        public Task<bool> ActAsync(UiAction action, UiSnapshot observed, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (action == UiAction.Escape) Scene = Scene == "list" ? "party" : "world";
            else if (action == UiAction.OpenParty) Scene = "party";
            else if (action == UiAction.ApplyParty) Scene = "world";
            return Task.FromResult(true);
        }
        public Task DelayAsync(int milliseconds, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Time.Advance(TimeSpan.FromMilliseconds(milliseconds)); return Task.CompletedTask; }
    }
}
