using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Mask;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Helpers;
using Fischless.GameCapture;
using Fischless.WindowsInput;
using Vanara.PInvoke;
using Microsoft.Extensions.Time.Testing;
using OpenCvSharp;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public sealed class LoginNetworkRecoveryTests
{
    [Fact]
    public async Task AuthenticationAtUnqualifiedLoginOverridesLegacyPromptAndClosableEvidence()
    {
        using var fixture = new LoginNetworkFixture { Page = LoginFixturePage.Entry, ExtraText = "请输入验证码" };
        fixture.ReadScene = image => new UiSnapshot(image.FrameStamp.Sequence) { Prompt = true, Closable = true };
        Assert.NotNull((await fixture.Recover()).Error);
        Assert.Empty(fixture.Clicks); Assert.Empty(fixture.OtherActions); Assert.Empty(fixture.NativeStages);
    }

    [Fact]
    public async Task TwoTextlessCentralMasksCannotAuthorizeTheVisibleBackgroundEntryButton()
    {
        using var fixture = new LoginNetworkFixture();
        fixture.Overlay = pixels =>
        {
            if (fixture.Page != LoginFixturePage.Entry) return;
            Cv2.Rectangle(pixels, new(550, 300, 320, 170), Scalar.White, -1);
            Cv2.Rectangle(pixels, new(980, 300, 320, 170), Scalar.White, -1);
        };
        Assert.NotNull((await fixture.Recover()).Error);
        Assert.Single(fixture.Clicks); Assert.Empty(fixture.OtherActions);
    }

    [Fact]
    public async Task SuccessfulRecoveryRetiresItsLoginGateBeforeTheNextKnownUi()
    {
        using var fixture = new LoginNetworkFixture();
        var result = await fixture.Recover();
        Assert.Null(result.Error); Assert.True(result.Snapshot?.Matches(UiTarget.Overworld));
        fixture.ReadScene = image => new UiSnapshot(image.FrameStamp.Sequence) { Party = true };
        var next = fixture.Driver.Capture();
        Assert.True(next.Matches(UiTarget.Party));
        Assert.Equal(2, fixture.Clicks.Count); Assert.Empty(fixture.OtherActions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PassiveOfflineAndRealSpinnerVetoRunBeforeOcrOrInput(bool spinner)
    {
        using var fixture = new LoginNetworkFixture { Offline = !spinner };
        fixture.OnOcr = () => throw new InvalidOperationException("Passive wait must not ask OCR to authorize input.");
        if (spinner) fixture.Overlay = pixels =>
        {
            Point[] centers = [new(960, 585), new(992, 572), new(1005, 540)];
            foreach (var center in centers) Cv2.Circle(pixels, center, 7, new Scalar(40, 210, 255), -1);
        };
        var result = await fixture.Recover();
        Assert.IsType<NetworkInterruptionException>(result.Error);
        Assert.Empty(fixture.Clicks); Assert.Empty(fixture.OtherActions); Assert.Empty(fixture.NativeStages);
    }

    [Fact]
    public async Task CancellationAfterMouseMovePreventsButtonDown()
    {
        using var cancel = new CancellationTokenSource();
        using var fixture = new LoginNetworkFixture();
        fixture.OnNativeStage = stage => { if (stage == "down") cancel.Cancel(); };
        Assert.IsAssignableFrom<OperationCanceledException>((await fixture.Recover(cancel.Token)).Error);
        Assert.Contains("move", fixture.NativeStages);
        Assert.Empty(fixture.Clicks); Assert.Empty(fixture.OtherActions);
    }

    [Fact]
    public async Task UncertainButtonSubmissionReleasesAndCannotBeRetriedByTheSameDriver()
    {
        using var fixture = new LoginNetworkFixture();
        fixture.OnClicked = _ => throw new IOException("Uncertain native submission after button down.");
        Assert.NotNull((await fixture.Recover()).Error);
        Assert.Single(fixture.Clicks); Assert.Contains("up", fixture.NativeStages);
        fixture.OnClicked = null;
        Assert.NotNull((await fixture.Recover()).Error);
        Assert.Single(fixture.Clicks); Assert.Empty(fixture.OtherActions);
    }

    [Fact]
    public void AnExistingReviveDialogIsNotReclassifiedAsALoginErrorFromItsPanelColour()
    {
        using var fixture = new LoginNetworkFixture { Title = "提示", Body = "角色已倒下", ErrorCode = "" };
        fixture.ReadScene = image => new UiSnapshot(image.FrameStamp.Sequence) { Revive = true, Prompt = true };
        var snapshot = fixture.Driver.Capture();
        Assert.True(snapshot.Revive);
        Assert.False(snapshot.LoginNetwork.IsCandidate);
        Assert.Empty(fixture.Clicks); Assert.Empty(fixture.OtherActions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedCaptureTimeDoesNotAuthorizeLoginInput(bool afterFirstConfirm)
    {
        using var fixture = new LoginNetworkFixture();
        void RepeatCaptureTime()
        {
            CaptureFrameStamp? first = null;
            fixture.OverrideSource = stamp =>
            {
                first ??= stamp;
                return first.Value with { Sequence = stamp.Sequence };
            };
        }
        if (afterFirstConfirm) fixture.OnClicked = _ => RepeatCaptureTime(); else RepeatCaptureTime();
        Assert.NotNull((await fixture.Recover()).Error);
        Assert.Equal(afterFirstConfirm ? 1 : 0, fixture.Clicks.Count);
        Assert.Empty(fixture.OtherActions);
    }

    [Fact]
    public async Task TwoSequenceNumbersAtOneCaptureTimeCannotCompleteWorldRecovery()
    {
        using var fixture = new LoginNetworkFixture();
        CaptureFrameStamp? firstWorld = null;
        var worldFrames = 0;
        fixture.OverrideSource = stamp =>
        {
            if (fixture.Page != LoginFixturePage.World) return stamp;
            worldFrames++; firstWorld ??= stamp;
            return worldFrames <= 2 ? firstWorld.Value with { Sequence = stamp.Sequence } : stamp;
        };
        var result = await fixture.Recover();
        Assert.Null(result.Error); Assert.True(result.Snapshot?.Matches(UiTarget.Overworld));
        Assert.True(worldFrames >= 3, "One capture time cannot supply both required post-login world confirmations.");
        Assert.Equal(2, fixture.Clicks.Count);
    }

    [Fact]
    public async Task ALoginEntryWithoutThisRootsConfirmedNetworkErrorReceivesNoInput()
    {
        using var fixture = new LoginNetworkFixture { Page = LoginFixturePage.Entry };
        var result = await fixture.Recover();
        Assert.NotNull(result.Error);
        Assert.Empty(fixture.Clicks);
        Assert.Empty(fixture.OtherActions);
    }

    [Theory]
    [InlineData("4202", "网络错误", 1)]
    [InlineData("4201", "提示", 1)]
    [InlineData("4201", "网络错误", 0)]
    [InlineData("4201", "网络错误", 2)]
    public async Task AmbiguousOrUnsupportedDialogDoesNotReceiveAnyInput(string code, string title, int buttons)
    {
        using var fixture = new LoginNetworkFixture { ErrorCode = code, Title = title, ConfirmCount = buttons };
        Assert.NotNull((await fixture.Recover()).Error);
        Assert.Empty(fixture.Clicks); Assert.Empty(fixture.OtherActions);
    }

    [Fact]
    public async Task AuthenticationAtTheErrorDialogHasZeroInput()
    {
        using var fixture = new LoginNetworkFixture { ExtraText = "请输入验证码" };
        Assert.NotNull((await fixture.Recover()).Error);
        Assert.Empty(fixture.Clicks); Assert.Empty(fixture.OtherActions);
    }

    [Fact]
    public async Task AuthenticationAfterConfirmRevokesEntryEligibility()
    {
        using var fixture = new LoginNetworkFixture();
        fixture.OnClicked = _ => fixture.ExtraText = "请输入验证码";
        Assert.NotNull((await fixture.Recover()).Error);
        Assert.Single(fixture.Clicks); Assert.Empty(fixture.OtherActions);
    }

    [Fact]
    public async Task AnUnknownModalOverTheEntryButtonRevokesEntryEligibility()
    {
        using var fixture = new LoginNetworkFixture();
        fixture.OnClicked = _ => fixture.UnknownEntryModal = true;
        Assert.NotNull((await fixture.Recover()).Error);
        Assert.Single(fixture.Clicks); Assert.Empty(fixture.OtherActions);
    }

    [Fact]
    public async Task Unchanged4201IsNotClickedTwice()
    {
        using var fixture = new LoginNetworkFixture { AutoAdvance = false };
        Assert.NotNull((await fixture.Recover()).Error);
        Assert.Single(fixture.Clicks); Assert.Empty(fixture.OtherActions);
    }

    [Fact]
    public async Task ANewCaptureSessionCannotInheritEntryEligibility()
    {
        using var fixture = new LoginNetworkFixture();
        var next = Guid.NewGuid();
        fixture.OnClicked = _ => fixture.OverrideSource = source => source with { SessionId = next };
        Assert.NotNull((await fixture.Recover()).Error);
        Assert.Single(fixture.Clicks); Assert.Empty(fixture.OtherActions);
    }

    [Fact]
    public async Task ParentCancellationDuringFocusPreventsTheFirstMouseSubmission()
    {
        using var user = new CancellationTokenSource();
        using var fixture = new LoginNetworkFixture(); fixture.OnFocus = user.Cancel;
        var result = await fixture.Recover(user.Token);
        Assert.IsAssignableFrom<OperationCanceledException>(result.Error);
        Assert.Empty(fixture.Clicks); Assert.Empty(fixture.NativeStages);
    }

    [Fact]
    public async Task ConsumingTheOriginalDeadlineBeforeTransportPreventsInput()
    {
        using var fixture = new LoginNetworkFixture();
        fixture.BeforeClick = () => fixture.Clock.Advance(TimeSpan.FromSeconds(25));
        Assert.NotNull((await fixture.Recover()).Error);
        Assert.Empty(fixture.Clicks); Assert.Empty(fixture.NativeStages);
    }

    [Fact]
    public async Task ClicksWithoutOrdinaryWorldFeedbackDoNotCompleteRecovery()
    {
        using var fixture = new LoginNetworkFixture { ObserveOrdinaryWorld = false };
        Assert.NotNull((await fixture.Recover()).Error);
        Assert.Equal(2, fixture.Clicks.Count); Assert.Empty(fixture.OtherActions);
    }

    [Fact]
    public async Task OrdinaryWorldTextAfterEntryDoesNotRevokeCompletedRecovery()
    {
        using var fixture = new LoginNetworkFixture { WorldText = "采集" };
        var result = await fixture.Recover();
        Assert.Null(result.Error);
        Assert.True(result.Snapshot?.Matches(UiTarget.Overworld));
        Assert.Equal(2, fixture.Clicks.Count);
        Assert.Empty(fixture.OtherActions);
    }
}

internal enum LoginFixturePage { Error, Entry, World, Unknown }
internal sealed class LoginNetworkFixture : IDisposable
{
    internal readonly FakeTimeProvider Clock = new();
    internal readonly CaptureFrameSource Producer;
    internal readonly List<(Rect Bounds, CaptureFrameStamp Source)> Clicks = new();
    internal readonly List<UiAction> OtherActions = new();
    internal readonly List<string> NativeStages = new();
    internal readonly NativeUiDriver Driver;
    internal LoginFixturePage Page = LoginFixturePage.Error;
    internal string ErrorCode = "4201", Title = "网络错误", Body = "无法连接网络，请检查网络设置。", ExtraText = "", WorldText = "";
    internal int ConfirmCount = 1;
    internal bool Offline, UnknownEntryModal, AutoAdvance = true, ObserveOrdinaryWorld = true;
    internal Func<CaptureFrameStamp, CaptureFrameStamp>? OverrideSource;
    internal Func<ImageRegion, UiSnapshot>? ReadScene;
    internal Action<Mat>? Overlay;
    internal Action? OnFocus, OnOcr, BeforeClick;
    internal Action<string>? OnNativeStage;
    internal Action<int>? OnClicked;
    private readonly Mat _confirmTemplate, _entryTemplate;
    private readonly IOcrService _ocr;

    internal LoginNetworkFixture()
    {
        Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
        Producer = new(Clock);
        _confirmTemplate = Cv2.ImRead(Global.Absolute(@"GameTask/Common/Element/Assets/1920x1080/btn_white_confirm.png"));
        _entryTemplate = Cv2.ImRead(Global.Absolute(@"GameTask/GameLoading/Assets/1920x1080/enter_game.png"));
        Assert.False(_confirmTemplate.Empty()); Assert.False(_entryTemplate.Empty());
        _ocr = new FixtureOcr(this);
        Driver = new(new NativeUiDriverIo
        {
            Capture = Capture,
            Focus = () => OnFocus?.Invoke(),
            NetworkAvailable = () => !Offline,
            ReadScene = image => ReadScene?.Invoke(image) ?? new UiSnapshot(image.FrameStamp.Sequence)
            {
                MainHud = Page == LoginFixturePage.World,
                World = Page == LoginFixturePage.World && ObserveOrdinaryWorld
                    ? new UiWorldObservation(false, false, false) { OrdinaryAvatarHud = true, ControlObserved = true } : null
            },
            Ocr = () => _ocr, Texts = () => new("Ley Line Disorder", "Click anywhere to close"),
            Click = Click, OtherAction = (action, _, admission) => { admission(); OtherActions.Add(action); return true; },
            Delay = (ms, ct) => { ct.ThrowIfCancellationRequested(); Clock.Advance(TimeSpan.FromMilliseconds(ms)); return Task.CompletedTask; },
            Clock = Clock, BeginExclusive = () => new Noop()
        });
    }

    internal async Task<(UiSnapshot? Snapshot, Exception? Error)> Recover(CancellationToken ct = default)
    {
        try { return (await UiRecovery.ToMainAsync(Driver, ct, true, clock: Clock), null); }
        catch (Exception error) { return (null, error); }
    }

    private ImageRegion Capture()
    {
        Clock.Advance(TimeSpan.FromMilliseconds(1));
        var pixels = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(180, 140, 100));
        if (Page == LoginFixturePage.Error)
        {
            Cv2.Rectangle(pixels, new(505, 254, 910, 572), new Scalar(88, 69, 62), -1);
            Cv2.Rectangle(pixels, new(780, 726, 360, 64), new Scalar(225, 229, 236), -1);
            using var icon = new Mat(pixels, new Rect(790, 737, _confirmTemplate.Width, _confirmTemplate.Height));
            _confirmTemplate.CopyTo(icon);
        }
        else if (Page == LoginFixturePage.Entry)
        {
            using var entry = new Mat(pixels, new Rect(900, 930, _entryTemplate.Width, _entryTemplate.Height));
            _entryTemplate.CopyTo(entry);
            if (UnknownEntryModal) Cv2.Rectangle(pixels, new(610, 290, 700, 400), Scalar.White, -1);
        }
        Overlay?.Invoke(pixels);
        var stamp = Producer.Next();
        return new ImageRegion(pixels, 0, 0, drawingBoard: NullMaskWindowDrawingBoard.Instance)
            { FrameStamp = OverrideSource?.Invoke(stamp) ?? stamp };
    }

    private void Click(ImageRegion image, Rect bounds, Action admission)
    {
        BeforeClick?.Invoke();
        var stage = "";
        var dispatcher = new WindowsInputMessageDispatcher(() => OnNativeStage?.Invoke(stage), inputs =>
        {
            NativeStages.Add(stage);
            if (stage == "down")
            {
                Clicks.Add((bounds, image.FrameStamp)); OnClicked?.Invoke(Clicks.Count);
                if (AutoAdvance) Page = Page == LoginFixturePage.Error ? LoginFixturePage.Entry : LoginFixturePage.World;
            }
            return (uint)inputs.Length;
        }, () => 0);
        void Dispatch(string next) { stage = next; dispatcher.DispatchInput(new User32.INPUT[1]); }
        DomainTipClick.Run(admission, () => Dispatch("move"), () => Dispatch("down"), () => Dispatch("up"),
            ms => Clock.Advance(TimeSpan.FromMilliseconds(ms)));
    }

    private sealed class FixtureOcr(LoginNetworkFixture fixture) : IOcrService
    {
        public string Ocr(Mat mat) => "网络错误 无法连接网络，请检查网络设置。错误码:" + fixture.ErrorCode;
        public string OcrWithoutDetector(Mat mat) { fixture.OnOcr?.Invoke(); return "点击进入"; }
        public OcrResult OcrResult(Mat mat)
        {
            fixture.OnOcr?.Invoke();
            if (fixture.Page == LoginFixturePage.Error && mat.Width == 960 && mat.Height == 648)
            {
                var result = new List<OcrResultRegion>
                {
                    new(new(new Point2f(480,84),new Size2f(160,40),0),fixture.Title,1),
                    new(new(new Point2f(480,278),new Size2f(440,32),0),fixture.Body,1),
                    new(new(new Point2f(390,310),new Size2f(220,32),0),"错误码:"+fixture.ErrorCode,1)
                };
                for (var i=0;i<fixture.ConfirmCount;i++) result.Add(new(new(new Point2f(480+i*120,543),new Size2f(80,32),0),"确认",1));
                if (fixture.ExtraText.Length>0) result.Add(new(new(new Point2f(480,400),new Size2f(180,32),0),fixture.ExtraText,1));
                return new(result.ToArray());
            }
            var text = fixture.Page == LoginFixturePage.World ? fixture.WorldText : fixture.Page == LoginFixturePage.Entry ? fixture.ExtraText : "";
            return text.Length == 0 ? new([]) : new([new(new(new Point2f(100,100),new Size2f(90,28),0),text,1)]);
        }
    }
    private sealed class Noop : IDisposable { public void Dispose() { } }
    public void Dispose() { Driver.Dispose(); _confirmTemplate.Dispose(); _entryTemplate.Dispose(); }
}
