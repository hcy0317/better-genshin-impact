using BetterGenshinImpact.Core.Script.Dependence;

namespace BetterGenshinImpact.UnitTest.CoreTests.ScriptTests;

public class HttpRequestTests
{
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
