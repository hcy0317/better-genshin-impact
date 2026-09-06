using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatFlowContextTests
{
    [Fact]
    public void LateRefreshCannotOverwriteANewerProducerGenerationOrReviveExpiredWindow()
    {
        var clock = new FakeTimeProvider();
        using var context = new CombatFlowContext(clock);
        context.TryRecord("水母", 0, 12, new("心海", "e"));
        var oldGeneration = context.Find("水母")!.Generation;
        clock.Advance(TimeSpan.FromSeconds(5));
        context.TryRecord("水母", 5, 12, new("心海", "e"));
        Assert.False(context.TryRefresh("水母", oldGeneration, 6, 12));
        var newGeneration = context.Find("水母")!.Generation;
        clock.Advance(TimeSpan.FromSeconds(12));
        Assert.False(context.TryRefresh("水母", newGeneration, 17, 12));
        Assert.Equal(0, context.Remaining("水母"));
    }

    [Fact]
    public void NamedRecordIsSharedWithinBattleButNeverInheritedByNextBattle()
    {
        var clock = new FakeTimeProvider();
        using var first = new CombatFlowContext(clock);
        Assert.True(first.TryRecord("窗口", first.Now, 8, new("甲", "attack")));
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(5, first.Remaining("窗口"));
        Assert.Equal("甲", first.Find("窗口")!.Source!.Actor);
        using var next = new CombatFlowContext(clock);
        Assert.NotEqual(first.BattleId, next.BattleId);
        Assert.Null(next.Find("窗口"));
        first.Dispose();
        Assert.Null(first.Find("窗口"));
        Assert.False(first.TryRecord("窗口", first.Now, 8));
    }
}
