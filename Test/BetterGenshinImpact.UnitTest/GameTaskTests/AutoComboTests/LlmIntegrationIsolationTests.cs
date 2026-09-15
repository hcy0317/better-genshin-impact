using System.Reflection;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoComboTests;

public class LlmIntegrationIsolationTests
{
    [Fact]
    public void RealLlmTestRequiresExplicitOptIn()
    {
        var method = typeof(AutoComboBuildTaskLlmTests)
            .GetMethod(nameof(AutoComboBuildTaskLlmTests.BuildTree_FromKnownTeam_PrintsTree))!;
        var fact = method.GetCustomAttribute<FactAttribute>()!;
        if (ExternalLlmFactAttribute.IsEnabled)
            Assert.Null(fact.Skip);
        else
        {
            Assert.False(string.IsNullOrWhiteSpace(fact.Skip));
            var fixture = new MainProjectConfigFixture();
            Assert.Null(fixture.ConfigPath);
            Assert.Null(fixture.AutoComboBuildConfig);
        }
    }
}
