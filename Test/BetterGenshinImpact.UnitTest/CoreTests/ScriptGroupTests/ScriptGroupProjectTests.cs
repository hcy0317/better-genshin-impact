using BetterGenshinImpact.Core.Script.Group;
using BetterGenshinImpact.Core.Script.Project;
using System.Dynamic;

namespace BetterGenshinImpact.UnitTest.CoreTests.ScriptGroupTests;

public class ScriptGroupProjectTests
{
    [Fact]
    public async Task Run_ShouldPreserveSavedSelectionsMissingFromDynamicSettings()
    {
        var fixture = Directory.CreateTempSubdirectory("BetterGI-SavedSelections-");
        try
        {
            File.WriteAllText(Path.Combine(fixture.FullName, "manifest.json"),
                """{"name":"设置保持测试","version":"1.0","main":"main.js","settings_ui":"settings.json"}""");
            File.WriteAllText(Path.Combine(fixture.FullName, "settings.json"),
                """[{"name":"selectFoodAndAlchemy","type":"multi-checkbox","options":["晶蝶"]}]""");
            var mainPath = Path.Combine(fixture.FullName, "main.js");
            File.WriteAllText(mainPath, "// The test never executes game scripts.");
            var project = new ScriptGroupProject(new ScriptProject(fixture.FullName))
            {
                JsScriptSettingsObject = new ExpandoObject()
            };
            var settings = (IDictionary<string, object?>)project.JsScriptSettingsObject;
            settings["selectFoodAndAlchemy"] = new List<object> { "甜甜花", "薄荷", "兽肉" };

            // Stop at the script-file boundary, before constructing a host or issuing game input.
            File.Delete(mainPath);
            await Assert.ThrowsAsync<FileNotFoundException>(() => project.Run());

            Assert.Equal(new[] { "甜甜花", "薄荷", "兽肉" },
                ((System.Collections.IEnumerable)settings["selectFoodAndAlchemy"]!).Cast<string>());
        }
        finally
        {
            fixture.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveJsScriptProjectName_ShouldUseTrimmedCustomName()
    {
        var name = ScriptGroupProject.ResolveJsScriptProjectName("默认脚本名", "  自定义采集名  ");

        Assert.Equal("自定义采集名", name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveJsScriptProjectName_ShouldFallbackToDefaultNameWhenCustomNameIsBlank(string? customName)
    {
        var name = ScriptGroupProject.ResolveJsScriptProjectName("默认脚本名", customName);

        Assert.Equal("默认脚本名", name);
    }

    [Fact]
    public void RenameDisplayName_ShouldTrimAndUpdateName()
    {
        var project = new ScriptGroupProject("默认脚本名", "ScriptFolder", "Javascript");

        var renamed = project.RenameDisplayName("  新脚本名  ");

        Assert.True(renamed);
        Assert.Equal("新脚本名", project.Name);
    }

    [Fact]
    public void RenameDisplayName_ShouldNotifyNameChange()
    {
        var project = new ScriptGroupProject("默认脚本名", "ScriptFolder", "Javascript");
        var notified = false;
        project.PropertyChanged += (_, args) => notified = args.PropertyName == nameof(ScriptGroupProject.Name);

        project.RenameDisplayName("新脚本名");

        Assert.True(notified);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RenameDisplayName_ShouldKeepNameWhenNewNameIsBlank(string? newName)
    {
        var project = new ScriptGroupProject("默认脚本名", "ScriptFolder", "Javascript");

        var renamed = project.RenameDisplayName(newName);

        Assert.False(renamed);
        Assert.Equal("默认脚本名", project.Name);
    }
}
