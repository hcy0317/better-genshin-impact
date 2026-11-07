using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Recognition;
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

    [Theory]
    [InlineData(0)]
    [InlineData(42)]
    public async Task PublicStartCoversAnEighteenTeamViewportUsingNativeGeometryInsteadOfFixedSmallWheels(int separatorGap)
    {
        var os = new GeometryOs { SeparatorGap = separatorGap };
        var started = os.Clock.GetTimestamp();
        var found = await new SwitchPartyTask(os.Io()).Start("岩", default);
        output.WriteLine($"geometry publicStart found={found} wheelCalls={os.WheelCalls} drags={os.Drags} offset={os.Offset} selected={os.Selected} nativeInputs=0");
        Assert.True(found);
        Assert.True(os.Selected); Assert.Equal("world", os.Scene);
        Assert.True(os.Clock.GetElapsedTime(started) < TimeSpan.FromSeconds(60));
        Assert.True(os.WheelCalls + os.Drags <= 15);
        Assert.False(os.LeftHeld); Assert.Equal(2 * os.Drags, os.HeldSettles);
        Assert.True(os.MinimumOverlap >= 2);
        Assert.True(ApplicationHostBootstrapGuard.IsProhibited); Assert.Null(System.Windows.Application.Current);
    }

    [Theory]
    [InlineData("no-effect", 1)]
    [InlineData("content-static", 1)]
    [InlineData("thumb-jitter", 1)]
    [InlineData("upward", 1)]
    [InlineData("foreign-source", 1)]
    [InlineData("bad-track", 0)]
    [InlineData("too-few-rows", 0)]
    [InlineData("stale-rows", 0)]
    [InlineData("foreign-window", 0)]
    public async Task GeometryDragRequiresRealIndependentContentAndThumbFeedback(string fault, int dragCount)
    {
        var os = new GeometryOs { Fault = fault };
        var error = await Record.ExceptionAsync(() => new SwitchPartyTask(os.Io()).Start("岩", default));
        Assert.NotNull(error); Assert.Equal(dragCount, os.Drags);
        Assert.False(os.LeftHeld); Assert.False(os.Selected);
    }

    [Theory]
    [InlineData("cancel-after-down")]
    [InlineData("cancel-after-move")]
    [InlineData("deadline-after-down")]
    [InlineData("foreign-after-down")]
    public async Task GeometryDragAlwaysReleasesItsOwnButtonWhenAdmissionExpires(string fault)
    {
        using var cancellation = new CancellationTokenSource();
        var os = new GeometryOs { Fault = fault, Cancellation = cancellation };
        var error = await Record.ExceptionAsync(() => new SwitchPartyTask(os.Io()).Start("岩", cancellation.Token));
        Assert.NotNull(error);
        if (fault.StartsWith("cancel")) Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.False(os.LeftHeld); Assert.Equal(1, os.Drags); Assert.False(os.Selected);
    }

    [Fact]
    public async Task PublicStartKeepsTwoIndependentObservedRowsWhenOneOcrRowHasTwoFragments()
    {
        var os = new GeometryOs { FragmentLastObservedRow = true };
        var found = await new SwitchPartyTask(os.Io()).Start("岩", default);
        output.WriteLine($"fragments publicStart found={found} drags={os.Drags} minConfirmedPhysicalRowOverlap={os.MinimumConfirmedOverlap} nativeInputs=0");
        Assert.True(found);
        Assert.True(os.MinimumConfirmedOverlap >= 2);
        Assert.False(os.LeftHeld);
    }

    [Fact]
    public async Task PublicStartStopsBeforeFinishMoveWhenOnlyForegroundChangesDuringHeldSettle()
    {
        var os = new GeometryOs { Fault = "focus-after-down" };
        var error = await Record.ExceptionAsync(() => new SwitchPartyTask(os.Io()).Start("岩", default));
        output.WriteLine($"focus publicStart error={error?.GetType().Name} wrongForegroundFinishMoves={os.WrongForegroundFinishMoves} drags={os.Drags} selected={os.Selected} nativeInputs=0");
        Assert.NotNull(error);
        Assert.Equal(0, os.WrongForegroundFinishMoves); Assert.Equal(1, os.Drags);
        Assert.False(os.Selected); Assert.False(os.LeftHeld);
    }

    [Fact]
    public async Task InitialBackgroundCanUseOriginalPrepareBeforeForegroundAdmission()
    {
        var os = new GeometryOs { Fault = "initial-background", Foreground = false };
        Assert.True(await new SwitchPartyTask(os.Io()).Start("岩", default));
        Assert.Equal(0, os.WrongForegroundFinishMoves); Assert.False(os.LeftHeld);
    }

    [Fact]
    public async Task AmbiguousVerticalOcrOverlapCannotAuthorizeADrag()
    {
        var os = new GeometryOs { FragmentLastObservedRow = true, FragmentVerticalOffset = 20 };
        Assert.NotNull(await Record.ExceptionAsync(() => new SwitchPartyTask(os.Io()).Start("岩", default)));
        Assert.Equal(0, os.Drags); Assert.False(os.LeftHeld);
    }

    [Fact]
    public async Task PublicStartUsesRecordedCompleteNameLineBeforeTheRecordedGeometryGuard()
    {
        var os = new GeometryOs { RecordedPage = true }; bool found = false;
        var error = await Record.ExceptionAsync(async () => found = await new SwitchPartyTask(os.Io()).Start("矿物", default));
        output.WriteLine($"recorded publicStart found={found} selected={os.Selected} drags={os.Drags} error={error?.GetType().Name} nativeInputs=0");
        Assert.Null(error); Assert.True(found); Assert.True(os.Selected); Assert.Equal("world", os.Scene); Assert.Equal(0, os.Drags);
        Assert.Equal(223,os.SelectedX); Assert.Equal(473,os.SelectedY); Assert.All(os.LineAttempts.Values,n=>Assert.Equal(1,n));
    }

    [Theory]
    [InlineData("")]
    [InlineData("物")]
    [InlineData("药物")]
    public async Task RecordedPartialOrDifferentLineNeverSelectsMineral(string text)
    {
        var os = new GeometryOs { RecordedPage = true, LineOverride = text };
        Assert.NotNull(await Record.ExceptionAsync(() => new SwitchPartyTask(os.Io()).Start("矿物", default)));
        Assert.False(os.Selected); Assert.Equal(0, os.Drags); Assert.All(os.LineAttempts.Values, n => Assert.Equal(1,n));
    }

    [Theory]
    [InlineData("stale-line")]
    [InlineData("foreign-line")]
    [InlineData("frequency-line")]
    [InlineData("focus-line")]
    [InlineData("window-line")]
    [InlineData("cancel-line")]
    [InlineData("deadline-line")]
    [InlineData("foreign-name-page")]
    public async Task RecordedLineRequiresTheOriginalPageAdmissionAfterRecognition(string fault)
    {
        using var cancellation = new CancellationTokenSource();
        var os = new GeometryOs { RecordedPage = true, Fault = fault, Cancellation = cancellation };
        var error = await Record.ExceptionAsync(() => new SwitchPartyTask(os.Io()).Start("矿物", cancellation.Token));
        Assert.NotNull(error); Assert.False(os.Selected); Assert.Equal(0, os.Drags); Assert.False(os.LeftHeld);
        if (fault == "cancel-line") Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.All(os.LineAttempts.Values, n => Assert.Equal(1,n));
    }

    [Fact]
    public async Task FullPageExactNameRetainsItsFastPathWithoutAnyLineRead()
    {
        var os = new GeometryOs { RecordedPage = true, FakeExactName = true };
        Assert.True(await new SwitchPartyTask(os.Io()).Start("矿物", default));
        Assert.Empty(os.LineAttempts); Assert.Equal(223,os.SelectedX); Assert.Equal(473,os.SelectedY);
    }

    internal sealed class GeometryOs : IUiDriver
    {
        internal readonly FakeTimeProvider Clock = new();
        private readonly CaptureFrameSource _source;
        private readonly CaptureFrameSource _foreign;
        internal string Fault = "";
        internal bool RecordedPage, FakeExactName;
        internal string? LineOverride;
        internal double SelectedX, SelectedY;
        internal System.Collections.Generic.Dictionary<Rect,int> LineAttempts = new();
        private static string FixturePath(string extension) => System.IO.Path.Combine(AppContext.BaseDirectory,"Fixtures","Ui","party-name-history-0550."+extension);
        internal CancellationTokenSource? Cancellation;
        private bool _sameTarget = true;
        internal bool Foreground = true;
        internal int WrongForegroundFinishMoves;
        private double? _visualThumbTop;
        internal int HeldSettles;
        internal int SeparatorGap;
        internal int MinimumOverlap = int.MaxValue;
        private int[] _visibleAtPress = Array.Empty<int>();
        private int[] _confirmedRows = Array.Empty<int>(), _confirmedAtPress = Array.Empty<int>();
        internal int MinimumConfirmedOverlap = int.MaxValue;
        internal bool FragmentLastObservedRow;
        internal int FragmentVerticalOffset;
        private const int ViewTop = 105, ViewHeight = 850, Pitch = 174, Count = 18;
        private const int TrackHeight = 850;
        internal double Offset;
        internal int WheelCalls = 0, Drags;
        internal bool Selected, LeftHeld;
        private Point2d _pointer;
        private double _grabOffset;
        internal string Scene = "party";
        private readonly string[] _teams = Enumerable.Range(0, Count).Select(i => i == 14 ? "岩" : "队伍" + i).ToArray();
        internal GeometryOs() { _source = new(Clock); _foreign = new(Clock); }
        private int ContentHeight => Count * Pitch + (Count - 1) / 4 * SeparatorGap;
        private double MaximumOffset => ContentHeight - ViewHeight;
        private int ThumbHeight => (int)Math.Round(TrackHeight * ViewHeight / (double)ContentHeight);
        private int RowTop(int index) => ViewTop + 30 + index * Pitch + index / 4 * SeparatorGap;
        private int[] VisibleRows() => Enumerable.Range(0, Count).Where(i => RowTop(i) - Offset >= ViewTop + 1 && RowTop(i) - Offset + 25 <= ViewTop + ViewHeight - 1).ToArray();
        private int ThumbTop => _visualThumbTop is { } visual ? (int)Math.Round(visual) : ViewTop + (int)Math.Round(Offset / MaximumOffset * (TrackHeight - ThumbHeight));
        internal SwitchPartyTaskIo Io() => new()
        {
            Scale = 1, Clock = Clock, Logger = NullLogger.Instance, Driver = () => this,
            Capture = Image, Read = Read, Element = (image, name) => name == "PartyBtnDelete" ? RecordedPage ? image.Derive(628,1000,40,40) : image.Derive(620, 990, 52, 50) : image.Derive(20, 30, 50, 30),
            OcrOne = (_, _) => "水", ReadNames = (image, _) => BindNames(image),
            Click = (_, x, y) =>
            {
                if (Scene == "party") { Scene = "list"; return; }
                if (RecordedPage) { SelectedX=x; SelectedY=y; Selected = x == 223 && y == 473; return; }
                var row = Enumerable.Range(0, Count).OrderBy(i => Math.Abs(RowTop(i) - Offset + 25 - y)).First();
                Selected = row == 14;
            },
            MovePointer = (_, point) =>
            {
                _pointer = point;
                if (!LeftHeld) return;
                if (!Foreground) { WrongForegroundFinishMoves++; return; }
                var requestedTop = point.Y - _grabOffset;
                if (Fault == "no-effect") return;
                if (Fault == "content-static") { _visualThumbTop = requestedTop; return; }
                if (Fault == "upward") { _visualThumbTop = ViewTop - 1; return; }
                Offset = Math.Clamp((requestedTop - ViewTop) / (TrackHeight - ThumbHeight) * MaximumOffset, 0, MaximumOffset);
                if (Fault == "thumb-jitter") _visualThumbTop = ViewTop + 1;
                if (Fault == "cancel-after-move") Cancellation!.Cancel();
            },
            BindLeftButton = () => (() =>
            {
                _grabOffset = _pointer.Y - ThumbTop; _visibleAtPress = VisibleRows(); _confirmedAtPress = _confirmedRows.ToArray(); LeftHeld = true;
                if (Fault == "cancel-after-down") Cancellation!.Cancel();
                if (Fault == "deadline-after-down") Clock.Advance(TimeSpan.FromSeconds(61));
                if (Fault == "foreign-after-down") _sameTarget = false;
            },
            () => { MinimumOverlap = Math.Min(MinimumOverlap, _visibleAtPress.Intersect(VisibleRows()).Count()); MinimumConfirmedOverlap = Math.Min(MinimumConfirmedOverlap, _confirmedAtPress.Intersect(VisibleRows()).Count()); LeftHeld = false; Drags++; }, () => true),
            ResetTop = _ => { Offset = 0; return Task.CompletedTask; }, Delay = DelayAsync,
            ReturnMain = token => UiRecovery.ToMainAsync(this, token, false, NullLogger.Instance),
            Confirm = (_, _) => { if (Scene == "list" && Selected) { Scene = "party"; return true; } if (Scene == "party" && Selected) { Scene = "world"; return true; } return false; },
            Prepare = () => { if (Fault == "initial-background") Foreground = true; }, SameTarget = () => _sameTarget && Fault != "foreign-window", ForegroundReady = () => Foreground, ClearDrawings = () => { }, ClearCombatScenes = () => { }
        };
        private ImageRegion Image()
        {
            Clock.Advance(TimeSpan.FromMilliseconds(2));
            var pixels = RecordedPage && Scene == "list" ? Cv2.ImRead(FixturePath("png")) : new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(35, 45, 55));
            if (Scene == "list" && !RecordedPage)
            {
                foreach (var row in VisibleRows())
                {
                    var y = (int)Math.Round(RowTop(row) - Offset);
                    Cv2.Rectangle(pixels, new Rect(40, y - 20, 630, 150), new Scalar(55, 65, 75), -1);
                    Cv2.PutText(pixels, "team" + row, new Point(70, y + 20), HersheyFonts.HersheySimplex, 0.7, Scalar.White);
                }
            }
            if (Scene == "list" && !RecordedPage && Fault != "bad-track") Cv2.Rectangle(pixels, new Rect(698, ThumbTop, 6, ThumbHeight), new Scalar(205, 205, 205), -1);
            return new GameCaptureRegion(pixels, 0, 0) { FrameStamp = Fault == "foreign-source" && Drags > 0 ? _foreign.Next() : _source.Next() };
        }
        private PartyNameOcrPage BindNames(ImageRegion image)
        {
            var names = Names(image);
            if (Fault == "foreign-name-page")
            {
                using var foreign = new ImageRegion(image.SrcMat.Clone(),0,0) { FrameStamp = _foreign.Next() };
                return new PartyNameOcrPage(foreign,names,_ => "矿物");
            }
            return new PartyNameOcrPage(image,names,band => ReadNameLine(image,band));
        }

        private string ReadNameLine(ImageRegion image, Rect band)
        {
            LineAttempts[band] = LineAttempts.GetValueOrDefault(band) + 1;
            if (!RecordedPage) return string.Empty;
            using var data = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(FixturePath("json")));
            var line = data.RootElement.GetProperty("line"); var expected = line.GetProperty("band").EnumerateArray().Select(n => n.GetInt32()).ToArray();
            if (band != new Rect(expected[0],expected[1],expected[2],expected[3])) return string.Empty;
            if (Fault == "stale-line") Clock.Advance(TimeSpan.FromSeconds(3));
            if (Fault == "deadline-line") Clock.Advance(TimeSpan.FromSeconds(61));
            if (Fault == "foreign-line") image.FrameStamp = _foreign.Next();
            if (Fault == "frequency-line") { var s=image.FrameStamp; image.FrameStamp = new CaptureFrameStamp(s.SessionId,s.Sequence,s.CapturedTimestamp,s.TimestampFrequency+1,s.CapturedAt); }
            if (Fault == "focus-line") Foreground=false;
            if (Fault == "window-line") _sameTarget=false;
            if (Fault == "cancel-line") Cancellation!.Cancel();
            return LineOverride ?? line.GetProperty("text").GetString()!;
        }

        private List<Region> Names(ImageRegion image)
        {
            var result = new List<Region>();
            if (RecordedPage)
            {
                using var data = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(FixturePath("json")));
                foreach (var row in data.RootElement.GetProperty("recordedRows").EnumerateArray())
                { var hit = image.Derive(row.GetProperty("X").GetInt32(),row.GetProperty("Y").GetInt32(),row.GetProperty("Width").GetInt32(),row.GetProperty("Height").GetInt32()); hit.Text = row.GetProperty("Text").GetString()!; if (FakeExactName && hit.Text == "物") hit.Text="矿物"; result.Add(hit); }
                return result;
            }
            if (FragmentLastObservedRow && Drags == 0)
            {
                _confirmedRows = new[] { 0, 1, 3 };
                foreach (var id in new[] { 0, 1 })
                { var row = image.Derive(70, RowTop(id), 65, 25); row.Text = _teams[id]; result.Add(row); }
                var a = image.Derive(70, RowTop(3), 20, 25); a.Text = "后"; result.Add(a);
                var b = image.Derive(96, RowTop(3) + FragmentVerticalOffset, 20, 25); b.Text = "排"; result.Add(b);
                return result;
            }
            _confirmedRows = VisibleRows();
            for (var i = 0; i < Count; i++)
            {
                var y = (int)Math.Round(RowTop(i) - Offset);
                if (y < ViewTop + 1 || y + 25 > ViewTop + ViewHeight - 1) continue;
                var hit = image.Derive(70, y, 65, 25); hit.Text = _teams[i]; result.Add(hit);
            }
            if (Fault == "stale-rows") Clock.Advance(TimeSpan.FromSeconds(3));
            if (Fault == "too-few-rows")
            {
                foreach (var extra in result.Skip(1)) extra.Dispose();
                return result.Take(1).ToList();
            }
            return result;
        }
        private UiSnapshot Read(ImageRegion image) => new UiSnapshot(image.FrameStamp.Sequence)
        { Party = Scene == "party", PartyList = Scene == "list", MainHud = Scene == "world", Closable = Scene != "world" }
            .WithSource(image.FrameStamp, Clock, UiSnapshot.RecoveryMaximumAge);
        public UiSnapshot Capture() { using var frame = Image(); return Read(frame); }
        public Task<bool> ActAsync(UiAction action, UiSnapshot observed, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); if (action == UiAction.Escape) Scene = Scene == "list" ? "party" : "world"; return Task.FromResult(true); }
        public Task DelayAsync(int ms, CancellationToken ct) { ct.ThrowIfCancellationRequested(); if (LeftHeld && ms == 60) { HeldSettles++; if (Fault == "focus-after-down") Foreground = false; } Clock.Advance(TimeSpan.FromMilliseconds(ms)); return Task.CompletedTask; }
    }

    [Fact]
    public async Task PublicStartVerifiesTheRequestedPartyIsAlreadyActiveAfterTheOriginalSelection()
    {
        // S44 source397 is manual ground truth; these are explicit fake perception values, not recorded OCR.
        var replay = new Replay { AlreadyActiveCase = true, VisiblePartyName = "岩" };
        bool? found = null;
        var error = await Record.ExceptionAsync(async () => found = await new SwitchPartyTask(replay.Io()).Start("岩", default));
        output.WriteLine($"already-active found={found} error={error?.GetType().Name} selectSent={replay.SelectConfirmSent} applyAttempts={replay.ApplyAttempts} nativeApplySent={replay.ApplySent} scene={replay.Scene}");
        Assert.True(replay.Selected);
        Assert.Equal(1, replay.InitialEmptyNameReads);
        Assert.Equal(1, replay.SelectConfirmSent);
        Assert.True(replay.SelectionSource.IsKnown);
        Assert.True(replay.PartyObservationsAfterSelection >= 2);
        Assert.Null(error);
        Assert.True(found);
        Assert.Equal(0, replay.ApplyAttempts);
        Assert.Equal(0, replay.ApplySent);
        Assert.Equal(2, replay.ActiveStateReadSources.Count);
        Assert.Single(replay.ActiveStateReadSources.Distinct());
        Assert.All(replay.ActiveStateReadSources, source =>
        {
            Assert.True(source.IsKnown);
            Assert.True(source.IsAfter(replay.SelectionSource));
            Assert.True(source.CapturedTimestamp >= replay.SelectionCompletedAt + replay.Time.TimestampFrequency * 60 / 1000);
        });
        Assert.Equal("world", replay.Scene);
        Assert.True(replay.WorldSources.Distinct().Count() >= 2);
        Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
        Assert.Null(System.Windows.Application.Current);
    }

    [Fact]
    public async Task PublicStartCannotTreatAnotherAlreadyActivePartyAsTheRequestedParty()
    {
        var replay = new Replay { AlreadyActiveCase = true, VisiblePartyName = "风" };
        bool? found = null;
        var error = await Record.ExceptionAsync(async () => found = await new SwitchPartyTask(replay.Io()).Start("岩", default));
        Assert.NotNull(error);
        Assert.NotEqual(true, found);
        Assert.Equal(1, replay.SelectConfirmSent);
        Assert.Equal(0, replay.ApplySent);
        Assert.NotEqual("world", replay.Scene);
        Assert.Empty(replay.WorldSources);
    }

    [Theory]
    [InlineData("empty-name")]
    [InlineData("source-after-read")]
    [InlineData("window-after-read")]
    public async Task PublicStartCannotVerifyAlreadyActiveFromAnUnknownOrInvalidatedRead(string fault)
    {
        var replay = new Replay { AlreadyActiveCase = true, VisiblePartyName = fault == "empty-name" ? "" : "岩", ActiveReadFault = fault };
        bool? found = null;
        var error = await Record.ExceptionAsync(async () => found = await new SwitchPartyTask(replay.Io()).Start("岩", default));
        Assert.NotNull(error);
        Assert.NotEqual(true, found);
        Assert.Equal(1, replay.SelectConfirmSent);
        Assert.Equal(0, replay.ApplySent);
        Assert.NotEqual("world", replay.Scene);
        Assert.Empty(replay.WorldSources);
    }

    internal sealed class Replay : IUiDriver
    {
        private PartyNameOcrPage BindNames(ImageRegion image) => new(image,Names(image),_ => string.Empty);
        private string ReadNameLine(ImageRegion image, Rect band) => string.Empty;
        internal readonly FakeTimeProvider Time = new();
        private readonly CaptureFrameSource _source;
        private readonly CaptureFrameSource _foreignSource;
        internal string Fault = "";
        internal bool MissingTarget, NeverAtEnd;
        internal string Scene = "party";
        internal int PageActions, Page;
        internal bool Selected;
        internal bool AlreadyActiveCase;
        internal string VisiblePartyName = "岩", ActiveReadFault = "";
        internal int InitialEmptyNameReads, SelectConfirmSent, ApplyAttempts, ApplySent, PartyObservationsAfterSelection;
        internal CaptureFrameStamp SelectionSource;
        internal long SelectionCompletedAt;
        internal readonly List<CaptureFrameStamp> ActiveStateReadSources = new(), WorldSources = new();
        private bool _selectionConfirmed, _samePartyWindow = true;
        private Point2d _pointer;
        private bool _leftHeld;
        private double _thumbTop = 112;
        private int ThumbHeight => MissingTarget && !NeverAtEnd ? 800 : NeverAtEnd ? 20 : 220;
        internal Replay() { _source = new(Time); _foreignSource = new(Time); }
        internal SwitchPartyTaskIo Io() => new()
        {
            Scale = 1, Clock = Time, Logger = NullLogger.Instance,
            Driver = () => this, Capture = Image, Read = Read,
            Element = (image, name) => name == "PartyBtnDelete" ? image.Derive(620, 990, 52, 50) : image.Derive(20, 30, 50, 30),
            OcrOne = ReadSingle, ReadNames = (image, _) => BindNames(image),
            Click = (_, x, y) => { if (Scene == "party") Scene = "list"; else if (Page == 1) Selected = true; else { PageActions++; Page = 1; } },
            MovePointer = (_, point) => { _pointer = point; },
            BindLeftButton = () => (() => { _leftHeld = true; },
            () =>
            {
                if (!_leftHeld) return; _leftHeld = false; PageActions++;
                if (Fault != "stuck") { Page++; _thumbTop = _pointer.Y - ThumbHeight / 2d; }
            }, () => true),
            ResetTop = _ => { Page = 0; return Task.CompletedTask; }, Delay = DelayAsync,
            ReturnMain = token => UiRecovery.ToMainAsync(this, token, false, NullLogger.Instance),
            Confirm = (image, _) =>
            {
                if (Scene == "list" && Selected)
                {
                    SelectConfirmSent++; SelectionSource = image.FrameStamp; SelectionCompletedAt = Time.GetTimestamp();
                    _selectionConfirmed = true; Scene = "party"; return true;
                }
                if (Scene == "party" && Selected)
                {
                    ApplyAttempts++;
                    if (AlreadyActiveCase) return false;
                    ApplySent++; Scene = "world"; return true;
                }
                return false;
            },
            Prepare = () => { }, SameTarget = () => Fault != "foreign-window" && _samePartyWindow, ForegroundReady = () => true, ClearDrawings = () => { }, ClearCombatScenes = () => { }
        };
        private string ReadSingle(ImageRegion image, RecognitionObject recognition)
        {
            if (!AlreadyActiveCase) return "水";
            if (!_selectionConfirmed) { InitialEmptyNameReads++; return string.Empty; }
            ActiveStateReadSources.Add(image.FrameStamp);
            var text = recognition.RegionOfInterest.X >= image.Width / 2 ? "已出战" : VisiblePartyName;
            if (ActiveStateReadSources.Count == 2)
            {
                if (ActiveReadFault == "source-after-read") image.FrameStamp = _foreignSource.Next();
                if (ActiveReadFault == "window-after-read") _samePartyWindow = false;
            }
            return text;
        }

        internal ImageRegion Image()
        {
            Time.Advance(TimeSpan.FromMilliseconds(2));
            var mat = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(35, 45, 55));
            if (Scene == "list" && Fault != "no-thumb")
                Cv2.Rectangle(mat, new Rect(698, (int)Math.Round(_thumbTop), 6, ThumbHeight), new Scalar(205, 205, 205), -1);
            return new GameCaptureRegion(mat, 0, 0) { FrameStamp = Fault == "foreign-session" && Page > 0 ? _foreignSource.Next() : _source.Next() };
        }
        internal UiSnapshot Read(ImageRegion image)
        {
            if (_selectionConfirmed && Scene == "party") PartyObservationsAfterSelection++;
            if (_selectionConfirmed && Scene == "world") WorldSources.Add(image.FrameStamp);
            return new UiSnapshot(image.FrameStamp.Sequence)
            { Party = Scene == "party", PartyList = Scene == "list" && Fault != "not-list", MainHud = Scene == "world", Closable = Scene != "world" }
                .WithSource(image.FrameStamp, Time, UiSnapshot.RecoveryMaximumAge);
        }
        private List<Region> Names(ImageRegion image)
        {
            if (Fault == "stale-ocr") Time.Advance(TimeSpan.FromSeconds(3));
            var names = Page == 0 || Fault == "thumb-only" ? new[] { "采集", "水", "火", "雷" } :
                new[] { "雷", "草", MissingTarget ? NeverAtEnd ? "风" + Page : "风" : AlreadyActiveCase ? "岩" : "矿物" };
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
