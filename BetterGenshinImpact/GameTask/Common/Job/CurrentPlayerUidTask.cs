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
internal sealed record CurrentPlayerUidFrame(UiSnapshot Scene, int Width, int Height, IReadOnlyList<CurrentPlayerUidText> Text);
internal interface ICurrentPlayerUidIo : IUiDriver
{
    CurrentPlayerUidFrame ReadUidFrame();
    Task<bool> OpenMenuAsync(UiSnapshot observed, CancellationToken ct);
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
                CurrentPlayerUidFrame Read()
                {
                    operation.Check();
                    var frame = io.ReadUidFrame();
                    operation.Check();
                    return frame;
                }
                var first = Read();
                if (!Usable(first)) return 0;
                if (first.Scene.Matches(UiTarget.Overworld))
                {
                    var footer = Decode(first, false);
                    if (footer > 0) return footer;
                    if (operation.Remaining <= ReturnReserve) return 0;
                    var applied = await io.OpenMenuAsync(first.Scene, operation.Token);
                    operation.Check();
                    operation.Action(UiAction.OpenMenu, applied, 1, 1);
                    if (!applied) return 0;
                    io.MarkInputCompleted(first.Scene);
                }
                else if (!first.Scene.Matches(UiTarget.Menu)) return 0;
                var menu = await UiTransition.WaitAsync(operation, UiTarget.Menu, io);
                operation.Check();
                var candidate = 0;
                CurrentPlayerUidFrame? readFrame = null;
                if (operation.Remaining > ReturnReserve)
                {
                    readFrame = Read();
                    if (Usable(readFrame) && readFrame.Scene.IsAfter(menu) &&
                        readFrame.Scene.SourceStamp.CapturedTimestamp > menu.SourceStamp.CapturedTimestamp)
                        candidate = Decode(readFrame, true);
                }
                operation.Check();
                var world = await UiRecovery.ToMainAsync(io, operation.Token, logger: _logger, clock: _clock,
                    requireOverworld: true, budget: operation.Remaining);
                operation.Check();
                var after = readFrame?.Scene ?? menu;
                return candidate > 0 && world.Matches(UiTarget.Overworld) && world.HasUsableEvidence &&
                    world.IsAfter(after) && world.SourceStamp.CapturedTimestamp > after.SourceStamp.CapturedTimestamp &&
                    world.SourceStamp.IsFresh(_clock, UiSnapshot.RecoveryMaximumAge) ? candidate : 0;
            }, _logger, _clock);
        }
        catch (Exception error) when (error is not (OperationCanceledException or NormalEndException) && !ct.IsCancellationRequested) { return 0; }
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
