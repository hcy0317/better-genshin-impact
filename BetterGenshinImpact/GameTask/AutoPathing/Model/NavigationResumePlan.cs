using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using Fischless.GameCapture;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.AutoPathing.Model;

internal readonly record struct ReachedNavigation(int Index, Point2f Position, CaptureFrameStamp Source);
internal readonly record struct WaypointActionSettlement(int Index, string CommandDigest, CombatExecutionResult Outcome)
{
    internal NavigationResumePlan.MiningBinding? Mining { get; init; }
}
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
    internal sealed record MiningBinding(string ContextDescriptor, string Strategy, string IrDigest,
        string Name, int Slot, CaptureFrameStamp Source)
    {
        internal bool SameStrategy(MiningBinding other) => ContextDescriptor == other.ContextDescriptor &&
            Strategy == other.Strategy && IrDigest == other.IrDigest && Name == other.Name && Slot == other.Slot;
    }

    internal bool FullSemantic { get; init; }
    internal ImmutableDictionary<int, MiningBinding> MiningBindings { get; init; } = ImmutableDictionary<int, MiningBinding>.Empty;
    internal static string CommandsDigest(IEnumerable<CombatCommand> commands) => string.Join(";", commands.Select(command =>
        command.Name + ":" + command.Method.Alias[0] + ":" + string.Join(",", command.Args ?? [])));
    internal static string SemanticDigest(IReadOnlyList<WaypointForTrack> segment)
    {
        var raw = segment.Select((point, index) =>
        {
            var parameters = point.ActionParams == null ? "<null>" :
                point.ActionParams.Length.ToString(CultureInfo.InvariantCulture) + ":" + point.ActionParams;
            var flow = point.CombatScript?.HasFlowCommands == true ||
                point.CombatScript?.CombatCommands.Any(command => command.RequiresFlow) == true;
            var rounds = string.Join(";", point.CombatScript?.CombatCommands.Select(command =>
                string.Join(",", command.ActivatingRound ?? [])) ?? []);
            return index.ToString(CultureInfo.InvariantCulture) + ":" + parameters + ":flow=" + flow + ":rounds=" + rounds;
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Digest(segment) + "|" + string.Join("|", raw))));
    }

    internal static string DescribeSemanticState(IReadOnlyList<WaypointForTrack> segment) => string.Join("|",
        segment.Select((point, index) => $"{index}:id={point.Id},ctx={point.MapContext},mode={point.MoveMode},action={point.Action}," +
            $"ir={CommandDigest(point)},flow={point.CombatScript?.HasFlowCommands},rounds=" +
            string.Join(";", point.CombatScript?.CombatCommands.Select(command => string.Join(",", command.ActivatingRound ?? [])) ?? [])));

    internal static bool IsBoundMiningStrategy(IReadOnlyList<CombatCommand> commands) => commands.Count == 1 &&
        commands[0].Name == "钟离" && commands[0].Method == Method.Skill && !commands[0].RequiresFlow &&
        commands[0].Args is { Count: 2 } args && args.Contains("hold") && args.Contains("wait");

    private static bool IsFinitePrefixMacro(WaypointForTrack point)
    {
        if (point.CombatScript is not { } script || script.HasFlowCommands || script.CombatCommands.Count is < 1 or > 2) return false;
        try
        {
            var original = CombatScriptParser.ParseContext(point.ActionParams ?? "", false);
            if (CommandsDigest(original.CombatCommands) != CommandsDigest(script.CombatCommands)) return false;
        }
        catch (Exception error) when (error is FormatException or InvalidOperationException) { return false; }
        var movement = 0d;
        var waiting = 0d;
        var skills = 0;
        foreach (var command in script.CombatCommands)
        {
            if (command.RequiresFlow) return false;
            if (script.CombatCommands.Count == 1 && command.Method == Method.Wait &&
                BetterGenshinImpact.GameTask.AutoFight.Script.Flow.LegacyPathingMacroPlan.IsRaw(command) &&
                command.Name == CombatScriptParser.CurrentAvatarName &&
                command.Args is { Count: 1 } wait && double.TryParse(wait[0], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var seconds) && double.IsFinite(seconds) && seconds is >= 0 and <= 1)
            { waiting += seconds; continue; }
            if (command.Name != "钟离") return false;
            if (command.Method == Method.Skill && (command.Args is null or { Count: 0 } ||
                command.Args is { Count: 1 } flags && flags[0] == "hold"))
            { if (++skills > 1) return false; continue; }
            if ((command.Method == Method.W || command.Method == Method.A || command.Method == Method.S || command.Method == Method.D) &&
                command.Args is { Count: 1 } move && double.TryParse(move[0], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var duration) && double.IsFinite(duration) && duration is > 0 and <= 1)
            { movement += duration; continue; }
            return false;
        }
        return movement <= 2 && waiting <= 1;
    }

    internal static NavigationResumePlan? TryCreateFullSemantic(string identity, IReadOnlyList<WaypointForTrack> segment,
        int current, ReachedNavigation? reached, WaypointAttemptBudget? owner,
        IReadOnlyDictionary<int, WaypointActionSettlement> settlements, string originalDigest, out string rejection)
    {
        rejection = "entry-owner-or-reached";
        if (owner == null || owner.CurrentIndex != current || reached is not { } last || !last.Source.IsKnown ||
            current <= 0 || current >= segment.Count || last.Index != current - 1 || segment[0].Type != "teleport" ||
            !string.IsNullOrEmpty(segment[0].Action)) return null;
        if (SemanticDigest(segment) != originalDigest) { rejection = "original-semantic-digest-changed"; return null; }
        var bindings = ImmutableDictionary.CreateBuilder<int, MiningBinding>();
        for (var index = 1; index < current; index++)
        {
            var point = segment[index];
            if (point.Type is not ("path" or "target") || point.MoveMode is not ("walk" or "run" or "fly") ||
                point.MapContext.MapName != segment[0].MapContext.MapName)
            { rejection = $"unsupported-node:index={index},type={point.Type},mode={point.MoveMode},map={point.MapContext}"; return null; }
            if (string.IsNullOrEmpty(point.Action)) continue;
            if (point.Action == "stop_flying")
            {
                if (point.MoveMode != "fly" || !string.IsNullOrEmpty(point.ActionParams))
                { rejection = $"unsupported-stop:index={index},mode={point.MoveMode},args={point.ActionParams}"; return null; }
                continue;
            }
            if (point.Action == "log_output") continue;
            if (!settlements.TryGetValue(index, out var settled) || settled.Outcome.Kind != CombatExecutionKind.Completed ||
                settled.CommandDigest != CommandDigest(point))
            { rejection = $"settlement:index={index},kind={settled.Outcome.Kind},settled={settled.CommandDigest},current={CommandDigest(point)}"; return null; }
            if (point.Action == "combat_script")
            {
                if (!IsFinitePrefixMacro(point))
                { rejection = $"unsupported-macro:index={index},ir={CommandDigest(point)},flow={point.CombatScript?.HasFlowCommands}"; return null; }
                continue;
            }
            if (point.Action != "mining" || !string.IsNullOrEmpty(point.ActionParams) || settled.Mining is not { } binding ||
                !binding.Source.IsKnown || binding.Source.SessionId != last.Source.SessionId || binding.Name != "钟离" ||
                binding.Slot is < 1 or > 4 || string.IsNullOrEmpty(binding.ContextDescriptor))
            { rejection = $"mining-binding:index={index},action={point.Action},binding={settled.Mining},reachedSource={last.Source}"; return null; }
            var parsed = CombatScriptParser.ParseContext(binding.Strategy);
            if (!IsBoundMiningStrategy(parsed.CombatCommands) || CommandsDigest(parsed.CombatCommands) != binding.IrDigest)
            { rejection = $"mining-ir:index={index},original={binding.IrDigest},parsed={CommandsDigest(parsed.CombatCommands)}"; return null; }
            bindings.Add(index, binding);
        }
        if (bindings.Count == 0) { rejection = "no-original-mining-binding"; return null; }
        rejection = "accepted-full-semantic";
        return new(identity, SemanticDigest(segment), owner.Owner, current, last,
            Enumerable.Range(1, current - 1).Select(index => new NavigationWaypoint(index, segment[index].X,
                segment[index].Y, segment[index].MoveMode)).ToImmutableArray())
        { FullSemantic = true, MiningBindings = bindings.ToImmutable() };
    }

    internal bool Matches(string identity, IReadOnlyList<WaypointForTrack> segment, WaypointAttemptBudget? owner) =>
        owner?.Owner == Owner && owner.CurrentIndex == ResumeCurrent && identity == RouteIdentity &&
        (FullSemantic ? SemanticDigest(segment) : Digest(segment)) == SegmentDigest;
}
