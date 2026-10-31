using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.Common;
using Fischless.GameCapture;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

public class PathPartyHealingTests
{
    [Theory]
    [InlineData("白术")]
    [InlineData("希格雯")]
    [InlineData("珊瑚宫心海")]
    public async Task ExistingHealerSuccessSequenceIsPreserved(string name)
    {
        var actions = new List<string>();
        var avatar = Avatar(name, actions);
        var result = await PathPartyHealing.TryAsync([avatar],
            () => { actions.Add("restore-main"); return Task.FromResult(true); },
            ms => { actions.Add("wait:" + ms); return Task.CompletedTask; }, CancellationToken.None);
        Assert.True(result);
        var expected = name switch
        {
            "白术" => new[] { "select:白术", "skill:白术", "wait:800", "skill:白术", "wait:800", "restore-main", "wait:4000" },
            "希格雯" => new[] { "select:希格雯", "skill:希格雯", "wait:11000", "restore-main" },
            _ => new[] { "select:珊瑚宫心海", "skill:珊瑚宫心海", "wait:500", "legacy-burst:珊瑚宫心海", "restore-main", "wait:5000" }
        };
        Assert.Equal(expected, actions.ToArray());
    }

    [Fact]
    public async Task CurrentPartyJeanUsesNativeBurstAndReturnsToWalkingAvatar()
    {
        var actions = new List<string>();
        var result = await PathPartyHealing.TryAsync([Avatar("钟离", actions), Avatar("芙宁娜", actions), Avatar("那维莱特", actions), Avatar("琴", actions)],
            () => { actions.Add("restore-main"); return Task.FromResult(true); },
            ms => { actions.Add("wait:" + ms); return Task.CompletedTask; }, CancellationToken.None);
        Assert.True(result, "当前琴在队且有可用爆发时，生产队内治疗分派不能直接落入神像恢复");
        Assert.Equal(new[] { "select:琴", "native-burst:琴", "restore-main" }, actions.ToArray());
    }

    [Fact]
    public async Task UnsupportedPartyDoesNotIssueHealingInputs()
    {
        var actions = new List<string>();
        var result = await PathPartyHealing.TryAsync([Avatar("钟离", actions), Avatar("那维莱特", actions)],
            () => throw new Exception("Unsupported party must not switch"),
            _ => throw new Exception("Unsupported party must not wait"), CancellationToken.None);
        Assert.False(result);
        Assert.Empty(actions);
    }

    [Theory]
    [InlineData(BurstCastResult.Confirmed, true)]
    [InlineData(BurstCastResult.Unconfirmed, true)]
    [InlineData(BurstCastResult.NotReady, false)]
    [InlineData(BurstCastResult.Unknown, false)]
    public async Task EveryJeanCastOutcomeRestoresTheWalkingAvatar(BurstCastResult cast, bool attempted)
    {
        var actions = new List<string>();
        var avatar = Avatar("琴", actions) with { NativeBurst = () => { actions.Add("native-burst:琴"); return cast; } };
        var result = await PathPartyHealing.TryAsync([avatar],
            () => { actions.Add("restore-main"); return Task.FromResult(true); }, _ => Task.CompletedTask, CancellationToken.None);
        Assert.Equal(attempted, result);
        Assert.Equal(new[] { "select:琴", "native-burst:琴", "restore-main" }, actions.ToArray());
    }

    [Fact]
    public async Task UnconfirmedHealerSelectionMustRestoreBeforeFallback()
    {
        var actions = new List<string>();
        var avatar = Avatar("琴", actions) with { TrySwitch = () => { actions.Add("select-unconfirmed"); return false; } };
        var result = await PathPartyHealing.TryAsync([avatar],
            () => { actions.Add("restore-main"); return Task.FromResult(true); }, _ => Task.CompletedTask, CancellationToken.None);
        Assert.False(result);
        Assert.Equal(new[] { "select-unconfirmed", "restore-main" }, actions.ToArray());
    }

