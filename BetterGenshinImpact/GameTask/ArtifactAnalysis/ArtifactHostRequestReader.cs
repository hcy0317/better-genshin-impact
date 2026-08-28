using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.GameTask.ArtifactAnalysis;

public sealed record ArtifactHostLaunchRequest(
    string RequestToken,
    string RequestPath,
    ArtifactHostRequest Request);

public sealed class ArtifactHostRequestReader
{
    private readonly string _requestRoot;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter<ArtifactHostOperation>(JsonNamingPolicy.SnakeCaseUpper) }
    };

    public ArtifactHostRequestReader(string requestRoot)
    {
        _requestRoot = Path.GetFullPath(requestRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
    }

    public async Task<ArtifactHostLaunchRequest> ReadAsync(
        string requestPath,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(requestPath);
        if (!fullPath.StartsWith(_requestRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Artifact host request path is outside the controlled directory.");
        }
        if (!string.Equals(Path.GetExtension(fullPath), ".json", StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParse(Path.GetFileNameWithoutExtension(fullPath), out var requestToken))
        {
            throw new InvalidOperationException("Artifact host request filename is invalid.");
        }
        if (!File.Exists(fullPath))
        {
            throw new InvalidOperationException("Artifact host request file does not exist.");
        }

        await using var stream = File.OpenRead(fullPath);
        var request = await JsonSerializer.DeserializeAsync<ArtifactHostRequest>(
            stream, _jsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Artifact host request is empty.");
        if (request.Version != 1
            || !string.Equals(request.Kind, "artifact-analysis", StringComparison.Ordinal)
            || request.ExpiresAtUtc <= DateTimeOffset.UtcNow
            || string.IsNullOrWhiteSpace(request.Uid)
            || string.IsNullOrWhiteSpace(request.JobId))
        {
            throw new InvalidOperationException("Artifact host request is invalid or expired.");
        }
        if (request.Operation == ArtifactHostOperation.ExecuteLockPlan
            && (request.SourceArtifactCount is null or < 0 || request.Targets is null))
        {
            throw new InvalidOperationException(
                "Artifact lock request is missing its approved target binding.");
        }
        if (request.Operation == ArtifactHostOperation.RebuildNativePlans
            && (request.NativeCapacity is null or < 1
                || string.IsNullOrWhiteSpace(request.NativePlanDigest)))
        {
            throw new InvalidOperationException(
                "Native artifact request is missing its reviewed plan binding.");
        }
        if (request.Operation == ArtifactHostOperation.ScanCharacterRoster
            && (request.CharacterLevelThreshold is null or < 0 or > 90
                || request.FavoriteOverride is null
                || request.MiliastraCharacterKey is not null
                && request.MiliastraCharacterKey is not "MannequinBoy" and not "MannequinGirl"))
        {
            throw new InvalidOperationException(
                "Character roster request is missing its activation settings.");
        }
        return new ArtifactHostLaunchRequest(requestToken.ToString(), fullPath, request);
    }
}
