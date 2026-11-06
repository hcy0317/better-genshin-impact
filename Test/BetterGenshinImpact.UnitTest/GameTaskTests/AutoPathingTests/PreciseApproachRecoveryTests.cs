using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using OpenCvSharp;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.Common.Ui;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

public class PreciseApproachRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LastPrecisePulseCanArriveOnItsAlreadyCapturedFinalObservation(bool transformed)
    {
        var position = new Point2f(128, 100);
        var pulses = 0;
        var turns = 0;
        var held = false;
        var replay = new PathReplay { EmitReceipts = true, Transformed = transformed, PositionAt = _ => position };
        replay.OnRotateUntil = () => turns++;
        replay.OnInput = (action, type) =>
        {
            if (action != GIActions.MoveForward) return;
            if (type == KeyType.KeyDown) held = true;
            else if (type == KeyType.KeyUp && held)
            {
                held = false;
                pulses++;
                position = new Point2f(pulses == 25 ? 101.5f : 128 - pulses, 100);
            }
        };
        var target = replay.Point("run"); target.Type = "target";

        await replay.Executor.MoveCloseTo(target);

        Assert.Equal(25, pulses);
        Assert.Equal(25, turns);
        Assert.Equal(25, replay.Inputs.Count(input => input.Type == KeyType.KeyDown));
        Assert.Equal(51, replay.Frames);
        Assert.Equal(50, replay.Delays.Count(delay => delay == 60));
        Assert.Empty(replay.RecoveryInputs);
        Assert.False(held);
        Assert.All(replay.Images, frame => Assert.True(frame.SrcMat.IsDisposed));
    }

    [Theory]
    [InlineData("outside")]
    [InlineData("climb")]
    [InlineData("swim")]
    [InlineData("unknown")]
    [InlineData("foreign")]
    [InlineData("duplicate")]
    [InlineData("stale")]
    [InlineData("before-fence")]
    [InlineData("unknown-receipt")]
    [InlineData("missing-release-receipt")]
    [InlineData("cache")]
    [InlineData("no-response")]
    [InlineData("water-without-normal-proof")]
    [InlineData("cancel")]
    [InlineData("deadline")]
    public async Task FinalPreciseObservationCannotCreateAnotherControlOrBypassArrivalEvidence(string kind)
    {
        using var cancellation = new CancellationTokenSource();
        var position = new Point2f(128, 100);
        var motion = kind == "water-without-normal-proof" ? MotionStatus.Swim : MotionStatus.Normal;
        var pulses = 0;
        var turns = 0;
        var held = false;
        Fischless.GameCapture.CaptureFrameStamp beforeFinalPulse = default;
        var replay = new PathReplay(cancellation.Token) { EmitReceipts = true,
            PositionAt = _ => position, MotionAt = _ => motion };
        replay.OnRotateUntil = () =>
        {
            turns++;
            if (pulses == 24 && kind == "unknown-receipt") replay.EmitReceipts = false;
        };
        replay.OnInput = (action, type) =>
        {
            if (action != GIActions.MoveForward) return;
            if (type == KeyType.KeyDown)
            {
                held = true;
                if (pulses == 24 && kind == "missing-release-receipt") replay.EmitReceipts = false;
            }
            else if (type == KeyType.KeyUp && held)
            {
                held = false;
                pulses++;
                position = new Point2f(pulses == 25 ? kind == "outside" ? 102.54f : 101.5f : 128 - pulses, 100);
                if (pulses != 25) return;
                motion = kind switch
                {
                    "climb" => MotionStatus.Climb,
                    "swim" => MotionStatus.Swim,
                    "unknown" => MotionStatus.Unknown,
                    _ => MotionStatus.Normal
                };
                if (kind == "cache") replay.Direct = false;
                if (kind == "cancel") cancellation.Cancel();
                if (kind == "deadline") replay.Clock.Advance(TimeSpan.FromMinutes(1));
            }
        };
        replay.StampTransform = stamp =>
        {
            if (pulses < 25) { beforeFinalPulse = stamp; return stamp; }
            return kind switch
            {
                "foreign" => stamp with { SessionId = Guid.NewGuid() },
                "duplicate" => beforeFinalPulse,
                "stale" => stamp with { CapturedTimestamp = stamp.CapturedTimestamp - 3 * stamp.TimestampFrequency },
                "before-fence" => stamp with { CapturedTimestamp = beforeFinalPulse.CapturedTimestamp + stamp.TimestampFrequency / 100 },
                _ => stamp
            };
        };
        replay.AvailabilityAt = _ => pulses == 25 && kind == "no-response" ? WorldFrameKind.Unknown : WorldFrameKind.Playable;
        var target = replay.Point("run"); target.Type = "target";

        var error = await Record.ExceptionAsync(() => replay.Executor.MoveCloseTo(target));

        if (kind == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.IsType<RetryException>(error);
        Assert.Equal(25, pulses);
        Assert.Equal(25, turns);
        Assert.Equal(25, replay.Inputs.Count(input => input.Type == KeyType.KeyDown));
        Assert.DoesNotContain(replay.Inputs, input => input.Type == KeyType.KeyDown && input.Action != GIActions.MoveForward);
        Assert.Empty(replay.RecoveryInputs);
        Assert.False(held);
        Assert.All(replay.Images, frame => Assert.True(frame.SrcMat.IsDisposed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaterEpisodesCannotReuseAConsumedNormalProgressAnchor(bool secondEpisodeProgress)
    {
        var position = new Point2f(108, 100);
        var motion = MotionStatus.Swim;
        var stage = "first-swimming";
        var normalPulses = 0;
        var firstResponses = 0;
        var secondSwimObservations = 0;
        var secondResponses = 0;
        var held = false;
        var replay = new PathReplay { EmitReceipts = true, PositionAt = _ => position };
        replay.MotionAt = _ =>
        {
            if (stage == "second-swimming") secondSwimObservations++;
            return motion;
        };
        replay.StampTransform = stamp =>
        {
            if (stage == "first-feedback") firstResponses++;
            if (stage == "second-feedback") secondResponses++;
            return stamp;
        };
        replay.OnRotateUntil = () =>
        {
            if (stage != "second-swimming") return;
            motion = MotionStatus.Normal;
            position = new Point2f(102.0005f, 100);
            stage = "second-normal";
        };
        replay.OnInput = (action, type) =>
        {
            if (action != GIActions.MoveForward) return;
            if (type == KeyType.KeyDown) held = true;
            else if (type == KeyType.KeyUp && held)
            {
                held = false;
                if (motion == MotionStatus.Swim)
                {
                    motion = MotionStatus.Normal;
                    position = new Point2f(103, 100);
                    stage = "first-normal";
                }
                else if (++normalPulses == 1)
                {
                    position = new Point2f(102.5f, 100);
                    stage = "first-feedback";
                }
                else if (normalPulses == 2)
                {
                    motion = MotionStatus.Swim;
                    stage = "second-swimming";
                }
                else
                {
                    position = new Point2f(secondEpisodeProgress ? 101.5f : 101.9995f, 100);
                    stage = "second-feedback";
                }
            }
        };
        var target = replay.Point("run"); target.Type = "target";

        var error = await Record.ExceptionAsync(() => replay.Executor.MoveCloseTo(target));

        Assert.True(firstResponses >= 2, "The first episode must actually consume two fresh Normal responses.");
        Assert.True(secondSwimObservations >= 2, "The second swimming episode must be admitted independently.");
        Assert.True(secondResponses > 0);
        if (secondEpisodeProgress) Assert.Null(error);
        else Assert.True(error is RetryException,
            $"The second episode reused consumed anchor 3 with only .001 progress: responses={secondResponses}, Normal pulses={normalPulses}.");
        Assert.Equal(3, normalPulses);
        Assert.Equal(4, replay.Inputs.Count(input => input.Type == KeyType.KeyDown));
        Assert.False(held);
        Assert.Empty(replay.RecoveryInputs);
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Theory]
    [InlineData("fresh")]
    [InlineData("duplicate")]
    [InlineData("before-fence")]
    public async Task ReswimmingRetiresTheOldNormalPulseProofAfterInvalidPostTurn(string feedbackKind)
    {
        var position = new Point2f(108, 100);
        var motion = MotionStatus.Swim;
        var stage = "swimming";
        var held = false;
        var normalPulses = 0;
        var returnedToSwimming = false;
        var invalidPostTurn = false;
        var feedbackReads = 0;
        var normalCompleted = 0L;
        Fischless.GameCapture.CaptureFrameStamp first = default;
        var replay = new PathReplay { EmitReceipts = true, PositionAt = _ => position, MotionAt = _ => motion };
        replay.OnInput = (action, type) =>
        {
            if (action != GIActions.MoveForward) return;
            if (type == KeyType.KeyDown) held = true;
            else if (type == KeyType.KeyUp && held)
            {
                held = false;
                if (motion == MotionStatus.Swim)
                {
                    motion = MotionStatus.Normal;
                    position = new Point2f(102.0005f, 100);
                    stage = "normal";
                }
                else
                {
                    normalPulses++;
                    normalCompleted = replay.Clock.GetTimestamp();
                    motion = MotionStatus.Swim;
                    stage = "reswimming";
                    returnedToSwimming = true;
                }
            }
        };
        replay.OnRotateUntil = () =>
        {
            if (stage != "reswimming") return;
            replay.Direct = false;
            invalidPostTurn = true;
            stage = "invalid-postturn";
        };
        replay.AfterDelay = milliseconds =>
        {
            if (stage != "invalid-postturn" || milliseconds != 100) return;
            replay.Direct = true;
            motion = MotionStatus.Normal;
            position = new Point2f(101.9995f, 100);
            stage = "normal-feedback";
        };
        replay.StampTransform = stamp =>
        {
            if (stage != "normal-feedback") return stamp;
            feedbackReads++;
            if (feedbackKind == "fresh") return stamp;
            if (feedbackKind == "before-fence")
                return stamp with { CapturedTimestamp = normalCompleted + replay.Clock.TimestampFrequency / 100 };
            if (!first.IsKnown) first = stamp;
            return first;
        };
        var target = replay.Point("run"); target.Type = "target";

        var error = await Record.ExceptionAsync(() => replay.Executor.MoveCloseTo(target));

        Assert.True(returnedToSwimming);
        Assert.True(invalidPostTurn);
        Assert.True(feedbackReads > 0);
        Assert.True(error is RetryException,
            $"Retired Normal proof granted arrival after {feedbackReads} same-position Normal frames; only .001 target change followed re-swimming.");
        Assert.Equal(1, normalPulses);
        Assert.Equal(2, replay.Inputs.Count(input => input.Type == KeyType.KeyDown));
        Assert.False(held);
        Assert.Empty(replay.RecoveryInputs);
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Fact]
    public async Task ConfirmedSwimmingAdvancesTheAuthoredGroundTargetBeforeOrdinaryArrival()
    {
        var position = new Point2f(108, 100);
        var motion = MotionStatus.Swim;
        var swimmingPulses = 0;
        var normalPulses = 0;
        var held = false;
        var replay = new PathReplay { EmitReceipts = true, PositionAt = _ => position, MotionAt = _ => motion };
        replay.OnInput = (action, type) =>
        {
            if (action != GIActions.MoveForward) return;
            if (type == KeyType.KeyDown) held = true;
            else if (type == KeyType.KeyUp && held)
            {
                held = false;
                if (motion == MotionStatus.Swim)
                {
                    swimmingPulses++;
                    position.X -= 1.5f;
                    if (swimmingPulses == 2) motion = MotionStatus.Normal;
                }
                else { normalPulses++; position.X -= 1; }
            }
        };
        var target = replay.Point("run");
        target.Type = "target";

        await replay.Executor.MoveCloseTo(target);

        Assert.Equal(2, swimmingPulses);
        Assert.InRange(normalPulses, 2, 23);
        Assert.False(held);
        Assert.InRange(replay.Inputs.Count(input => input.Type == KeyType.KeyDown), 4, 25);
        Assert.DoesNotContain(replay.Inputs, input => input.Type == KeyType.KeyDown && input.Action != GIActions.MoveForward);
        Assert.Empty(replay.RecoveryInputs);
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Fact]
    public async Task StillSwimmingUsesOnlyOriginalGroundTargetOpportunitiesAndNeverArrives()
    {
        var replay = new PathReplay { EmitReceipts = true,
            PositionAt = _ => new Point2f(108, 100), MotionAt = _ => MotionStatus.Swim };
        var target = replay.Point("run");
        target.Type = "target";

        await Assert.ThrowsAsync<RetryException>(() => replay.Executor.MoveCloseTo(target));

        Assert.Equal(25, replay.Inputs.Count(input => input.Action == GIActions.MoveForward && input.Type == KeyType.KeyDown));
        Assert.DoesNotContain(replay.Inputs, input => input.Type == KeyType.KeyDown && input.Action != GIActions.MoveForward);
        Assert.Equal(KeyType.KeyUp, replay.Inputs[^1].Type);
        Assert.Empty(replay.RecoveryInputs);
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Theory]
    [InlineData("unknown-target")]
    [InlineData("fly")]
    [InlineData("climb")]
    [InlineData("same-xy-swim")]
    [InlineData("normal-without-normal-movement")]
    [InlineData("unknown-receipt")]
    [InlineData("missing-release-receipt")]
    [InlineData("wrong-runtime")]
    [InlineData("climb-after-pulse")]
    public async Task WaterMovementNeverConvertsMissingEvidenceIntoGroundArrival(string kind)
    {
        var pulsed = false;
        var held = false;
        var replay = new PathReplay { EmitReceipts = kind != "unknown-receipt" };
        replay.PositionAt = _ => kind == "same-xy-swim" || pulsed && kind == "normal-without-normal-movement"
            ? new Point2f(100, 100) : new Point2f(108, 100);
        replay.MotionAt = _ => pulsed && kind == "normal-without-normal-movement" ? MotionStatus.Normal :
            pulsed && kind == "climb-after-pulse" ? MotionStatus.Climb : MotionStatus.Swim;
        replay.StampTransform = stamp => pulsed && kind == "wrong-runtime"
            ? stamp with { SessionId = Guid.Parse("502b7d53-c026-41b8-9a18-5cb1372ad604") } : stamp;
        replay.OnInput = (action, type) =>
        {
            if (action != GIActions.MoveForward) return;
            if (type == KeyType.KeyDown)
            {
                held = true;
                if (kind == "missing-release-receipt") replay.EmitReceipts = false;
            }
            else if (type == KeyType.KeyUp && held) { held = false; pulsed = true; }
        };
        var target = replay.Point(kind is "fly" or "climb" ? kind : "run");
        target.Type = kind == "unknown-target" ? "path" : "target";

        await Assert.ThrowsAsync<RetryException>(() => replay.Executor.MoveCloseTo(target));

        var forward = replay.Inputs.Count(input => input.Action == GIActions.MoveForward && input.Type == KeyType.KeyDown);
        if (kind is "unknown-target" or "fly" or "climb" or "same-xy-swim") Assert.Equal(0, forward);
        else Assert.Equal(1, forward);
        Assert.False(held);
        Assert.DoesNotContain(replay.Inputs, input => input.Type == KeyType.KeyDown && input.Action != GIActions.MoveForward);
        Assert.Empty(replay.RecoveryInputs);
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Theory]
    [InlineData("transform")]
    [InlineData("duplicate")]
    [InlineData("fallback")]
    [InlineData("missing-hud")]
    [InlineData("expired-control-read")]
    public async Task WaterMovementCannotUsePostTurnEvidenceFromAnotherControlState(string kind)
    {
        var replay = new PathReplay { EmitReceipts = true, PositionAt = _ => new Point2f(108, 100), MotionAt = _ => MotionStatus.Swim };
        var afterTurn = false;
        replay.ReadTransformation = _ =>
        {
            if (afterTurn && kind == "expired-control-read") replay.Clock.Advance(TimeSpan.FromMinutes(1));
            return replay.Transformed;
        };
        replay.OnRotateUntil = () =>
        {
            afterTurn = true;
            if (kind == "transform") replay.Transformed = true;
            if (kind == "duplicate") replay.Duplicate = true;
            if (kind == "fallback") replay.Direct = false;
            if (kind == "missing-hud") replay.Hud = false;
        };
        var target = replay.Point("run"); target.Type = "target";

        await Assert.ThrowsAsync<RetryException>(() => replay.Executor.MoveCloseTo(target));

        Assert.DoesNotContain(replay.Inputs, input => input.Type == KeyType.KeyDown);
        Assert.Empty(replay.RecoveryInputs);
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Fact]
    public async Task OneNormalPulseIntoArrivalNeedsTwoFreshResponsesNotAnotherPulse()
    {
        var position = new Point2f(108, 100);
        var motion = MotionStatus.Swim;
        var waterPulses = 0;
        var normalPulses = 0;
        var held = false;
        var replay = new PathReplay { EmitReceipts = true, PositionAt = _ => position, MotionAt = _ => motion };
        replay.OnInput = (action, type) =>
        {
            if (action != GIActions.MoveForward) return;
            if (type == KeyType.KeyDown) held = true;
            else if (type == KeyType.KeyUp && held)
            {
                held = false;
                if (motion == MotionStatus.Swim)
                {
                    if (++waterPulses == 2) { motion = MotionStatus.Normal; position = new Point2f(102.5f, 100); }
                    else position = new Point2f(106, 100);
                }
                else { normalPulses++; position = new Point2f(101.5f, 100); }
            }
        };
        var target = replay.Point("run"); target.Type = "target";

        await replay.Executor.MoveCloseTo(target);

        Assert.Equal(2, waterPulses);
        Assert.Equal(1, normalPulses);
        Assert.False(held);
        Assert.Equal(3, replay.Inputs.Count(input => input.Type == KeyType.KeyDown));
        Assert.Empty(replay.RecoveryInputs);
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("before-fence")]
    public async Task OneNormalResponseCannotBeReusedAsTwoNearTargetObservations(string kind)
    {
        var position = new Point2f(108, 100);
        var motion = MotionStatus.Swim;
        var waterPulses = 0;
        var normalPulses = 0;
        var held = false;
        var feedbackReads = 0;
        var completed = 0L;
        Fischless.GameCapture.CaptureFrameStamp first = default;
        var replay = new PathReplay { EmitReceipts = true, PositionAt = _ => position, MotionAt = _ => motion };
        replay.OnInput = (action, type) =>
        {
            if (action != GIActions.MoveForward) return;
            if (type == KeyType.KeyDown) held = true;
            else if (type == KeyType.KeyUp && held)
            {
                held = false;
                if (motion == MotionStatus.Swim)
                {
                    if (++waterPulses == 2) { motion = MotionStatus.Normal; position = new Point2f(102.5f, 100); }
                    else position = new Point2f(106, 100);
                }
                else { normalPulses++; position = new Point2f(101.5f, 100); completed = replay.Clock.GetTimestamp(); }
            }
        };
        replay.StampTransform = stamp =>
        {
            if (normalPulses == 0) return stamp;
            if (++feedbackReads == 1) { first = stamp; return stamp; }
            return kind == "duplicate" ? first : stamp with { CapturedTimestamp = completed + replay.Clock.TimestampFrequency / 100 };
        };
        var target = replay.Point("run"); target.Type = "target";

        await Assert.ThrowsAsync<RetryException>(() => replay.Executor.MoveCloseTo(target));

        Assert.True(feedbackReads >= 2);
        Assert.Equal(1, normalPulses);
        Assert.Equal(3, replay.Inputs.Count(input => input.Type == KeyType.KeyDown));
        Assert.False(held);
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Fact]
    public async Task SubthresholdNormalJitterCannotRestoreNearTargetMovementAuthority()
    {
        var position = new Point2f(108, 100);
        var motion = MotionStatus.Swim;
        var waterPulses = 0;
        var normalPulses = 0;
        var held = false;
        var replay = new PathReplay { EmitReceipts = true, PositionAt = _ => position, MotionAt = _ => motion };
        replay.OnInput = (action, type) =>
        {
            if (action != GIActions.MoveForward) return;
            if (type == KeyType.KeyDown) held = true;
            else if (type == KeyType.KeyUp && held)
            {
                held = false;
                if (motion == MotionStatus.Swim)
                {
                    if (++waterPulses == 2) { motion = MotionStatus.Normal; position = new Point2f(102.0005f, 100); }
                    else position = new Point2f(106, 100);
                }
                else { normalPulses++; position = new Point2f(101.9995f, 100); }
            }
        };
        var target = replay.Point("run"); target.Type = "target";

        await Assert.ThrowsAsync<RetryException>(() => replay.Executor.MoveCloseTo(target));

        Assert.Equal(1, normalPulses);
        Assert.Equal(3, replay.Inputs.Count(input => input.Type == KeyType.KeyDown));
        Assert.False(held);
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Theory]
    [InlineData(true, "hold")]
    [InlineData(false, "hold")]
    [InlineData(true, "feedback")]
    [InlineData(false, "feedback")]
    public async Task WaterMovementKeepsOriginalCancellationAndDeadline(bool cancel, string phase)
    {
        using var cancellation = new CancellationTokenSource();
        var held = false;
        var released = false;
        var stopped = false;
        var replay = new PathReplay(cancellation.Token) { EmitReceipts = true,
            PositionAt = _ => new Point2f(108, 100), MotionAt = _ => MotionStatus.Swim };
        void Stop()
        {
            if (stopped) return;
            stopped = true;
            if (cancel) cancellation.Cancel();
            else replay.Clock.Advance(TimeSpan.FromMinutes(1));
        }
        replay.OnInput = (action, type) =>
        {
            if (action != GIActions.MoveForward) return;
            if (type == KeyType.KeyDown) { held = true; if (phase == "hold") Stop(); }
            else if (type == KeyType.KeyUp && held) { held = false; released = true; }
        };
        replay.AfterDelay = milliseconds => { if (phase == "feedback" && released && milliseconds == 60) Stop(); };
        var target = replay.Point("run"); target.Type = "target";

        var error = await Record.ExceptionAsync(() => replay.Executor.MoveCloseTo(target));

        Assert.True(stopped);
        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.IsType<RetryException>(error);
        Assert.Single(replay.Inputs.Where(input => input.Type == KeyType.KeyDown));
        Assert.False(held);
        Assert.Equal(KeyType.KeyUp, replay.Inputs[^1].Type);
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("queued-before-response")]
    public async Task WaterMovementDoesNotSpendAnotherInputOnUnacceptedPulseFeedback(string kind)
    {
        var held = false;
        var pulsed = false;
        var completed = 0L;
        Fischless.GameCapture.CaptureFrameStamp before = default;
        var replay = new PathReplay { EmitReceipts = true,
            PositionAt = _ => new Point2f(108, 100), MotionAt = _ => MotionStatus.Swim };
        replay.StampTransform = stamp =>
        {
            if (!pulsed) { before = stamp; return stamp; }
            return kind == "duplicate" ? before : stamp with { CapturedTimestamp = completed + replay.Clock.TimestampFrequency / 100 };
        };
        replay.OnInput = (action, type) =>
        {
            if (action != GIActions.MoveForward) return;
            if (type == KeyType.KeyDown) held = true;
            else if (type == KeyType.KeyUp && held) { held = false; pulsed = true; completed = replay.Clock.GetTimestamp(); }
        };
        var target = replay.Point("run"); target.Type = "target";

        await Assert.ThrowsAsync<RetryException>(() => replay.Executor.MoveCloseTo(target));

        Assert.Single(replay.Inputs.Where(input => input.Type == KeyType.KeyDown));
        Assert.False(held);
        Assert.Empty(replay.RecoveryInputs);
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Fact]
    public async Task PostTurnPositionCrossingTargetCannotUseTheOldForwardHeading()
    {
        var position = new Point2f(97, 100);
        var rotations = 0;
        var oldHeadingForward = false;
        var forwardHeld = false;
        var replay = new PathReplay { EmitReceipts = true, PositionAt = _ => position };
        replay.OnRotateUntil = () =>
        {
            rotations++;
            if (rotations == 1) position = new Point2f(103, 100);
        };
        replay.OnInput = (action, type) =>
        {
            if (action != GIActions.MoveForward) return;
            if (type == KeyType.KeyDown)
            {
                oldHeadingForward |= rotations == 1;
                forwardHeld = true;
            }
            if (type == KeyType.KeyUp && forwardHeld)
            {
                forwardHeld = false;
                position = new Point2f(100, 100);
            }
        };

        await replay.Executor.MoveCloseTo(replay.Point("walk"));

        Assert.False(oldHeadingForward, "The post-turn position crossed the target, but W used the previous heading.");
        Assert.InRange(rotations, 2, 25);
        Assert.False(forwardHeld);
        Assert.Empty(replay.RecoveryInputs);
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Fact]
    public async Task PostTurnFreshArrivalCannotSendAnUnnecessaryForwardPulse()
    {
        var position = new Point2f(104, 100);
        var replay = new PathReplay { EmitReceipts = true, PositionAt = _ => position };
        replay.OnRotateUntil = () => position = new Point2f(101, 100);

        await replay.Executor.MoveCloseTo(replay.Point("walk"));

        Assert.DoesNotContain(replay.Inputs, input => input.Action == GIActions.MoveForward && input.Type == KeyType.KeyDown);
        Assert.Empty(replay.RecoveryInputs);
        Assert.Equal(KeyType.KeyUp, replay.Inputs[^1].Type);
        Assert.Contains(1000, replay.Delays); // The original positive arrival settling still runs.
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("duplicate")]
    [InlineData("swim")]
    [InlineData("climb")]
    public async Task PostTurnIneligibleObservationCannotGrantNearbyArrivalOrForwardInput(string kind)
    {
        var rotated = false;
        var replay = new PathReplay { EmitReceipts = true,
            PositionAt = _ => rotated ? new Point2f(101, 100) : new Point2f(104, 100),
            MotionAt = _ => rotated && kind == "swim" ? MotionStatus.Swim :
                rotated && kind == "climb" ? MotionStatus.Climb : MotionStatus.Normal };
        replay.OnRotateUntil = () =>
        {
            rotated = true;
            if (kind == "invalid") replay.Direct = false;
            if (kind == "duplicate") replay.Duplicate = true;
        };

        await Assert.ThrowsAsync<RetryException>(() => replay.Executor.MoveCloseTo(replay.Point("walk")));

        Assert.DoesNotContain(replay.Inputs, input => input.Action == GIActions.MoveForward && input.Type == KeyType.KeyDown);
        Assert.Equal(KeyType.KeyUp, replay.Inputs[^1].Type);
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostTurnCancellationOrDeadlineCannotBecomeArrival(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var arrived = false;
        var replay = new PathReplay(cancellation.Token) { EmitReceipts = true,
            PositionAt = _ => arrived ? new Point2f(100, 100) : new Point2f(104, 100) };
        replay.OnRotateUntil = () =>
        {
            arrived = true;
            if (cancel) cancellation.Cancel();
            else replay.Clock.Advance(TimeSpan.FromMinutes(1));
        };

        var error = await Record.ExceptionAsync(() => replay.Executor.MoveCloseTo(replay.Point("walk")));

        Assert.NotNull(error);
        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.IsType<RetryException>(error);
        Assert.DoesNotContain(replay.Inputs, input => input.Type == KeyType.KeyDown);
        Assert.Equal(KeyType.KeyUp, replay.Inputs[^1].Type);
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Fact]
    public async Task PostTurnHeadingChangesStillExhaustOnlyTheOriginalTwentyFiveSteps()
    {
        var position = new Point2f(97, 100);
        var rotations = 0;
        var replay = new PathReplay { EmitReceipts = true, PositionAt = _ => position };
        replay.OnRotateUntil = () =>
        {
            rotations++;
            position = new Point2f(position.X < 100 ? 103 : 97, 100);
        };

        await Assert.ThrowsAsync<RetryException>(() => replay.Executor.MoveCloseTo(replay.Point("walk")));

        Assert.Equal(25, rotations);
        Assert.DoesNotContain(replay.Inputs, input => input.Type == KeyType.KeyDown);
        Assert.Empty(replay.RecoveryInputs);
        Assert.Equal(KeyType.KeyUp, replay.Inputs[^1].Type);
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Fact]
    public async Task TransformationEnteringWaterDuringRotationCannotSendGroundMovement()
    {
        var rotated = false;
        var replay = new PathReplay { EmitReceipts = true, Transformed = true, Hud = false,
            PositionAt = _ => new Point2f(104, 100),
            MotionAt = _ => rotated ? MotionStatus.Swim : MotionStatus.Fly };
        replay.OnRotateUntil = () => rotated = true;
        await Assert.ThrowsAsync<RetryException>(() => replay.Executor.MoveCloseTo(replay.Point("dash")));
        Assert.True(rotated);
        Assert.DoesNotContain(replay.Inputs, input => input.Type == KeyType.KeyDown);
        Assert.Empty(replay.RecoveryInputs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransformationDoesNotOverrideSwimmingOrLossOfItsHudAfterRotation(bool losesHud)
    {
        var replay = new PathReplay { EmitReceipts = true, Transformed = true, Hud = false,
            PositionAt = _ => new Point2f(104, 100), MotionAt = _ => losesHud ? MotionStatus.Fly : MotionStatus.Swim };
        replay.OnRotateUntil = () => replay.Transformed = false;
        await Assert.ThrowsAsync<RetryException>(() => replay.Executor.MoveCloseTo(replay.Point("dash")));
        Assert.DoesNotContain(replay.Inputs, input => input.Type == KeyType.KeyDown);
        Assert.Empty(replay.RecoveryInputs);
    }

    [Theory]
    [InlineData("walk")]
    [InlineData("run")]
    [InlineData("dash")]
    public async Task ConfirmedTransformationCanFinishAuthoredGroundApproachWithoutHumanoidRecovery(string mode)
    {
        // 16:10 run, 620 node 24: transformed UI, dash, legacy motion Fly.
        // Production must consume its direct position, not wait for a humanoid HUD.
        var moved = false;
        var replay = new PathReplay { EmitReceipts = true, Transformed = true, Hud = false,
            MotionAt = _ => MotionStatus.Fly };
        replay.PositionAt = _ => moved ? new Point2f(100, 100) : new Point2f(104, 100);
        replay.OnInput = (action, type) =>
        { if (action == GIActions.MoveForward && type == KeyType.KeyUp && replay.Inputs.Any(i => i.Type == KeyType.KeyDown)) moved = true; };
        await replay.Executor.MoveCloseTo(replay.Point(mode));
        Assert.True(moved);
        Assert.Single(replay.Inputs.Where(i => i.Type == KeyType.KeyDown && i.Action == GIActions.MoveForward));
        Assert.DoesNotContain(replay.Inputs, i => i.Action is GIActions.Drop or GIActions.Jump);
        Assert.Empty(replay.RecoveryInputs);
        Assert.All(replay.Images, frame => Assert.True(frame.SrcMat.IsDisposed));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(.35f)]
    public async Task BodyBlockedApproachSidestepsThenResumesTheSameWaypointWithoutLongRetreat(float jitter)
    {
        // Noon 606923 -> 607243: generic retreat moved 15 pixels away from the
        // body-blocked target. Model the collision boundary, not an instant arrival.
        var replay = new PathReplay { EmitReceipts = true };
        var position = new Point2f(104, 100);
        var held = GIActions.MoveForward;
        var moving = false;
        var cleared = false;
        var blockedPulses = 0;
        replay.PositionAt = _ => position;
        replay.OnInput = (action, type) =>
        {
            if (type == KeyType.KeyDown) { held = action; moving = true; }
            if (type == KeyType.KeyUp && action == held) moving = false;
        };
        replay.AfterDelay = ms =>
        {
            if (!moving) return;
            if (held == GIActions.MoveLeft) position.Y += ms * .02f;
            cleared |= position.Y >= 102;
            if (held == GIActions.MoveBackward) position.X += ms * .01f;
            // A collision can move the marker sideways without getting closer to the target.
            if (held == GIActions.MoveForward && !cleared)
                position.Y = 100 + (++blockedPulses % 2 == 0 ? jitter : -jitter);
            if (held == GIActions.MoveForward && cleared)
            { position.X = Math.Max(100, position.X - .6f); position.Y = Math.Max(100, position.Y - .3f); }
        };
        var error = await Record.ExceptionAsync(() => replay.Executor.MoveCloseTo(replay.Point("walk")));
        Assert.True(error == null, $"blockedPulses={blockedPulses}, recoveryInputs={replay.RecoveryInputs.Count}, error={error}");
        Assert.True(Math.Pow(position.X - 100, 2) + Math.Pow(position.Y - 100, 2) < 4);
        Assert.Single(replay.RecoveryInputs.Where(input => input.Action == GIActions.MoveLeft && input.Type == KeyType.KeyDown));
        Assert.InRange(replay.Inputs.Count(input => input.Action == GIActions.MoveForward && input.Type == KeyType.KeyDown), 1, 24);
        Assert.DoesNotContain(replay.RecoveryInputs, input => input.Action is GIActions.Jump or GIActions.Drop or GIActions.MoveBackward);
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Fact]
    public async Task WallOnTheLeftUsesTheOtherSideBeforeResumingTheSameWaypoint()
    {
        // 2026-10-27 645: the left probe stayed within .05px against the wall.
        // The other side is clear; the recovery still must return to the target.
        var replay = new PathReplay { EmitReceipts = true };
        var position = new Point2f(104, 100);
        GIActions? held = null;
        var cleared = false;
        replay.PositionAt = _ => position;
        replay.OnInput = (action, type) =>
        {
            if (type == KeyType.KeyDown) held = action;
            if (type == KeyType.KeyUp && held == action) held = null;
        };
        replay.AfterDelay = ms =>
        {
            if (held == GIActions.MoveRight) position.Y -= ms * .02f;
            cleared |= position.Y <= 98;
            if (held == GIActions.MoveForward && cleared)
            { position.X = Math.Max(100, position.X - .6f); position.Y = Math.Min(100, position.Y + .3f); }
        };
        await replay.Executor.MoveCloseTo(replay.Point("dash"));
        Assert.True(cleared);
        Assert.True(Math.Pow(position.X - 100, 2) + Math.Pow(position.Y - 100, 2) < 4);
        Assert.Contains(replay.RecoveryInputs, input => input.Action == GIActions.MoveRight && input.Type == KeyType.KeyDown);
        Assert.DoesNotContain(replay.RecoveryInputs, input => input.Action is GIActions.Jump or GIActions.MoveBackward);
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Theory]
    [InlineData(0f, false)]
    [InlineData(.35f, false)]
    [InlineData(.35f, true)]
    public async Task BothSidesBlockedStillFailWithinTheOriginalApproachBudget(float jitter, bool driftingAway)
    {
        var pulses = 0;
        var replay = new PathReplay { EmitReceipts = true,
            PositionAt = _ => new Point2f(104 + (driftingAway ? pulses * .1f : 0),
                100 + (pulses % 2 == 0 ? jitter : -jitter)) };
        replay.OnInput = (action, type) =>
        { if (action == GIActions.MoveForward && type == KeyType.KeyDown) pulses++; };
        var error = await Assert.ThrowsAsync<RetryException>(() => replay.Executor.MoveCloseTo(replay.Point("dash")));
        Assert.Equal("精确接近目标点超时，重试当前路线分段", error.Message);
        Assert.Equal(24, pulses); // One of the original 25 steps runs the sole recovery.
        Assert.DoesNotContain(replay.RecoveryInputs, input => input.Action is GIActions.Jump or GIActions.Drop or GIActions.MoveBackward);
        Assert.Single(replay.RecoveryInputs.Where(input => input.Action == GIActions.MoveLeft && input.Type == KeyType.KeyDown));
        Assert.Single(replay.RecoveryInputs.Where(input => input.Action == GIActions.MoveRight && input.Type == KeyType.KeyDown));
        Assert.True(replay.Clock.GetUtcNow() - replay.Started < TimeSpan.FromSeconds(60));
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Fact]
    public async Task RecoveryCannotUseAQueuedPreInputFrameToSendTheNextAction()
    {
        var replay = new PathReplay { EmitReceipts = true, PositionAt = _ => new Point2f(104, 100) };
        long? inputCompleted = null;
        GIActions? movement = null;
        replay.OnInput = (action, type) =>
        {
            if (UiOperation.Current?.Name != "path-precise-recovery" ||
                action is not (GIActions.MoveBackward or GIActions.MoveLeft or GIActions.MoveRight) || type != KeyType.KeyDown) return;
            movement = action;
            replay.Clock.Advance(TimeSpan.FromMilliseconds(5));
            inputCompleted = replay.Clock.GetTimestamp();
        };
        replay.StampTransform = stamp => inputCompleted is { } completed && UiOperation.Current?.Name == "path-precise-recovery"
            ? stamp with { CapturedTimestamp = completed - 1,
                CapturedAt = replay.Clock.GetUtcNow() - replay.Clock.GetElapsedTime(completed - 1) }
            : stamp;
        await Assert.ThrowsAsync<RetryException>(() => replay.Executor.MoveCloseTo(replay.Point("walk")));
        Assert.DoesNotContain(replay.RecoveryInputs, input => input.Action == GIActions.Jump);
        Assert.NotNull(movement);
        Assert.Contains(replay.RecoveryInputs, input => input.Action == movement && input.Type == KeyType.KeyUp);
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Theory]
    [InlineData(MotionStatus.Climb)]
    [InlineData(MotionStatus.Fly)]
    public async Task GroundApproachRechecksMotionAfterRotation(MotionStatus motion)
    {
        var rotated = false;
        var landed = false;
        var replay = new PathReplay { EmitReceipts = true };
        replay.OnRotateUntil = () => rotated = true;
        replay.MotionAt = _ => landed || !rotated ? MotionStatus.Normal : motion;
        replay.PositionAt = _ => landed ? new Point2f(100, 100) : new Point2f(104, 100);
        replay.OnInput = (action, type) =>
        {
            if (action == GIActions.MoveForward && type == KeyType.KeyDown)
                Assert.True(landed, "转向后的新帧已攀爬/飞行，不能继续W");
            if (action == GIActions.Drop && type == KeyType.KeyUp) landed = true;
        };
        replay.AfterDelay = ms => { if (motion == MotionStatus.Fly && ms == 100) landed = true; };
        await replay.Executor.MoveCloseTo(replay.Point("walk"));
        Assert.True(landed);
        Assert.DoesNotContain(replay.Inputs, x => x.Action == GIActions.MoveForward && x.Type == KeyType.KeyDown);
        if (motion == MotionStatus.Climb)
            Assert.Single(replay.Inputs.Where(x => x.Action == GIActions.Drop && x.Type == KeyType.KeyDown));
    }

    [Fact]
    public async Task GroundApproachDetachesFromPillarBeforeResumingMovement()
    {
        var detached = false;
        var replay = new PathReplay { EmitReceipts = true };
        replay.MotionAt = _ => detached ? MotionStatus.Normal : MotionStatus.Climb;
        replay.PositionAt = _ => detached ? new Point2f(100, 100) : new Point2f(103, 100);
        replay.OnInput = (action, type) => { if (action == GIActions.Drop && type == KeyType.KeyUp) detached = true; };
        await replay.Executor.MoveCloseTo(replay.Point("walk"));
        Assert.True(detached);
        Assert.Single(replay.Inputs.Where(x => x.Action == GIActions.Drop && x.Type == KeyType.KeyDown));
        Assert.Contains(300, replay.Delays);
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Fact]
    public async Task DetachMustLandBeforeDistanceCanConfirmArrival()
    {
        var detached = false;
        var afterDrop = 0;
        var replay = new PathReplay { EmitReceipts = true };
        replay.MotionAt = _ => !detached ? MotionStatus.Climb : ++afterDrop <= 3 ? MotionStatus.Fly : MotionStatus.Normal;
        replay.PositionAt = _ => detached ? new Point2f(100, 100) : new Point2f(103, 100);
        replay.OnInput = (action, type) => { if (action == GIActions.Drop && type == KeyType.KeyUp) detached = true; };
        await replay.Executor.MoveCloseTo(replay.Point("walk"));
        Assert.True(afterDrop >= 4);
        Assert.DoesNotContain(replay.Inputs, x => x.Action == GIActions.MoveForward && x.Type == KeyType.KeyDown);
    }

    [Fact]
    public async Task GroundApproachCannotPulseForwardWhileStillClimbing()
    {
        var replay = new PathReplay { EmitReceipts = true, MotionAt = _ => MotionStatus.Climb,
            PositionAt = _ => new Point2f(103, 100) };
        await Assert.ThrowsAsync<RetryException>(() => replay.Executor.MoveCloseTo(replay.Point("walk")));
        Assert.Equal(2, replay.Inputs.Count(x => x.Action == GIActions.Drop && x.Type == KeyType.KeyDown));
        Assert.DoesNotContain(replay.Inputs, x => x.Action == GIActions.MoveForward && x.Type == KeyType.KeyDown);
    }

    [Fact]
    public async Task CancelledDetachStillReleasesDropAndNeverReportsArrival()
    {
        using var cancellation = new CancellationTokenSource();
        var replay = new PathReplay(cancellation.Token) { EmitReceipts = true,
            MotionAt = _ => MotionStatus.Climb, PositionAt = _ => new Point2f(103, 100) };
        replay.AfterDelay = ms => { if (ms == 60) cancellation.Cancel(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replay.Executor.MoveCloseTo(replay.Point("walk")));
        Assert.Single(replay.Inputs.Where(x => x.Action == GIActions.Drop && x.Type == KeyType.KeyDown));
        Assert.Single(replay.Inputs.Where(x => x.Action == GIActions.Drop && x.Type == KeyType.KeyUp));
        Assert.DoesNotContain(replay.Inputs, x => x.Action == GIActions.MoveForward && x.Type == KeyType.KeyDown);
    }

    [Fact]
    public async Task NextPositionCaptureWaitsForGameResponseAfterMovementRelease()
    {
        var replay = new PathReplay { EmitReceipts = true };
        DateTimeOffset? releasedAt = null;
        var observedAfterRelease = false;
        replay.PositionAt = _ => releasedAt.HasValue ? new Point2f(100, 100) : new Point2f(104, 100);
        replay.OnInput = (action, type) =>
        {
            if (action == GIActions.MoveForward && type == KeyType.KeyUp)
                releasedAt = replay.Clock.GetUtcNow();
        };
        replay.BeforeCapture = _ =>
        {
            if (releasedAt is not { } released) return;
            observedAfterRelease = true;
            Assert.True(replay.Clock.GetUtcNow() - released >= TimeSpan.FromMilliseconds(60),
                "不能在游戏30–60ms响应窗口尚未结束时重新识别位置");
        };
        await replay.Executor.MoveCloseTo(replay.Point("walk"));
        Assert.True(observedAfterRelease);
        Assert.Single(replay.Inputs.Where(input => input.Type == KeyType.KeyDown));
        Assert.Empty(replay.RecoveryInputs);
    }

    [Fact]
    public async Task KnownTransformationCanConfirmItsDirectPositionWithoutOrdinaryAvatarHud()
    {
        var replay = new PathReplay { Transformed = true, Hud = false };
        await replay.Executor.MoveCloseTo(replay.Point("climb"));
        Assert.Empty(replay.RecoveryInputs);
        Assert.Empty(replay.Inputs.Where(input => input.Type == KeyType.KeyDown));
    }

    [Fact]
    public async Task WaitingForValidFramesDoesNotSpendTheMovementAttemptBudget()
    {
        var replay = new PathReplay { HudAt = count => count > 30 };
        await replay.Executor.MoveCloseTo(replay.Point("walk"));
        Assert.True(replay.Frames > 30);
        Assert.Empty(replay.Inputs.Where(input => input.Type == KeyType.KeyDown));
    }

    [Theory]
    [InlineData("climb", MotionStatus.Climb, false)]
    [InlineData("dash", MotionStatus.Fly, false)]
    [InlineData("climb", MotionStatus.Normal, true)]
    [InlineData("dash", MotionStatus.Unknown, false)]
    public async Task AuthoredGroundRecoveryModesNeverOverrideActualUnsafePosture(string mode, MotionStatus motion, bool transformed)
    {
        var replay = new PathReplay { EmitReceipts = true, Transformed = transformed,
            PositionAt = _ => new Point2f(104, 100), MotionAt = _ => motion };
        await Assert.ThrowsAsync<RetryException>(() => replay.Executor.MoveCloseTo(replay.Point(mode)));
        Assert.Empty(replay.RecoveryInputs);
    }

    [Theory]
    [InlineData("climb")]
    [InlineData("dash")]
    public async Task RecordedGroundStallUsesObservedPostureNotAuthoredTransitMode(string mode)
    {
        var replay = new PathReplay { EmitReceipts = true };
        var escaped = false;
        replay.PositionAt = _ => escaped ? new Point2f(39055.75f, 32238.62f) : new Point2f(39058.55f, 32238.57f);
        replay.OnInput = (action, type) =>
        {
            if (type == KeyType.KeyDown && action is GIActions.MoveBackward or GIActions.MoveLeft or GIActions.MoveRight)
                escaped = true;
        };
        var target = replay.Point(mode);
        target.X = 39055.75; target.Y = 32238.62;
        await replay.Executor.MoveCloseTo(target);
        Assert.True(escaped);
        Assert.NotEmpty(replay.RecoveryInputs);
    }

    [Theory]
    [InlineData(100, 100, 103.94f, 100)]
    // S56 20260924.log:29801，07-跳崖点东x18.json segment=1 node=79。
    [InlineData(1871.02f, 1256.85f, 1870.55f, 1252.94f)]
    public async Task ConfirmedGroundStallRecoversOnceThenRequiresActualTwoPixelArrival(
        float targetX, float targetY, float stalledX, float stalledY)
    {
        var replay = new PathReplay { EmitReceipts = true };
        var escaped = false;
        replay.PositionAt = _ => escaped ? new Point2f(targetX, targetY) : new Point2f(stalledX, stalledY);
        replay.OnInput = (action, type) =>
        {
            if (type == KeyType.KeyDown && action is GIActions.MoveBackward or GIActions.MoveLeft or GIActions.MoveRight)
                escaped = true;
        };
        var target = replay.Point("walk");
        target.X = targetX;
        target.Y = targetY;
        await replay.Executor.MoveCloseTo(target);
        Assert.True(escaped);
        Assert.NotEmpty(replay.RecoveryInputs);
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
        Assert.Equal(KeyType.KeyUp, replay.Inputs[^1].Type);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HealthyApproachAllowsCumulativeSmallGainsAndShortDetoursWithoutRecovery(bool detours)
    {
        var pulses = 0;
        var distances = detours
            ? new[] { 6f, 6.4f, 6.2f, 6.3f, 5.5f, 5.9f, 5.7f, 5.6f, 5f, 4.8f, 4.5f, 4.7f, 4f, 3.8f, 3.5f, 3.2f, 3.3f, 2.8f, 2.4f, 1.9f }
            : Enumerable.Range(0, 25).Select(step => 2.9f - .04f * step).ToArray();
        var replay = new PathReplay { EmitReceipts = true,
            PositionAt = _ => new Point2f(100 + distances[Math.Min(pulses, distances.Length - 1)], 100) };
        replay.OnInput = (action, type) =>
        { if (action == GIActions.MoveForward && type == KeyType.KeyDown) pulses++; };
        await replay.Executor.MoveCloseTo(replay.Point("walk"));
        Assert.Empty(replay.RecoveryInputs);
        Assert.InRange(pulses, 1, 24);
        Assert.True(distances[pulses] < 2);
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Theory]
    [InlineData("fallback")]
    [InlineData("duplicate")]
    [InlineData("stale")]
    [InlineData("transformed")]
    [InlineData("fly")]
    [InlineData("climb")]
    [InlineData("no-receipt")]
    public async Task IneligibleStallNeverStartsRecoveryOrReportsArrival(string kind)
    {
        var replay = new PathReplay { EmitReceipts = kind != "no-receipt", Direct = kind != "fallback",
            Duplicate = kind == "duplicate", Transformed = kind == "transformed",
            PositionAt = frame => new Point2f(103.94f, 100 + (frame % 4 < 2 ? .35f : -.35f)) };
        if (kind == "stale")
        {
            replay.Clock.Advance(TimeSpan.FromSeconds(3));
            replay.StampTransform = stamp => stamp with { CapturedTimestamp = 0, CapturedAt = replay.Started };
        }
        if (kind == "fly") replay.MotionAt = _ => MotionStatus.Fly;
        if (kind == "climb") replay.MotionAt = _ => MotionStatus.Climb;
        await Assert.ThrowsAsync<RetryException>(() => replay.Executor.MoveCloseTo(replay.Point("walk")));
        Assert.Empty(replay.RecoveryInputs);
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
        Assert.Equal(KeyType.KeyUp, replay.Inputs[^1].Type);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationOrParentDeadlineDuringRecoveryCleansKeysAndCannotSucceed(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var recoveryEntered = false;
        // Keep both captures of a step on the same heading. Per-frame direction
        // changes exercise re-rotation instead of the recovery under test.
        var replay = new PathReplay(cancellation.Token) { EmitReceipts = true,
            PositionAt = _ => new Point2f(104, 100) };
        replay.OnInput = (_, _) =>
        {
            if (UiOperation.Current?.Name != "path-precise-recovery") return;
            recoveryEntered = true;
            if (cancel) cancellation.Cancel();
            else replay.Clock.Advance(TimeSpan.FromSeconds(11));
        };
        var error = await Record.ExceptionAsync(() => replay.Executor.MoveCloseTo(replay.Point("walk")));
        Assert.True(recoveryEntered, "The cancellation/deadline must occur inside the actual recovery.");
        Assert.NotNull(error);
        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.IsType<RetryException>(error);
        Assert.Equal(KeyType.KeyUp, replay.Inputs[^1].Type);
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Fact]
    public async Task RecoveryWithoutPositionProgressStillFailsRatherThanCreditingTheWaypoint()
    {
        var replay = new PathReplay { EmitReceipts = true, PositionAt = _ => new Point2f(1870.55f, 1252.94f) };
        var target = replay.Point("walk");
        target.X = 1871.02f;
        target.Y = 1256.85f;
        await Assert.ThrowsAsync<RetryException>(() => replay.Executor.MoveCloseTo(target));
        Assert.NotEmpty(replay.RecoveryInputs);
        var recoveryMoves = replay.RecoveryInputs.Where(input => input.Type == KeyType.KeyDown &&
            input.Action is GIActions.MoveBackward or GIActions.MoveLeft or GIActions.MoveRight).ToArray();
        Assert.Equal(new[] { GIActions.MoveLeft, GIActions.MoveRight }, recoveryMoves.Select(input => input.Action));
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
        Assert.Equal(KeyType.KeyUp, replay.Inputs[^1].Type);
    }
}
