using System.Net;
using System.Text;
using BetterGenshinImpact.GameTask.ArtifactAnalysis;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.ArtifactAnalysisTests;

public class ArtifactToolsClientTests
{
    [Fact]
    public async Task Claim_AcceptsA200AcknowledgementWithNullData()
    {
        var handler = new RecordingHandler("""
        {"code":200,"message":"ok","data":null}
        """);
        var client = new ArtifactToolsClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:18081/bgi/") });

        await client.ClaimAsync(
            "job-1", "102550550", ArtifactHostOperation.ScanCharacterRoster,
            "request-token", CancellationToken.None);

        Assert.Contains("/claim?", handler.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task CharacterRoster_UsesBoundHostEndpointAndCamelCasePayload()
    {
        var handler = new RecordingHandler("""
        {"code":200,"message":"ok","data":{"characterCount":1}}
        """);
        var client = new ArtifactToolsClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:18081/bgi/") });

        await client.SubmitCharacterRosterAsync(
            "job-roster", "request-token",
            new ArtifactCharacterRosterDto(
                "102550550", [new ArtifactCharacterRosterEntryDto("Furina", 90, true)]),
            CancellationToken.None);

        Assert.Equal(
            "/bgi/artifacts/host/jobs/job-roster/characters?requestToken=request-token",
            handler.RequestUri?.PathAndQuery);
        Assert.Contains("\"characterKey\":\"Furina\"", handler.RequestBody);
        Assert.Contains("\"favorite\":true", handler.RequestBody);
    }

    [Fact]
    public async Task Preflight_UsesOneTimeTokenAndParsesActions()
    {
        var handler = new RecordingHandler("""
        {"code":200,"message":"ok","data":{"preflight":{"status":"READY","actions":[{"scanIndex":4,"expectedLocked":false,"desiredLocked":true,"expectedFingerprint":"hash"}],"reasons":[]}}}
        """);
        var client = new ArtifactToolsClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:18081/bgi/") });

        var result = await client.PreflightAsync(
            "job-1", "request-token",
            new ArtifactExecutionObservationDto("102550550", 0, [], null, CountOnly: true),
            CancellationToken.None);

        Assert.Equal(ArtifactPreflightStatus.Ready, result.Status);
        Assert.Equal(4, Assert.Single(result.Actions).ScanIndex);
        Assert.Equal(
            "/bgi/artifacts/host/jobs/job-1/preflight?requestToken=request-token",
            handler.RequestUri?.PathAndQuery);
        Assert.Contains("\"countOnly\":true", handler.RequestBody);
    }

    [Fact]
    public async Task BusinessOrHttpFailure_IsFailClosed()
    {
        var business = new ArtifactToolsClient(new HttpClient(new RecordingHandler(
            "{\"code\":500,\"message\":\"rejected\",\"data\":null}"))
            { BaseAddress = new Uri("http://127.0.0.1:18081/bgi/") });
        await Assert.ThrowsAsync<InvalidOperationException>(() => business.SubmitSnapshotAsync(
            "job-1", "token", Snapshot(), CancellationToken.None));

        var http = new ArtifactToolsClient(new HttpClient(new RecordingHandler(
            "{}", HttpStatusCode.ServiceUnavailable))
            { BaseAddress = new Uri("http://127.0.0.1:18081/bgi/") });
        await Assert.ThrowsAsync<HttpRequestException>(() => http.SubmitSnapshotAsync(
            "job-1", "token", Snapshot(), CancellationToken.None));
    }

    private static ArtifactSnapshotDto Snapshot() => ArtifactSnapshotDto.Create(
        "102550550", "scan", "CURRENT_INVENTORY_ORDER", "genshin-7.0", []);

    private sealed class RecordingHandler(string json, HttpStatusCode status = HttpStatusCode.OK)
        : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            if (request.Content is not null)
            {
                RequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }
}
