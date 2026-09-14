using BetterGenshinImpact.Core.Script.Dependence;

namespace BetterGenshinImpact.UnitTest.CoreTests.ScriptTests;

public class HttpRequestTests
{
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
        Assert.Equal("http://localhost/path", Http.SafeAddress("http://private:password@localhost/path?token=secret#fragment"));
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
