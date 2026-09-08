using Microsoft.Extensions.Logging;
using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.GameTask.ArtifactAnalysis;

public interface IArtifactEquipmentJobRunner
{
    Task RunAsync(ArtifactHostRequest request, string token, CancellationToken ct);
}

public sealed class ArtifactEquipmentJobRunner(HttpClient client) : IArtifactEquipmentJobRunner
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    public async Task RunAsync(ArtifactHostRequest request, string token, CancellationToken ct)
    {
        var path = $"artifacts/optimizer/host/plans/{Uri.EscapeDataString(request.JobId)}" +
                   $"?uid={Uri.EscapeDataString(request.Uid)}&requestToken={Uri.EscapeDataString(token)}";
        string Endpoint(string action) => path.Replace("?", "/" + action + "?");
        using var claim = await client.PostAsync(Endpoint("claim"), null, ct);
        var plan = await Read<ArtifactEquipmentPlanDto>(claim, ct);
        if (!plan.Confirmed || plan.Id != request.JobId || plan.Uid != request.Uid || plan.Digest != request.NativePlanDigest
            || plan.InventoryCount != request.SourceArtifactCount)
            throw new InvalidOperationException("Equipment confirmation binding does not match the host request.");
        var task = new EquipmentTask(plan, client, Endpoint, _json, ct);
        await new TaskRunner().RunSoloTaskAsync(task, propagateExceptions: true);
        if (task.Result?.Status != "completed") throw new InvalidOperationException(task.Result?.Message ?? "Equipment execution requires a new observation.");
    }
    private async Task<T> Read<T>(HttpResponseMessage response, CancellationToken ct)
    {
        response.EnsureSuccessStatusCode();
        var envelope = await response.Content.ReadFromJsonAsync<Envelope<T>>(_json, ct);
        if (envelope is not { Code: 200, Data: not null }) throw new InvalidOperationException(envelope?.Message ?? "Equipment service returned no data.");
        return envelope.Data;
    }
    private sealed record Envelope<T>(int Code, string? Message, T? Data);
    private sealed class EquipmentTask(ArtifactEquipmentPlanDto plan, HttpClient client, Func<string, string> endpoint,
        JsonSerializerOptions json, CancellationToken external) : ISoloTask
    {
        public string Name => "已确认圣遗物穿戴";
        public ArtifactEquipmentResultDto? Result { get; private set; }
        public async Task Start(CancellationToken taskToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(taskToken, external);
            using var monitor = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
            var watching = PollCancellation(monitor.Token, linked);
            try
            {
                Result = await new ArtifactEquipmentExecution().RunAsync(plan, new ArtifactEquipmentGamePort(App.GetLogger<ArtifactEquipmentGamePort>()),
                    async state =>
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        using var response = await client.PostAsJsonAsync(endpoint("progress"), state, json, timeout.Token);
                        response.EnsureSuccessStatusCode();
                        var ack = await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(json, timeout.Token);
                        if (ack is null || ack.Code != 200) throw new InvalidOperationException(ack?.Message ?? "Equipment checkpoint failed.");
                        if (ack.Data.TryGetProperty("cancelRequested", out var cancel) && cancel.GetBoolean()) linked.Cancel();
                    }, linked.Token);
            }
            finally { monitor.Cancel(); try { await watching; } catch (OperationCanceledException) { } }
        }
        private async Task PollCancellation(CancellationToken ct, CancellationTokenSource owner)
        {
            var failures = 0;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    using var response = await client.PostAsync(endpoint("status"), null, timeout.Token);
                    response.EnsureSuccessStatusCode();
                    var ack = await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(json, timeout.Token);
                    if (ack is null || ack.Code != 200) throw new InvalidOperationException("Equipment ownership status unavailable.");
                    if (ack.Data.TryGetProperty("cancelRequested", out var cancel) && cancel.GetBoolean()) { owner.Cancel(); return; }
                    failures = 0;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch { if (++failures >= 3) { owner.Cancel(); return; } }
                await Task.Delay(1000, ct);
            }
        }
    }
}
