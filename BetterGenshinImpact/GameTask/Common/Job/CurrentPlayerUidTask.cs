using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.Common.Ui;
using Microsoft.Extensions.Logging;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;

namespace BetterGenshinImpact.GameTask.Common.Job;

internal readonly record struct CurrentPlayerUidText(string Text, int X, int Y, int Width, int Height);
internal sealed record CurrentPlayerUidFrame(UiSnapshot Scene, int Width, int Height, IReadOnlyList<CurrentPlayerUidText> Text, UiAction? SubmittedInput = null);
internal sealed record CurrentPlayerUidInput(UiAction Action, UiSnapshot Intent);
internal interface ICurrentPlayerUidIo : IUiDriver
{
    CurrentPlayerUidFrame ReadUidFrame(Func<CurrentPlayerUidFrame, CurrentPlayerUidInput?>? chooseInput = null, bool readMenuUid = true);
}

internal sealed partial class CurrentPlayerUidTask
{
    internal static readonly TimeSpan TotalBudget = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan ReturnReserve = TimeSpan.FromSeconds(1);
    private readonly Func<ICurrentPlayerUidIo> _createIo;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    internal CurrentPlayerUidTask(ICurrentPlayerUidIo io, TimeProvider? clock = null, ILogger? logger = null)
    { _createIo = () => io; _clock = clock ?? TimeProvider.System; _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance; }
    private CurrentPlayerUidTask(Func<ICurrentPlayerUidIo> createIo, ILogger logger, TimeProvider clock)
    { _createIo = createIo; _clock = clock; _logger = logger; }

    public async Task<int> Start(CancellationToken ct)
    {
        try
        {
            return await UiOperation.RunAsync("current-player-uid", TotalBudget, ct, async operation =>
            {
                var io = _createIo();
                using var ownedIo = io as IDisposable;
                operation.Check();
                var firstCaptureStarted = _clock.GetTimestamp();
                var first = io.ReadUidFrame(frame =>
                {
                    operation.Check();
                    return Usable(frame) && frame.Scene.Matches(UiTarget.Overworld) && Decode(frame, false) == 0 &&
                        operation.Remaining > ReturnReserve ? new CurrentPlayerUidInput(UiAction.OpenMenu, frame.Scene) : null;
                }, readMenuUid: false);
                operation.Check();
                if (!Usable(first)) return 0;
                if (first.Scene.Matches(UiTarget.Overworld))
                {
                    var footer = Decode(first, false);
                    if (footer > 0) return footer;
                    operation.Action(UiAction.OpenMenu, first.SubmittedInput == UiAction.OpenMenu, 1, 1);
                    if (first.SubmittedInput != UiAction.OpenMenu) return 0;
                    await io.DelayAsync(60, operation.Token);
                }
                else if (!first.Scene.Matches(UiTarget.Menu)) return 0;
                var driver = new MenuReadDriver(this, io, operation, first.Scene.Matches(UiTarget.Menu) ? first.Scene : null, firstCaptureStarted, first.Scene.SourceStamp.SessionId);
                UiSnapshot menu;
                try { menu = await UiTransition.WaitAsync(operation, UiTarget.Menu, driver); }
                catch (UidObservationUnavailable)
                {
                    await WaitForOwnedWorldAsync(operation, driver);
                    return 0;
                }
                operation.Check();
                var read = driver.ReadFrame;
                var candidate = read != null && read.Scene.SourceStamp == menu.SourceStamp && Usable(read) ? Decode(read, true) : 0;
                UiSnapshot world;
                if (read?.SubmittedInput == UiAction.Escape)
                {
                    operation.Action(UiAction.Escape, true, 1, 1);
                    await io.DelayAsync(60, operation.Token);
                    world = await WaitForOwnedWorldAsync(operation, driver);
                }
                else
                {
                    world = await WaitForOwnedWorldAsync(operation, driver);
                }
                operation.Check();
                return candidate > 0 && read != null && world.HasUsableEvidence && world.IsAfter(read.Scene) &&
                    world.SourceStamp.CapturedTimestamp > read.Scene.SourceStamp.CapturedTimestamp &&
                    world.SourceStamp.IsFresh(_clock, UiSnapshot.RecoveryMaximumAge) ? candidate : 0;
            }, _logger, _clock);
        }
        catch (Exception error) when (error is not (OperationCanceledException or NormalEndException) && !ct.IsCancellationRequested) { return 0; }
    }

    private Task<UiSnapshot> WaitForOwnedWorldAsync(UiOperation operation, MenuReadDriver driver)
    {
        driver.ObserveWorld = true;
        return UiTransition.WaitAsync(operation, UiTarget.Overworld, driver,
            observed => driver.CanRequestCleanupEscape(observed) ? UiAction.Escape : null);
    }

