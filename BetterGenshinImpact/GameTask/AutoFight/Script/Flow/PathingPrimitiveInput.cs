using System;
using System.Globalization;
using System.Threading;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using Fischless.GameCapture;
using BetterGenshinImpact.Helpers;
using BetterGenshinImpact.ViewModel.Pages;
using Vanara.PInvoke;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

/// <summary>路径已有匿名导航命令的物理叶层，不负责调度、角色确认或判定游戏目标完成。</summary>
internal static class PathingPrimitiveInput
{
    internal static bool Supports(CombatCommand command)
    {
        if (command.Name != CombatScriptParser.CurrentAvatarName) return false;
        if (command.Method == Method.Jump || command.Method == Method.Wait || command.Method == Method.W || command.Method == Method.A ||
            command.Method == Method.S || command.Method == Method.D) return true;
        if (command.Method != Method.KeyPress || command.Args is not { Count: 1 }) return false;
        var key = User32Helper.ToVk(command.Args[0]);
        // 不放开技能、鼠标、确认/消费或任意按键；只支持已有大世界交互及导航指令。
        // SPACE用于路径中的跳跃/收起风之翼；不能先等待禁止飞行切人的选角协议。
        return key is User32.VK.VK_F or User32.VK.VK_ESCAPE or User32.VK.VK_SPACE or User32.VK.VK_W or
            User32.VK.VK_A or User32.VK.VK_S or User32.VK.VK_D;
    }

    internal static bool RequiresCannonScene(CombatCommand command) =>
        command.Name == CombatScriptParser.CurrentAvatarName && command.Method == Method.KeyPress &&
        command.Args is { Count: 1 } && User32Helper.ToVk(command.Args[0]) == User32.VK.VK_RETURN;

    internal static bool Supports(CombatCommand command, CannonUiObservation scene, CaptureFrameStamp source) =>
        Supports(command) || RequiresCannonScene(command) && scene.CanFire && scene.IsFor(source);

    internal static void Send(CombatCommand command, CancellationToken ct,
        CannonUiObservation scene = default, CaptureFrameStamp source = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!Supports(command, scene, source) || command.Method == Method.Wait)
            throw new ArgumentException("该命令不是可发送的匿名路径导航输入");
        if (command.Method == Method.Jump)
        {
            Simulation.SendInput.SimulateAction(GIActions.Jump);
            return;
        }
        if (command.Method == Method.KeyPress)
        {
            Simulation.SendInput.Keyboard.KeyPress(KeyBindingsSettingsPageViewModel.MappingKey(User32Helper.ToVk(command.Args![0])));
            return;
        }
        var direction = command.Method == Method.W ? GIActions.MoveForward :
            command.Method == Method.A ? GIActions.MoveLeft :
            command.Method == Method.S ? GIActions.MoveBackward : GIActions.MoveRight;
        var key = direction.ToActionKey().ToVK();
        var milliseconds = checked((int)Math.Ceiling(double.Parse(command.Args![0], CultureInfo.InvariantCulture) * 1000));
        Simulation.SendInput.Keyboard.KeyDown(key);
        try { TaskControl.Sleep(milliseconds, ct); }
        finally { Simulation.SendInput.Keyboard.KeyUp(key); }
    }
}
