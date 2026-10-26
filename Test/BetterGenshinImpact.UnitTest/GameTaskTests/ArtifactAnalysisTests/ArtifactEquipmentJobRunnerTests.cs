using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BetterGenshinImpact.GameTask.ArtifactAnalysis;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.ArtifactAnalysisTests;

public class ArtifactEquipmentJobRunnerTests
{
    [Fact]
    public async Task ClaimedPlanBindingFailureReportsTerminalStateWithoutStartingGame()
    {
        using var handler = new ClaimHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/bgi/") };
        var request = new ArtifactHostRequest(1, "equipment", "100000001", "plan-1",
            ArtifactHostOperation.ExecuteEquipPlan, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1),
            0, null, null, "expected", null, null, null, null, null);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ArtifactEquipmentJobRunner(client, new IsolatedArtifactHostTaskRunner()).RunAsync(request, "one-time-token", CancellationToken.None));

        Assert.NotNull(handler.Progress);
        Assert.Equal("rejected", handler.Progress.Value.GetProperty("status").GetString());
        Assert.Contains("binding", handler.Progress.Value.GetProperty("message").GetString());
        Assert.False(handler.ProgressTokenCancelled);
    }

    [Fact]
    public async Task QueuedEquipmentCancellationReportsCancelledWithoutClaimOrGameStartup()
    {
        using var semaphore = new SemaphoreSlim(0, 1);
        using var cancelled = new CancellationTokenSource();
        using var handler = new ClaimHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/bgi/") };
        var runner = new IsolatedArtifactHostTaskRunner(semaphore);
        var waiting = new ArtifactEquipmentJobRunner(client, runner).RunAsync(Request(), "token", cancelled.Token);
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        Assert.Equal(0, handler.Claims);
        Assert.Equal(0, runner.Starts);
        Assert.Equal(0, semaphore.CurrentCount);
        Assert.Equal("cancelled", handler.Progress!.Value.GetProperty("status").GetString());
        Assert.False(handler.ProgressTokenCancelled);
        semaphore.Release();
    }

    [Fact]
    public async Task EquipmentExpiryAfterWaitingPreventsClaimAndGameStartup()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var handler = new ClaimHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/bgi/") };
        var runner = new IsolatedArtifactHostTaskRunner
        {
            BeforePrepare = () => { clock.Advance(TimeSpan.FromMinutes(10)); return Task.CompletedTask; }
        };

        await Assert.ThrowsAsync<ArtifactHostRequestExpiredException>(() =>
            new ArtifactEquipmentJobRunner(client, runner, clock).RunAsync(Request(), "token", CancellationToken.None));

        Assert.Equal(0, handler.Claims);
        Assert.Equal(0, runner.Starts);
        Assert.Equal("rejected", handler.Progress!.Value.GetProperty("status").GetString());
    }

    private static ArtifactHostRequest Request() => new(1, "artifact-analysis", "100000001", "plan-1",
        ArtifactHostOperation.ExecuteEquipPlan, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1),
        0, null, null, "expected", null, null, null, null, null);

    private sealed class ClaimHandler : HttpMessageHandler
    {
        public JsonElement? Progress { get; private set; }
        public bool ProgressTokenCancelled { get; private set; }
        public int Claims { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/claim"))
            {
                Claims++;
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new
                {
                    code = 200, data = new ArtifactEquipmentPlanDto("plan-1", "100000001", "wrong", true, 0, [], [], [])
                }) };
            }
            Assert.EndsWith("/progress", request.RequestUri.AbsolutePath);
            ProgressTokenCancelled = ct.IsCancellationRequested;
            Progress = await request.Content!.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { code = 200, data = new { cancelRequested = false } }) };
        }
    }
}
