using BetterGenshinImpact.Helpers;

namespace BetterGenshinImpact.UnitTest.CoreTests;

public class ApplicationHostBootstrapGuardTests
{
    [Fact]
    public void ApplicationHostDoesNotAllowEarlyFieldInitialization()
    {
        // Metadata only: never invoke the application's constructor as a test probe.
        Assert.Equal(0, (int)(typeof(App).Attributes & System.Reflection.TypeAttributes.BeforeFieldInit));
    }

    [Fact]
    public void TestModuleProhibitsHostBeforeAnyTestRuns()
    {
        Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
        var error = Assert.Throws<InvalidOperationException>(ApplicationHostBootstrapGuard.EnsureAllowed);
        Assert.Contains("Inject isolated dependencies", error.Message);
    }

    [Fact]
    public void RepeatedProhibitionCannotEnableApplicationStartup()
    {
        ApplicationHostBootstrapGuard.ProhibitForCurrentProcess();
        ApplicationHostBootstrapGuard.ProhibitForCurrentProcess();
        Assert.Throws<InvalidOperationException>(ApplicationHostBootstrapGuard.EnsureAllowed);
    }
}
