using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.Common;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class TaskControlCancellationTests
{
    [Fact]
    public void LegacySleep_ShouldKeepItsNormalEndCancellationContract()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<NormalEndException>(() => TaskControl.Sleep(1, cancellation.Token));
    }
}
