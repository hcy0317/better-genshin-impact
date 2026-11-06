using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Input;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.Common.Ui;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask.Common.Job;

internal sealed partial class CurrentPlayerUidTask
{
    internal static CurrentPlayerUidTask CreateNative(ILogger logger)
    {
        var prepared = NativeUiDriverIo.CreateNative(inspectWorld: true);
        return CreatePrepared(prepared, prepared.Ocr, logger);
    }

    internal static CurrentPlayerUidTask CreatePrepared(NativeUiDriverIo prepared, Func<IOcrService> resolveOcr, ILogger logger,
        Func<NativeUiDriverIo, IOcrService, ICurrentPlayerUidIo>? createSession = null)
    {
        var ocr = resolveOcr();
        var texts = prepared.Texts();
        var captured = new NativeUiDriverIo
        {
            Capture = prepared.Capture,
            Focus = prepared.Focus,
            ReadScene = image => NativeUiDriver.ReadNativeScene(image, inspectWorld: true, ocr: ocr),
            ReadOrdinaryWorld = image => NativeUiDriver.ReadNativeScene(image, inspectWorld: true, ocr: ocr),
            Ocr = () => ocr,
            Texts = () => texts,
            Click = prepared.Click,
            OtherAction = prepared.OtherAction,
            Delay = prepared.Delay,
            Clock = prepared.Clock,
            BeginExclusive = prepared.BeginExclusive,
            NetworkAvailable = prepared.NetworkAvailable
        };
        return new CurrentPlayerUidTask(() => createSession != null
            ? createSession(captured, ocr) : new NativeIo(captured, ocr), logger, captured.Clock);
    }

    private sealed class NativeIo : ICurrentPlayerUidIo, IDisposable
    {
        private readonly NativeUiDriver _driver;
        private readonly IOcrService _ocr;
        internal NativeIo(NativeUiDriverIo prepared, IOcrService ocr)
        { _ocr = ocr; _driver = new NativeUiDriver(prepared); }
        public UiSnapshot Capture() => _driver.Capture();
        public Task DelayAsync(int milliseconds, CancellationToken ct) => _driver.DelayAsync(milliseconds, ct);
        public Task<bool> ActAsync(UiAction action, UiSnapshot observed, CancellationToken ct) => _driver.ActAsync(action, observed, ct);
        public void MarkInputCompleted(UiSnapshot before) => _driver.MarkInputCompleted(before);
        public void MarkTransitionCompleted(UiTarget target, UiSnapshot observed) => _driver.MarkTransitionCompleted(target, observed);

        public CurrentPlayerUidFrame ReadUidFrame()
        {
            var operation = UiOperation.Current!;
            operation.Check();
            var remaining = operation.Remaining - ReturnReserve;
            if (remaining <= TimeSpan.Zero) return new(new UiSnapshot(0), 0, 0, Array.Empty<CurrentPlayerUidText>());
            using var deadline = new CancellationTokenSource(remaining);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(operation.Token, deadline.Token);
            using var recognition = new RecognitionExecutionScope(linked.Token);
            try { return ReadOwned(); }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !operation.Token.IsCancellationRequested)
            { return new(new UiSnapshot(0), 0, 0, Array.Empty<CurrentPlayerUidText>()); }
        }

        private CurrentPlayerUidFrame ReadOwned() => _driver.CaptureOwned((image, scene) =>
        {
            var operation = UiOperation.Current!;
            operation.Check();
            var texts = Array.Empty<CurrentPlayerUidText>();
            if (scene.HasUsableEvidence && (scene.Matches(UiTarget.Menu) || scene.Matches(UiTarget.Overworld)) && operation.Remaining > ReturnReserve)
            {
                var scale = image.Height / 1080d;
                var menu = scene.Matches(UiTarget.Menu);
                using var deadline = new CancellationTokenSource(operation.Remaining - ReturnReserve);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(operation.Token, deadline.Token);
                using var recognition = new RecognitionExecutionScope(linked.Token);
                try
                {
                    var results = image.FindMulti(menu
                        ? RecognitionObject.Ocr(95 * scale, 0, 675 * scale, 780 * scale)
                        : RecognitionObject.Ocr(1683 * scale, 1051 * scale, 234 * scale, 28 * scale), ocrService: _ocr);
                    operation.Check();
                    if (!deadline.IsCancellationRequested)
                        texts = results.Select(r => new CurrentPlayerUidText(r.Text, r.X, r.Y, r.Width, r.Height)).ToArray();
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested && !operation.Token.IsCancellationRequested) { }
            }
            operation.Check();
            return new CurrentPlayerUidFrame(scene, image.Width, image.Height, texts);
        });

        public async Task<bool> OpenMenuAsync(UiSnapshot observed, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var applied = _driver.CaptureOwned((_, current) =>
            {
                ct.ThrowIfCancellationRequested();
                UiOperation.Current?.Check();
                if (!current.HasUsableEvidence || !current.SourceStamp.IsFresh(TimeProvider.System, UiSnapshot.RecoveryMaximumAge) || !current.Matches(UiTarget.Overworld) ||
                    !current.IsAfter(observed) || current.SourceStamp.CapturedTimestamp <= observed.SourceStamp.CapturedTimestamp) return false;
                InputHub.Background.SimulateAction(GIActions.OpenPaimonMenu);
                return true;
            });
            if (applied) await _driver.DelayAsync(60, ct);
            return applied;
        }
        public void Dispose() => _driver.Dispose();
    }
}
