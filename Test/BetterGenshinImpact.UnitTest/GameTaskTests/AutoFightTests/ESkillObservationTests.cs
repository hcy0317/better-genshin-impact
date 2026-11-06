using BetterGenshinImpact.GameTask.AutoFight.Model;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class ESkillObservationTests
{
    [Theory]
    [InlineData("S Zhongli 01 nocd", .9996728, "Ready")]
    [InlineData("S Zhongli 01 nocd", .7, "Unknown")]
    [InlineData("S Furina 01 nocd", .999, "Unknown")]
    [InlineData("S Zhongli 02 nocd", .999, "Cooldown")]
    [InlineData("S Zhongli 01 cd", .999, "Cooldown")]
    [InlineData("unreadable", .999, "Unknown")]
    [InlineData(null, .999, "Unknown")]
    public void OriginalRoleModeAndConfidenceRulesRemain(string? label, double confidence, string expected)
    {
        var reading = CombatHudReader.DecodeESkillClassification(label, confidence, "Zhongli");
        Assert.Equal(expected, reading.State.ToString());
        var observation = CombatHudReader.CombineESkillObservation(double.NaN, false, reading, true);
        Assert.True(double.IsNaN(observation.Cooldown));
        Assert.Equal(expected == "Ready" ? true : expected == "Cooldown" ? false : (bool?)null,
            observation.Ready);
    }

    [Fact]
    public void VisualReadinessDoesNotInventCooldownAndDisabledPreferenceKeepsUnknown()
    {
        var classified = CombatHudReader.DecodeESkillClassification("S Zhongli 01 nocd", .9996728, "Zhongli");
        var allowed = CombatHudReader.CombineESkillObservation(double.NaN, false, classified, true);
        Assert.True(allowed.Ready);
        Assert.True(double.IsNaN(allowed.Cooldown));
        var disabled = CombatHudReader.CombineESkillObservation(double.NaN, false, classified, false);
        Assert.Null(disabled.Ready);
        Assert.Null(disabled.Classification);
        Assert.True(double.IsNaN(disabled.Cooldown));
        var realCooldown = CombatHudReader.CombineESkillObservation(8.25, false, classified, true);
        Assert.False(realCooldown.Ready);
        Assert.Equal(8.25, realCooldown.Cooldown);
    }

    [Fact]
    public void ClassificationCooldownCannotBecomeCastEvidence()
    {
        var classified = CombatHudReader.DecodeESkillClassification("S Zhongli 01 cd", .99, "Zhongli");
        var observation = CombatHudReader.CombineESkillObservation(double.NaN, false, classified, true);
        Assert.False(observation.Ready);
        Assert.True(double.IsNaN(observation.Cooldown));
    }
}
