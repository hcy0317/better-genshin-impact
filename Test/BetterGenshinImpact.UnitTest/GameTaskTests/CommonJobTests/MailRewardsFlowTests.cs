using BetterGenshinImpact.GameTask.Common.Job;
using BetterGenshinImpact.GameTask.Common.Ui;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class MailRewardsFlowTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MailStepCompletesOnlyAfterClosingThePaimonMenu(bool hasRewards)
    {
        var clock = new FakeTimeProvider();
        var game = new MailUi(clock);
        var claimed = 0;
        await ClaimMailRewardsTask.RunAsync(game,
            _ => { game.MenuOpen = true; return Task.CompletedTask; },
            _ => { game.MailOpen = hasRewards; return Task.FromResult(hasRewards); },
            _ => { claimed++; return Task.CompletedTask; }, default, clock: clock);
        Assert.False(game.MenuOpen);
        Assert.False(game.MailOpen);
        Assert.Equal(hasRewards ? 1 : 0, claimed);
    }

    [Fact]
    public async Task ExitFailureCannotCompleteTheMailStepOrRepeatItsClaim()
    {
        var clock = new FakeTimeProvider();
        var game = new MailUi(clock) { CanClose = false };
        var claimed = 0;
        var nextTaskStarted = false;
        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await ClaimMailRewardsTask.RunAsync(game,
                _ => { game.MenuOpen = true; return Task.CompletedTask; },
                _ => Task.FromResult(true),
                _ => { claimed++; return Task.CompletedTask; }, default, clock: clock);
            nextTaskStarted = true;
        });
        Assert.False(nextTaskStarted);
        Assert.Equal(1, claimed);
    }

    [Fact]
    public async Task KnownMenuBackFeatureIsNotMistakenForMainHud()
    {
        var clock = new FakeTimeProvider();
        var game = new MailUi(clock) { MenuOpen = true, HudBehindMenu = true };
        var result = await UiRecovery.ToMainAsync(game, default, clock: clock);
        Assert.False(game.MenuOpen);
        Assert.True(result.MainReady);
    }

    [Fact]
    public async Task UserCancellationAfterClaimDoesNotContinueClosingOrStartAnotherStep()
    {
        var clock = new FakeTimeProvider();
        using var user = new CancellationTokenSource();
        var game = new MailUi(clock);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ClaimMailRewardsTask.RunAsync(game,
            _ => { game.MenuOpen = true; return Task.CompletedTask; },
            _ => Task.FromResult(true),
            _ => { user.Cancel(); return Task.CompletedTask; }, user.Token, clock: clock));
        Assert.True(game.MenuOpen);
    }

    [Fact]
    public void MenuRecognitionDefinitionAndTemplateAreShippedWithTheApplication()
    {
        var assets = Path.Combine(AppContext.BaseDirectory, "GameTask", "UseRedeemCode", "Assets");
        var configPath = Path.Combine(assets, "Recognition.json");
        Assert.True(File.Exists(configPath), "菜单识别定义必须进入构建输出，不能只存在于源码目录");
        var config = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(configPath));
        var template = (string?)config["objects"]?["MenuBack"]?["template"];
        Assert.False(string.IsNullOrWhiteSpace(template));
        Assert.True(File.Exists(Path.Combine(assets, "1920x1080", template!)));
    }

    private sealed class MailUi(FakeTimeProvider clock) : IUiDriver
    {
        private long _frame;
        public bool MenuOpen { get; set; }
        public bool MailOpen { get; set; }
        public bool CanClose { get; init; } = true;
        public bool HudBehindMenu { get; init; }
        public UiSnapshot Capture() => new(++_frame)
        {
            MainHud = !MenuOpen && !MailOpen || HudBehindMenu,
            MenuBack = MenuOpen && !MailOpen,
            Closable = MailOpen
        };
        public Task DelayAsync(int milliseconds, CancellationToken ct)
        {
            clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
        public Task<bool> ActAsync(UiAction action, UiSnapshot observed, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.Equal(UiAction.Escape, action);
            if (CanClose)
            {
                if (MailOpen) MailOpen = false;
                else MenuOpen = false;
            }
            return Task.FromResult(CanClose);
        }
    }
}
