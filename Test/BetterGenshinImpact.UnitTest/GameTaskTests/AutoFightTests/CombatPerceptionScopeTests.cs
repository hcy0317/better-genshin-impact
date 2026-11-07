using BetterGenshinImpact.GameTask.AutoFight.Model;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatPerceptionScopeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PublicationAndProjectionPreserveOnlyTheSameFramePendingWorkingState(bool pending)
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var source = new Fischless.GameCapture.CaptureFrameSource(clock).Next();
        var gate = AvatarRecognition.PassiveCaptureGate;
        PassiveTargetObservation? observed = null;
        Assert.True(AvatarRecognition.PublishPassiveObservation(false,false,null,1920,1080,
            clock.GetUtcNow().UtcDateTime,gate.Epoch,source:source,battleId:Guid.NewGuid(),
            quality:BetterGenshinImpact.GameTask.AutoFight.CombatObservationQuality.Available,
            absenceReason:"indicator-awaiting-stability",evidence:value=>observed=value,
            directionConfirmationPending:pending));
        Assert.NotNull(observed);
        var published = observed.Value;
        var projected = BetterGenshinImpact.GameTask.AutoFight.NativeCombatBattleHostIo.ProjectObservation(
            published,gate.Epoch,gate.CanCapture,clock.GetUtcNow().UtcDateTime);
        Assert.Equal(source,published.Source); Assert.Equal(source,projected.Source);
        Assert.Equal(published.BattleId,projected.BattleId); Assert.Equal(gate.Epoch,projected.CaptureEpoch);
        Assert.Equal(pending,published.DirectionConfirmationPending); Assert.Equal(pending,projected.DirectionConfirmationPending);
        Assert.Null(projected.Target); Assert.False(published.HasNormalHealthBar);
    }

    [Theory]
    [InlineData("epoch")]
    [InlineData("exclusive")]
    [InlineData("fault")]
    public void InvalidPublicationContextCannotProjectPendingAsAvailable(string fault)
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var source = new Fischless.GameCapture.CaptureFrameSource(clock).Next();
        var gate = AvatarRecognition.PassiveCaptureGate;
        var published = new PassiveTargetObservation(false,false,clock.GetUtcNow().UtcDateTime,null,1920,1080,null)
        { Source=source,BattleId=Guid.NewGuid(),CaptureEpoch=gate.Epoch,DirectionConfirmationPending=true,
          Quality=fault=="fault"?BetterGenshinImpact.GameTask.AutoFight.CombatObservationQuality.Faulted
              :BetterGenshinImpact.GameTask.AutoFight.CombatObservationQuality.Available };
        var projected = BetterGenshinImpact.GameTask.AutoFight.NativeCombatBattleHostIo.ProjectObservation(published,
            fault=="epoch"?gate.Epoch+1:gate.Epoch,fault!="exclusive",clock.GetUtcNow().UtcDateTime);
        Assert.False(projected.DirectionConfirmationPending);
        Assert.NotEqual(BetterGenshinImpact.GameTask.AutoFight.CombatObservationQuality.Available,projected.Quality);
        Assert.Null(projected.Target);
    }

    [Fact]
    public void DiagnosticTextAloneDoesNotProduceTypedPending()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var gate = AvatarRecognition.PassiveCaptureGate;
        PassiveTargetObservation? observed = null;
        Assert.True(AvatarRecognition.PublishPassiveObservation(false,false,null,1920,1080,
            clock.GetUtcNow().UtcDateTime,gate.Epoch,source:new Fischless.GameCapture.CaptureFrameSource(clock).Next(),
            battleId:Guid.NewGuid(),quality:BetterGenshinImpact.GameTask.AutoFight.CombatObservationQuality.Available,
            absenceReason:"indicator-awaiting-stability",evidence:value=>observed=value));
        Assert.NotNull(observed); Assert.False(observed.Value.DirectionConfirmationPending);
    }

    [Fact]
    public void ExceptionalScopeExitRestoresPerception()
    {
        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var combat = AvatarRecognition.BeginExclusiveOperation(allowPassiveObservation: true);
            using var menu = AvatarRecognition.BeginExclusiveOperation();
            throw new InvalidOperationException();
        }));
        Assert.True(AvatarRecognition.PassiveCaptureGate.CanCapture);
    }

    [Fact]
    public void CombatInputAllowsPerceptionButNestedMenuStillBlocksAndInvalidatesOldFrames()
    {
        var before = AvatarRecognition.PassiveCaptureGate;
        Assert.True(before.CanCapture);
        using (AvatarRecognition.BeginExclusiveOperation(allowPassiveObservation: true))
        {
            Assert.True(AvatarRecognition.PassiveCaptureGate.CanCapture);
            Assert.Equal(before.Epoch, AvatarRecognition.PassiveCaptureGate.Epoch);
            using (AvatarRecognition.BeginExclusiveOperation())
            {
                Assert.False(AvatarRecognition.PassiveCaptureGate.CanCapture);
                using (AvatarRecognition.BeginExclusiveOperation(allowPassiveObservation: true))
                    Assert.False(AvatarRecognition.PassiveCaptureGate.CanCapture);
            }
            Assert.True(AvatarRecognition.PassiveCaptureGate.CanCapture);
            Assert.False(AvatarRecognition.CanPublishPassiveObservation(before.Epoch,
                AvatarRecognition.PassiveCaptureGate.Epoch, 0));
        }
        Assert.True(AvatarRecognition.PassiveCaptureGate.CanCapture);
    }
}
