using BetterGenshinImpact.GameTask.Common.Job;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class ItemIconRecognizerTests
{
    [Theory]
    [InlineData(0.699, null)]
    [InlineData(0.700, "幻造裂晶")]
    [InlineData(0.850, "幻造裂晶")]
    public void SelectRecognizedName_UsesInventoryItemThreshold(double score, string? expected)
    {
        var candidate = new ItemIconCandidate("幻造裂晶", score, -1);

        Assert.Equal(expected, ItemRecognizer.SelectRecognizedName(candidate));
    }
}
