using BetterGenshinImpact.GameTask.AutoFight.Script;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class SkillCooldownObservationTests
{
    [Fact]
    public void MissingActorObservationDoesNotCreateZeroOrRenewTheCooldown()
    {
        var actor = "observation-" + Guid.NewGuid();
        ESkillCdTracker.Record(actor, 10);
        Assert.True(ESkillCdTracker.TryGetKnownRemainingCd(actor, out var before));
        Assert.Null(ESkillCdTracker.RecordObservation(actor, null, log: false));
        Assert.True(ESkillCdTracker.TryGetKnownRemainingCd(actor, out var after));
        Assert.InRange(after, 0, before);
    }

    [Fact]
    public void ObservedCooldownAndObservedZeroRemainDistinctFromUnavailable()
    {
        var actor = "observation-" + Guid.NewGuid();
        Assert.Equal(5, ESkillCdTracker.RecordObservation(actor, 5, log: false));
        Assert.Equal(0, ESkillCdTracker.RecordObservation(actor, 0, log: false));
    }
}
