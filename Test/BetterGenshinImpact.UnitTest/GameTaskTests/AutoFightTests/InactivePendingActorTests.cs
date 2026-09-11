using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Model.Area;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class InactivePendingActorTests
{
    [Fact]
    public void PendingActorCanBeRecheckedOnceWithoutCreditingOrReleasingItsSkill()
    {
        using var attempts = new CombatSkillAttempts(Guid.NewGuid());
        var attempt = attempts.TryBegin("钟离", Method.Skill, "shield", 0, 15)!;
        Assert.Null(attempts.TakeInactiveActorProbe(_ => null));
        Assert.Null(attempts.TakeInactiveActorProbe(_ => true));
        Assert.Same(attempt, attempts.TakeInactiveActorProbe(_ => false));
        Assert.Null(attempts.TakeInactiveActorProbe(_ => false));
        Assert.True(attempts.IsOccupied("钟离", Method.Skill));
        Assert.Null(attempts.TakeConfirmation("钟离", Method.Skill, "shield", 1));
    }

    [Fact]
    public void MorningScreenshotCannotAttributeNaviasSkillToZhongli()
    {
        using var image = new ImageRegion(Cv2.ImRead(Path.Combine(AppContext.BaseDirectory,
            "Assets", "Ui", "inactive-zhongli-20260911.png")), 0, 0);
        using var index = image.Find(ElementRecognition.Get("Index1", image));
        Assert.True(index.IsExist());
        var zhongli = new Avatar(null!, "钟离", 1, default) { IndexRect = index.ToRect() };
        Assert.False(zhongli.IsActive(image));
        var otherActorCooldown = new CombatSkillObservation(Guid.NewGuid(), 1, 1, true, false);
        var gated = NativeCombatFlowRunner.GatePendingSkillObservation(otherActorCooldown, zhongli.IsActive(image));
        Assert.Null(gated.CoolingDown);
        Assert.Null(gated.Ready);
    }
}
