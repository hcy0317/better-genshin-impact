using BetterGenshinImpact.GameTask.Common.Ui;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class TimeSettingUiReaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KnownClockUsesProductionEscapeAndConfirmsTheReturnedWorld(bool recovery)
    {
        using var fixture = new DomainTipNativeFixture { Scene = new(1) { TimeSetting = true }, Title = false, Footer = false };
        fixture.OnAction = action => { Assert.Equal(UiAction.Escape, action); fixture.Scene = new(1) { MainHud = true }; return true; };
        var result = await UiRecovery.ToMainAsync(fixture.Driver, default, requireOverworld: recovery, clock: fixture.Clock);
        Assert.True(result.MainReady);
        Assert.Single(fixture.Actions);
    }

    [Fact]
    public void RecordedDisabledClockRequiresBothLabelsAndValidTimes()
    {
        var page = TimeSettingUiReader.Parse("当前时间 12:01", "调整到 12：01 今天", "时间少于30分钟", default);
        Assert.True(page.Visible);
        Assert.True(page.TooClose);
        Assert.Equal(721, page.CurrentMinutes);
        Assert.False(TimeSettingUiReader.Parse("12:01", "调整到 12:01", "时间少于30分钟", default).Visible);
        Assert.False(TimeSettingUiReader.Parse("当前时间 25:01", "调整到 12:01", "", default).Visible);
    }
}
