using System;
using System.Globalization;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

internal static partial class CombatFlowValidation
{
    private static void ValidateActionArguments(CombatCommand command)
    {
        if (command.Method.IsFlowControl || command.Method == Method.Skill || command.Method == Method.Burst) return;
        var args = command.Args!;
        void Count(int min, int max)
        {
            if (args.Count < min || args.Count > max) throw command.Error(command.Method.Alias[0] + " 的位置参数数量不正确");
        }
        void Seconds(int index)
        {
            if (!double.TryParse(args[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
                !double.IsFinite(seconds) || seconds < 0 || seconds > int.MaxValue / 1000d)
                throw command.Error("动作时长必须是可表示为毫秒的有限非负秒数");
        }
        if (command.Method == Method.Attack || command.Method == Method.Charge || command.Method == Method.Dash)
        {
            Count(0, 1);
            if (args.Count > 0) Seconds(0);
        }
        else if (command.Method == Method.Wait || command.Method == Method.W || command.Method == Method.A ||
                 command.Method == Method.S || command.Method == Method.D)
        {
            Count(1, 1);
            Seconds(0);
        }
        else if (command.Method == Method.Walk)
        {
            Count(2, 2);
            Seconds(1);
            if (args[0] is not ("w" or "a" or "s" or "d")) throw command.Error("walk 方向必须为 w/a/s/d");
        }
        else if (command.Method == Method.MoveBy)
        {
            Count(2, 2);
            foreach (var value in args)
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                    throw command.Error("moveby 的 x/y 必须为整数");
        }
        else if (command.Method == Method.MouseDown || command.Method == Method.MouseUp || command.Method == Method.Click)
        {
            Count(0, 1);
            if (args.Count == 1 && args[0].ToLowerInvariant() is not ("left" or "right" or "middle"))
                throw command.Error("鼠标按键必须为 left/right/middle");
        }
        else if (command.Method == Method.Ready || command.Method == Method.Check || command.Method == Method.Jump) Count(0, 0);
        else if (command.Method == Method.KeyDown || command.Method == Method.KeyUp || command.Method == Method.KeyPress || command.Method == Method.Scroll)
            Count(1, 1);
    }
}
