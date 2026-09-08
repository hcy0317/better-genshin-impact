using BetterGenshinImpact.GameTask.ArtifactAnalysis;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.ArtifactAnalysisTests;

public class ArtifactEquipmentExecutionTests
{
    [Fact]
    public async Task TwoWayExchangeAdvancesExpectedStateAndNeverChangesLocks()
    {
        var pieces = new[] { Piece(0, "甲"), Piece(1, "乙") };
        var plan = new ArtifactEquipmentPlanDto("plan", "123456789", "digest", true, 2, pieces,
            [new("a", "甲", "甲", [1]), new("b", "乙", "乙", [0])], []);
        var port = new FakePort(pieces);
        var result = await new ArtifactEquipmentExecution().RunAsync(plan, port, _ => Task.CompletedTask, CancellationToken.None);
        Assert.Equal("completed", result.Status);
        Assert.All(result.Steps, step => Assert.Equal("completed", step.State));
        Assert.Equal("乙", port.Items.Single(i => i.ScanIndex == 0).Location);
        Assert.Equal("甲", port.Items.Single(i => i.ScanIndex == 1).Location);
        Assert.All(port.Items, item => Assert.True(item.Locked));
    }

    [Fact]
    public async Task AmbiguousFailureStopsAndDoesNotBlindlyReplay()
    {
        var pieces = new[] { Piece(0, "甲"), Piece(1, "乙") };
        var plan = new ArtifactEquipmentPlanDto("plan", "123456789", "digest", true, 2, pieces,
            [new("a", "甲", "甲", [1]), new("b", "乙", "乙", [0])], []);
        var port = new FakePort(pieces) { Fail = true };
        var result = await new ArtifactEquipmentExecution().RunAsync(plan, port, _ => Task.CompletedTask, CancellationToken.None);
        Assert.Equal("needs_observation", result.Status);
        Assert.Equal("unknown", result.Steps[0].State);
        Assert.Equal("not_executed", result.Steps[1].State);
        Assert.Equal(1, port.Inputs);
    }

    [Fact]
    public async Task UnconfirmedPlanHasNoInput()
    {
        var pieces = new[] { Piece(0, "甲") };
        var plan = new ArtifactEquipmentPlanDto("plan", "123456789", "digest", false, 1, pieces, [], []);
        var port = new FakePort(pieces);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ArtifactEquipmentExecution().RunAsync(plan, port, _ => Task.CompletedTask, CancellationToken.None));
        Assert.Equal(0, port.Inputs);
    }

    private static ArtifactItemDto Piece(int id, string owner) => new(id, "EmblemOfSeveredFate", "flower", 20, 5, "hp", [new("critDMG_", 14 + id)], owner, true);
    private sealed class FakePort(ArtifactItemDto[] items) : IArtifactEquipmentPort
    {
        public ArtifactItemDto[] Items = items;
        public bool Fail;
        public int Inputs;
        public Task<ArtifactSnapshotDto> ObserveAsync(string uid, CancellationToken ct) => Task.FromResult(ArtifactSnapshotDto.Create(uid, "observed", "order", "v1", Items));
        public Task EquipAsync(ArtifactEquipTargetDto target, ArtifactItemDto item, CancellationToken ct)
        {
            Inputs++;
            if (Fail) throw new IOException("result not observed");
            Items = Items.Select(i => i.ScanIndex == item.ScanIndex ? i with { Location = target.InventoryName }
                : i.Location == target.InventoryName && i.SlotKey == item.SlotKey ? i with { Location = "" } : i).ToArray();
            return Task.CompletedTask;
        }
    }
}
