using BetterGenshinImpact.Service.Instance;

namespace BetterGenshinImpact.UnitTest.ServiceTests;

public class InstanceRegistrationWaiterTests
{
    [Fact]
    public async Task TemporaryUnregistrationBeforeDispatchWaitsForFormalConnection()
    {
        var formalConnection = new object();
        var polls = 0;
        var registration = await InstanceRegistrationWaiter.WaitAsync(
            () => ++polls < 3 ? null : formalConnection,
            TimeSpan.FromSeconds(2), "missing registration", CancellationToken.None);
        Assert.Same(formalConnection, registration);
        Assert.Equal(3, polls);
    }

    [Fact]
    public async Task MissingConnectionFailsWithinTheBudget()
    {
        var error = await Assert.ThrowsAsync<TimeoutException>(() => InstanceRegistrationWaiter.WaitAsync<object>(
            () => null, TimeSpan.FromMilliseconds(20), "missing registration", CancellationToken.None));
        Assert.Equal("missing registration", error.Message);
    }

    [Fact]
    public async Task CancellationPreventsWaitingOrDispatching()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var reads = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InstanceRegistrationWaiter.WaitAsync(
            () => { reads++; return new object(); }, TimeSpan.FromSeconds(2), "missing registration", cancellation.Token));
        Assert.Equal(0, reads);
    }
}
