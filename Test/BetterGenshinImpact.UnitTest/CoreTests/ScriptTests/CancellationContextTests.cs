using BetterGenshinImpact.Core.Script;

namespace BetterGenshinImpact.UnitTest.CoreTests.ScriptTests;

public class CancellationContextTests
{
    [Fact]
    public void GetTokenOrNoneReturnsNonCanceledFallbackAfterClear()
    {
        var context = CancellationContext.Instance;
        context.Set();
        context.Clear();

        var token = context.GetTokenOrNone();

        Assert.Equal(CancellationToken.None, token);
        Assert.False(token.IsCancellationRequested);
    }
}