    [Theory]
    [InlineData("琴")]
    [InlineData("白术")]
    [InlineData("希格雯")]
    [InlineData("珊瑚宫心海")]
    public async Task UnconfirmedWalkingAvatarNeverReturnsSuccess(string name)
    {
        var actions = new List<string>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => PathPartyHealing.TryAsync([Avatar(name, actions)],
            () => Task.FromResult(false), _ => Task.CompletedTask, CancellationToken.None));
    }

    [Theory]
    [InlineData("before")]
    [InlineData("selected")]
    [InlineData("cast")]
    [InlineData("restored")]
    public async Task ParentCancellationNeverIssuesALaterInput(string stage)
    {
        var actions = new List<string>();
        using var cts = new CancellationTokenSource();
        if (stage == "before") cts.Cancel();
        var avatar = new PathPartyHealingAvatar("琴",
            () => { actions.Add("select"); if (stage == "selected") cts.Cancel(); return true; },
            () => throw new Exception("Jean must not use raw skill"), () => throw new Exception("Jean must not use raw burst"),
            () => { actions.Add("cast"); if (stage == "cast") cts.Cancel(); return BurstCastResult.Confirmed; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PathPartyHealing.TryAsync([avatar],
            () => { actions.Add("restore"); if (stage == "restored") cts.Cancel(); return Task.FromResult(true); },
            _ => Task.CompletedTask, cts.Token));
        var expected = stage switch { "before" => Array.Empty<string>(), "selected" => ["select"], "cast" => ["select", "cast"], _ => new[] { "select", "cast", "restore" } };
        Assert.Equal(expected, actions.ToArray());
    }

    [Theory]
    [InlineData("白术")]
    [InlineData("希格雯")]
    [InlineData("珊瑚宫心海")]
    public async Task LegacySelectionCancellationDoesNotIssueTheNextSkill(string name)
    {
        var actions = new List<string>();
        using var cts = new CancellationTokenSource();
        var avatar = Avatar(name, actions) with { TrySwitch = () => { actions.Add("select"); cts.Cancel(); return true; } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PathPartyHealing.TryAsync([avatar],
            () => throw new Exception("Cancelled selection must not restore"), _ => Task.CompletedTask, cts.Token));
        Assert.Equal(new[] { "select" }, actions.ToArray());
    }

    [Theory]
    [InlineData("白术")]
    [InlineData("珊瑚宫心海")]
    public async Task LegacyWaitCancellationDoesNotIssueAnotherSkillOrBurst(string name)
    {
        var actions = new List<string>();
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PathPartyHealing.TryAsync([Avatar(name, actions)],
            () => throw new Exception("Cancelled wait must not restore"),
            _ => { cts.Cancel(); return Task.CompletedTask; }, cts.Token));
        Assert.Equal(new[] { "select:" + name, "skill:" + name }, actions.ToArray());
    }

    [Fact]
    public async Task NativeFailureIsPreservedWithoutFinallySelection()
    {
        var actions = new List<string>();
        var failure = new InvalidOperationException("native fixture failure");
        var avatar = Avatar("琴", actions) with { NativeBurst = () => throw failure };
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => PathPartyHealing.TryAsync([avatar],
            () => throw new Exception("Failure must not select another avatar"), _ => Task.CompletedTask, CancellationToken.None));
        Assert.Same(failure, actual);
        Assert.Equal(new[] { "select:琴" }, actions.ToArray());
    }

    [Theory]
    [InlineData("fresh", true)]
    [InlineData("stale", false)]
    [InlineData("different-session", false)]
    [InlineData("old-sequence", false)]
    [InlineData("same-capture-time", false)]
    [InlineData("wrong-avatar", false)]
    [InlineData("unknown-avatar", false)]
    [InlineData("unknown-ready", false)]
    [InlineData("not-main", false)]
    [InlineData("expired-after-observation", false)]
    [InlineData("budget-after-observation", false)]
    public void ActualBurstProtocolDoesNotPressWithoutCurrentAdmission(string kind, bool mayPress)
    {
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        var before = source.Next();
        clock.Advance(TimeSpan.FromMilliseconds(10));
        var candidate = source.Next();
        var frame = new PathPartyBurstFrame(candidate, true, 4, new(true, false));
        frame = kind switch
        {
            "different-session" => frame with { Source = new CaptureFrameSource(clock).Next() },
            "old-sequence" => frame with { Source = before },
            "same-capture-time" => frame with { Source = before with { Sequence = before.Sequence + 1 } },
            "wrong-avatar" => frame with { ActiveIndex = 1 },
            "unknown-avatar" => frame with { ActiveIndex = -1 },
            "unknown-ready" => frame with { Burst = default },
            "not-main" => frame with { MainReady = false },
            _ => frame
        };
        if (kind == "stale") clock.Advance(TimeSpan.FromMilliseconds(151));
        var presses = 0;
        var result = PathPartyHealing.TryGuardedBurst(admit => BurstCastProtocol.TryCast(
                () => presses == 0 ? new(true, false) : new(false, true), () => presses++,
                ms => clock.Advance(TimeSpan.FromMilliseconds(ms)), CancellationToken.None, clock,
                timeoutSeconds: 2.4, maxSamples: 16, tryBeginInput: admit),
            () =>
            {
                if (kind == "expired-after-observation") clock.Advance(TimeSpan.FromMilliseconds(151));
                if (kind == "budget-after-observation")
                {
                    clock.Advance(TimeSpan.FromSeconds(2.4));
                    return frame with { Source = source.Next() };
                }
                return frame;
            }, before, 4, TimeSpan.FromMilliseconds(150), () => { }, clock);
        Assert.Equal(mayPress ? 1 : 0, presses);
        Assert.Equal(mayPress ? BurstCastResult.Confirmed : BurstCastResult.Unknown, result);
    }

    [Fact]
    public void CancellationDuringAdmissionDoesNotPressQ()
    {
        var clock = new FakeTimeProvider();
        var producer = new CaptureFrameSource(clock);
        var before = producer.Next();
        clock.Advance(TimeSpan.FromMilliseconds(10));
        using var cts = new CancellationTokenSource();
        var presses = 0;
        Assert.ThrowsAny<OperationCanceledException>(() => PathPartyHealing.TryGuardedBurst(admit => BurstCastProtocol.TryCast(
                () => new(true, false), () => presses++, ms => clock.Advance(TimeSpan.FromMilliseconds(ms)),
                cts.Token, clock, tryBeginInput: admit),
            () => { cts.Cancel(); return new(producer.Next(), true, 4, new(true, false)); },
            before, 4, TimeSpan.FromMilliseconds(150), () => cts.Token.ThrowIfCancellationRequested(), clock));
        Assert.Equal(0, presses);
    }

    [Fact]
    public void UnconfirmedNativeCastDoesNotRetryOrExtendItsOriginalBudget()
    {
        var clock = new FakeTimeProvider();
        var producer = new CaptureFrameSource(clock);
        var before = producer.Next();
        clock.Advance(TimeSpan.FromMilliseconds(10));
        var frame = new PathPartyBurstFrame(producer.Next(), true, 4, new(true, false));
        var presses = 0;
        var samples = 0;
        var result = PathPartyHealing.TryGuardedBurst(admit => BurstCastProtocol.TryCast(
                () => { samples++; return new(true, false); }, () => presses++,
                ms => clock.Advance(TimeSpan.FromMilliseconds(ms)), CancellationToken.None, clock,
                timeoutSeconds: 2.4, maxSamples: 16, tryBeginInput: admit),
            () => frame, before, 4, TimeSpan.FromMilliseconds(150), () => { }, clock);
        Assert.Equal(BurstCastResult.Unconfirmed, result);
        Assert.Equal(1, presses);
        Assert.True(samples <= 17); // one original readiness sample plus original maximum 16 confirmation samples
    }

    [Theory]
    [InlineData("expected", true)]
    [InlineData("other-avatar", false)]
    [InlineData("unknown-avatar", false)]
    [InlineData("unknown-health", false)]
    [InlineData("still-low", false)]
    [InlineData("old-frame", false)]
    [InlineData("not-main", false)]
    public async Task OriginalHealthFenceAcceptsOnlyExpectedWalkingAvatar(string kind, bool healed)
    {
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        var before = source.Next();
        var fence = new CaptureFrameFence(before, clock.GetTimestamp());
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var result = await HealingObservation.WaitAsync(fence, () => PathPartyHealing.HealthFrame(
            kind == "old-frame" ? before : source.Next(), kind != "not-main",
            kind == "other-avatar" ? 4 : kind == "unknown-avatar" ? -1 : 1, 1,
            kind == "unknown-health" ? null : kind == "still-low"),
            ms => { clock.Advance(TimeSpan.FromMilliseconds(ms)); return Task.CompletedTask; }, CancellationToken.None, clock);
        Assert.Equal(healed, result);
    }

    private static PathPartyHealingAvatar Avatar(string name, List<string> actions) => new(name,
        () => { actions.Add("select:" + name); return true; },
        () => actions.Add("skill:" + name), () => actions.Add("legacy-burst:" + name),
        () => { actions.Add("native-burst:" + name); return BurstCastResult.Confirmed; });
}
