using BetterGenshinImpact.Core.Mask;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.Common.Job;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Helpers;
using Fischless.GameCapture;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OpenCvSharp;
using Vanara.PInvoke;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

// Spec candidate, SetupPending (no production placeholder is included):
// CommissionPageEntryTask(NativeUiDriver driver, TimeProvider clock, ILogger logger).
// Task EnterAsync(CancellationToken ct), plus typed UiAction.OpenAdventurerHandbook.
// The driver still needs owned commission-tab/reward ROI observations and native action admission.
// A missing type/member or an early stub exception is setup failure, never this behavior's RED.
public sealed class CommissionPageEntryTests
{
    [Fact]
    public async Task PartyRecoveryAndMissingCommissionFeedbackShareTheOriginalTenSecondBudget()
    {
        ApplicationHostBootstrapGuard.ProhibitForCurrentProcess();
        using var taskOwner = TaskExecutionScope.BeginOwned();
        using var device = new EntryDevice();
        // An independent caller guard bounds an incorrectly renewed entry budget; it is not its 10s budget.
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(12), device.Clock);
        var subject = new CommissionPageEntryTask(device.Driver, device.Clock, NullLogger.Instance);
        var returnedNormally = false;
        Assert.Null(UiOperation.Current); // The domain entry owns the single original 10s UI scope.

        var error = await Record.ExceptionAsync(async () =>
        {
            await subject.EnterAsync(cancellation.Token);
            returnedNormally = true;
        });

        // These device-level preconditions must pass before a timeout can establish the target behavior.
        // An empty implementation which throws immediately cannot satisfy this test.
        Assert.True(device.PartyFrames > 0, $"No initial Party observation: {error}");
        Assert.Equal(1, device.CloseClicks);
        Assert.True(device.MainFrames.Count >= 2, $"Two fresh Main frames were not reached: {error}");
        var firstMain = device.MainFrames[0];
        var secondMain = device.MainFrames[1];
        Assert.True(secondMain.Source.IsAfter(firstMain.Source));
        Assert.True(secondMain.At - firstMain.At >= TimeSpan.FromMilliseconds(250));
        Assert.InRange((secondMain.At - device.Started).TotalSeconds, 7, 7.5);
        var handbook = Assert.Single(device.HandbookInputs);
        Assert.Equal(0, device.HandbookInputsBeforeMain);
        Assert.True(handbook.Source.IsAfter(secondMain.Source));
        Assert.True(handbook.StartedAt >= secondMain.At);
        Assert.Equal(2, handbook.NativeRequested);
        Assert.Equal(2, handbook.NativeSubmitted);
        Assert.True(handbook.CompletedAt - handbook.StartedAt >= TimeSpan.FromMilliseconds(60));
        Assert.NotEmpty(device.AfterHandbookFrames);
        Assert.Contains(device.AfterHandbookFrames, source =>
            source.IsAfter(handbook.Source) && source.CapturedTimestamp > handbook.CompletedTimestamp +
                source.TimestampFrequency * .06);
        Assert.Contains(device.Ocr.Requests, size => size.Width == 89 && size.Height == 47);
        Assert.All(device.Captures, frame => Assert.True(frame.SrcMat.IsDisposed));
        TaskExecutionScope.ThrowIfFailed();