    private sealed class UidObservationUnavailable : Exception { }
    private sealed class MenuReadDriver(CurrentPlayerUidTask owner, ICurrentPlayerUidIo io, UiOperation operation, UiSnapshot? seed, long seedCaptureStarted, Guid ownerSession) : IUiDriver
    {
        private UiSnapshot? _previousMenu;
        private long _captureStarted;
        internal CurrentPlayerUidFrame? ReadFrame { get; private set; }
        internal bool ObserveWorld { get; set; }
        private bool _escapeAuthorized;
        internal bool CanRequestCleanupEscape(UiSnapshot observed) => !_escapeAuthorized &&
            observed.SourceBound && observed.HasUsableEvidence && observed.SourceStamp.SessionId == ownerSession &&
            observed.Matches(UiTarget.Menu) && observed.CanEscape &&
            observed.SourceStamp.IsFresh(owner._clock, UiSnapshot.RecoveryMaximumAge);
        private bool TryAuthorizeEscape()
        {
            operation.Check();
            if (_escapeAuthorized) return false;
            _escapeAuthorized = true;
            return true;
        }
        public UiSnapshot Capture()
        {
            operation.Check();
            _captureStarted = owner._clock.GetTimestamp();
            if (ObserveWorld) return io.Capture();
            if (seed != null) { var result = seed; seed = null; _previousMenu = result; _captureStarted = seedCaptureStarted; return result; }
            if (_previousMenu == null)
            {
                var scene = io.Capture();
                if (scene.HasUsableEvidence && scene.Matches(UiTarget.Menu)) _previousMenu = scene;
                return scene;
            }
            var previous = _previousMenu;
            var frame = io.ReadUidFrame(current =>
            {
                operation.Check();
                if (!owner.Usable(current) || !current.Scene.Matches(UiTarget.Menu) || !current.Scene.IsAfter(previous) ||
                    current.Scene.SourceStamp.CapturedTimestamp <= previous.SourceStamp.CapturedTimestamp ||
                    current.Scene.Signature != previous.Signature ||
                    !previous.SourceStamp.IsFresh(owner._clock, UiSnapshot.RecoveryMaximumAge)) return null;
                return TryAuthorizeEscape() ? new CurrentPlayerUidInput(UiAction.Escape, previous) : null;
            });
            operation.Check();
            if (!owner.Usable(frame) || frame.Scene.SourceStamp.SessionId != previous.SourceStamp.SessionId || !frame.Scene.Matches(UiTarget.Menu))
                throw new UidObservationUnavailable();
            _previousMenu = frame.Scene;
            ReadFrame = frame;
            return frame.Scene;
        }
        public Task DelayAsync(int milliseconds, CancellationToken ct)
        {
            // Preserve the existing start-to-start observation cadence without adding it again after slow recognition.
            var elapsed = owner._clock.GetElapsedTime(_captureStarted).TotalMilliseconds;
            return io.DelayAsync((int)Math.Ceiling(Math.Max(60, milliseconds - elapsed)), ct);
        }
        public Task<bool> ActAsync(UiAction action, UiSnapshot observed, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (action != UiAction.Escape || !CanRequestCleanupEscape(observed) || !TryAuthorizeEscape()) return Task.FromResult(false);
            return io.ActAsync(action, observed, ct);
        }
        public void MarkInputCompleted(UiSnapshot before) => io.MarkInputCompleted(before);
        public void MarkTransitionCompleted(UiTarget target, UiSnapshot observed) => io.MarkTransitionCompleted(target, observed);
    }

    private bool Usable(CurrentPlayerUidFrame frame) => frame.Width > 0 && frame.Height > 0 &&
        frame.Width * 9 == frame.Height * 16 && frame.Scene.HasUsableEvidence &&
        frame.Scene.SourceStamp.IsFresh(_clock, UiSnapshot.RecoveryMaximumAge);

    internal static int Decode(CurrentPlayerUidFrame frame, bool menu)
    {
        if (menu)
        {
            if (!frame.Scene.Matches(UiTarget.Menu)) return 0;
            var card = frame.Text.Where(t => In(t, 95, 0, 675, 780)).ToArray();
            if (!card.Any(t => t.Text.Contains("冒险等阶", StringComparison.Ordinal)) ||
                !card.Any(t => t.Text.Contains("世界等级", StringComparison.Ordinal)) ||
                !card.Any(t => t.Text.Contains("生日", StringComparison.Ordinal)) ||
                !card.Any(t => t.Text.Contains("队伍配置", StringComparison.Ordinal))) return 0;
        }
        else if (!frame.Scene.Matches(UiTarget.Overworld)) return 0;
        var text = frame.Text.Where(t => menu ? In(t, 110, 190, 190, 42) : In(t, 1683, 1051, 234, 28)).ToArray();
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var label in text)
        {
            var combined = Regex.Match(label.Text.Trim(), @"^UID\s*[:：]?\s*([1-9]\d{8,9})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (combined.Success) { values.Add(combined.Groups[1].Value); continue; }
            if (!Regex.IsMatch(label.Text.Trim(), @"^UID\s*[:：]?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) continue;
            var adjacent = text.Where(value => Regex.IsMatch(value.Text.Trim(), @"^[1-9]\d{8,9}$") &&
                Math.Min(label.Y + label.Height, value.Y + value.Height) - Math.Max(label.Y, value.Y) >= Math.Min(label.Height, value.Height) * .6 &&
                value.X >= label.X + label.Width && value.X - label.X - label.Width <= Math.Max(label.Height, value.Height) * 2)
                .Select(t => t.Text.Trim()).Distinct(StringComparer.Ordinal).ToArray();
            if (adjacent.Length > 1) return 0;
            if (adjacent.Length == 1) values.Add(adjacent[0]);
        }
        return values.Count == 1 && int.TryParse(values.Single(), NumberStyles.None, CultureInfo.InvariantCulture, out var uid) && uid > 0 ? uid : 0;

        bool In(CurrentPlayerUidText t, int x, int y, int width, int height)
        {
            var scale = frame.Height / 1080d;
            return t.Width > 0 && t.Height > 0 && t.X >= x * scale && t.Y >= y * scale &&
                t.X + t.Width <= (x + width) * scale && t.Y + t.Height <= (y + height) * scale;
        }
    }
}
