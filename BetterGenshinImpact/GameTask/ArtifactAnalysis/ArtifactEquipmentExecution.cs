using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.GameTask.ArtifactAnalysis;

public sealed record ArtifactEquipTargetDto(string Character, string NativeName, string InventoryName, IReadOnlyList<int> Artifacts);
public sealed record ArtifactEquipmentPlanDto(string Id, string Uid, string Digest, bool Confirmed, int InventoryCount,
    IReadOnlyList<ArtifactItemDto> Snapshot, IReadOnlyList<ArtifactEquipTargetDto> Targets, IReadOnlyList<string> ProtectedOwners);
public sealed record ArtifactEquipStepDto(string Character, int ArtifactId, string State, string? Message = null);
public sealed record ArtifactEquipmentResultDto(string Status, IReadOnlyList<ArtifactEquipStepDto> Steps,
    IReadOnlyList<ArtifactItemDto>? Observation, string? Message = null);

public interface IArtifactEquipmentPort
{
    Task<ArtifactSnapshotDto> ObserveAsync(string uid, CancellationToken ct);
    Task EquipAsync(ArtifactEquipTargetDto target, ArtifactItemDto observedItem, CancellationToken ct);
}

/// <summary>Confirmed real-instance execution, with an observable-equivalence binding
/// and a durable unknown checkpoint before every mutating input.</summary>
public sealed class ArtifactEquipmentExecution
{
    public async Task<ArtifactEquipmentResultDto> RunAsync(ArtifactEquipmentPlanDto plan, IArtifactEquipmentPort port,
        Func<ArtifactEquipmentResultDto, Task> checkpoint, CancellationToken ct)
    {
        if (!plan.Confirmed || string.IsNullOrWhiteSpace(plan.Digest) || plan.Snapshot.Count > 10000)
            throw new InvalidOperationException("A specifically confirmed equipment plan is required.");
        var expected = plan.Snapshot.ToDictionary(item => item.ScanIndex);
        if (plan.Targets.Select(t => t.InventoryName).Distinct(StringComparer.Ordinal).Count() != plan.Targets.Count
            || plan.Targets.Any(t => t.Artifacts.Any(id => !expected.ContainsKey(id))
                || t.Artifacts.Select(id => expected[id].SlotKey).Distinct(StringComparer.Ordinal).Count() != t.Artifacts.Count))
            throw new InvalidDataException("Targets contain duplicate characters, slots or missing physical instances.");
        var desired = plan.Targets.SelectMany(target => target.Artifacts.Select(id => (Target: target, Id: id))).ToArray();
        if (desired.Length > 160 || desired.Select(x => x.Id).Distinct().Count() != desired.Length)
            throw new InvalidDataException("The confirmed plan reuses a physical artifact.");
        foreach (var (target, id) in desired)
        {
            if (!expected.TryGetValue(id, out var piece) || string.IsNullOrWhiteSpace(target.InventoryName))
                throw new InvalidDataException("Confirmed equipment target is incomplete.");
            if (plan.ProtectedOwners.Contains(piece.Location) && piece.Location != target.InventoryName)
                throw new InvalidDataException("Protected equipment cannot be borrowed.");
            if (plan.ProtectedOwners.Contains(target.InventoryName) && piece.Location != target.InventoryName)
                throw new InvalidDataException("A protected outfit cannot be replaced.");
        }
        var steps = desired.Select(x => new ArtifactEquipStepDto(x.Target.Character, x.Id, "not_executed")).ToArray();
        ArtifactSnapshotDto? latest = null;
        for (var index = 0; index < desired.Length; index++)
        {
            var (target, id) = desired[index];
            var inputEntered = false;
            try
            {
                ct.ThrowIfCancellationRequested();
                latest = await port.ObserveAsync(plan.Uid, ct);
                var bindings = Bind(plan, expected.Values, latest);
                if (expected[id].Location == target.InventoryName)
                {
                    steps[index] = steps[index] with { State = "completed", Message = "already_equipped" };
                    await checkpoint(new("running", steps.ToArray(), latest.Artifacts));
                    continue;
                }
                var before = expected[id];
                var displaced = expected.Values.SingleOrDefault(item => item.Location == target.InventoryName && item.SlotKey == before.SlotKey);
                var next = new Dictionary<int, ArtifactItemDto>(expected) { [id] = before with { Location = target.InventoryName } };
                if (displaced is not null) next[displaced.ScanIndex] = displaced with { Location = "" };

                steps[index] = steps[index] with { State = "unknown", Message = "mutation_checkpoint" };
                await checkpoint(new("running", steps.ToArray(), latest.Artifacts));
                ct.ThrowIfCancellationRequested();
                inputEntered = true;
                await port.EquipAsync(target, bindings[id], ct);
                latest = await port.ObserveAsync(plan.Uid, ct);
                try { Bind(plan, next.Values, latest); }
                catch (InvalidDataException) when (displaced is not null && !string.IsNullOrEmpty(before.Location))
                {
                    // Accept an actual automatic swap only within the already
                    // confirmed source/target closure; all other items still match.
                    next[displaced.ScanIndex] = displaced with { Location = before.Location };
                    Bind(plan, next.Values, latest);
                }
                expected = next;
                steps[index] = steps[index] with { State = "completed", Message = null };
                await checkpoint(new("running", steps.ToArray(), latest.Artifacts));
            }
            catch (Exception error)
            {
                if (!inputEntered && steps[index].State == "unknown") steps[index] = steps[index] with { State = "not_executed" };
                var result = new ArtifactEquipmentResultDto(inputEntered ? "needs_observation" : ct.IsCancellationRequested ? "cancelled" : "rejected",
                    steps.ToArray(), latest?.Artifacts, error.Message);
                try { await checkpoint(result); } catch { /* Preserve the original outcome. */ }
                return result;
            }
        }
        var completed = new ArtifactEquipmentResultDto("completed", steps.ToArray(), latest?.Artifacts);
        await checkpoint(completed);
        return completed;
    }

    private static Dictionary<int, ArtifactItemDto> Bind(ArtifactEquipmentPlanDto plan, IEnumerable<ArtifactItemDto> expected,
        ArtifactSnapshotDto observation)
    {
        if (observation.Uid != plan.Uid || observation.ArtifactCount != plan.InventoryCount)
            throw new InvalidDataException("Observed account or inventory count changed.");
        static string Key(ArtifactItemDto item) => item.ContentFingerprint + "/" + item.Locked;
        var groups = observation.Artifacts.GroupBy(Key).ToDictionary(g => g.Key, g => g.OrderBy(i => i.ScanIndex).ToArray());
        var wanted = expected.ToArray();
        if (wanted.Length != observation.Artifacts.Count) throw new InvalidDataException("Artifact observations are incomplete.");
        var result = new Dictionary<int, ArtifactItemDto>();
        foreach (var group in wanted.GroupBy(Key))
        {
            var originals = group.OrderBy(i => i.ScanIndex).ToArray();
            if (!groups.TryGetValue(group.Key, out var actual) || actual.Length != originals.Length)
                throw new InvalidDataException("Artifact content, ownership, protection-related lock state or equivalence count changed.");
            for (var index = 0; index < originals.Length; index++) result[originals[index].ScanIndex] = actual[index];
        }
        return result;
    }
}
