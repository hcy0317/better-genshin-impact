using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoFight;
using Fischless.GameCapture;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class AvatarSelectionProtocolTests
{
    [Fact]
    public void MovementReceiptKeepsItsFenceWhenTheAdmittedFrameAgesDuringThePhysicalPulse()
    {
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        using var goal = new AvatarSelectionProtocol.Continuation<ObservedFrame>(1, 10, TimeSpan.FromSeconds(2),
            () => new(source.Next()), f => f.Source, _ => true, _ => 2, f => new(f.Source),
            (_, _) => new(CombatBattleHostInputStatus.Sent, clock.GetTimestamp()), clock);
        using (goal.Advance(default)) { }
        clock.Advance(TimeSpan.FromMilliseconds(50));
        using (goal.Advance(default)) { }
        clock.Advance(TimeSpan.FromMilliseconds(50));
        using (goal.Advance(default)) { }
        var admitted = source.Next();
        Assert.True(goal.CanAssistFrom(admitted));
        clock.Advance(TimeSpan.FromMilliseconds(180)); // 已准入后的物理持续时间/调度迟到，不再产生新输入许可。
        goal.ObserveAssistance(admitted, clock.GetTimestamp());
        Assert.False(goal.CanAssist);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        using var next = goal.Advance(default, allowInput: false);
        Assert.True(next.InputFence!.Value.Accepts(source.Next()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetirementNeedsTwoFreshFramesAndUnknownStillRequiresItsOriginalTarget(bool unknown)
    {
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        var active = 2;
        var sends = 0;
        var releases = 0;
        using var goal = new AvatarSelectionProtocol.Continuation<ObservedFrame>(1, 10, TimeSpan.FromSeconds(2),
            () => new(source.Next()), f => f.Source, _ => true, _ => active, f => new(f.Source),
            (_, _) => { sends++; return new(unknown ? CombatBattleHostInputStatus.Unknown : CombatBattleHostInputStatus.Sent,
                clock.GetTimestamp()); }, clock, release: () => releases++);
        using (var first = goal.Advance(default)) Assert.True(first.AwaitingObservation);
        goal.RequestRetirement();
        clock.Advance(TimeSpan.FromMilliseconds(50));
        using (var second = goal.Advance(default)) Assert.True(second.AwaitingObservation);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        using (var third = goal.Advance(default))
            Assert.Equal(unknown ? AvatarSelectionProtocol.Outcome.Awaiting : AvatarSelectionProtocol.Outcome.RetiredWithoutCompletion, third.State);
        Assert.Equal(unknown ? 0 : 1, releases);
        if (unknown)
        {
            active = 1;
            clock.Advance(TimeSpan.FromMilliseconds(50));
            using (var fourth = goal.Advance(default)) Assert.False(fourth.Confirmed);
            clock.Advance(TimeSpan.FromMilliseconds(50));
            using (var fifth = goal.Advance(default)) Assert.Equal(AvatarSelectionProtocol.Outcome.Ready, fifth.State);
        }
        Assert.Equal(1, releases);
        Assert.Equal(1, sends);
    }

    [Fact]
    public void FailedKeyReleaseCannotPublishReadyOrRetireTheSelectionOwner()
    {
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        using var goal = new AvatarSelectionProtocol.Continuation<ObservedFrame>(1, 10, TimeSpan.FromSeconds(2),
            () => new(source.Next()), f => f.Source, _ => true, _ => 1, f => new(f.Source),
            (_, _) => throw new InvalidOperationException("already active"), clock,
            release: () => throw new IOException("key release failed"));
        using (var first = goal.Advance(default)) Assert.True(first.AwaitingObservation);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        Assert.Throws<IOException>(() => goal.Advance(default));
        Assert.False(goal.IsClosed);
    }

    [Fact]
    public void FailedReceiptWithKnownSubmissionPreservesTheFenceBeforeRethrowing()
    {
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        var failure = new IOException("post submission failed");
        var sends = 0;
        using var selection = new AvatarSelectionProtocol.Continuation<ObservedFrame>(1, 10, TimeSpan.FromSeconds(2),
            () => new(source.Next()), frame => frame.Source, _ => true, _ => 2,
            frame => new(frame.Source), (_, _) =>
            {
                sends++;
                return new(CombatBattleHostInputStatus.Failed, clock.GetTimestamp(), Error: failure)
                { NativeRequested = 2, NativeSubmitted = 2 };
            }, clock);
        Assert.Same(failure, Assert.Throws<IOException>(() => selection.Advance(default)));
        Assert.True(selection.HasSubmittedInput);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        using var next = selection.Advance(default);
        Assert.NotNull(next.InputFence);
        Assert.Equal(1, sends);
    }

    [Fact]
    public void NotSentKeepsTheOriginalRequestAndDoesNotCreateAnInputFence()
    {
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        var active = 2;
        var requests = new List<Guid>();
        using var selection = new AvatarSelectionProtocol.Continuation<ObservedFrame>(1, 1, TimeSpan.FromSeconds(2),
            () => new(source.Next()), frame => frame.Source, _ => true, _ => active,
            frame => new(frame.Source), (_, request) =>
            {
                requests.Add(request.Id);
                if (requests.Count == 1) return new(CombatBattleHostInputStatus.NotSent);
                active = 1;
                return new(CombatBattleHostInputStatus.Sent, clock.GetTimestamp());
            }, clock);
        using (var first = selection.Advance(default))
        {
            Assert.True(first.AwaitingObservation);
            Assert.Null(first.InputFence);
            Assert.False(selection.HasSubmittedInput);
        }
        clock.Advance(TimeSpan.FromMilliseconds(50));
        using (var second = selection.Advance(default)) Assert.True(selection.HasSubmittedInput);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        using (var third = selection.Advance(default)) Assert.False(third.Confirmed);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        using (var fourth = selection.Advance(default)) Assert.True(fourth.Confirmed);
        Assert.Equal(2, requests.Count);
        Assert.Equal(requests[0], requests[1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyKnownSubmissionCanRetryAgainstFreshStableMismatchWithinTheOriginalDeadline(bool unknown)
    {
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        var requests = 0;
        using var selection = new AvatarSelectionProtocol.Continuation<ObservedFrame>(1, 10, TimeSpan.FromSeconds(2),
            () => new(source.Next()), frame => frame.Source, _ => true, _ => 2,
            frame => new(frame.Source), (_, _) =>
            {
                requests++;
                return new(unknown ? CombatBattleHostInputStatus.Unknown : CombatBattleHostInputStatus.Sent,
                    unknown ? null : clock.GetTimestamp()) { ObservableAfterTimestamp = clock.GetTimestamp() };
            }, clock);
        for (var index = 0; index < 8; index++)
        {
            using var result = selection.Advance(default);
            Assert.True(result.AwaitingObservation);
            clock.Advance(TimeSpan.FromMilliseconds(200));
        }
        if (unknown) Assert.Equal(1, requests);
        else Assert.InRange(requests, 2, 10);
        clock.Advance(TimeSpan.FromSeconds(1));
        using var expired = selection.Advance(default);
        Assert.False(expired.Confirmed);
        Assert.False(expired.AwaitingObservation);
    }

    [Fact]
    public void RepeatedCopiesOfOneFrameCannotConfirmSelectionOrSendAnotherInput()
    {
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock).Next();
        var frames = new List<ObservedFrame>();
        using (var selection = AvatarSelectionProtocol.Select(1, 4,
            () => { var frame = new ObservedFrame(source); frames.Add(frame); return frame; },
            frame => frame.Source, _ => true, _ => 1, frame => new ObservedFrame(frame.Source),
            (_, _) => throw new InvalidOperationException("不能重新选择已在场的角色"),
            ms => clock.Advance(TimeSpan.FromMilliseconds(ms)), default, clock: clock))
        {
            Assert.False(selection.Confirmed);
            Assert.False(selection.NeedsRecovery);
        }
        Assert.All(frames, frame => Assert.True(frame.Disposed));
    }

    private sealed class ObservedFrame(CaptureFrameStamp source) : IDisposable
    {
        public CaptureFrameStamp Source { get; } = source;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