        Assert.IsType<TimeoutException>(error);
        Assert.False(returnedNormally);
        Assert.Equal(0, device.CommissionTabClicks);
        Assert.InRange((device.Clock.GetUtcNow() - device.Started).TotalSeconds, 10, 10.3);
        Assert.False(cancellation.IsCancellationRequested); // The entry's original deadline wins over the caller guard.
        Assert.Null(UiOperation.Current);
    }

    [Fact]
    public async Task PartyRecoveryEntersCommissionOnlyAfterOneTabClickAndTwoFreshRewardFrames()
    {
        ApplicationHostBootstrapGuard.ProhibitForCurrentProcess();
        using var taskOwner = TaskExecutionScope.BeginOwned();
        using var device = new EntryDevice(success: true);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(12), device.Clock);
        var subject = new CommissionPageEntryTask(device.Driver, device.Clock, NullLogger.Instance);

        var error = await Record.ExceptionAsync(() => subject.EnterAsync(cancellation.Token));

        Assert.Null(error);
        Assert.True(device.PartyFrames > 0);
        Assert.Equal(1, device.CloseClicks);
        Assert.True(device.MainFrames.Count >= 2);
        Assert.True(device.MainFrames[1].Source.IsAfter(device.MainFrames[0].Source));
        Assert.True(device.MainFrames[1].At - device.MainFrames[0].At >= TimeSpan.FromMilliseconds(250));
        Assert.InRange((device.MainFrames[1].At - device.Started).TotalSeconds, 7, 7.5);
        var handbook = Assert.Single(device.HandbookInputs);
        Assert.Equal(0, device.HandbookInputsBeforeMain);
        Assert.Equal(2, handbook.NativeRequested);
        Assert.Equal(2, handbook.NativeSubmitted);
        Assert.True(handbook.Source.IsAfter(device.MainFrames[1].Source));
        var tab = Assert.Single(device.CommissionTabInputs);
        Assert.Equal(1, device.CommissionTabClicks);
        Assert.True(tab.Source.IsAfter(handbook.Source));
        Assert.InRange(tab.Bounds.X, 260, 348);
        Assert.InRange(tab.Bounds.Y, 317, 363);
        Assert.True(tab.Bounds.Right <= 349 && tab.Bounds.Bottom <= 364);
        var rewards = device.RewardFrames.GroupBy(frame => frame.Source).Select(group => group.First()).ToArray();
        Assert.True(rewards.Length >= 2);
        Assert.True(rewards[1].Source.IsAfter(rewards[0].Source));
        Assert.True(rewards[1].At - rewards[0].At >= TimeSpan.FromMilliseconds(250));
        Assert.All(rewards, reward =>
        {
            Assert.True(reward.Source.IsAfter(tab.Source));
            Assert.True(reward.Source.CapturedTimestamp > tab.CompletedTimestamp + reward.Source.TimestampFrequency * .06);
        });
        Assert.Contains(device.Ocr.Requests, size => size.Width == 89 && size.Height == 47);
        Assert.Contains(device.Ocr.Requests, size => size.Width == 142 && size.Height == 36);
        Assert.InRange((device.Clock.GetUtcNow() - device.Started).TotalSeconds, 7, 10);
        Assert.False(cancellation.IsCancellationRequested);
        Assert.All(device.Captures, frame => Assert.True(frame.SrcMat.IsDisposed));
        TaskExecutionScope.ThrowIfFailed();
        Assert.Null(UiOperation.Current);
    }

    [Fact]
    public async Task CallerCancellationDuringMainRecoveryRemainsCancellationAndSendsNoHandbookOrTabInput()
    {
        ApplicationHostBootstrapGuard.ProhibitForCurrentProcess();
        using var taskOwner = TaskExecutionScope.BeginOwned();
        using var cancellation = new CancellationTokenSource();
        using var device = new EntryDevice(cancelDuringMain: cancellation.Cancel);
        var subject = new CommissionPageEntryTask(device.Driver, device.Clock, NullLogger.Instance);

        var error = await Record.ExceptionAsync(() => subject.EnterAsync(cancellation.Token));

        Assert.True(device.PartyFrames > 0);
        Assert.Equal(1, device.CloseClicks);
        Assert.True(device.MainCancellationDelivered);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.True(device.MainFrames.Count < 2);
        Assert.Empty(device.HandbookInputs);
        Assert.Equal(0, device.HandbookInputsBeforeMain);
        Assert.Equal(0, device.CommissionTabClicks);
        Assert.Empty(device.CommissionTabInputs);
        Assert.Empty(device.RewardFrames);
        Assert.True(device.Clock.GetUtcNow() - device.Started < TimeSpan.FromSeconds(10));
        Assert.All(device.Captures, frame => Assert.True(frame.SrcMat.IsDisposed));
        TaskExecutionScope.ThrowIfFailed();
        Assert.Null(UiOperation.Current);
    }

    [Fact]
    public async Task VisibleCommissionTabOnAnotherHandbookPageDoesNotRequireSelectedCommissionContent()
    {
        ApplicationHostBootstrapGuard.ProhibitForCurrentProcess();
        using var taskOwner = TaskExecutionScope.BeginOwned();
        using var device = new EntryDevice(success: true, recognizeHandbookFromOcr: true);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(12), device.Clock);
        var subject = new CommissionPageEntryTask(device.Driver, device.Clock, NullLogger.Instance);

        var error = await Record.ExceptionAsync(() => subject.EnterAsync(cancellation.Token));

        // Establish the real device prefix and the production reader's non-commission result first.
        Assert.True(device.PartyFrames > 0, $"No initial Party observation: {error}");
        Assert.Equal(1, device.CloseClicks);
        Assert.True(device.MainFrames.Count >= 2, $"Two fresh Main frames were not reached: {error}");
        Assert.True(device.MainFrames[1].Source.IsAfter(device.MainFrames[0].Source));
        Assert.True(device.MainFrames[1].At - device.MainFrames[0].At >= TimeSpan.FromMilliseconds(250));
        Assert.InRange((device.MainFrames[1].At - device.Started).TotalSeconds, 7, 7.5);
        var handbook = Assert.Single(device.HandbookInputs);
        Assert.Equal(0, device.HandbookInputsBeforeMain);
        Assert.Equal(2, handbook.NativeRequested);
        Assert.Equal(2, handbook.NativeSubmitted);
        Assert.True(handbook.Source.IsAfter(device.MainFrames[1].Source));
        Assert.True(handbook.CompletedAt - handbook.StartedAt >= TimeSpan.FromMilliseconds(60));
        Assert.NotEmpty(device.SidebarFrames);
        Assert.All(device.SidebarFrames, frame => Assert.True(frame.Source.IsAfter(handbook.Source)));
        Assert.Contains(device.HandbookScenes, frame => !frame.CommissionSelected && frame.TabClicks == 0);
        Assert.Contains(device.Ocr.Requests, size => size.Width == 190 && size.Height == 630);
        Assert.Contains(device.Ocr.Requests, size => size.Width == 1220 && size.Height == 730);
        Assert.All(device.Captures, frame => Assert.True(frame.SrcMat.IsDisposed));
        TaskExecutionScope.ThrowIfFailed();
        Assert.False(cancellation.IsCancellationRequested);

        Assert.Null(error);
        var tab = Assert.Single(device.CommissionTabInputs);
        Assert.Equal(1, device.CommissionTabClicks);
        Assert.True(tab.Source.IsAfter(handbook.Source));
        Assert.Contains(device.Ocr.Requests, size => size.Width == 89 && size.Height == 47);
        Assert.Contains(device.Ocr.Requests, size => size.Width == 142 && size.Height == 36);
        var rewards = device.RewardFrames.GroupBy(frame => frame.Source).Select(group => group.First()).ToArray();
        Assert.True(rewards.Length >= 2);
        Assert.True(rewards[1].Source.IsAfter(rewards[0].Source));
        Assert.True(rewards[1].At - rewards[0].At >= TimeSpan.FromMilliseconds(250));
        Assert.All(rewards, reward =>
        {
            Assert.True(reward.Source.IsAfter(tab.Source));
            Assert.True(reward.Source.CapturedTimestamp > tab.CompletedTimestamp + reward.Source.TimestampFrequency * .06);
        });
        Assert.InRange((device.Clock.GetUtcNow() - device.Started).TotalSeconds, 7, 10);
        Assert.Null(UiOperation.Current);
    }

    private sealed record MainObservation(CaptureFrameStamp Source, DateTimeOffset At);
    private sealed record HandbookScene(CaptureFrameStamp Source, bool CommissionSelected, int TabClicks);
    private sealed record TabSubmission(CaptureFrameStamp Source, long CompletedTimestamp, Rect Bounds);
    private sealed record HandbookSubmission(CaptureFrameStamp Source, DateTimeOffset StartedAt,
        DateTimeOffset CompletedAt, long CompletedTimestamp, int NativeRequested, int NativeSubmitted);

    private sealed class EntryDevice : IDisposable
    {
        internal readonly FakeTimeProvider Clock = new();
        internal readonly CaptureFrameSource Producer;
        internal readonly NativeUiDriver Driver;
        internal readonly EmptyOcr Ocr;
        internal readonly DateTimeOffset Started;
        internal readonly List<ImageRegion> Captures = new();
        internal readonly List<MainObservation> MainFrames = new();
        internal readonly List<HandbookSubmission> HandbookInputs = new();
        internal readonly List<CaptureFrameStamp> AfterHandbookFrames = new();
        internal readonly List<TabSubmission> CommissionTabInputs = new();
        internal readonly List<MainObservation> RewardFrames = new();
        internal readonly List<MainObservation> SidebarFrames = new();
        internal readonly List<HandbookScene> HandbookScenes = new();
        internal int PartyFrames, CloseClicks, CommissionTabClicks, HandbookInputsBeforeMain;
        internal bool MainCancellationDelivered;
        private readonly Mat _closeButton;
        private readonly bool _success;
        private readonly bool _recognizeHandbookFromOcr;
        private readonly Action? _cancelDuringMain;
        private bool _partyClosed, _handbookOpened;

        internal EntryDevice(bool success = false, Action? cancelDuringMain = null, bool recognizeHandbookFromOcr = false)
        {
            Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
            Started = Clock.GetUtcNow();
            Producer = new(Clock);
            _success = success;
            _recognizeHandbookFromOcr = recognizeHandbookFromOcr;
            _cancelDuringMain = cancelDuringMain;
            Ocr = new(ReadDeviceText);
            _closeButton = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory,
                "GameTask", "QuickTeleport", "Assets", "1920x1080", "MapCloseButton.png"));
            Assert.False(_closeButton.Empty()); // The existing unit-test asset, not a new root Assets directory.
            Driver = new(new NativeUiDriverIo
            {
                Capture = Capture,
                Focus = () => { },
                ReadScene = ReadScene,
                Ocr = () => Ocr,
                Texts = () => new("", ""),
                Click = Click,
                OtherAction = OtherAction,
                Delay = (milliseconds, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    Clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
                    return Task.CompletedTask;
                },
                Clock = Clock,
                BeginExclusive = () => new Lease()
            });
        }

        private ImageRegion Capture()
        {
            if (_partyClosed && !_handbookOpened && MainFrames.Count == 0 &&
                !MainCancellationDelivered && _cancelDuringMain != null)
            {
                MainCancellationDelivered = true;
                _cancelDuringMain();
            }
            // The first Main image becomes available at 6.75s on the original attempt clock.
            // The real two-frame transition then consumes its next 250ms poll, reaching about 7s.
            if (_partyClosed && !_handbookOpened && MainFrames.Count == 0 &&
                Clock.GetUtcNow() < Started + TimeSpan.FromMilliseconds(6750))
                Clock.Advance(Started + TimeSpan.FromMilliseconds(6750) - Clock.GetUtcNow());
            Clock.Advance(TimeSpan.FromMilliseconds(1));
            var pixels = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black);
            if (!_partyClosed)
            {
                using var roi = new Mat(pixels, new Rect(1820, 24, _closeButton.Width, _closeButton.Height));
                _closeButton.CopyTo(roi);
            }
            var frame = new ImageRegion(pixels, 0, 0, drawingBoard: NullMaskWindowDrawingBoard.Instance)
                { FrameStamp = Producer.Next() };
            Captures.Add(frame);
            if (_handbookOpened) AfterHandbookFrames.Add(frame.FrameStamp);
            return frame;
        }

        private UiSnapshot ReadScene(ImageRegion frame)
        {
            if (!_partyClosed)
            {
                PartyFrames++;
                return new(frame.FrameStamp.Sequence) { Party = true, Closable = true };
            }
            if (!_handbookOpened)
            {
                MainFrames.Add(new(frame.FrameStamp, Clock.GetUtcNow()));
                return new(frame.FrameStamp.Sequence) { MainHud = true };
            }
            if (_recognizeHandbookFromOcr)
            {
                // Use the real production combination reader: sidebar alone cannot make Handbook true.
                var selectedCommission = HandbookUiRecognition.Read(frame, Ocr);
                HandbookScenes.Add(new(frame.FrameStamp, selectedCommission, CommissionTabClicks));
                return new(frame.FrameStamp.Sequence) { Handbook = selectedCommission };
            }
            // A handbook background is present, but neither commission label nor reward label exists.
            // All following frames are fresh; an input receipt cannot replace either missing ROI.
            return new(frame.FrameStamp.Sequence) { Handbook = true };
        }

        private void Click(ImageRegion image, Rect bounds, Action admission)
        {
            DomainTipClick.Run(admission, () => SubmitNative(1),
                () => SubmitNative(1), () => SubmitNative(1), Wait);
            if (bounds.X >= 1813 && bounds.X < 1872 && bounds.Y < 100)
            {
                CloseClicks++;
                _partyClosed = true;
            }
            else
            {
                CommissionTabClicks++; // This entry has no other permitted click after Party closes.
                CommissionTabInputs.Add(new(image.FrameStamp, Clock.GetTimestamp(), bounds));
            }
        }

        private bool OtherAction(UiAction action, ImageRegion image, Action admission)
        {
            if (action == UiAction.Escape)
            {
                UiEscapeInput.Run(admission, () => SubmitNative(1), () => SubmitNative(1), Wait);
                return true; // Party deliberately remains open; the real matched-X fallback must close it.
            }
            if (action != UiAction.OpenAdventurerHandbook)
                throw new InvalidOperationException($"Unexpected entry device action: {action}");
            if (MainFrames.Count < 2 || Clock.GetUtcNow() - Started < TimeSpan.FromSeconds(7))
                HandbookInputsBeforeMain++;
            var started = Clock.GetUtcNow();
            var requested = 0;
            var submitted = 0;
            void Send()
            {
                var sent = SubmitNative(1);
                requested += sent.Requested;
                submitted += sent.Submitted;
            }
            UiEscapeInput.Run(admission, Send, Send, milliseconds => Wait(Math.Max(60, milliseconds)), "F1冒险之证");
            HandbookInputs.Add(new(image.FrameStamp, started, Clock.GetUtcNow(), Clock.GetTimestamp(), requested, submitted));
            _handbookOpened = true;
            return true; // The real driver receives complete native submission, never an entry-success result.
        }

        private string ReadDeviceText(Mat image)
        {
            if (!_success || !_handbookOpened) return "";
            if (_recognizeHandbookFromOcr && image.Width == 190 && image.Height == 630)
            {
                SidebarFrames.Add(new(Captures[^1].FrameStamp, Clock.GetUtcNow()));
                return "委托\n秘境"; // Two separate OCR regions, not a mocked Handbook result.
            }
            if (_recognizeHandbookFromOcr && image.Width == 1220 && image.Height == 730)
                return CommissionTabClicks > 0 ? "每日委托奖励" : "";
            if (image.Width == 89 && image.Height == 47) return "委托";
            if (image.Width == 142 && image.Height == 36 && CommissionTabClicks > 0)
            {
                RewardFrames.Add(new(Captures[^1].FrameStamp, Clock.GetUtcNow()));
                return "每日委托奖励";
            }
            return "";
        }

        private static (int Requested, int Submitted) SubmitNative(int count)
        {
            var requested = 0;
            var submitted = 0;
            var dispatcher = new Fischless.WindowsInput.WindowsInputMessageDispatcher(null, inputs =>
            {
                requested += inputs.Length;
                submitted += inputs.Length;
                return (uint)inputs.Length;
            }, () => 0);
            dispatcher.DispatchInput(new User32.INPUT[count]);
            return (requested, submitted);
        }

        private void Wait(int milliseconds) => Clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
        public void Dispose() { Driver.Dispose(); _closeButton.Dispose(); }
        private sealed class Lease : IDisposable { public void Dispose() { } }
    }

    private sealed class EmptyOcr(Func<Mat, string>? read = null) : IOcrService
    {
        internal readonly List<Size> Requests = new();
        private string Read(Mat image)
        {
            Requests.Add(image.Size());
            return read?.Invoke(image) ?? "";
        }
        public string Ocr(Mat image) => Read(image);
        public string OcrWithoutDetector(Mat image) => Read(image);
        public OcrResult OcrResult(Mat image)
        {
            var text = Read(image);
            if (text.Length == 0) return new([]);
            var labels = text.Split('\n');
            if (labels.Length > 1)
            {
                var height = image.Height / (float)labels.Length;
                return new(labels.Select((label, index) => new OcrResultRegion(
                    new RotatedRect(new Point2f(image.Width / 2f, (index + .5f) * height),
                        new Size2f(image.Width - 4, height - 4), 0), label, 1f)).ToArray());
            }
            var bounds = new RotatedRect(new Point2f(image.Width / 2f, image.Height / 2f),
                new Size2f(image.Width - 4, image.Height - 4), 0);
            return new([new OcrResultRegion(bounds, text, 1f)]);
        }
    }
}
