using BetterGenshinImpact.Core.Script.Dependence;
using BetterGenshinImpact.GameTask.Common.Job;
using BetterGenshinImpact.GameTask.Model.GameUI;
using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;
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

    [Fact]
    public void CountInventoryItemConfig_ShouldSelectItemRecognizerWhenRequested()
    {
        using var engine = new V8ScriptEngine();
        var config = Assert.IsAssignableFrom<ScriptObject>(engine.Evaluate(
            "({ gridScreenName: 'CharacterDevelopmentItems', itemNames: ['狮牙斗士的理想'], iconRecognitionMode: 'Item' })"));

        var param = Dispatcher.ParseCountInventoryItemParam(config);

        Assert.Equal(GridScreenName.CharacterDevelopmentItems, param.GridScreenName);
        Assert.Equal(["狮牙斗士的理想"], param.ItemNames);
        Assert.Equal(ItemIconRecognitionMode.Item, param.IconRecognitionMode);
    }
}
