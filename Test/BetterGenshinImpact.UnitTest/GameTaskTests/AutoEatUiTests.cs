using BetterGenshinImpact.GameTask.AutoEat;
using BetterGenshinImpact.GameTask.Common.Ui;

namespace BetterGenshinImpact.UnitTest.GameTaskTests;

public class AutoEatUiTests
{
    [Fact]
    public void GadgetInputRequiresUnobstructedHud()
    {
        Assert.True(AutoEatTrigger.CanUseGadget(new(1) { MainHud = true }));
        Assert.False(AutoEatTrigger.CanUseGadget(new(1)));
        Assert.False(AutoEatTrigger.CanUseGadget(new(1) { MainHud = true, Prompt = true }));
        Assert.False(AutoEatTrigger.CanUseGadget(new(1) { MainHud = true, Revive = true }));
        Assert.False(AutoEatTrigger.CanUseGadget(new(1) { MainHud = true, FullPartyDefeat = true }));
        Assert.False(AutoEatTrigger.CanUseGadget(new(1) { MainHud = true, BigMap = true }));
    }
}
