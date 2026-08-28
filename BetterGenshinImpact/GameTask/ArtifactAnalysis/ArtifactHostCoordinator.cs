using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.GameTask.ArtifactAnalysis;

public enum ArtifactHostOperation
{
    Analyze,
    ScanCharacterRoster,
    ExecuteLockPlan,
    RebuildNativePlans
}

public sealed record ArtifactHostRequest(
    int Version,
    string Kind,
    string Uid,
    string JobId,
    ArtifactHostOperation Operation,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    int? SourceArtifactCount,
    IReadOnlyList<ArtifactLaunchTargetDto>? Targets,
    int? NativeCapacity,
    string? NativePlanDigest,
    int? CharacterLevelThreshold,
    bool? FavoriteOverride,
    string? GameNickname,
    string? MiliastraNickname,
    string? MiliastraCharacterKey);

public sealed record ArtifactLaunchTargetDto(
    int ScanIndex,
    string ExpectedFingerprint,
    bool ExpectedLocked);

public sealed record ArtifactNativeSetPlanDto(
    string SetKey,
    string SlotKey,
    IReadOnlyList<string> MainStats,
    IReadOnlyList<string> Substats);

public sealed record ArtifactNativeSyncPlanDto(
    string Status,
    bool ReplaceAll,
    bool RequiresPreMutationEvidence,
    int Capacity,
    int SourceBuildCount,
    IReadOnlyList<ArtifactNativeSetPlanDto> Plans,
    string PlanDigest,
    string TranslationMode,
    string Message);

public interface IArtifactInventoryScanner
{
    Task<ArtifactSnapshotDto> ScanAsync(string uid, CancellationToken cancellationToken);

    Task<ArtifactExecutionObservationDto> InspectForExecutionAsync(
        string uid,
        int expectedArtifactCount,
        IReadOnlyList<ArtifactLaunchTargetDto> targets,
        CancellationToken cancellationToken);
}

public interface IArtifactCharacterRosterScanner
{
    Task<ArtifactCharacterRosterDto> ScanAsync(
        string uid,
        string? gameNickname,
        string? miliastraNickname,
        string? miliastraCharacterKey,
        CancellationToken cancellationToken);
}

public interface IArtifactToolsClient
{
    Task ClaimAsync(
        string jobId,
        string uid,
        ArtifactHostOperation operation,
        string requestToken,
        CancellationToken cancellationToken);

    Task SubmitSnapshotAsync(
        string jobId,
        string requestToken,
        ArtifactSnapshotDto snapshot,
        CancellationToken cancellationToken);

    Task<ArtifactPreflightResult> PreflightAsync(
        string jobId,
        string requestToken,
        ArtifactExecutionObservationDto observation,
        CancellationToken cancellationToken);

    Task SubmitCharacterRosterAsync(
        string jobId,
        string requestToken,
        ArtifactCharacterRosterDto roster,
        CancellationToken cancellationToken);

    Task<ArtifactNativeSyncPlanDto> GetNativeSyncPlanAsync(
        string jobId,
        string requestToken,
        CancellationToken cancellationToken);

    Task ReportCompletionAsync(
        string jobId,
        string requestToken,
        ArtifactHostOperation operation,
        bool success,
        string? message,
        CancellationToken cancellationToken);
}

public interface IArtifactLockPlanExecutor
{
    Task ExecuteAsync(
        IReadOnlyList<ArtifactExecutionActionDto> actions,
        bool reusePreparedInventory,
        CancellationToken cancellationToken);
}

public interface IArtifactNativePlanExecutor
{
    Task ReplaceAllAsync(ArtifactNativeSyncPlanDto plan, CancellationToken cancellationToken);
}

