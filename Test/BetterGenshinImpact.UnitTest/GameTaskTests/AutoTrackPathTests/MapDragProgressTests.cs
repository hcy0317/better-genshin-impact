using BetterGenshinImpact.GameTask.AutoTrackPath;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoTrackPathTests;

public class MapDragProgressTests
{
    [Fact]
    public void ImprovingObservedDistanceDoesNotReanchor()
    {
        var progress = new MapDragProgress();
        foreach (var distance in new[] { 100d, 90, 80, 70, 60 })
            Assert.Equal(MapDragProgressDecision.Continue, progress.Observe(distance));
    }

    [Fact]
    public void StagnationAndOscillationReanchorOnlyOnceThenStop()
    {
        var progress = new MapDragProgress();
        progress.Observe(100);
        Assert.Equal(MapDragProgressDecision.Continue, progress.Observe(110));
        Assert.Equal(MapDragProgressDecision.Continue, progress.Observe(100));
        Assert.Equal(MapDragProgressDecision.ReanchorSlowly, progress.Observe(110));
        progress.Reanchor(100);
        Assert.Equal(MapDragProgressDecision.Continue, progress.Observe(100));
        Assert.Equal(MapDragProgressDecision.Continue, progress.Observe(100));
        Assert.Equal(MapDragProgressDecision.Stop, progress.Observe(100));
    }

    [Fact]
    public void UnrecognizedFramesCannotPretendToMakeProgress()
    {
        var progress = new MapDragProgress();
        Assert.Equal(MapDragProgressDecision.Continue, progress.Observe(null));
        Assert.Equal(MapDragProgressDecision.Continue, progress.Observe(null));
        Assert.Equal(MapDragProgressDecision.ReanchorSlowly, progress.Observe(null));
    }

    [Fact]
    public void RealProgressResetsStagnationWithoutRestoringRetryBudget()
    {
        var progress = new MapDragProgress();
        progress.Reanchor(100);
        progress.Observe(100);
        progress.Observe(100);
        Assert.Equal(MapDragProgressDecision.Continue, progress.Observe(80));
        progress.Observe(80);
        progress.Observe(80);
        Assert.Equal(MapDragProgressDecision.ReanchorSlowly, progress.Observe(80));
        progress.Reanchor(80);
        Assert.Equal(MapDragProgressDecision.Continue, progress.Observe(60));
        progress.Observe(60);
        progress.Observe(60);
        Assert.Equal(MapDragProgressDecision.Stop, progress.Observe(60));
    }

    [Fact]
    public void InvalidCoordinatesAreRejected()
    {
        var progress = new MapDragProgress();
        Assert.Throws<ArgumentOutOfRangeException>(() => progress.Observe(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => progress.Observe(double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => progress.Reanchor(-1));
    }
}
