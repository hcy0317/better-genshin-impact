using Fischless.GameCapture;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.GameTask.Common.Map.Maps.Base;

namespace BetterGenshinImpact.GameTask.AutoPathing;

// 跟随同一ImageRegion，不能把另一帧/另一个selector的匹配结果移植过来。
internal readonly record struct NavigationFrameEvidence(bool Available, CaptureFrameStamp Source,
    string RequestedMethod, string ActualMethod, string RequestedLayer, string? ActualLayer,
    int? ActualFloor, double? Score, bool Fallback, string Search, string MiniMapRegion, bool? MatchAccepted = null)
{
    private static object Key(string map, string method, MapLayerSelector? selector) =>
        (typeof(NavigationFrameEvidence), map, method, selector?.StateKey ?? MapLayerSelector.Empty.StateKey);

    internal void Store(ImageRegion frame, string map, string method, MapLayerSelector? selector)
    {
        var value = this;
        try { frame.ReadOnce(Key(map, method, selector), () => value); }
        catch { /* 证据缓存不得改变定位结果。 */ }
    }

    internal static NavigationFrameEvidence Read(ImageRegion frame, string map, string method, MapLayerSelector? selector)
    {
        try { return frame.ReadOnce(Key(map, method, selector), () => default(NavigationFrameEvidence)); }
        catch { return default; }
    }

    internal string Describe() => Available
        ? System.FormattableString.Invariant($"minimapSource={Source.SessionId}/{Source.Sequence} roi={MiniMapRegion} requestedMethod={RequestedMethod} actualMethod={ActualMethod} requestedLayer={RequestedLayer} actualLayer={ActualLayer ?? "unknown:not-exposed-by-matcher"} actualFloor={ActualFloor?.ToString() ?? "unknown"} candidateScore={Score?.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) ?? "unknown:not-exposed-by-matcher"} matchAccepted={MatchAccepted?.ToString() ?? "unknown"} templateFallback={Fallback} search={Search}")
        : "minimapSource=unknown:no-match-on-this-frame actualLayer=unknown:not-exposed candidateScore=unknown:not-exposed-by-location-result";
}
