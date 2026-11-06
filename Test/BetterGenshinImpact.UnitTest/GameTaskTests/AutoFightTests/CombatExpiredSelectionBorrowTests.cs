using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using Fischless.GameCapture;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatExpiredSelectionBorrowTests
{
    [Theory]
    [InlineData("complete", true)]
    [InlineData("partial", false)]
    [InlineData("unknown", false)]
    [InlineData("unpaired", false)]
    [InlineData("error", false)]
    [InlineData("missing-start", false)]
    [InlineData("fence", false)]
    [InlineData("not-expired", false)]
    [InlineData("wrong-actor", false)]
    [InlineData("wrong-battle", false)]
    [InlineData("wrong-attempt", false)]
    [InlineData("other-unresolved", false)]
    public void OnlyTheExactSoleExpiredCompleteRequestMayYieldForReadiness(string fault, bool allowed)
    {
        var clock = new FakeTimeProvider();
        var producer = new CaptureFrameSource(clock);
        var before = producer.Next();
        var started = clock.GetTimestamp();
        var battle = Guid.NewGuid();
        using var attempts = new CombatSkillAttempts(battle);
        var attempt = attempts.TryBegin("钟离", Method.Skill, "original", 0, 5)!;
        clock.Advance(TimeSpan.FromMilliseconds(60));
        var completed = clock.GetTimestamp();
        attempts.MarkInputCompleted(attempt.AttemptId, new(before, completed));
        var receipt = new CombatBattleHostInputResult(
            fault is "partial" or "unknown" ? CombatBattleHostInputStatus.Unknown : CombatBattleHostInputStatus.Sent,
            fault == "fence" ? completed - 1 : completed)
        {
            Error = fault == "error" ? new InvalidOperationException("native failure") : null,
            NativeRequested = fault == "unpaired" ? 1 : 2,
            NativeSubmitted = fault == "unknown" ? null : fault is "partial" or "unpaired" ? 1 : 2,
            StartedTimestamp = fault == "missing-start" ? null : started
        };
        attempts.MarkOriginalReceipt(attempt.AttemptId, receipt, .06, clock, Guid.NewGuid());
        clock.Advance(TimeSpan.FromSeconds(fault == "not-expired" ? 1 : 6));
        var now = clock.GetElapsedTime(started).TotalSeconds;
        if (fault == "other-unresolved") attempts.TryBegin("班尼特", Method.Burst, "other", now, now + 5);
        var claimed = fault switch
        {
            "wrong-actor" => attempt with { Actor = "班尼特" },
            "wrong-battle" => attempt with { BattleId = Guid.NewGuid() },
            "wrong-attempt" => attempt with { AttemptId = Guid.NewGuid() },
            _ => attempt
        };
        Assert.Equal(allowed, attempts.CanYieldExpiredForReadiness(claimed, now));
        Assert.True(attempts.HasUnresolved("钟离", Method.Skill));
        Assert.Null(attempts.TakeConfirmation("钟离", Method.Skill, "original", now));
    }
}
