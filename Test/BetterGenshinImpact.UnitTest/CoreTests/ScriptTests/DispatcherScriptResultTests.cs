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
    public void InventoryConfigSupportsAutomaticPagesAndSortWithoutInitializingTheApp()
    {
        using var engine = new V8ScriptEngine();
        var config = Assert.IsAssignableFrom<ScriptObject>(engine.Evaluate("({itemNames:['fixture'],stopByItemSort:true})"));
        var param = Dispatcher.ParseCountInventoryItemParam(config);
        param.Validate();
        Assert.Null(param.GridScreenName);
        Assert.True(param.StopByItemSort);
        param.UseDefaultIconRecognitionMode(ItemIconRecognitionMode.Item);
        Assert.Equal(ItemIconRecognitionMode.Item, param.IconRecognitionMode);
        param.IconRecognitionMode = ItemIconRecognitionMode.GridIcon;
        param.UseDefaultIconRecognitionMode(ItemIconRecognitionMode.Item);
        Assert.Equal(ItemIconRecognitionMode.GridIcon, param.IconRecognitionMode);
    }

    [Fact]
    public void InventoryLegacySingleItemContractIsStillValidAndCannotBeMixedWithMultiItem()
    {
        using var engine = new V8ScriptEngine();
        var config = Assert.IsAssignableFrom<ScriptObject>(engine.Evaluate("({gridScreenName:'PreciousItems',itemName:'fixture'})"));
        var param = Dispatcher.ParseCountInventoryItemParam(config);
        param.Validate();
        Assert.Equal("fixture", param.ItemName);
        param.ItemNames.Add("other");
        Assert.Throws<ArgumentException>(param.Validate);
    }

    [Fact]
    public void VerifiedInventorySnapshotPreservesKnownUnknownAndConfirmedAbsentCounts()
    {
        var snapshot = new InventoryCountSnapshot(new Dictionary<string, int>
        { ["脆弱树脂"] = 38, ["须臾树脂"] = 0 }, true, "verified-top-to-bottom", 3, 0);
        var value = Assert.IsAssignableFrom<IDictionary<string, object>>(Dispatcher.ToScriptInventoryResult(snapshot));
        Assert.Equal("bgi.inventory-count.v1", value["schema"]);
        Assert.Equal(true, value["coverageComplete"]);
        var counts = Assert.IsAssignableFrom<IDictionary<string, object>>(value["counts"]);
        Assert.Equal(0, counts["须臾树脂"]);
        Assert.Equal(38, counts["脆弱树脂"]);
    }

    [Fact]
    public void InventoryEvidenceMustBeExplicitAndIsRestrictedToTheVerifiedPage()
    {
        using var engine = new V8ScriptEngine();
        var config = Assert.IsAssignableFrom<ScriptObject>(engine.Evaluate(
            "({gridScreenName:'PreciousItems',itemNames:['须臾树脂','脆弱树脂'],iconRecognitionMode:'Item',includeScanEvidence:true})"));
        var param = Dispatcher.ParseCountInventoryItemParam(config);
        Assert.True(param.IncludeScanEvidence);
        param.Validate();
        param.GridScreenName = GridScreenName.Food;
        Assert.Throws<ArgumentException>(param.Validate);
    }

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
