using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BetterGenshinImpact.GameTask.ArtifactAnalysis;

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
            new ArtifactEquipmentJobRunner(client).RunAsync(request, "one-time-token", CancellationToken.None));

        Assert.NotNull(handler.Progress);
        Assert.Equal("rejected", handler.Progress.Value.GetProperty("status").GetString());
        Assert.Contains("binding", handler.Progress.Value.GetProperty("message").GetString());
        Assert.False(handler.ProgressTokenCancelled);
    }

    private sealed class ClaimHandler : HttpMessageHandler
    {
        public JsonElement? Progress { get; private set; }
        public bool ProgressTokenCancelled { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/claim"))
            {
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
