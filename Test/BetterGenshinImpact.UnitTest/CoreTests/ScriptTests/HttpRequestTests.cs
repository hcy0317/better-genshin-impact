using BetterGenshinImpact.Core.Script.Dependence;

namespace BetterGenshinImpact.UnitTest.CoreTests.ScriptTests;

public class HttpRequestTests
{
    [Fact]
    public async Task ScriptEvidenceBridgeCannotPersistArbitraryArguments()
    {
        var saved = new List<BetterGenshinImpact.GameTask.Common.DiagnosticEvidence>();
        await using var scope = new BetterGenshinImpact.GameTask.Common.DiagnosticEvidenceScope((item, _) =>
        { saved.Add(item); return Task.CompletedTask; });
        using var frame = new BetterGenshinImpact.GameTask.Model.Area.ImageRegion(
            new OpenCvSharp.Mat(2, 2, OpenCvSharp.MatType.CV_8UC3, OpenCvSharp.Scalar.Black), 0, 0)
        { FrameStamp = new Fischless.GameCapture.CaptureFrameSource().Next() };
        scope.ObserveExistingFrame(frame);
        GlobalMethod.RequestEvidenceWindow("token=secret", "Authorization: secret", "{\"body\":\"secret\"}");
        await scope.DisposeAsync();
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(Assert.Single(saved));
        Assert.DoesNotContain("secret", json);
        Assert.Contains("redacted", json);
    }

