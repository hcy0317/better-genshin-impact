using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.Model.Area;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatHostInputOwnershipTests
{
    [Fact]
    public async Task HostAndStrategyShareTheNativeSessionAndAnOldBattleCannotReleaseTheNextOwner()
    {
        var io = new OwnershipIo();
        var program = CombatFlowProgram.Compile("琴 attack(0.1)");
        using var first = NativeCombatFlowRunner.Create(program, io);
        using var second = NativeCombatFlowRunner.Create(program, io);
        await first.RunHostOperationAsync(async ct =>
        {
            Assert.True(io.Suppressed);
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await first.RunHostOperationAsync(_ => ValueTask.CompletedTask, ct));
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await second.RunHostOperationAsync(_ => ValueTask.CompletedTask, ct));
        }, default);
        Assert.False(io.Suppressed);
        first.Dispose();
        var releases = io.Releases;
        await second.RunHostOperationAsync(_ => ValueTask.CompletedTask, default);
        first.ReleaseHostInput();
        Assert.Equal(releases, io.Releases);
        second.Dispose();
        Assert.True(io.Releases > releases);
        using var third = NativeCombatFlowRunner.Create(program, io);
        await third.RunHostOperationAsync(_ => ValueTask.CompletedTask, default);
    }

    private sealed class OwnershipIo : INativeCombatIo
    {
        public bool Suppressed { get; private set; }
        public int Releases { get; private set; }
        public TimeProvider Clock { get; } = new FakeTimeProvider();
        public ILogger Logger => NullLogger.Instance;
        public CombatInputCoordinator InputCoordinator { get; } = new();
        public IReadOnlyList<NativeCombatActor> Actors { get; } = [new("琴", 1)];
        public double LastFinishCheckAge => 0;
        public Task PrepareVisionAsync(CancellationToken ct) => Task.CompletedTask;
        public IDisposable BeginExclusive(bool allowPassiveObservation)
        {
            Suppressed = !allowPassiveObservation;
            return new Cleanup(() => Suppressed = false);
        }
        public void ReleaseInput() => Releases++;
        public ImageRegion? Capture() => throw new NotSupportedException();
        public bool IsCombatHud(ImageRegion frame) => throw new NotSupportedException();
        public bool IsMainUi(ImageRegion frame) => throw new NotSupportedException();
        public int ReadActive(ImageRegion frame, AvatarActiveCheckContext context) => throw new NotSupportedException();
        public bool? IsActorActive(NativeCombatActor actor, ImageRegion frame) => throw new NotSupportedException();
        public double ReadSkillCooldown(NativeCombatActor actor, ImageRegion frame) => throw new NotSupportedException();
        public bool IsSkillReady(NativeCombatActor actor, ImageRegion frame, double cooldown) => throw new NotSupportedException();
        public BurstObservation ReadBurst(ImageRegion frame, bool active) => throw new NotSupportedException();
        public bool ReadLowHp(ImageRegion frame) => throw new NotSupportedException();
        public HashSet<int> ReadSideBurstReady(ImageRegion frame) => throw new NotSupportedException();
        public bool TryGetKnownSkillCooldown(string actor, out double cooldown) => throw new NotSupportedException();
        public void ConfirmSkill(NativeCombatActor actor, double cooldown, DateTime inputAtUtc) => throw new NotSupportedException();
        public Task WaitSkillCooldown(NativeCombatActor actor, CancellationToken ct) => throw new NotSupportedException();
        public void SelectActor(int index, CancellationToken ct) => throw new NotSupportedException();
        public void WaitForSelection(int milliseconds, CancellationToken ct) => throw new NotSupportedException();
        public bool OnSelectionMismatch(NativeCombatActor actor, int attempt, int attempts, int observed, CancellationToken ct) => throw new NotSupportedException();
        public void ResolveSelectionRecovery(NativeCombatActor actor, AvatarSelectionProtocol.Result<ImageRegion> selection, CancellationToken ct) => throw new NotSupportedException();
        public void CheckDefeated(ImageRegion frame, CancellationToken ct) => throw new NotSupportedException();
        public void SendSkill(NativeCombatActor actor, bool hold) => throw new NotSupportedException();
        public void SendBurst(NativeCombatActor actor) => throw new NotSupportedException();
        public void ExecutePrimitive(NativeCombatActor actor, CombatCommand command) => throw new NotSupportedException();
        public Task DelayAsync(int milliseconds, CancellationToken ct) => throw new NotSupportedException();
        private sealed class Cleanup(Action close) : IDisposable { public void Dispose() => close(); }
    }
}
