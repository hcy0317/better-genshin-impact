using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.Core.Script.Dependence;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.ViewModel.Pages;
using Fischless.WindowsInput;
using Microsoft.Extensions.Logging;
using Vanara.PInvoke;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

internal sealed class NativePathingMacroIo(Func<string> context, Action<PathingMacroInput>? transport = null) : IPathingMacroIo
{
    private static readonly Core.Input.IInputChannel CleanupInput = new Core.Input.Backends.Win32.SendInputChannel(new InputSimulator());
    private readonly HashSet<string> _unknownReported = new(StringComparer.Ordinal);
    private PathingMacroEvidence? _evidence;
    private RuntimeStallDiagnostics? _stall;
    public void BeginEvidence(IReadOnlyList<CombatCommand> commands)
    {
        _evidence?.End();
        _evidence = null;
        if (DiagnosticEvidenceScope.Current == null) return;
        _evidence = new PathingMacroEvidence(context(), string.Join(",", commands.Select(command =>
            $"{command.Method.Alias[0]}({string.Join(",", command.Args ?? [])})")));
    }
    public TimeProvider Clock => TimeProvider.System;
    public CombatInputCoordinator Coordinator => NativeCombatIo.Coordinator;
    public User32.VK Map(User32.VK key)
    {
        var physical = KeyBindingsSettingsPageViewModel.MappingKey(key);
        _evidence?.Mapping(key.ToString(), physical.ToString());
        return physical;
    }
    public void FailEvidence(Exception error) => _evidence?.Failed(error, TaskControl.Logger);
    public void EvidenceBoundary(int commandIndex, long deadline, long inputFence) =>
        _evidence?.Boundary(commandIndex, deadline, inputFence);
    public void EndEvidence() { _evidence?.End(); _evidence = null; }
    public Task Delay(int milliseconds, CancellationToken ct) => TaskControl.Delay(milliseconds, ct);
    public IDisposable BeginExclusive() => AvatarRecognition.BeginExclusiveOperation(allowPassiveObservation: false);

    public PathingMacroObservation Observe(string phase = "boundary")
    {
        _stall ??= new(TaskControl.Logger, "pathing-macro", context());
        ImageRegion Capture()
        {
            using var measured = _stall.Measure("capture:" + phase);
            return TaskControl.CaptureToRectArea();
        }
        using var frame = Capture();
        PathingMacroObservation observation;
        using (_stall.Measure("scene:" + phase)) observation = ReadScene(frame, OcrFactory.Paddle,
            inspectCannonRejection: phase.StartsWith("cannon-handshake", StringComparison.Ordinal));
        _evidence?.Capture(frame, phase, observation, TaskControl.Logger);
        var scene = observation.Scene;
        if (scene == PathingMacroScene.Unknown)
        {
            try
            {
                var identity = context();
                if (_unknownReported.Add(identity + ":" + phase))
                {
                    DiagnosticEvidenceScope.Current?.TryCapture(frame, "pathing-macro:" + identity, phase + "-unknown-scene",
                        "原帧场景未准入匿名物理宏；" + identity, TaskControl.Logger);
                    TaskControl.Logger.LogWarning("PATH_RAW_SCENE_UNKNOWN {Context} phase={Phase} source={Session}/{Sequence} sourceAgeMs={Age:F1}",
                        identity, phase, frame.FrameStamp.SessionId, frame.FrameStamp.Sequence,
                        frame.FrameStamp.IsKnown ? Clock.GetElapsedTime(frame.FrameStamp.CapturedTimestamp).TotalMilliseconds : -1);
                }
            }
            catch { /* 诊断不能改变准入。 */ }
        }
        return observation;
    }

    internal static PathingMacroObservation ReadScene(ImageRegion frame, IOcrService ocr, ReviveUiDetector? revive = null,
        bool inspectCannonRejection = false)
    {
        var scene = SaurianUiReader.IsKnownTransformation(frame) ? PathingMacroScene.Transformed :
            (revive?.IsCombatHud(frame) ?? Bv.IsCombatHud(frame)) ? PathingMacroScene.World : PathingMacroScene.Unknown;
        var cannon = scene == PathingMacroScene.Unknown ? CannonUiReader.Read(frame, ocr) : default;
        if (cannon.IsFor(frame.FrameStamp) && cannon.CanExit) scene = PathingMacroScene.Cannon;
        return new(scene, frame.FrameStamp, cannon.IsFor(frame.FrameStamp) && cannon.CanFire)
        {
            ActivationRejection = inspectCannonRejection && scene == PathingMacroScene.World
                ? CannonUiReader.ReadActivationRejection(frame, ocr) : null
        };
    }

    // raw段已经做过场景准入。每个真正SendInput只查原期限/取消/lease；不伪造150ms源帧。
    public CombatBattleHostInputResult Send(PathingMacroInput input, Action admit)
        => Submit(input, admit, cleanup: false);

    public CombatBattleHostInputResult Release(PathingMacroInput input)
    {
        if (input.Kind is not (PathingMacroInputKind.KeyUp or PathingMacroInputKind.MiddleUp or PathingMacroInputKind.LeftUp))
            throw new ArgumentException("清理通道只允许松开已记录输入", nameof(input));
        return Submit(input, () => { }, cleanup: true);
    }

    private CombatBattleHostInputResult Submit(PathingMacroInput input, Action admit, bool cleanup)
    {
        Exception? error = null;
        using var capture = new InputDispatchCapture(admit);
        try
        {
            admit();
            if (!cleanup) TaskControl.CheckAndSleep(0);
            admit();
            if (transport != null) transport(input);
            else Dispatch(input, cleanup && Core.Input.InputHub.Backend.Kind == Core.Input.InputBackendKind.Win32
                ? CleanupInput : Core.Input.InputHub.Foreground);
        }
        catch (Exception failure) { error = failure; }
        var finished = Clock.GetTimestamp();
        var result = CombatNativeInput.Classify(capture, error, finished) with
        { NativeRequested = capture.Requested, NativeSubmitted = capture.Submitted,
            TransportRequested = capture.TransportCalls, TransportAcknowledged = capture.TransportAcknowledged,
            ObservableAfterTimestamp = finished };
        _evidence?.Input(input, result);
        return result;
    }

    private static void Dispatch(PathingMacroInput input, Core.Input.IInputChannel simulator)
    {
            switch (input.Kind)
            {
                case PathingMacroInputKind.KeyDown: simulator.Keyboard.KeyDown(input.Key); break;
                case PathingMacroInputKind.KeyUp: simulator.Keyboard.KeyUp(input.Key); break;
                case PathingMacroInputKind.MoveBy: GlobalMethod.MoveMouseBy(input.X, input.Y); break;
                case PathingMacroInputKind.MiddleDown: simulator.Mouse.MiddleButtonDown(); break;
                case PathingMacroInputKind.MiddleUp: simulator.Mouse.MiddleButtonUp(); break;
                case PathingMacroInputKind.LeftDown: simulator.Mouse.LeftButtonDown(); break;
                case PathingMacroInputKind.LeftUp: simulator.Mouse.LeftButtonUp(); break;
                default: throw new ArgumentOutOfRangeException(nameof(input));
            }
    }
}
