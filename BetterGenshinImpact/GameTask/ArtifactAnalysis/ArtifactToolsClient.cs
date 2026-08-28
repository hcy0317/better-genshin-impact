using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.GameTask.ArtifactAnalysis;

public sealed class ArtifactToolsClient(HttpClient httpClient) : IArtifactToolsClient
{
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter<ArtifactPreflightStatus>(JsonNamingPolicy.SnakeCaseUpper) }
    };

    public async Task ClaimAsync(
        string jobId,
        string uid,
        ArtifactHostOperation operation,
        string requestToken,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync(
            $"artifacts/host/jobs/{Uri.EscapeDataString(jobId)}/claim" +
            $"?uid={Uri.EscapeDataString(uid)}" +
            $"&operation={OperationKey(operation)}" +
            $"&requestToken={Uri.EscapeDataString(requestToken)}",
            null,
            cancellationToken);
        await ReadAckAsync(response, cancellationToken);
    }

    public async Task SubmitSnapshotAsync(
        string jobId,
        string requestToken,
        ArtifactSnapshotDto snapshot,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(
            HostPath(jobId, "snapshot", requestToken), snapshot, _jsonOptions, cancellationToken);
        await ReadEnvelopeAsync<JsonElement>(response, cancellationToken);
    }

    public async Task<ArtifactPreflightResult> PreflightAsync(
        string jobId,
        string requestToken,
        ArtifactExecutionObservationDto observation,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(
            HostPath(jobId, "preflight", requestToken), observation, _jsonOptions, cancellationToken);
        var data = await ReadEnvelopeAsync<ArtifactPreflightEnvelope>(response, cancellationToken);
        return data.Preflight;
    }

    public async Task<ArtifactNativeSyncPlanDto> GetNativeSyncPlanAsync(
        string jobId,
        string requestToken,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync(
            $"artifacts/host/native-sync/plan?jobId={Uri.EscapeDataString(jobId)}&requestToken={Uri.EscapeDataString(requestToken)}",
            null,
            cancellationToken);
        return await ReadEnvelopeAsync<ArtifactNativeSyncPlanDto>(response, cancellationToken);
    }

    public async Task SubmitCharacterRosterAsync(
        string jobId,
        string requestToken,
        ArtifactCharacterRosterDto roster,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(
            HostPath(jobId, "characters", requestToken), roster, _jsonOptions, cancellationToken);
        await ReadEnvelopeAsync<JsonElement>(response, cancellationToken);
    }

    public async Task ReportCompletionAsync(
        string jobId,
        string requestToken,
        ArtifactHostOperation operation,
        bool success,
        string? message,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(
            $"artifacts/host/jobs/{Uri.EscapeDataString(jobId)}/completion" +
            $"?requestToken={Uri.EscapeDataString(requestToken)}",
            new ArtifactHostCompletionDto(OperationKey(operation), success, message),
            _jsonOptions,
            cancellationToken);
        await ReadEnvelopeAsync<JsonElement>(response, cancellationToken);
    }

    private static string HostPath(string jobId, string operation, string requestToken) =>
        $"artifacts/host/jobs/{Uri.EscapeDataString(jobId)}/{operation}?requestToken={Uri.EscapeDataString(requestToken)}";

    private static string OperationKey(ArtifactHostOperation operation) => operation switch
    {
        ArtifactHostOperation.Analyze => "ANALYZE",
        ArtifactHostOperation.ScanCharacterRoster => "SCAN_CHARACTER_ROSTER",
        ArtifactHostOperation.ExecuteLockPlan => "EXECUTE_LOCK_PLAN",
        ArtifactHostOperation.RebuildNativePlans => "REBUILD_NATIVE_PLANS",
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
    };

    private async Task<T> ReadEnvelopeAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        response.EnsureSuccessStatusCode();
        var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<T>>(
            _jsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Artifact tools response is empty.");
        if (envelope.Code != 200 || envelope.Data is null)
        {
            throw new InvalidOperationException(
                $"Artifact tools request failed: {envelope.Message ?? "unknown error"}");
        }
        return envelope.Data;
    }

    private async Task ReadAckAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        response.EnsureSuccessStatusCode();
        var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<JsonElement>>(
            _jsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Artifact tools response is empty.");
        if (envelope.Code != 200)
        {
            throw new InvalidOperationException(
                $"Artifact tools request failed: {envelope.Message ?? "unknown error"}");
        }
    }

    private sealed record ApiEnvelope<T>(int Code, string? Message, T? Data);
    private sealed record ArtifactPreflightEnvelope(ArtifactPreflightResult Preflight);
    private sealed record ArtifactHostCompletionDto(string Operation, bool Success, string? Message);
}
