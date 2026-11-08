using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.AutoFight;
using Fischless.GameCapture;
using Microsoft.Extensions.Time.Testing;
using Vanara.PInvoke;
using System.Text.Json;
using BetterGenshinImpact.GameTask;
using Fischless.WindowsInput;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public partial class LegacyPathingMacroTests
{
    [Fact]
    public async Task AuthoredThirdInteractionCanAcquireCannonBeforeTheOriginalFire()
    {
        const string program = "w(.5),wait(1),keypress(f),wait(.2),keypress(f),wait(.1),keypress(f),wait(.1),keypress(RETURN),wait(.1),keypress(ESCAPE)";
        var plan = LegacyPathingMacroPlan.Create(CombatScriptParser.ParseContext(program, false), ["钟离"]);
        var segment = Assert.Single(plan.Segments);
        Assert.True(segment.IsRaw);
        Assert.True(segment.CannonProgram);
        var beforeReturn = segment.Commands.TakeWhile(command => command.Method != Method.KeyPress ||
            !string.Equals(command.Args![0], "RETURN", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.Equal(3, beforeReturn.Count(command => command.Method == Method.KeyPress &&
            string.Equals(command.Args![0], "f", StringComparison.OrdinalIgnoreCase)));
        var io = new MacroReplay { Scene = PathingMacroScene.World };
        using var cancellation = new CancellationTokenSource();
        var started = io.Time.GetTimestamp();
        var originalDeadline = started + (long)(Math.Min(plan.BudgetSeconds, segment.RawBudgetSeconds) * io.Time.TimestampFrequency);
        var worldResponses = new HashSet<int>();
        CaptureFrameStamp entry = default, cannon = default, beforeFire = default;
        io.OnObservation = (phase, frame) =>
        {
            Assert.True(frame.Source.IsKnown);
            Assert.Equal(io.Time.TimestampFrequency, frame.Source.TimestampFrequency);
            Assert.True(frame.Source.IsFresh(io.Time, TimeSpan.FromMilliseconds(150)));
            if (!entry.IsKnown) entry = frame.Source;
            else Assert.True(frame.Source.IsAfter(entry));
            var releasedF = io.InputReceipts.Count(item => item.Input.Kind == PathingMacroInputKind.KeyUp && item.Input.Key == User32.VK.VK_F);
            if (io.InputReceipts.Count > 0)
            {
                var receipt = io.InputReceipts[^1].Receipt;
                Assert.Equal(CombatBattleHostInputStatus.Sent, receipt.Status);
                Assert.Equal(1, receipt.NativeRequested);
                Assert.Equal(1, receipt.NativeSubmitted);
                var completed = Assert.IsType<long>(receipt.CompletedTimestamp);
                Assert.True(new CaptureFrameFence(entry, completed).Accepts(frame.Source));
                Assert.True(io.Time.GetElapsedTime(completed, frame.Source.CapturedTimestamp) >= TimeSpan.FromMilliseconds(60));
            }
            // This is an external sensor simulation. Production never infers Cannon from a press count.
            var exited = io.InputReceipts.Any(item => item.Input.Kind == PathingMacroInputKind.KeyUp && item.Input.Key == User32.VK.VK_ESCAPE);
            var scene = exited || releasedF < 3 ? PathingMacroScene.World : PathingMacroScene.Cannon;
            io.Scene = scene;
            var observed = frame with { Scene = scene, CanFire = scene == PathingMacroScene.Cannon };
            if (releasedF is 1 or 2 && worldResponses.Add(releasedF))
            {
                Assert.Equal(PathingMacroScene.World, observed.Scene);
                Assert.False(observed.CanFire);
                Assert.False(cancellation.IsCancellationRequested);
                Assert.True(io.Time.GetTimestamp() < originalDeadline);
                using var otherOwner = io.Coordinator.TryAcquire(Guid.NewGuid(), () => { });
                Assert.Null(otherOwner);
            }
            if (scene == PathingMacroScene.Cannon && !cannon.IsKnown) cannon = frame.Source;
            if (phase == "before-fire")
            {
                Assert.Equal(PathingMacroScene.Cannon, observed.Scene);
                Assert.True(observed.CanFire);
                beforeFire = frame.Source;
            }
            return observed;
        };
        using var session = new PathingMacroSession(io);
        var result = await session.ExecuteAsync(plan, (_, _) => throw new Exception("unexpected native segment"), cancellation.Token);
        Assert.True(result.CanContinue);
        Assert.Contains(1, worldResponses);
        Assert.Contains(2, worldResponses);
        Assert.True(cannon.IsKnown);
        Assert.True(beforeFire.IsKnown);
        Assert.True(beforeFire == cannon || beforeFire.IsAfter(cannon));
        Assert.Equal(3, io.Inputs.Count(input => input == "KeyDown:VK_F"));
        var fire = Assert.Single(io.InputReceipts.Where(item => item.Input.Kind == PathingMacroInputKind.KeyDown && item.Input.Key == User32.VK.VK_RETURN));
        Assert.True(Assert.IsType<long>(fire.Receipt.CompletedTimestamp) >= beforeFire.CapturedTimestamp);
        Assert.Single(io.Inputs.Where(input => input == "KeyDown:VK_ESCAPE"));
        Assert.Equal(PathingMacroScene.World, io.Scene);
        Assert.True(io.Time.GetTimestamp() < originalDeadline);
    }

    [Fact]
    public async Task TwoAuthoredInteractionsStillInWorldCannotInventAThirdOrFire()
    {
        var plan = LegacyPathingMacroPlan.Create(CombatScriptParser.ParseContext(
            "w(.5),wait(1),keypress(f),wait(.2),keypress(f),wait(.1),keypress(RETURN),keypress(ESCAPE)", false), ["钟离"]);
        var io = new MacroReplay { Scene = PathingMacroScene.World };
        using var session = new PathingMacroSession(io);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteAsync(plan,
            (_, _) => throw new Exception("unexpected native segment"), default));
        Assert.Equal(2, io.Inputs.Count(input => input == "KeyDown:VK_F"));
        Assert.DoesNotContain("KeyDown:VK_RETURN", io.Inputs);
        Assert.DoesNotContain("KeyDown:VK_ESCAPE", io.Inputs);
        Assert.All(io.InputReceipts, item =>
        {
            Assert.Equal(CombatBattleHostInputStatus.Sent, item.Receipt.Status);
            Assert.Equal(1, item.Receipt.NativeRequested);
            Assert.Equal(1, item.Receipt.NativeSubmitted);
        });
    }


    [Theory]
    [InlineData(100, 500)]
    [InlineData(600, 100)]
    [InlineData(600, 100, 500)]
    public async Task CannonHandshakeConfirmationConsumesOnlyItsImmediatelyFollowingWait(int settleMs, int scriptWaitMs, int secondWaitMs = 0)
    {
        var io = new MacroReplay { Scene = PathingMacroScene.World, CrossCannonScenes = true, TwoStepCannonEntry = true };
        DateTimeOffset? started = null, confirmed = null;
        io.OnObservation = (phase, frame) =>
        {
            if (phase != "cannon-handshake-complete") return frame;
            started ??= io.Time.GetUtcNow();
            if ((io.Time.GetUtcNow() - started.Value).TotalMilliseconds < settleMs)
                return frame with { Scene = PathingMacroScene.Unknown };
            confirmed = io.Time.GetUtcNow();
            return frame;
        };
        var wait = (scriptWaitMs / 1000d).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var secondWait = (secondWaitMs / 1000d).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var plan = LegacyPathingMacroPlan.Create(CombatScriptParser.ParseContext(
            $"keypress(f),wait(.2),keypress(f),wait({wait}),wait({secondWait}),keypress(RETURN),keypress(ESCAPE)", false), ["钟离"]);
        using var session = new PathingMacroSession(io);
        var result = await session.ExecuteAsync(plan, (_, _) => throw new Exception("unexpected native segment"), default);
        Assert.True(result.CanContinue);
        Assert.NotNull(confirmed);
        Assert.Equal(2, io.Inputs.Count(input => input == "KeyDown:VK_F"));
        Assert.Single(io.Inputs.Where(input => input == "KeyDown:VK_RETURN"));
        Assert.Single(io.Inputs.Where(input => input == "KeyDown:VK_ESCAPE"));
        // Two key holds (70ms), exit feedback (~61ms) and only the unused portion of the wait.
        Assert.InRange((io.Time.GetUtcNow() - confirmed!.Value).TotalMilliseconds,
            130 + secondWaitMs, 135 + secondWaitMs + Math.Max(0, scriptWaitMs - settleMs - 60));
        Assert.Equal(PathingMacroScene.World, io.Scene);
    }

    [Fact]
    public async Task CannonFeedbackWaitingConsumesTheFollowingScriptWaitInsteadOfDoublingIt()
    {
        var io = new MacroReplay { Scene = PathingMacroScene.World, CrossCannonScenes = true, TwoStepCannonEntry = true };
        var lastExitInput = 0;
        var exitReady = io.Start;
        io.OnObservation = (phase, observation) =>
        {
            if (phase != "scene-transition" || io.Inputs.LastOrDefault() != "KeyUp:VK_ESCAPE") return observation;
            if (lastExitInput != io.Inputs.Count)
            {
                lastExitInput = io.Inputs.Count;
                exitReady = io.Time.GetUtcNow().AddMilliseconds(2500); // 同轮退出炮台约2.5s的反馈延迟。
            }
            return io.Time.GetUtcNow() < exitReady ? observation with { Scene = PathingMacroScene.Unknown } : observation;
        };
        var program = string.Join(",", Enumerable.Repeat("keypress(f),wait(.2),keypress(f),a(.04),keypress(ESCAPE),wait(3)", 8));
        var plan = LegacyPathingMacroPlan.Create(CombatScriptParser.ParseContext(program, false), ["钟离"]);
        using var session = new PathingMacroSession(io);
        var result = await session.ExecuteAsync(plan, (_, _) => throw new Exception("unexpected native segment"), default);
        Assert.True(result.CanContinue);
        Assert.Equal(64, io.Inputs.Count);
        Assert.Equal(PathingMacroScene.World, io.Scene);
        Assert.InRange((io.Time.GetUtcNow() - io.Start).TotalSeconds, 24, plan.Segments[0].RawBudgetSeconds);
    }

    [Fact]
    public async Task CannonActivationRejectionStopsBeforeSecondInteractionOrFiring()
    {
        var io = new MacroReplay { Scene = PathingMacroScene.World, CrossCannonScenes = true, TwoStepCannonEntry = true };
        io.OnObservation = (phase, observation) => phase.StartsWith("cannon-handshake", StringComparison.Ordinal)
            ? observation with { ActivationRejection = "requires-electro-or-electrogranum" } : observation;
        using var session = new PathingMacroSession(io);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteAsync(LegacyPathingMacroPlan.Create(
            CombatScriptParser.ParseContext("keypress(f),wait(.2),keypress(f),keypress(RETURN),keypress(ESCAPE)", false), ["钟离"]),
            (_, _) => throw new InvalidOperationException(), default));
        Assert.Contains("BGI_CANNON_PREREQUISITE", error.Message);
        Assert.Single(io.Inputs.Where(x => x == "KeyDown:VK_F"));
        Assert.DoesNotContain("KeyDown:VK_RETURN", io.Inputs);
        Assert.True((io.Time.GetUtcNow() - io.Start).TotalSeconds < 1);
    }

    [Fact]
    public async Task DeadlineCrossedInDelayStillSavesTheLastObservedPixelsWithoutAnotherCapture()
    {
        var saved = new List<BetterGenshinImpact.GameTask.Common.DiagnosticEvidence>();
        await using var evidenceScope = new BetterGenshinImpact.GameTask.Common.DiagnosticEvidenceScope(
            (item, _) => { saved.Add(item); return Task.CompletedTask; });
        var evidence = new PathingMacroEvidence("node=7", "keypress(f)");
        var io = new MacroReplay { Scene = PathingMacroScene.World };
        CaptureFrameStamp last = default;
        var waiting = false;
        io.OnObservation = (phase, observation) =>
        {
            using var frame = new BetterGenshinImpact.GameTask.Model.Area.ImageRegion(
                new OpenCvSharp.Mat(2, 2, OpenCvSharp.MatType.CV_8UC3, OpenCvSharp.Scalar.Black), 0, 0)
            { FrameStamp = observation.Source };
            evidence.Capture(frame, phase, observation);
            last = observation.Source;
            waiting = phase == "cannon-handshake-complete";
            return observation;
        };
        io.AfterDelay = () => { if (waiting) io.Time.Advance(TimeSpan.FromHours(1)); };
        io.OnEvidenceFailure = error => evidence.Failed(error);
        using var session = new PathingMacroSession(io);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ExecuteAsync(LegacyPathingMacroPlan.Create(
            CombatScriptParser.ParseContext("keypress(f),wait(.2),keypress(f),keypress(RETURN),keypress(ESCAPE)", false), ["钟离"]),
            (_, _) => throw new Exception("unexpected native segment"), default));
        evidence.End();
        await evidenceScope.DisposeAsync();
        Assert.Contains(saved, item => item.Phase == "macro-last-observation" && item.Source == last);
        Assert.Equal(4, io.Observations); // entry, before-handshake, first-F, one completion observation
        Assert.DoesNotContain("KeyDown:VK_RETURN", io.Inputs);
    }
    [Fact]
    public async Task EarlyCannonSuppressesOnlyThisAuthoredRunAndKeepsTheNextRunAfterExit()
    {
        const string program = "keypress(f),wait(.2),keypress(f),wait(.1),keypress(f),wait(.1),keypress(RETURN),keypress(ESCAPE),wait(3)," +
            "keypress(f),wait(.2),keypress(f),wait(.1),keypress(RETURN),keypress(ESCAPE)";
        var plan = LegacyPathingMacroPlan.Create(CombatScriptParser.ParseContext(program, false), ["钟离"]);
        Assert.True(Assert.Single(plan.Segments).CannonProgram);
        var io = new MacroReplay { Scene = PathingMacroScene.World, CrossCannonScenes = true };
        var fireSources = new List<CaptureFrameStamp>();
        io.OnObservation = (phase, frame) =>
        {
            if (phase == "before-fire")
            {
                Assert.Equal(PathingMacroScene.Cannon, frame.Scene);
                Assert.True(frame.CanFire);
                Assert.True(frame.Source.IsFresh(io.Time, TimeSpan.FromMilliseconds(150)));
                var receipt = io.InputReceipts[^1].Receipt;
                Assert.Equal(CombatBattleHostInputStatus.Sent, receipt.Status);
                var completed = Assert.IsType<long>(receipt.CompletedTimestamp);
                Assert.True(io.Time.GetElapsedTime(completed, frame.Source.CapturedTimestamp) >= TimeSpan.FromMilliseconds(60));
                fireSources.Add(frame.Source);
            }
            return frame;
        };
        using var session = new PathingMacroSession(io);
        var result = await session.ExecuteAsync(plan, (_, _) => throw new Exception("unexpected native segment"), default);
        Assert.True(result.CanContinue);
        Assert.Equal(new[] { "KeyDown:VK_F", "KeyDown:VK_RETURN", "KeyDown:VK_ESCAPE",
            "KeyDown:VK_F", "KeyDown:VK_RETURN", "KeyDown:VK_ESCAPE" },
            io.Inputs.Where(input => input is "KeyDown:VK_F" or "KeyDown:VK_RETURN" or "KeyDown:VK_ESCAPE").ToArray());
        Assert.Equal(2, fireSources.Count);
        Assert.True(fireSources[1].IsAfter(fireSources[0]));
        Assert.Equal(PathingMacroScene.World, io.Scene);
    }

    [Fact]
    public async Task FirstInteractionAlreadyInCannonDoesNotSendAnotherInteraction()
    {
        var io = new MacroReplay { Scene = PathingMacroScene.World, CrossCannonScenes = true };
        using var session = new PathingMacroSession(io);
        var result = await session.ExecuteAsync(LegacyPathingMacroPlan.Create(CombatScriptParser.ParseContext(
            "keypress(f),wait(.2),keypress(f),keypress(RETURN),keypress(ESCAPE)", false), ["钟离"]),
            (_, _) => throw new Exception("unexpected native segment"), default);
        Assert.True(result.CanContinue);
        Assert.Single(io.Inputs.Where(input => input == "KeyDown:VK_F"));
        Assert.Equal(PathingMacroScene.World, io.Scene);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("foreign")]
    [InlineData("before-input")]
    [InlineData("transformed")]
    public async Task InvalidFirstInteractionFeedbackCannotAuthorizeAnotherKey(string fault)
    {
        var io = new MacroReplay { Scene = PathingMacroScene.World, CrossCannonScenes = true };
        io.OnObservation = (phase, frame) => phase != "cannon-handshake-first-f" ? frame : fault switch
        {
            "stale" => frame with { Source = frame.Source with { CapturedTimestamp = frame.Source.CapturedTimestamp - io.Time.TimestampFrequency } },
            "foreign" => frame with { Source = frame.Source with { SessionId = Guid.NewGuid() } },
            "before-input" => frame with { Source = frame.Source with { CapturedTimestamp = 0 } },
            _ => frame with { Scene = PathingMacroScene.Transformed }
        };
        using var session = new PathingMacroSession(io);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteAsync(LegacyPathingMacroPlan.Create(
            CombatScriptParser.ParseContext("keypress(f),wait(.2),keypress(f),keypress(RETURN),keypress(ESCAPE)", false), ["钟离"]),
            (_, _) => throw new Exception("unexpected native segment"), default));
        Assert.Equal(new[] { "KeyDown:VK_F", "KeyUp:VK_F" }, io.Inputs);
    }
    [Fact]
    public async Task EvidenceFailureCannotReplaceRawInputFailureOrPreventHeldKeyCleanup()
    {
        var original = new IOException("original dispatch");
        var callbacks = 0;
        var io = new MacroReplay
        {
            FaultAt = 1, Fault = CombatBattleHostInputStatus.Failed, FaultError = original
        };
        io.OnEvidenceFailure = error =>
        {
            callbacks++;
            Assert.Same(original, error.InnerException);
            Assert.Equal("KeyUp:VK_W", io.Inputs[^1]);
            throw new IOException("diagnostics");
        };
        using var session = new PathingMacroSession(io);
        var failed = await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteAsync(
            LegacyPathingMacroPlan.Create(CombatScriptParser.ParseContext("keypress(w)", false), ["琴"]),
            (_, _) => throw new Exception("unexpected native segment"), default));
        Assert.Same(original, failed.InnerException);
        Assert.Equal(1, callbacks);
        Assert.Equal(new[] { "KeyDown:VK_W", "KeyUp:VK_W" }, io.Inputs);
    }

    [Fact]
    public async Task NativeSegmentFailureCannotBeAttributedToPreviousSuccessfulRawSegment()
    {
        var original = new IOException("humanoid runner");
        var callbacks = 0;
        var io = new MacroReplay { OnEvidenceFailure = _ => callbacks++ };
        using var session = new PathingMacroSession(io);
        Assert.Same(original, await Assert.ThrowsAsync<IOException>(() => session.ExecuteAsync(
            LegacyPathingMacroPlan.Create(CombatScriptParser.ParseContext("wait(.1);琴 q", false), ["琴"]),
            (_, _) => throw original, default)));
        Assert.Equal(0, callbacks);
    }

    [Fact]
    public async Task RecordedFlightPhysicalMouseSequenceNeverEntersHumanoidRunner()
    {
        var io = new MacroReplay { Scene = PathingMacroScene.Transformed };
        using var session = new PathingMacroSession(io);
        var result = await session.ExecuteAsync(LegacyPathingMacroPlan.Create(CombatScriptParser.ParseContext(
            "j,wait(4.5),j,wait(0.3),mousedown,wait(1.2),moveby(-2100,0),wait(0.25),moveby(-2200,0)", false), ["钟离"]),
            (_, _) => throw new InvalidOperationException("飞行形态的物理鼠标宏不能变成人形技能"), default);
        Assert.True(result.CanContinue);
        Assert.Single(io.Inputs.Where(x => x.StartsWith("LeftDown:")));
        Assert.Single(io.Inputs.Where(x => x.StartsWith("LeftUp:")));
        Assert.True(io.Inputs[^1].StartsWith("LeftUp:"));
    }

    [Fact]
    public async Task CannonHandshakeCanFinishItsOriginalSecondInteractionBeforeObservingStableScene()
    {
        var io = new MacroReplay { Scene = PathingMacroScene.World, CrossCannonScenes = true, TwoStepCannonEntry = true };
        using var session = new PathingMacroSession(io);
        var plan = LegacyPathingMacroPlan.Create(CombatScriptParser.ParseContext(
            "keypress(f),wait(.2),keypress(f),keypress(w),keypress(RETURN),keypress(ESCAPE)", false), ["钟离"]);
        var result = await session.ExecuteAsync(plan, (_, _) => throw new Exception("unexpected native segment"), default);
        Assert.True(result.CanContinue);
        Assert.Equal(new[] { "KeyDown:VK_F", "KeyUp:VK_F", "KeyDown:VK_F", "KeyUp:VK_F",
            "KeyDown:VK_W", "KeyUp:VK_W", "KeyDown:VK_RETURN", "KeyUp:VK_RETURN",
            "KeyDown:VK_ESCAPE", "KeyUp:VK_ESCAPE" }, io.Inputs);
        Assert.Equal(PathingMacroScene.World, io.Scene);
    }

    [Theory]
    [InlineData("keypress(w)")]
    [InlineData("d(.1)")]
    public async Task CannonTurningAnimationMustFinishBeforeExit(string direction)
    {
        var io = new MacroReplay { Scene = PathingMacroScene.World, CrossCannonScenes = true,
            TwoStepCannonEntry = true, CannonAnimationMilliseconds = 1200 };
        using var session = new PathingMacroSession(io);
        var plan = LegacyPathingMacroPlan.Create(CombatScriptParser.ParseContext(
            $"keypress(f),wait(.2),keypress(f),{direction},wait(.1),keypress(ESCAPE),wait(.1),keypress(f),wait(.2),keypress(f),keypress(ESCAPE)",
            false), ["钟离"]);
        var result = await session.ExecuteAsync(plan, (_, _) => throw new Exception("unexpected native segment"), default);
        Assert.True(result.CanContinue);
        Assert.Equal(2, io.Inputs.Count(x => x == "KeyDown:VK_ESCAPE"));
        Assert.Equal(PathingMacroScene.World, io.Scene);
    }

    [Fact]
    public async Task CannonAnimationCannotRenewDeadlineOrCauseRepeatedDirectionInput()
    {
        var io = new MacroReplay { Scene = PathingMacroScene.World, CrossCannonScenes = true,
            TwoStepCannonEntry = true, CannonAnimationMilliseconds = int.MaxValue };
        using var session = new PathingMacroSession(io);
        var plan = LegacyPathingMacroPlan.Create(CombatScriptParser.ParseContext(
            "keypress(f),wait(.2),keypress(f),keypress(w),keypress(RETURN),keypress(ESCAPE)", false), ["钟离"]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteAsync(plan,
            (_, _) => throw new Exception("unexpected native segment"), default));
        Assert.Single(io.Inputs.Where(x => x == "KeyDown:VK_W"));
        Assert.Single(io.Inputs.Where(x => x == "KeyUp:VK_W"));
        Assert.DoesNotContain("KeyDown:VK_ESCAPE", io.Inputs);
        Assert.DoesNotContain("KeyDown:VK_RETURN", io.Inputs);
        Assert.True((io.Time.GetUtcNow() - io.Start).TotalSeconds <= plan.Segments[0].RawBudgetSeconds + .1);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task TransientUnknownAtBoundaryWaitsWithoutReplayingMacro(int unknownAt)
    {
        var io = new MacroReplay { UnknownAt = unknownAt, UnknownCount = 2 };
        using var session = new PathingMacroSession(io);
        var result = await session.ExecuteAsync(LegacyPathingMacroPlan.Create(
            CombatScriptParser.ParseContext("keypress(e)", false), ["钟离"]), (_, _) => throw new Exception(), default);
        Assert.Equal(CombatExecutionKind.Completed, result.Kind);
        Assert.Equal(new[] { "KeyDown:VK_E", "KeyUp:VK_E" }, io.Inputs);
        Assert.True(io.Observations >= 4);
        Assert.InRange((io.Time.GetUtcNow() - io.Start).TotalSeconds, .29, 1);
    }

    [Fact]
    public async Task TrailingXCleanupFailureBlocksOwnerWithoutRepeatingUnknownRelease()
    {
        var original = new IOException("original X-up failure");
        var io = new MacroReplay { FaultAt = 2, Fault = CombatBattleHostInputStatus.Unknown, FaultError = original };
        var session = new PathingMacroSession(io);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteAsync(LegacyPathingMacroPlan.Create(
            CombatScriptParser.ParseContext("keydown(x)", false), ["钟离"]), (_, _) => throw new Exception(), default));
        Assert.Same(original, failure.InnerException);
        Assert.Equal(new[] { "KeyDown:VK_X", "KeyUp:VK_X" }, io.Inputs);
        Assert.Null(io.Coordinator.TryAcquire(Guid.NewGuid(), () => { }));
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => session.Release()));
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => session.Dispose()));
    }

    [Fact]
    public async Task Recorded07TrailingXUsesLegacyFragmentCleanupInsteadOfAbortingRoute()
    {
        var io = new MacroReplay();
        using var session = new PathingMacroSession(io);
        var result = await session.ExecuteAsync(LegacyPathingMacroPlan.Create(
            CombatScriptParser.ParseContext("keydown(x)", false), ["钟离"]), (_, _) => throw new Exception(), default);
        Assert.Equal(CombatExecutionKind.Completed, result.Kind);
        Assert.Equal(new[] { "KeyDown:VK_X", "KeyUp:VK_X" }, io.Inputs);
        Assert.False(session.HasTail);
        using var next = io.Coordinator.TryAcquire(Guid.NewGuid(), () => { });
        Assert.NotNull(next);
    }

    [Fact]
    public void NativeCleanupStillSubmitsOnlyUpAfterRealTaskScopeFailure()
    {
        using var scope = TaskExecutionScope.BeginOwned();
        Assert.NotNull(Record.Exception(() => TaskExecutionScope.StopUnconfirmedCombat("terminal test")));
        var submissions = 0;
        var dispatcher = new WindowsInputMessageDispatcher(null,
            inputs => { submissions++; return (uint)inputs.Length; }, () => 0);
        var io = new NativePathingMacroIo(() => "offline", input =>
        {
            Assert.Equal(PathingMacroInputKind.KeyUp, input.Kind);
            dispatcher.DispatchInput(new User32.INPUT[1]);
        });
        var receipt = io.Release(new(PathingMacroInputKind.KeyUp, User32.VK.VK_W));
        Assert.Equal(CombatBattleHostInputStatus.Sent, receipt.Status);
        Assert.Equal(1, receipt.NativeSubmitted);
        Assert.Equal(1, submissions);
        Assert.Throws<ArgumentException>(() => io.Release(new(PathingMacroInputKind.KeyDown, User32.VK.VK_W)));
    }

    [Fact]
    public async Task FailedFirstCleanupStillAttemptsOtherHeldKeysButNeverTransfersOwnership()
    {
        using var cancellation = new CancellationTokenSource();
        var io = new MacroReplay { FaultAt = 3, Fault = CombatBattleHostInputStatus.Unknown,
            AfterDelay = () => cancellation.Cancel() };
        var session = new PathingMacroSession(io);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteAsync(LegacyPathingMacroPlan.Create(
            CombatScriptParser.ParseContext("keydown(q),keydown(w),wait(.1)", false), ["琴"]),
            (_, _) => throw new Exception(), cancellation.Token));
        Assert.Equal(new[] { "KeyDown:VK_Q", "KeyDown:VK_W", "KeyUp:VK_Q", "KeyUp:VK_W" }, io.Inputs);
        Assert.Null(io.Coordinator.TryAcquire(Guid.NewGuid(), () => { }));
        Assert.Throws<InvalidOperationException>(() => session.Dispose());
    }

    [Fact]
    public async Task CancellationDuringOriginalWaitReleasesHeldKeyAndStopsLaterInput()
    {
        using var cancellation = new CancellationTokenSource();
        var io = new MacroReplay { AfterDelay = () => cancellation.Cancel() };
        using var session = new PathingMacroSession(io);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ExecuteAsync(LegacyPathingMacroPlan.Create(
            CombatScriptParser.ParseContext("keydown(w),wait(0.5),keypress(e)", false), ["琴"]),
            (_, _) => throw new Exception(), cancellation.Token));
        Assert.Equal(new[] { "KeyDown:VK_W", "KeyUp:VK_W" }, io.Inputs);
        Assert.False(session.HasTail);
    }

    [Fact]
    public async Task NativeSegmentCannotRenewWholeFragmentBudget()
    {
        var io = new MacroReplay();
        using var session = new PathingMacroSession(io);
        var plan = LegacyPathingMacroPlan.Create(CombatScriptParser.ParseContext("wait(.1);琴 q;keypress(t)", false), ["琴"]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ExecuteAsync(plan, (_, _) =>
        {
            io.Time.Advance(TimeSpan.FromSeconds(30));
            return Task.FromResult(new CombatExecutionResult(CombatExecutionKind.Completed, "late-native"));
        }, default));
        Assert.Empty(io.Inputs);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task UnknownEntryOrPostSceneCannotProceedToNextSegment(int unknownAt)
    {
        var io = new MacroReplay { UnknownAt = unknownAt };
        using var session = new PathingMacroSession(io);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteAsync(LegacyPathingMacroPlan.Create(
            CombatScriptParser.ParseContext("keydown(w),wait(.1),keyup(w);琴 e", false), ["琴"]),
            (_, _) => throw new Exception("未知不能进入具名段"), default));
        Assert.False(session.HasTail);
    }

    [Fact]
    public async Task All38RecordedNatlanMacrosKeepOriginalCommandsAndCompletePhysicalSegments()
    {
        using var corpus = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "Combat", "recorded-natlan-macros.json")));
        Assert.Equal(38, corpus.RootElement.GetArrayLength());
        foreach (var item in corpus.RootElement.EnumerateArray())
        {
            var script = CombatScriptParser.ParseContext(item.GetProperty("script").GetString()!, false);
            var plan = LegacyPathingMacroPlan.Create(script, ["枫原万叶", "琴", "芙宁娜"]);
            var planned = plan.Segments.SelectMany(segment => segment.Commands).ToArray();
            // 用户新规则只抑制相邻的琴聚物替代块，匿名/万叶及其他角色顺序保持。
            Assert.Equal(script.CombatCommands.Where(command => command.Name != "琴"), planned.Where(command => command.Name != "琴"));
            if (script.CombatCommands.Any(command => command.Name == "枫原万叶")) Assert.DoesNotContain(planned, command => command.Name == "琴");
            var io = new MacroReplay();
            using var session = new PathingMacroSession(io);
            var result = await session.ExecuteAsync(plan, (commands, _) =>
            {
                // NativeRunner入口的完整命令必须保留配对E；这些指令不送入raw物理端口。
                var down = commands.Count(command => command.Method == Method.KeyDown);
                var up = commands.Count(command => command.Method == Method.KeyUp);
                Assert.Equal(down, up);
                return Task.FromResult(new CombatExecutionResult(CombatExecutionKind.Completed, "native-boundary"));
            }, default);
            Assert.Equal(CombatExecutionKind.Completed, result.Kind);
        }
    }

    [Theory]
    [InlineData((int)CombatBattleHostInputStatus.Failed)]
    [InlineData((int)CombatBattleHostInputStatus.Unknown)]
    [InlineData((int)CombatBattleHostInputStatus.NotSent)]
    public async Task NativeFailureStopsWithoutReplayingAndCleansOriginalKey(int fault)
    {
        var io = new MacroReplay { FaultAt = 1, Fault = (CombatBattleHostInputStatus)fault };
        using var session = new PathingMacroSession(io);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteAsync(LegacyPathingMacroPlan.Create(
            CombatScriptParser.ParseContext("keydown(w),keypress(e),keypress(t)", false), ["琴"]),
            (_, _) => throw new Exception(), default));
        Assert.Equal(new[] { "KeyDown:VK_W", "KeyUp:VK_W" }, io.Inputs);
        using var next = io.Coordinator.TryAcquire(Guid.NewGuid(), () => { });
        Assert.NotNull(next);
    }

    [Fact]
    public async Task MappingChangeStillReleasesOriginalPhysicalKey()
    {
        var io = new MacroReplay { Mapping = _ => User32.VK.VK_Z };
        using var session = new PathingMacroSession(io);
        await session.ExecuteAsync(LegacyPathingMacroPlan.Create(
            CombatScriptParser.ParseContext("keydown(w)", false), ["琴"]), (_, _) => throw new Exception(), default);
        io.Mapping = _ => User32.VK.VK_Y;
        session.AdoptNavigation(io.Observe(), true, default);
        Assert.False(session.HasTail);
        Assert.Equal(new[] { "KeyDown:VK_Z", "KeyUp:VK_Z" }, io.Inputs);
    }

    [Fact]
    public async Task FailedReleaseRetainsCoordinatorOwnership()
    {
        var io = new MacroReplay { FaultAt = 2, Fault = CombatBattleHostInputStatus.Unknown };
        var session = new PathingMacroSession(io);
        await session.ExecuteAsync(LegacyPathingMacroPlan.Create(
            CombatScriptParser.ParseContext("keydown(w)", false), ["琴"]), (_, _) => throw new Exception(), default);
        Assert.Throws<InvalidOperationException>(() => session.Release());
        Assert.Null(io.Coordinator.TryAcquire(Guid.NewGuid(), () => { }));
        Assert.Throws<InvalidOperationException>(() => session.Dispose());
        Assert.Equal(2, io.Inputs.Count);
    }

    [Fact]
    public async Task TailDeadlineBeforeNavigationReleasesAndCannotBeRenewed()
    {
        var io = new MacroReplay();
        using var session = new PathingMacroSession(io);
        await session.ExecuteAsync(LegacyPathingMacroPlan.Create(
            CombatScriptParser.ParseContext("keydown(w)", false), ["琴"]), (_, _) => throw new Exception(), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.WaitAsync(9000, default));
        Assert.False(session.HasTail);
        Assert.Equal(new[] { "KeyDown:VK_W", "KeyUp:VK_W" }, io.Inputs);
    }

    [Fact]
    public async Task Recorded638TailIsNotRepressedAndNavigationUpRetiresOwner()
    {
        var io = new MacroReplay();
        using var session = new PathingMacroSession(io);
        await session.ExecuteAsync(LegacyPathingMacroPlan.Create(
            CombatScriptParser.ParseContext("keydown(w)", false), ["琴"]), (_, _) => throw new Exception(), default);
        await session.WaitAsync(1000, default);
        session.AdoptNavigation(io.Observe(), validPosition: true, default);
        Assert.True(session.TryNavigationForward(down: true, default));
        Assert.True(session.TryNavigationForward(down: false, default));
        Assert.Equal(new[] { "KeyDown:VK_W", "KeyUp:VK_W" }, io.Inputs);
        using var nextOwner = io.Coordinator.TryAcquire(Guid.NewGuid(), () => { });
        Assert.NotNull(nextOwner);
    }

    [Fact]
    public async Task Recorded619ExitMacroKeepsTimingWithoutPerKeyObservation()
    {
        var io = new MacroReplay();
        using var session = new PathingMacroSession(io);
        var script = CombatScriptParser.ParseContext(
            "wait(0.5),keydown(Q),keypress(f),wait(0.8),keypress(f),keyup(Q),wait(0.1),j,keypress(f)", false);
        var result = await session.ExecuteAsync(LegacyPathingMacroPlan.Create(script, ["琴"]),
            (_, _) => throw new Exception("物理宏不能选择人形角色"), default);
        Assert.True(result.CanContinue);
        Assert.Equal(new[] { "KeyDown:VK_Q", "KeyDown:VK_F", "KeyUp:VK_F", "KeyDown:VK_F", "KeyUp:VK_F", "KeyUp:VK_Q", "KeyDown:VK_SPACE", "KeyUp:VK_SPACE", "KeyDown:VK_F", "KeyUp:VK_F" }, io.Inputs);
        Assert.Equal(2, io.Observations);
        // 原wait共1.4秒，四个35ms脉冲，最后60ms响应等待；不逐键取帧。
        Assert.InRange(io.Time.GetUtcNow() - io.Start, TimeSpan.FromSeconds(1.6), TimeSpan.FromSeconds(1.61));
    }

    [Fact]
    public async Task CannonProgramKeepsFireAndExitWithinItsOwnedPhysicalSequence()
    {
        var io = new MacroReplay { Scene = PathingMacroScene.Cannon, CrossCannonScenes = true };
        using var session = new PathingMacroSession(io);
        var plan = LegacyPathingMacroPlan.Create(CombatScriptParser.ParseContext(
            "keypress(f),wait(.1),keypress(w),keypress(RETURN),wait(.1),keypress(ESCAPE)", false), ["琴"]);
        var result = await session.ExecuteAsync(plan,
            (_, _) => throw new InvalidOperationException("Cannon program must not enter the humanoid skill runner"), default);
        Assert.True(result.CanContinue);
        Assert.Contains("KeyDown:VK_RETURN", io.Inputs);
        Assert.Contains("KeyDown:VK_ESCAPE", io.Inputs);
        Assert.False(session.HasTail);
    }

    [Theory]
    [InlineData("keypress(e)")]
    [InlineData("keypress(q)")]
    [InlineData("keydown(w)")]
    [InlineData("moveby(10,0)")]
    public async Task CannonDoesNotGrantSkillMouseOrNavigationTail(string text)
    {
        var io = new MacroReplay { Scene = PathingMacroScene.Cannon };
        using var session = new PathingMacroSession(io);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteAsync(
            LegacyPathingMacroPlan.Create(CombatScriptParser.ParseContext(text, false), ["琴"]),
            (_, _) => throw new InvalidOperationException("not admitted"), default));
        Assert.Empty(io.Inputs);
    }

    [Fact]
    public async Task CannonWithoutFirePromptCannotSendReturn()
    {
        var io = new MacroReplay { Scene = PathingMacroScene.Cannon, FirePrompt = false };
        using var session = new PathingMacroSession(io);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteAsync(
            LegacyPathingMacroPlan.Create(CombatScriptParser.ParseContext("keypress(f),keypress(RETURN),keypress(ESCAPE)", false), ["琴"]),
            (_, _) => throw new Exception(), default));
        Assert.DoesNotContain("KeyDown:VK_RETURN", io.Inputs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThreeRecordedCannonProgramsCrossWorldBoundariesWithoutReplayingInput(bool delayedFeedback)
    {
        using var programs = System.Text.Json.JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Combat", "s56-cannon-programs.json")));
        Assert.Equal(3, programs.RootElement.GetArrayLength());
        foreach (var item in programs.RootElement.EnumerateArray())
        {
            var text = item.GetProperty("script").GetString()!;
            var io = new MacroReplay { Scene = PathingMacroScene.World, CrossCannonScenes = true, TwoStepCannonEntry = true };
            if (delayedFeedback)
            {
                var transitions = new Dictionary<(string Phase, int InputCount), DateTimeOffset>();
                io.OnObservation = (phase, frame) =>
                {
                    var milliseconds = phase == "cannon-handshake-complete" ? 600 :
                        phase == "scene-transition" && io.Inputs.LastOrDefault() == "KeyUp:VK_ESCAPE" ? 2500 : 0;
                    if (milliseconds == 0) return frame;
                    var key = (phase, io.Inputs.Count);
                    if (!transitions.TryGetValue(key, out var started)) transitions[key] = started = io.Time.GetUtcNow();
                    return (io.Time.GetUtcNow() - started).TotalMilliseconds < milliseconds
                        ? frame with { Scene = PathingMacroScene.Unknown } : frame;
                };
            }
            var parsed = CombatScriptParser.ParseContext(text, false);
            var plan = LegacyPathingMacroPlan.Create(parsed, ["琴"]);
            var raw = Assert.Single(plan.Segments);
            Assert.True(raw.IsRaw);
            Assert.True(raw.CannonProgram);
            Assert.Equal(parsed.CombatCommands.ToArray(), raw.Commands.ToArray());
            static User32.VK? AuthoredKey(CombatCommand command)
            {
                if (command.Method == Method.W) return User32.VK.VK_W;
                if (command.Method == Method.A) return User32.VK.VK_A;
                if (command.Method == Method.S) return User32.VK.VK_S;
                if (command.Method == Method.D) return User32.VK.VK_D;
                return command.Method == Method.KeyPress
                    ? BetterGenshinImpact.Helpers.User32Helper.ToVk(command.Args![0]) : null;
            }
            Assert.All(raw.Commands, command => Assert.True(command.Method == Method.Wait || AuthoredKey(command).HasValue));
            var authoredF = raw.Commands.Select((command, index) => (command, index))
                .Where(value => AuthoredKey(value.command) == User32.VK.VK_F).Select(value => value.index).ToArray();
            Assert.Equal(text.Split("keypress(f)").Length - 1, authoredF.Length);
            var suppressedOriginalIndices = new HashSet<int>();
            var threeFProgram = item.GetProperty("name").GetString() == "001【激活程序】稻妻大炮1.json";
            if (threeFProgram)
            {
                Assert.Equal(23, authoredF.Length); // Original fixture/plan is unchanged, including its third F.
                Assert.Equal(new[] { 2, 4, 6 }, raw.Commands.Select((command, index) => (command, index))
                    .TakeWhile(value => AuthoredKey(value.command) != User32.VK.VK_RETURN)
                    .Where(value => AuthoredKey(value.command) == User32.VK.VK_F).Select(value => value.index).ToArray());
                Assert.Equal(Method.Wait, raw.Commands[5].Method);
                Assert.Equal("0.1", raw.Commands[5].Args![0]);
            }
            using var session = new PathingMacroSession(io);
            var result = await session.ExecuteAsync(plan, (_, _) => throw new Exception("unexpected humanoid runner"), default);
            Assert.True(result.CanContinue);
            Assert.Equal(PathingMacroScene.World, io.Scene);
            Assert.False(session.HasTail);
            Assert.Equal(text.Split("keypress(RETURN)").Length - 1, io.Inputs.Count(x => x == "KeyDown:VK_RETURN"));
            Assert.Equal(text.Split("keypress(ESCAPE)").Length - 1, io.Inputs.Count(x => x == "KeyDown:VK_ESCAPE"));
            var entry = io.ObservationReceipts.First().Observation.Source;
            if (threeFProgram)
            {
                const int thirdOriginalF = 6;
                var prefixEvents = raw.Commands.Take(thirdOriginalF).Count(command => AuthoredKey(command).HasValue) * 2;
                var satisfied = Assert.Single(io.ObservationReceipts.Where(value => value.Phase == "cannon-handshake-continue" &&
                    value.InputCount == prefixEvents && value.Observation.Scene == PathingMacroScene.Cannon));
                var observed = satisfied.Observation;
                Assert.Equal(thirdOriginalF - 1, satisfied.CommandIndex);
                Assert.True(satisfied.ObservedAt < satisfied.Deadline);
                Assert.True(observed.CanFire);
                Assert.Equal(entry.SessionId, observed.Source.SessionId);
                Assert.True(observed.Source.IsAfter(entry));
                Assert.Equal(io.Time.TimestampFrequency, observed.Source.TimestampFrequency);
                Assert.InRange(io.Time.GetElapsedTime(observed.Source.CapturedTimestamp, satisfied.ObservedAt).TotalMilliseconds, 0, 150);
                var completed = Assert.IsType<long>(io.InputReceipts[prefixEvents - 1].Receipt.CompletedTimestamp);
                Assert.Equal(completed, satisfied.InputFence);
                Assert.Equal(new PathingMacroInput(PathingMacroInputKind.KeyUp, User32.VK.VK_F), io.InputReceipts[prefixEvents - 1].Input);
                Assert.True(new CaptureFrameFence(entry, completed).Accepts(observed.Source));
                Assert.True(io.Time.GetElapsedTime(completed, observed.Source.CapturedTimestamp) >= TimeSpan.FromMilliseconds(60));
                suppressedOriginalIndices.Add(thirdOriginalF); // Only this run's already-satisfied third interaction is omitted.
            }
            else Assert.DoesNotContain(io.ObservationReceipts, value => value.Phase == "cannon-handshake-continue");
            var expectedKeys = raw.Commands.Select((command, index) => (Key: AuthoredKey(command), Index: index))
                .Where(value => value.Key.HasValue && !suppressedOriginalIndices.Contains(value.Index))
                .Select(value => value.Key!.Value).ToArray();
            Assert.Equal(authoredF.Length - suppressedOriginalIndices.Count, io.Inputs.Count(x => x == "KeyDown:VK_F"));
            Assert.Equal(expectedKeys, io.InputReceipts.Where(value => value.Input.Kind == PathingMacroInputKind.KeyDown).Select(value => value.Input.Key).ToArray());
            Assert.Equal(expectedKeys, io.InputReceipts.Where(value => value.Input.Kind == PathingMacroInputKind.KeyUp).Select(value => value.Input.Key).ToArray());
            Assert.All(io.InputReceipts, value =>
            {
                Assert.Equal(CombatBattleHostInputStatus.Sent, value.Receipt.Status);
                Assert.Null(value.Receipt.Error);
                Assert.Equal(1, value.Receipt.NativeRequested);
                Assert.Equal(1, value.Receipt.NativeSubmitted);
                Assert.NotNull(value.Receipt.CompletedTimestamp);
            });
            var fireChecks = io.ObservationReceipts.Where(value => value.Phase == "before-fire").ToArray();
            Assert.Equal(text.Split("keypress(RETURN)").Length - 1, fireChecks.Length);
            foreach (var fire in fireChecks)
            {
                Assert.Equal(PathingMacroScene.Cannon, fire.Observation.Scene);
                Assert.True(fire.Observation.CanFire);
                Assert.Equal(entry.SessionId, fire.Observation.Source.SessionId);
                Assert.InRange(io.Time.GetElapsedTime(fire.Observation.Source.CapturedTimestamp, fire.ObservedAt).TotalMilliseconds, 0, 150);
                var completed = Assert.IsType<long>(io.InputReceipts[fire.InputCount - 1].Receipt.CompletedTimestamp);
                Assert.True(new CaptureFrameFence(entry, completed).Accepts(fire.Observation.Source));
                Assert.True(io.Time.GetElapsedTime(completed, fire.Observation.Source.CapturedTimestamp) >= TimeSpan.FromMilliseconds(60));
                Assert.Equal(new PathingMacroInput(PathingMacroInputKind.KeyDown, User32.VK.VK_RETURN), io.InputReceipts[fire.InputCount].Input);
            }
            Assert.InRange((io.Time.GetUtcNow() - io.Start).TotalSeconds, 0, Math.Min(plan.BudgetSeconds, raw.RawBudgetSeconds));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task CannonHandshakeNeverContinuesAfterUncertainInput(int faultAt)
    {
        var io = new MacroReplay { Scene = PathingMacroScene.World, CrossCannonScenes = true,
            TwoStepCannonEntry = true, FaultAt = faultAt, Fault = CombatBattleHostInputStatus.Unknown };
        using var session = new PathingMacroSession(io);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteAsync(LegacyPathingMacroPlan.Create(
            CombatScriptParser.ParseContext("keypress(f),wait(.2),keypress(f),keypress(RETURN),keypress(ESCAPE)", false), ["钟离"]),
            (_, _) => throw new Exception(), default));
        Assert.DoesNotContain("KeyDown:VK_RETURN", io.Inputs);
        Assert.True(io.Inputs.Count(x => x == "KeyDown:VK_F") <= (faultAt <= 2 ? 1 : 2));
    }

    [Theory]
    [InlineData("keypress(f),wait(.8),keypress(f),keypress(RETURN),keypress(ESCAPE)")]
    [InlineData("keypress(f),wait(.2),keypress(f),keypress(f),keypress(RETURN),keypress(ESCAPE)")]
    [InlineData("keypress(f),wait(.2),keypress(f)")]
    public async Task CannonHandshakeDoesNotBroadenOtherMacroShapes(string text)
    {
        var io = new MacroReplay { Scene = PathingMacroScene.World, CrossCannonScenes = true, TwoStepCannonEntry = true };
        using var session = new PathingMacroSession(io);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteAsync(
            LegacyPathingMacroPlan.Create(CombatScriptParser.ParseContext(text, false), ["钟离"]), (_, _) => throw new Exception(), default));
        Assert.Single(io.Inputs.Where(x => x == "KeyDown:VK_F"));
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("mapping")]
    [InlineData("never-cannon")]
    [InlineData("never-exit")]
    public async Task CannonHandshakeStillRequiresCancellationMappingAndPostSceneEvidence(string failure)
    {
        using var cancellation = new CancellationTokenSource();
        var io = new MacroReplay { Scene = PathingMacroScene.World, CrossCannonScenes = true, TwoStepCannonEntry = true };
        if (failure == "mapping") io.Mapping = key => key == User32.VK.VK_F ? User32.VK.VK_Z : key;
        io.AfterDelay = () =>
        {
            if (failure == "cancel" && io.Inputs.Contains("KeyUp:VK_F")) cancellation.Cancel();
            if (failure == "never-cannon" && io.Inputs.Count(x => x == "KeyUp:VK_F") >= 2) io.Scene = PathingMacroScene.Unknown;
            if (failure == "never-exit" && io.Inputs.Contains("KeyUp:VK_ESCAPE")) io.Scene = PathingMacroScene.Cannon;
        };
        using var session = new PathingMacroSession(io);
        Assert.NotNull(await Record.ExceptionAsync(() => session.ExecuteAsync(LegacyPathingMacroPlan.Create(
            CombatScriptParser.ParseContext("keypress(f),wait(.2),keypress(f),keypress(RETURN),keypress(ESCAPE)", false), ["钟离"]),
            (_, _) => throw new Exception(), cancellation.Token)));
        if (failure != "never-exit") Assert.DoesNotContain("KeyDown:VK_RETURN", io.Inputs);
        if (failure == "mapping") Assert.Empty(io.Inputs);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("down-unknown")]
    [InlineData("down-failed")]
    public async Task FlightMouseFailureReleasesHeldPhysicalInput(string failure)
    {
        using var cancellation = new CancellationTokenSource();
        var io = new MacroReplay();
        if (failure == "cancel") io.AfterDelay = () =>
        {
            if (io.Inputs.Any(x => x.StartsWith("LeftDown:"))) cancellation.Cancel();
        };
        else
        {
            io.FaultAt = 1;
            io.Fault = failure == "down-unknown" ? CombatBattleHostInputStatus.Unknown : CombatBattleHostInputStatus.Failed;
        }
        using var session = new PathingMacroSession(io);
        Assert.NotNull(await Record.ExceptionAsync(() => session.ExecuteAsync(LegacyPathingMacroPlan.Create(
            CombatScriptParser.ParseContext("mousedown,wait(.2),moveby(100,0)", false), ["琴"]),
            (_, _) => throw new Exception("unexpected humanoid runner"), cancellation.Token)));
        Assert.Single(io.Inputs.Where(x => x.StartsWith("LeftDown:")));
        Assert.Single(io.Inputs.Where(x => x.StartsWith("LeftUp:")));
        Assert.DoesNotContain(io.Inputs, x => x.StartsWith("MoveBy:"));
        using var next = io.Coordinator.TryAcquire(Guid.NewGuid(), () => { });
        Assert.NotNull(next);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UncertainFlightMouseReleaseCleansOtherKeysAndBlocksHandoff(bool explicitUp)
    {
        var io = new MacroReplay { FaultAt = 3, Fault = CombatBattleHostInputStatus.Unknown };
        var session = new PathingMacroSession(io);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteAsync(LegacyPathingMacroPlan.Create(
            CombatScriptParser.ParseContext("keydown(w),mousedown" + (explicitUp ? ",mouseup" : ""), false), ["琴"]),
            (_, _) => throw new Exception("unexpected humanoid runner"), default));
        Assert.Single(io.Inputs.Where(x => x.StartsWith("LeftUp:")));
        Assert.Contains("KeyUp:VK_W", io.Inputs);
        Assert.Null(io.Coordinator.TryAcquire(Guid.NewGuid(), () => { }));
        Assert.ThrowsAny<Exception>(() => session.Dispose());
        Assert.Single(io.Inputs.Where(x => x.StartsWith("LeftUp:")));
    }

    [Fact]
    public async Task CannonSceneDoesNotPermitFlightMouseInput()
    {
        var io = new MacroReplay { Scene = PathingMacroScene.Cannon };
        using var session = new PathingMacroSession(io);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteAsync(LegacyPathingMacroPlan.Create(
            CombatScriptParser.ParseContext("mousedown,mouseup", false), ["琴"]),
            (_, _) => throw new Exception(), default));
        Assert.Empty(io.Inputs);
    }

    internal sealed class MacroReplay : IPathingMacroIo
    {
        internal FakeTimeProvider Time { get; } = new();
        public TimeProvider Clock => Time;
        public CombatInputCoordinator Coordinator { get; } = new();
        internal DateTimeOffset Start;
        internal List<string> Inputs { get; } = [];
        internal List<(PathingMacroInput Input, CombatBattleHostInputResult Receipt)> InputReceipts { get; } = [];
        internal List<(string Phase, PathingMacroObservation Observation, int InputCount, long ObservedAt, int CommandIndex, long Deadline, long InputFence)> ObservationReceipts { get; } = [];
        private int _evidenceCommandIndex = -1;
        private long _evidenceDeadline, _evidenceInputFence;
        public void EvidenceBoundary(int commandIndex, long deadline, long inputFence)
        {
            _evidenceCommandIndex = commandIndex;
            _evidenceDeadline = deadline;
            _evidenceInputFence = inputFence;
        }
        internal int Observations;
        internal Func<string, PathingMacroObservation, PathingMacroObservation>? OnObservation;
        internal Func<User32.VK, User32.VK> Mapping = key => key;
        internal int FaultAt;
        internal CombatBattleHostInputStatus Fault;
        internal Exception? FaultError;
        internal Action? AfterDelay;
        internal Action<Exception>? OnEvidenceFailure;
        public void FailEvidence(Exception error) => OnEvidenceFailure?.Invoke(error);
        internal int UnknownAt;
        internal int UnknownCount = int.MaxValue;
        internal PathingMacroScene Scene = PathingMacroScene.Transformed;
        internal bool FirePrompt = true, CrossCannonScenes, TwoStepCannonEntry;
        internal int CannonAnimationMilliseconds;
        private DateTimeOffset _animationUntil;
        private readonly CaptureFrameSource _source;
        internal CaptureFrameStamp NextFrame() { Time.Advance(TimeSpan.FromMilliseconds(1)); return _source.Next(); }
        public MacroReplay() { _source = new(Time); Start = Time.GetUtcNow(); }
        public PathingMacroObservation Observe(string phase = "boundary")
        {
            Observations++;
            Time.Advance(TimeSpan.FromMilliseconds(1));
            if (_animationUntil != default && Time.GetUtcNow() >= _animationUntil)
            {
                Scene = PathingMacroScene.Cannon;
                _animationUntil = default;
            }
            var unknown = UnknownAt > 0 && Observations >= UnknownAt && Observations - UnknownAt < UnknownCount;
            var observation = new PathingMacroObservation(unknown ? PathingMacroScene.Unknown : Scene, _source.Next(), Scene == PathingMacroScene.Cannon && FirePrompt);
            var observed = OnObservation?.Invoke(phase, observation) ?? observation;
            ObservationReceipts.Add((phase, observed, Inputs.Count, Time.GetTimestamp(),
                _evidenceCommandIndex, _evidenceDeadline, _evidenceInputFence));
            return observed;
        }
        public User32.VK Map(User32.VK key) => Mapping(key);
        public CombatBattleHostInputResult Send(PathingMacroInput input, Action admit)
        {
            admit();
            if (_animationUntil != default && input.Kind == PathingMacroInputKind.KeyDown)
                throw new InvalidOperationException("Input sent during cannon turning animation");
            Inputs.Add($"{input.Kind}:{input.Key}");
            if (CrossCannonScenes && input.Kind == PathingMacroInputKind.KeyUp)
            {
                if (input.Key == User32.VK.VK_ESCAPE) Scene = PathingMacroScene.World;
                if (input.Key == User32.VK.VK_F)
                    Scene = TwoStepCannonEntry && Scene == PathingMacroScene.World
                        ? PathingMacroScene.Unknown : PathingMacroScene.Cannon;
                if (Scene == PathingMacroScene.Cannon && CannonAnimationMilliseconds > 0 &&
                    input.Key is User32.VK.VK_W or User32.VK.VK_A or User32.VK.VK_S or User32.VK.VK_D)
                {
                    Scene = PathingMacroScene.Unknown;
                    _animationUntil = Time.GetUtcNow().AddMilliseconds(CannonAnimationMilliseconds);
                }
            }
            CombatBattleHostInputResult receipt = Inputs.Count == FaultAt
                ? new(Fault, Reason: "simulated-native-failure", Error: FaultError)
                : new(CombatBattleHostInputStatus.Sent, Clock.GetTimestamp())
                    { NativeRequested = 1, NativeSubmitted = 1, ObservableAfterTimestamp = Clock.GetTimestamp() };
            InputReceipts.Add((input, receipt));
            return receipt;
        }
        public Task Delay(int milliseconds, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Time.Advance(TimeSpan.FromMilliseconds(milliseconds));
            AfterDelay?.Invoke();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void Recorded620MixedHeldMovementRemainsOneNativeSequence()
    {
        var script = CombatScriptParser.ParseContext(
            "keydown(w),wait(0.1),dash,wait(0.3),attack(0.22),j,wait(0.35),keyup(w),wait(0.25),j", false);
        var plan = LegacyPathingMacroPlan.Create(script, ["琴"]);
        var held = plan.Segments[0];
        Assert.False(held.IsRaw);
        Assert.Equal(8, held.Commands.Count);
        Assert.Equal(Method.KeyDown, held.Commands[0].Method);
        Assert.Equal(Method.KeyUp, held.Commands[^1].Method);
        Assert.True(plan.Segments[1].IsRaw);
        Assert.Equal(2, plan.Segments[1].Commands.Count);
    }
}
