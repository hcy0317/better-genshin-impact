using BetterGenshinImpact.Core.Script.Dependence;
using System.Collections.Generic;
using System.Dynamic;

namespace BetterGenshinImpact.UnitTest.CoreTests.ScriptTests;

public class DispatcherScriptResultTests
{
    [Fact]
    public void RewardSummary_ShouldBeExposedAsScriptEnumerableObject()
    {
        var result = Dispatcher.ToScriptDictionary(new Dictionary<string, int>
        {
            ["「公平」的教导"] = 4,
            ["「公平」的指引"] = 2
        });

        var expando = Assert.IsType<ExpandoObject>(result);
        var values = Assert.IsAssignableFrom<IDictionary<string, object>>(expando);
        Assert.Equal(4, values["「公平」的教导"]);
        Assert.Equal(2, values["「公平」的指引"]);
    }
}
