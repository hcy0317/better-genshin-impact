using BetterGenshinImpact.GameTask.AutoFight.Model;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatPerceptionScopeTests
{
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
