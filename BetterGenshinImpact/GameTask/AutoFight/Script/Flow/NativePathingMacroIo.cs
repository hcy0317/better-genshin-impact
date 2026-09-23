using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Script.Dependence;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.ViewModel.Pages;
using Fischless.WindowsInput;
using Microsoft.Extensions.Logging;
using Vanara.PInvoke;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

internal sealed class NativePathingMacroIo(Func<string> context, Action<PathingMacroInput>? transport = null) : IPathingMacroIo
{
    private static readonly InputSimulator CleanupInput = new();
    public TimeProvider Clock => TimeProvider.System;
    public CombatInputCoordinator Coordinator => NativeCombatIo.Coordinator;
    public User32.VK Map(User32.VK key) => KeyBindingsSettingsPageViewModel.MappingKey(key);
    public Task Delay(int milliseconds, CancellationToken ct) => TaskControl.Delay(milliseconds, ct);
    public IDisposable BeginExclusive() => AvatarRecognition.BeginExclusiveOperation(allowPassiveObservation: false);

    public PathingMacroObservation Observe()
    {
        using var frame = TaskControl.CaptureToRectArea();
        var scene = SaurianUiReader.IsKnownTransformation(frame) ? PathingMacroScene.Transformed :
            Bv.IsCombatHud(frame) ? PathingMacroScene.World : PathingMacroScene.Unknown;
        if (scene == PathingMacroScene.Unknown)
        {
            try
            {
                DiagnosticEvidenceScope.Current?.TryCapture(frame, "pathing-macro:" + context(), "unknown-scene",
                    "原帧场景未准入匿名物理宏；" + context(), TaskControl.Logger);
                TaskControl.Logger.LogWarning("PATH_RAW_SCENE_UNKNOWN {Context} source={Session}/{Sequence}",
                    context(), frame.FrameStamp.SessionId, frame.FrameStamp.Sequence);
            }
            catch { /* 诊断不能改变准入。 */ }
        }
        return new(scene, frame.FrameStamp);
    }

    // raw段已经做过场景准入。每个真正SendInput只查原期限/取消/lease；不伪造150ms源帧。
    public CombatBattleHostInputResult Send(PathingMacroInput input, Action admit)
        => Submit(input, admit, cleanup: false);

    public CombatBattleHostInputResult Release(PathingMacroInput input)
    {
        if (input.Kind is not (PathingMacroInputKind.KeyUp or PathingMacroInputKind.MiddleUp))
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
            else Dispatch(input, cleanup ? CleanupInput : Simulation.SendInput);
        }
        catch (Exception failure) { error = failure; }
        var finished = Clock.GetTimestamp();
        return CombatNativeInput.Classify(capture, error, finished) with
        { NativeRequested = capture.Requested, NativeSubmitted = capture.Submitted, ObservableAfterTimestamp = finished };
    }

    private static void Dispatch(PathingMacroInput input, InputSimulator simulator)
    {
            switch (input.Kind)
            {
                case PathingMacroInputKind.KeyDown: simulator.Keyboard.KeyDown(input.Key); break;
                case PathingMacroInputKind.KeyUp: simulator.Keyboard.KeyUp(input.Key); break;
                case PathingMacroInputKind.MoveBy: GlobalMethod.MoveMouseBy(input.X, input.Y); break;
                case PathingMacroInputKind.MiddleDown: simulator.Mouse.MiddleButtonDown(); break;
                case PathingMacroInputKind.MiddleUp: simulator.Mouse.MiddleButtonUp(); break;
                default: throw new ArgumentOutOfRangeException(nameof(input));
            }
    }
}
