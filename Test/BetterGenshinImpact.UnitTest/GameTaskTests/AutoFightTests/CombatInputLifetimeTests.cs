using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatInputLifetimeTests
{
    [Fact]
    public void AClosedOrBusyOwnerCannotIssueALateStandaloneRelease()
    {
        var inputs = new CombatInputCoordinator();
        var releases = 0;
        var old = inputs.TryAcquire(Guid.NewGuid(), () => releases++)!;
        var operation = old.EnterOperation();
        Assert.False(old.TryReleaseInput(() => releases++));
        old.Dispose();
        operation.Dispose();
        using var next = inputs.TryAcquire(Guid.NewGuid(), () => { });
        Assert.NotNull(next);
        Assert.False(old.TryReleaseInput(() => releases++));
        Assert.Equal(1, releases);
        Assert.True(next.TryReleaseInput(() => releases++));
        Assert.Equal(2, releases);
    }

    [Fact]
    public void ClosingBattleCannotHandOffUntilItsInputOperationAndReleaseHaveFinished()
    {
        var inputs = new CombatInputCoordinator();
        var oldReleases = 0;
        var newReleases = 0;
        var old = inputs.TryAcquire(Guid.NewGuid(), () => oldReleases++);
        Assert.NotNull(old);
        var operation = old.EnterOperation();
        old.Dispose();
        Assert.Equal(0, oldReleases);
        Assert.Null(inputs.TryAcquire(Guid.NewGuid(), () => newReleases++));
        operation.Dispose();
        Assert.Equal(1, oldReleases);
        using var next = inputs.TryAcquire(Guid.NewGuid(), () => newReleases++);
        Assert.NotNull(next);
        old.Dispose();
        operation.Dispose();
        Assert.Equal(0, newReleases);
        Assert.Throws<ObjectDisposedException>(() => old.EnterOperation());
    }
}
