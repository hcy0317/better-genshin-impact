using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using Fischless.GameCapture;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.AutoPathing.Model;

internal readonly record struct ReachedNavigation(int Index, Point2f Position, CaptureFrameStamp Source);
internal readonly record struct WaypointActionSettlement(int Index, string CommandDigest, CombatExecutionResult Outcome);
internal readonly record struct NavigationWaypoint(int OriginalIndex, double X, double Y, string MoveMode);
internal sealed record NavigationResumePlan(string RouteIdentity, string SegmentDigest, Guid Owner,
    int ResumeCurrent, ReachedNavigation Reached, ImmutableArray<NavigationWaypoint> Steps)
{
    internal static string CommandDigest(WaypointForTrack point) => string.Join(";",
        point.CombatScript?.CombatCommands.Select(command => command.Name + ":" + command.Method.Alias[0] + ":" +
            string.Join(",", command.Args ?? [])) ?? []);
    internal static string Digest(IReadOnlyList<WaypointForTrack> segment) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join("|", segment.Select(point => string.Join("~", point.Id,
            point.X.ToString("R", CultureInfo.InvariantCulture), point.Y.ToString("R", CultureInfo.InvariantCulture),
            point.Type, point.MoveMode, point.Action, point.MapContext.ToString(), point.MapLayerSelector?.ToString(), CommandDigest(point)))))));
    internal static NavigationResumePlan? TryCreate(string identity, IReadOnlyList<WaypointForTrack> segment,
        int current, ReachedNavigation? reached, WaypointAttemptBudget? owner,
        IReadOnlyDictionary<int, WaypointActionSettlement> settlements)
    {
        if (owner == null || owner.CurrentIndex != current || reached is not { } last || !last.Source.IsKnown ||
            current <= 0 || current >= segment.Count || last.Index < 0 || last.Index >= current || segment[0].Type != "teleport") return null;
        for (var index = 1; index <= last.Index; index++)
        {
            var point = segment[index];
            // Special flight/action transitions are not silently erased from a navigation view.
            if (point.Type is not ("path" or "target") || point.MoveMode is not ("walk" or "run" or "dash" or "climb") ||
                !string.IsNullOrEmpty(point.Action) && point.Action != "combat_script" ||
                point.CombatScript?.CombatCommands.Any(command => command.RequiresFlow) == true) return null;
            if (!string.IsNullOrEmpty(point.Action) && (!settlements.TryGetValue(index, out var settled) ||
                settled.CommandDigest != CommandDigest(point) || !settled.Outcome.CanContinue)) return null;
        }
        return new(identity, Digest(segment), owner.Owner, current, last,
            Enumerable.Range(1, last.Index).Select(index => new NavigationWaypoint(index, segment[index].X, segment[index].Y, segment[index].MoveMode)).ToImmutableArray());
    }
    internal bool Matches(string identity, IReadOnlyList<WaypointForTrack> segment, WaypointAttemptBudget? owner) =>
        owner?.Owner == Owner && owner.CurrentIndex == ResumeCurrent && identity == RouteIdentity && Digest(segment) == SegmentDigest;
}