    [Fact]
    public async Task RepeatedBusyResponsesShareIncidentButKeepActualRequestIds()
    {
        var saved = new List<BetterGenshinImpact.GameTask.Common.DiagnosticEvidence>();
        await using var scope = new BetterGenshinImpact.GameTask.Common.DiagnosticEvidenceScope((item, _) =>
        { saved.Add(item); return Task.CompletedTask; });
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var source = new Fischless.GameCapture.CaptureFrameSource(clock);
        using var frame = new BetterGenshinImpact.GameTask.Model.Area.ImageRegion(
            new OpenCvSharp.Mat(2, 2, OpenCvSharp.MatType.CV_8UC3, OpenCvSharp.Scalar.Black), 0, 0);
        var first = Guid.NewGuid();
        var last = Guid.NewGuid();
        foreach (var request in new[] { first, last })
        {
            frame.FrameStamp = source.Next();
            scope.ObserveExistingFrame(frame);
            Http.RecordResponseEvidence(request, "http://localhost/api?token=secret", new Http.HttpReponse
            { status_code = 200, body = "{\"code\":500,\"msg\":\"系统繁忙\"}" }, 10,
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        await scope.DisposeAsync();
        Assert.Single(saved.Select(item => item.Window!.WindowId).Distinct());
        Assert.Contains(saved, item => item.Detail.Contains(first.ToString()));
        Assert.Contains(saved, item => item.Detail.Contains(last.ToString()));
        Assert.DoesNotContain(saved, item => item.Window!.Occurrence == "change");
    }

    [Theory]
    [InlineData("https://example.com/api/token/secret")]
    [InlineData("https://example.com/users/private-user-123/share/key")]
    [InlineData("https://example.com/%73%65%63%72%65%74?key=secret")]
    public void DiagnosticAddressDoesNotDiscloseDynamicOrEncodedPathSegments(string url)
    {
        Assert.Equal("https://example.com/[redacted-path]", Http.SafeAddress(url));
    }

    [Fact]
    public async Task BusyResponseEvidenceContainsCodesButNeverTokensOrResponsePayload()
    {
        var saved = new List<BetterGenshinImpact.GameTask.Common.DiagnosticEvidence>();
        await using var scope = new BetterGenshinImpact.GameTask.Common.DiagnosticEvidenceScope((item, _) =>
        { saved.Add(item); return Task.CompletedTask; });
        using var image = new BetterGenshinImpact.GameTask.Model.Area.ImageRegion(
            new OpenCvSharp.Mat(2, 2, OpenCvSharp.MatType.CV_8UC3, OpenCvSharp.Scalar.Black), 0, 0)
        { FrameStamp = new Fischless.GameCapture.CaptureFrameSource().Next() };
        scope.ObserveExistingFrame(image);
        Http.RecordResponseEvidence(Guid.NewGuid(), "https://user:secret@example.com/api?token=secret", new Http.HttpReponse
        { status_code = 200, body = "{\"retcode\":-1000,\"message\":\"系统繁忙\",\"token\":\"secret\"}" }, 123,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        await scope.DisposeAsync();
        var evidence = Assert.Single(saved);
        Assert.Contains("businessCode=-1000", evidence.Detail);
        Assert.Contains("httpStatus=200", evidence.Detail);
        Assert.Contains("elapsedMs=123", evidence.Detail);
        Assert.Contains("lockHolder=unknown", evidence.Detail);
        Assert.DoesNotContain("secret", evidence.Detail);
        Assert.DoesNotContain("token", evidence.Detail);
    }

    [Fact]
    public async Task CancellationAlsoInterruptsBodyReadingAndTheRequestDeadlineIsBounded()
    {
        using var body = new WaitingBody();
        using var client = new HttpClient(new BodyHandler(body));
        using var request = Http.CreateRequest("GET", "http://localhost/body", null, null);
        using var cts = new CancellationTokenSource();
        var task = Http.SendAsync(client, request, cts.Token, TimeSpan.FromSeconds(30));
        await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task.WaitAsync(TimeSpan.FromSeconds(2)));

        using var waiting = new HttpClient(new WaitingHandler());
        using var expiredRequest = Http.CreateRequest("GET", "http://localhost/deadline", null, null);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Http.SendAsync(waiting, expiredRequest, default, TimeSpan.FromMilliseconds(50)).WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void EndpointDiagnosticsOmitUserInfoQueryAndFragmentAndHeadersRemainPerRequest()
    {
        Assert.Equal("http://localhost/[redacted-path]", Http.SafeAddress("http://private:password@localhost/path?token=secret#fragment"));
        using var first = Http.CreateRequest("POST", "http://localhost/path", "{}", "{\"X-Token\":\"first\"}");
        using var second = Http.CreateRequest("POST", "http://localhost/path", "{}", null);
        Assert.Equal("first", Assert.Single(first.Headers.GetValues("X-Token")));
        Assert.False(second.Headers.Contains("X-Token"));
    }

    private sealed class BodyHandler(HttpContent content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content });
    }

    private sealed class WaitingBody : HttpContent
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
            => throw new InvalidOperationException("body read did not receive cancellation");
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context, CancellationToken ct)
        {
            Entered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    [Fact]
    public async Task ScriptCancellationReachesAnOutstandingRequestWithoutWaitingForClientTimeout()
    {
        using var handler = new WaitingHandler();
        using var client = new HttpClient(handler);
        using var request = Http.CreateRequest("POST", "http://localhost/test", "{}", null);
        using var cts = new CancellationTokenSource();
        var send = Http.SendAsync(client, request, cts.Token, TimeSpan.FromMinutes(1));
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await send.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    private sealed class WaitingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Entered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new(System.Net.HttpStatusCode.OK);
        }
    }

    [Theory]
    [InlineData("{\"\":\"required-token\"}")]
    [InlineData("not json")]
    public void InvalidHeadersAreNotSilentlyDropped(string headers)
    {
        Assert.ThrowsAny<ArgumentException>(() => Http.CreateRequest("GET", "http://localhost/test", null, headers));
    }

    [Fact]
    public void EmptyOptionalHeaderDoesNotAbortValidRequest()
    {
        using var request = Http.CreateRequest("POST", "http://localhost/test", "{}",
            """{"":"","Content-Type":"application/json","X-Test":"value"}""");
        Assert.Equal("value", Assert.Single(request.Headers.GetValues("X-Test")));
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
    }
}
