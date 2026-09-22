using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using Vanara.PInvoke;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatHeldEMappingTests
{
    [Theory]
    [InlineData(User32.VK.VK_1)]
    [InlineData(User32.VK.VK_2)]
    [InlineData(User32.VK.VK_3)]
    [InlineData(User32.VK.VK_4)]
    [InlineData(User32.VK.VK_5)]
    [InlineData(User32.VK.VK_LBUTTON)]
    [InlineData(User32.VK.VK_XBUTTON2)]
    [InlineData((User32.VK)0)]
    [InlineData((User32.VK)0x0A)]
    public void ActualMappedEIsRejectedWhenItCanSwitchActorsOrIsNotAKeyboardKey(User32.VK key)
    {
        Assert.Null(NativeCombatIo.ResolveHeldEPhysicalKey(source => source == User32.VK.VK_E ? key : source));
    }

    [Fact]
    public void CollisionUsesTheActualMappedSwitchKeyRatherThanItsOriginalDigit()
    {
        Assert.Null(NativeCombatIo.ResolveHeldEPhysicalKey(source =>
            source == User32.VK.VK_E || source == User32.VK.VK_3 ? User32.VK.VK_R : source));
        Assert.Equal(User32.VK.VK_R, NativeCombatIo.ResolveHeldEPhysicalKey(source =>
            source == User32.VK.VK_E ? User32.VK.VK_R : source));
        Assert.Equal(User32.VK.VK_E, NativeCombatIo.ResolveHeldEPhysicalKey(source => source));
    }
}
