using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using Fischless.GameCapture;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class PathingPrimitiveInputTests
{
    [Fact]
    public void CannonPermissionBelongsToOneObservedSourceAndDoesNotGrantNamedOrGenericConfirmation()
    {
        var source = new CaptureFrameSource().Next();
        var observation = new CannonUiObservation(source, 4, true, "神居岛崩炮", "Enter发射");
        var fire = new CombatCommand(CombatScriptParser.CurrentAvatarName, "keypress(RETURN)");
        Assert.True(PathingPrimitiveInput.Supports(fire, observation, source));
        Assert.False(PathingPrimitiveInput.Supports(fire));
        Assert.False(PathingPrimitiveInput.Supports(fire, observation, new CaptureFrameSource().Next()));
        Assert.False(PathingPrimitiveInput.Supports(fire, default, source));
        Assert.False(PathingPrimitiveInput.Supports(new("钟离", "keypress(RETURN)"), observation, source));
        Assert.False(PathingPrimitiveInput.Supports(fire, observation with { Title = "确认购买" }, source));
        Assert.False(PathingPrimitiveInput.Supports(fire, observation with { FirePrompt = null }, source));
        Assert.False(PathingPrimitiveInput.Supports(fire, observation with { DirectionKeys = 3 }, source));
        Assert.False(PathingPrimitiveInput.Supports(fire, observation with { ExitControl = false }, source));
    }

    [Theory]
    [InlineData("keypress(f)", true)]
    [InlineData("keypress(ESCAPE)", true)]
    [InlineData("keypress(VK_SPACE)", true)]
    [InlineData("keypress(SPACE)", true)]
    [InlineData("w(0.5)", true)]
    [InlineData("wait(3)", true)]
    [InlineData("keypress(e)", false)]
    [InlineData("keypress(q)", false)]
    [InlineData("keypress(RETURN)", false)]
    [InlineData("keypress(VK_LBUTTON)", false)]
    [InlineData("keydown(w)", false)]
    [InlineData("e(hold)", false)]
    public void AnonymousNavigationNeverGrantsSkillConsumptionMouseOrHeldInput(string text, bool permitted)
    {
        Assert.Equal(permitted, PathingPrimitiveInput.Supports(new(CombatScriptParser.CurrentAvatarName, text)));
        Assert.False(PathingPrimitiveInput.Supports(new("钟离", text)));
    }
}
