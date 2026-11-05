using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using BetterGenshinImpact.GameTask.GameLoading;
using BetterGenshinImpact.Helpers;
using Fischless.GameCapture;
using OpenCvSharp;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.GameLoadingTests;

public class GameLoadingAgreementTests
{
    [ModuleInitializer]
    internal static void ForbidApp() => ApplicationHostBootstrapGuard.ProhibitForCurrentProcess();

    private static readonly Size Screen = new(1920, 1080);
    private static GameLoadingAgreementText[] Popup(string title = "用户协议和隐私政策更新提示") =>
    [
        new(title, new Rect(803, 395, 315, 27)),
        new("您应阅读并同意用户协议、隐私政策。该条款将对您产生法律约束力。", new Rect(684, 450, 545, 145)),
        new("拒绝", new Rect(787, 648, 51, 27)),
        new("同意", new Rect(1082, 648, 51, 27))
    ];

    [RecordedAgreementTheory]
    [InlineData("0001", "B7DF5F8FB2546D2C5433558BB15EF8C9909FEA45FA257AB8008BCFFB0358B090")]
    [InlineData("0003", "BF4882D689FBDCF2CB55F5C8B9AD90FA9F5A4B73B4200F32EB5D55BC8AAE7171")]
    public void RecordedOfficialPopupNeedsTwoFreshFramesAndOneAttempt(string id, string hash)
    {
        Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
        Assert.Null(System.Windows.Application.Current);
        var root = Environment.GetEnvironmentVariable("BGI_AGREEMENT_EVIDENCE_ROOT")!;
        var path = Path.Combine(root, $"evidence-{id}.png");
        using (var input = File.OpenRead(path)) Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(input)));
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(path, ".json")));
        Assert.Equal("d863c149-210e-4486-a689-efe34fce8a6a", metadata.RootElement.GetProperty("RunId").GetString());
        Assert.Equal(hash, metadata.RootElement.GetProperty("ImageContentSha256").GetString());
        using var image = Cv2.ImRead(path);
        Assert.Equal(Screen, image.Size());
        // OCR是边界替身，文本和框对应真实原图；以下source明确为模拟，不把历史帧伪作实时fresh。
        var clock = new SimulatedClock();
        var gate = new GameLoadingAgreement();
        var clicks = new List<Rect>();
        var waits = new List<int>();
        Assert.True(gate.Handle(Popup(), clock.Frame(1), Screen, clock, clicks.Add, waits.Add));
        Assert.Empty(clicks);
        clock.Advance(2000);
        Assert.True(gate.Handle(Popup(), clock.Frame(2), Screen, clock, clicks.Add, ms => { waits.Add(ms); clock.Advance(ms); }));
        Assert.Single(clicks);
        Assert.Equal(new Rect(1082, 648, 51, 27), clicks[0]);
        Assert.Contains(waits, ms => ms >= 60);
        clock.Advance(2000);
        Assert.True(gate.Handle(Popup(), clock.Frame(3), Screen, clock, clicks.Add, waits.Add));
        Assert.Single(clicks);
        clock.Advance(2000);
        Assert.False(gate.Handle([new("点击进入", new Rect(900, 850, 120, 25))], clock.Frame(4), Screen, clock, clicks.Add, waits.Add));
        Assert.Single(clicks);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("stale")]
    [InlineData("foreign")]
    [InlineData("frequency")]
    [InlineData("different-button")]
    public void InvalidSecondObservationCannotClick(string variation)
    {
        var gate = new GameLoadingAgreement(); var clock = new SimulatedClock(); var clicks = 0;
        var first = clock.Frame(1);
        gate.Handle(Popup(), first, Screen, clock, _ => clicks++, _ => { });
        clock.Advance(2000);
        var second = clock.Frame(2);
        var popup = Popup();
        if (variation == "duplicate") second = first;
        if (variation == "stale") second = second with { CapturedTimestamp = 0 };
        if (variation == "foreign") second = second with { SessionId = Guid.NewGuid() };
        if (variation == "frequency") second = second with { TimestampFrequency = 1 };
        if (variation == "different-button") popup[3] = popup[3] with { Bounds = new Rect(1250, 648, 51, 27) };
        gate.Handle(popup, second, Screen, clock, _ => clicks++, _ => { });
        Assert.Equal(0, clicks);
    }

    [Theory]
    [InlineData("登录")]
    [InlineData("验证码")]
    [InlineData("扫码登录")]
    [InlineData("未知更新提示")]
    public void OtherPromptsCannotAuthorizeConsent(string text)
    {
        var gate = new GameLoadingAgreement(); var clock = new SimulatedClock(); var clicks = 0;
        var regions = Popup(text);
        gate.Handle(regions, clock.Frame(1), Screen, clock, _ => clicks++, _ => { });
        clock.Advance(2000);
        gate.Handle(regions, clock.Frame(2), Screen, clock, _ => clicks++, _ => { });
        Assert.Equal(0, clicks);
    }

    [Fact]
    public void UnclearDispatchIsConsumedAndUnknownUiCannotResume()
    {
        var gate = new GameLoadingAgreement(); var clock = new SimulatedClock(); var clicks = 0;
        gate.Handle(Popup(), clock.Frame(1), Screen, clock, _ => clicks++, _ => { });
        clock.Advance(2000);
        Assert.Throws<InvalidOperationException>(() => gate.Handle(Popup(), clock.Frame(2), Screen, clock,
            _ => { clicks++; throw new InvalidOperationException("simulated unknown dispatch"); }, clock.Advance));
        clock.Advance(2000);
        Assert.True(gate.Handle(Popup(), clock.Frame(3), Screen, clock, _ => clicks++, _ => { }));
        clock.Advance(2000);
        Assert.True(gate.Handle([], clock.Frame(4), Screen, clock, _ => clicks++, _ => { }));
        Assert.Equal(1, clicks);
    }

    [Fact]
    public void ConfirmedAgePromptAfterAgreementReturnsToExistingAgeHandler()
    {
        var gate = new GameLoadingAgreement(); var clock = new SimulatedClock(); var clicks = 0;
        gate.HandleAttempt(Popup(), clock.Frame(1), Screen, clock, _ => { clicks++; return true; }, clock.Advance);
        clock.Advance(2000);
        gate.HandleAttempt(Popup(), clock.Frame(2), Screen, clock, _ => { clicks++; return true; }, clock.Advance);
        clock.Advance(2000);
        var age = new[] { new GameLoadingAgreementText("适龄提示 监护人确认", new Rect(800, 400, 250, 30)) };
        Assert.False(gate.HandleAttempt(age, clock.Frame(3), Screen, clock, _ => { clicks++; return true; }, clock.Advance,
            ageConfirmationVisible: true));
        Assert.Equal(1, clicks);
        Assert.True(gate.HandleAttempt([new("验证码", new Rect(800, 400, 100, 30))], clock.Frame(4), Screen, clock,
            _ => { clicks++; return true; }, clock.Advance, ageConfirmationVisible: true));
    }

    [Fact]
    public void ExplicitNoInputAfterPreparationExpiryRequiresTwoNewObservations()
    {
        var gate = new GameLoadingAgreement(); var clock = new SimulatedClock(); var clicks = 0;
        gate.HandleAttempt(Popup(), clock.Frame(1), Screen, clock, _ => { clicks++; return true; }, clock.Advance);
        clock.Advance(2000);
        var expiresDuringPreparation = clock.Frame(2) with { CapturedTimestamp = clock.GetTimestamp() - 19_500_000 };
        gate.HandleAttempt(Popup(), expiresDuringPreparation, Screen, clock, _ =>
        {
            clock.Advance(100);
            if (!expiresDuringPreparation.IsFresh(clock, GameLoadingAgreement.MaximumFrameAge)) return false;
            clicks++; return true;
        }, clock.Advance);
        Assert.Equal(0, clicks);
        clock.Advance(2000);
        gate.HandleAttempt(Popup(), clock.Frame(3), Screen, clock, _ => { clicks++; return true; }, clock.Advance);
        Assert.Equal(0, clicks);
        clock.Advance(2000);
        gate.HandleAttempt(Popup(), clock.Frame(4), Screen, clock, _ => { clicks++; return true; }, clock.Advance);
        Assert.Equal(1, clicks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConsumedResizeSameWindowNeedsTwoFreshMatchingSuccessors(bool age)
    {
        var gate = new GameLoadingAgreement(); var clock = new SimulatedClock(); var clicks = 0;
        var runtime = new object(); var window = new IntPtr(1234);
        gate.HandleAttempt(Popup(), clock.Frame(1), Screen, clock, _ => { clicks++; return true; }, clock.Advance,
            targetRuntime: runtime, targetWindow: window);
        clock.Advance(2000);
        gate.HandleAttempt(Popup(), clock.Frame(2), Screen, clock, _ => { clicks++; return true; }, clock.Advance,
            targetRuntime: runtime, targetWindow: window);
        Assert.Equal(1, clicks);
        var generation = Guid.NewGuid();
        var successor = new[] { new GameLoadingAgreementText(age ? "适龄提示 监护人确认" : "点击进入", new Rect(800, 400, 250, 30)) };
        clock.Advance(200);
        var first = clock.Frame(1) with { SessionId = generation };
        Assert.True(gate.HandleAttempt(successor, first, Screen, clock, _ => { clicks++; return true; }, clock.Advance,
            ageConfirmationVisible: age, targetRuntime: runtime, targetWindow: window));
        clock.Advance(200);
        var second = clock.Frame(2) with { SessionId = generation };
        Assert.False(gate.HandleAttempt(successor, second, Screen, clock, _ => { clicks++; return true; }, clock.Advance,
            ageConfirmationVisible: age, targetRuntime: runtime, targetWindow: window));
        Assert.True(gate.HandleAttempt(Popup(), clock.Frame(3) with { SessionId = generation }, Screen, clock,
            _ => { clicks++; return true; }, clock.Advance, targetRuntime: runtime, targetWindow: window));
        Assert.Equal(1, clicks);
    }

    [Theory]
    [InlineData("single")]
    [InlineData("foreign-runtime")]
    [InlineData("foreign-window")]
    [InlineData("old-time")]
    [InlineData("frequency")]
    [InlineData("duplicate")]
    [InlineData("mixed-kind")]
    [InlineData("unknown")]
    [InlineData("captcha")]
    public void ConsumedResizeCannotReleaseWithoutWindowAndFreshnessProof(string invalid)
    {
        var gate = new GameLoadingAgreement(); var clock = new SimulatedClock(); var clicks = 0;
        var runtime = new object(); var window = new IntPtr(1234);
        gate.HandleAttempt(Popup(), clock.Frame(1), Screen, clock, _ => { clicks++; return true; }, clock.Advance,
            targetRuntime: runtime, targetWindow: window);
        clock.Advance(2000);
        gate.HandleAttempt(Popup(), clock.Frame(2), Screen, clock, _ => { clicks++; return true; }, clock.Advance,
            targetRuntime: runtime, targetWindow: window);
        var inputEndAndSettle = clock.GetTimestamp();
        var generation = Guid.NewGuid();
        var currentRuntime = invalid == "foreign-runtime" ? new object() : runtime;
        var currentWindow = invalid == "foreign-window" ? new IntPtr(4321) : window;
        var text = invalid == "unknown" ? "未知提示" : invalid == "captcha" ? "验证码" : "点击进入";
        var successor = new[] { new GameLoadingAgreementText(text, new Rect(800, 400, 250, 30)) };
        clock.Advance(200);
        var first = clock.Frame(1) with { SessionId = generation };
        if (invalid == "old-time") first = first with { CapturedTimestamp = inputEndAndSettle - 1_000_000 };
        if (invalid == "frequency") first = first with { TimestampFrequency = 1 };
        Assert.True(gate.HandleAttempt(successor, first, Screen, clock, _ => { clicks++; return true; }, clock.Advance,
            targetRuntime: currentRuntime, targetWindow: currentWindow));
        if (invalid != "single")
        {
            clock.Advance(200);
            var second = clock.Frame(2) with { SessionId = generation };
            if (invalid == "old-time") second = second with { CapturedTimestamp = inputEndAndSettle - 1_000_000 };
            if (invalid == "frequency") second = second with { TimestampFrequency = 1 };
            if (invalid == "duplicate") second = first;
            if (invalid == "mixed-kind") successor = [new("适龄提示 监护人确认", new Rect(800, 400, 250, 30))];
            Assert.True(gate.HandleAttempt(successor, second, Screen, clock, _ => { clicks++; return true; }, clock.Advance,
                ageConfirmationVisible: invalid == "mixed-kind", targetRuntime: currentRuntime, targetWindow: currentWindow));
        }
        Assert.Equal(1, clicks);
    }

    private sealed class RecordedAgreementTheoryAttribute : TheoryAttribute
    {
        public RecordedAgreementTheoryAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BGI_AGREEMENT_EVIDENCE_ROOT")))
                Skip = "Set BGI_AGREEMENT_EVIDENCE_ROOT to replay the two private original frames.";
        }
    }

    private sealed class SimulatedClock : TimeProvider
    {
        private long _ticks = 100_000_000;
        private readonly Guid _session = Guid.NewGuid();
        public override long TimestampFrequency => 10_000_000;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_ticks);
        internal void Advance(int milliseconds) => _ticks += milliseconds * 10_000L;
        internal CaptureFrameStamp Frame(long sequence) => new(_session, sequence, _ticks, TimestampFrequency, GetUtcNow());
    }
}