public sealed class ArtifactHostCoordinator(
    IArtifactInventoryScanner scanner,
    IArtifactToolsClient client,
    IArtifactLockPlanExecutor lockExecutor,
    IArtifactNativePlanExecutor nativePlanExecutor,
    IArtifactCharacterRosterScanner? characterRosterScanner = null)
{
    public async Task RunAsync(
        ArtifactHostRequest request,
        string requestToken,
        CancellationToken cancellationToken)
    {
        Validate(request, requestToken);
        try
        {
            await client.ClaimAsync(
                request.JobId, request.Uid, request.Operation,
                requestToken, cancellationToken);
            switch (request.Operation)
            {
                case ArtifactHostOperation.Analyze:
                {
                    var snapshot = await scanner.ScanAsync(request.Uid, cancellationToken);
                    EnsureUid(request, snapshot);
                    await client.SubmitSnapshotAsync(
                        request.JobId, requestToken, snapshot, cancellationToken);
                    break;
                }
                case ArtifactHostOperation.ScanCharacterRoster:
                {
                    if (request.CharacterLevelThreshold is null or < 0 or > 90
                        || request.FavoriteOverride is null)
                    {
                        throw new InvalidOperationException(
                            "Character roster request is missing its activation settings.");
                    }
                    if (characterRosterScanner is null)
                    {
                        throw new InvalidOperationException("Character roster scanner is unavailable.");
                    }
                    var roster = await characterRosterScanner.ScanAsync(
                        request.Uid, request.GameNickname,
                        request.MiliastraNickname,
                        request.MiliastraCharacterKey,
                        cancellationToken);
                    if (!string.Equals(request.Uid, roster.Uid, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "Character roster UID does not match the host request.");
                    }
                    await client.SubmitCharacterRosterAsync(
                        request.JobId, requestToken, roster, cancellationToken);
                    break;
                }
                case ArtifactHostOperation.ExecuteLockPlan:
                {
                    if (request.SourceArtifactCount is null or < 0)
                    {
                        throw new InvalidOperationException(
                            "Artifact lock request is missing its approved inventory count.");
                    }
                    var observation = await scanner.InspectForExecutionAsync(
                        request.Uid,
                        request.SourceArtifactCount.Value,
                        request.Targets ?? [],
                        cancellationToken);
                    if (!string.Equals(request.Uid, observation.Uid, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "Artifact execution observation UID does not match the host request.");
                    }
                    var preflight = await client.PreflightAsync(
                        request.JobId, requestToken, observation, cancellationToken);
                    if (preflight.Status == ArtifactPreflightStatus.RescanRequired) break;
                    if (preflight.Status != ArtifactPreflightStatus.Ready)
                    {
                        throw new InvalidOperationException(
                            $"Artifact lock preflight rejected execution: {preflight.Status}");
                    }
                    await lockExecutor.ExecuteAsync(
                        preflight.Actions,
                        observation.CountOnly,
                        cancellationToken);
                    break;
                }
                case ArtifactHostOperation.RebuildNativePlans:
                {
                    if (request.NativeCapacity is null || string.IsNullOrWhiteSpace(request.NativePlanDigest))
                    {
                        throw new InvalidOperationException(
                            "Native artifact request is missing its reviewed plan binding.");
                    }
                    var plan = await client.GetNativeSyncPlanAsync(
                        request.JobId, requestToken, cancellationToken);
                    if (!string.Equals(plan.Status, "READY", StringComparison.Ordinal)
                        || !plan.ReplaceAll
                        || !plan.RequiresPreMutationEvidence
                        || plan.Capacity != request.NativeCapacity
                        || !string.Equals(plan.PlanDigest, request.NativePlanDigest, StringComparison.Ordinal)
                        || !string.Equals(plan.TranslationMode, "CONSERVATIVE_SET_UNION", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Native artifact plan preflight rejected replacement: {plan.Status}");
                    }
                    await nativePlanExecutor.ReplaceAllAsync(plan, cancellationToken);
                    break;
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(request.Operation), request.Operation, null);
            }
            await client.ReportCompletionAsync(
                request.JobId, requestToken, request.Operation,
                true, null, cancellationToken);
        }
        catch (Exception exception)
        {
            var completionMessage = exception is OperationCanceledException
                ? "用户已停止任务"
                : exception.ToString();
            try
            {
                await client.ReportCompletionAsync(
                    request.JobId, requestToken, request.Operation,
                    false, completionMessage, CancellationToken.None);
            }
            catch
            {
                // Preserve the original host failure.
            }
            throw;
        }
    }

    private static void Validate(ArtifactHostRequest request, string requestToken)
    {
        if (request.Version != 1 || !string.Equals(request.Kind, "artifact-analysis", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Unsupported artifact host request format.");
        }
        if (string.IsNullOrWhiteSpace(requestToken))
        {
            throw new InvalidOperationException("Artifact host request token is required.");
        }
        if (request.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            throw new InvalidOperationException("Artifact host request has expired.");
        }
    }

    private static void EnsureUid(ArtifactHostRequest request, ArtifactSnapshotDto snapshot)
    {
        if (!string.Equals(request.Uid, snapshot.Uid, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Artifact snapshot UID does not match the host request.");
        }
    }
}
