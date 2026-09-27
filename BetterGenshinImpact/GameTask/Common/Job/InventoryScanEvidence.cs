using System;
using System.Collections.Generic;
using System.Linq;
using Fischless.GameCapture;

namespace BetterGenshinImpact.GameTask.Common.Job;

internal readonly record struct InventoryScrollObservation(bool Known, double Top, double Bottom, bool AtTop, bool AtBottom);

internal sealed record InventoryCountSnapshot(Dictionary<string, int> Counts, bool CoverageComplete,
    string Reason, int Pages, int UnrecognizedSlots);

internal sealed class InventoryScanEvidence(IEnumerable<string> requestedNames, int columns)
{
    private readonly string[] _requestedNames = requestedNames.Distinct(StringComparer.Ordinal).ToArray();
    private string? _failure;
    private bool _startedAtTop;
    private InventoryScrollObservation _lastScroll;
    private CaptureFrameStamp _lastSource;
    private string?[]? _lastItems;
    private int _pages, _unrecognizedSlots;

    internal void ObservePage(CaptureFrameStamp source, InventoryScrollObservation scroll,
        IReadOnlyList<string?> items, bool layoutComplete)
    {
        void Reject(string reason) => _failure ??= reason;
        if (!source.IsKnown) Reject("source-unknown");
        if (!scroll.Known) Reject("scrollbar-unavailable");
        if (!layoutComplete || items.Count == 0) Reject("grid-incomplete");
        _unrecognizedSlots += items.Count(string.IsNullOrWhiteSpace);
        if (_unrecognizedSlots != 0) Reject("unrecognized-items");
        if (_pages == 0)
        {
            _startedAtTop = scroll.Known && scroll.AtTop;
            if (!_startedAtTop) Reject("scan-did-not-start-at-top");
        }
        else
        {
            if (!source.IsAfter(_lastSource)) Reject("source-not-advanced-or-changed");
            if (Math.Abs((scroll.Bottom - scroll.Top) - (_lastScroll.Bottom - _lastScroll.Top)) > 2)
                Reject("inventory-extent-changed");
            if (scroll.Top < _lastScroll.Top - 2 || scroll.Top - _lastScroll.Top > _lastScroll.Bottom - _lastScroll.Top + 2)
                Reject("scroll-coverage-gap");
            // 至少一整行唯一的前页后缀/后页前缀交接；重复图标不能任选一个重合位置。
            var matches = 0;
            for (var offset = 0; offset <= _lastItems!.Length - columns; offset++)
            {
                var overlap = _lastItems.Length - offset;
                if (overlap > items.Count) continue;
                if (_lastItems.Skip(offset).SequenceEqual(items.Take(overlap), StringComparer.Ordinal)) matches++;
            }
            if (matches != 1) Reject("page-overlap-not-unique");
        }
        _pages++;
        _lastSource = source;
        _lastScroll = scroll;
        _lastItems = items.ToArray();
    }

    internal InventoryCountSnapshot Finish(IReadOnlyDictionary<string, int> observed, string? incompleteReason = null)
    {
        var complete = incompleteReason == null && _failure == null && _pages > 0 && _startedAtTop && _lastScroll.AtBottom;
        var counts = _requestedNames.ToDictionary(name => name,
            name => observed.TryGetValue(name, out var value) ? value : complete ? 0 : -1, StringComparer.Ordinal);
        return new(counts, complete, complete ? "verified-top-to-bottom" :
            _failure ?? incompleteReason ?? "bottom-not-confirmed", _pages, _unrecognizedSlots);
    }
}
