using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace BetterGenshinImpact.GameTask.ArtifactAnalysis;

public sealed record ArtifactSubstatDto(string Key, double Value, bool Dormant = false);

public sealed record ArtifactItemDto(
    int ScanIndex,
    string SetKey,
    string SlotKey,
    int Level,
    int Rarity,
    string MainStatKey,
    IReadOnlyList<ArtifactSubstatDto> Substats,
    string Location,
    bool Locked)
{
    [JsonIgnore]
    internal ArtifactPanelSignature? LocalDetailSignature { get; init; }

    [JsonIgnore]
    public string ContentFingerprint => ArtifactHashes.Sha256(string.Join("|",
        SetKey,
        SlotKey,
        Level.ToString(CultureInfo.InvariantCulture),
        Rarity.ToString(CultureInfo.InvariantCulture),
        MainStatKey,
        string.Join(";", Substats
            .OrderBy(stat => stat.Key, StringComparer.Ordinal)
            .ThenBy(stat => stat.Value)
            .ThenBy(stat => stat.Dormant)
            .Select(stat => $"{stat.Key}={ArtifactHashes.Decimal(stat.Value)}{(stat.Dormant ? "@dormant" : string.Empty)}")),
        Location ?? string.Empty));
}

public sealed record ArtifactSnapshotDto(
    string Uid,
    string ScanSessionId,
    int ArtifactCount,
    string OrderingMode,
    string CatalogVersion,
    IReadOnlyList<ArtifactItemDto> Artifacts,
    string SnapshotDigest)
{
    public static ArtifactSnapshotDto Create(
        string uid,
        string scanSessionId,
        string orderingMode,
        string catalogVersion,
        IReadOnlyList<ArtifactItemDto> artifacts,
        int? inventoryArtifactCount = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uid);
        ArgumentException.ThrowIfNullOrWhiteSpace(scanSessionId);
        var artifactCount = inventoryArtifactCount ?? artifacts.Count;
        if (artifactCount < artifacts.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(inventoryArtifactCount));
        }
        var itemDigest = string.Join("|", artifacts.Select(item =>
            $"{item.ScanIndex}:{item.ContentFingerprint}:{item.Locked.ToString().ToLowerInvariant()}"));
        var digest = ArtifactHashes.Sha256(string.Join("|",
            uid, scanSessionId, artifactCount.ToString(CultureInfo.InvariantCulture),
            orderingMode, catalogVersion, itemDigest));
        return new ArtifactSnapshotDto(
            uid, scanSessionId, artifactCount, orderingMode, catalogVersion,
            artifacts.ToArray(), digest);
    }
}

public sealed record ArtifactExecutionObservationDto(
    string Uid,
    int ArtifactCount,
    IReadOnlyList<ArtifactItemDto> Artifacts,
    ArtifactSnapshotDto? FullSnapshot,
    bool CountOnly = false);

public sealed record ArtifactCharacterRosterEntryDto(
    string CharacterKey,
    int Level,
    bool Favorite);

public sealed record ArtifactCharacterRosterDto(
    string Uid,
    IReadOnlyList<ArtifactCharacterRosterEntryDto> Characters);

public sealed record ArtifactDecisionDto(
    int ScanIndex,
    string ExpectedFingerprint,
    bool ExpectedLocked,
    bool DesiredLocked);

public sealed record ArtifactDecisionPlanDto(
    string PlanId,
    string Uid,
    int SourceArtifactCount,
    string SourceSnapshotDigest,
    bool Approved,
    IReadOnlyList<ArtifactDecisionDto> Decisions);

public sealed record ArtifactExecutionActionDto(
    int ScanIndex,
    bool ExpectedLocked,
    bool DesiredLocked,
    string ExpectedFingerprint);

public enum ArtifactPreflightStatus
{
    Unknown,
    Ready,
    NotApproved,
    RescanRequired,
    StaleAbort
}

public sealed record ArtifactPreflightResult(
    ArtifactPreflightStatus Status,
    IReadOnlyList<ArtifactExecutionActionDto> Actions,
    IReadOnlyList<string> Reasons);

internal static class ArtifactHashes
{
    internal static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    internal static string Decimal(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.') ? text.TrimEnd('0').TrimEnd('.') : text;
    }
}
