using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.AutoFight.Script;

public class Method
{
    private static readonly ILogger Logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    public static readonly Method Skill = new(["skill", "e"]);
    public static readonly Method Burst = new(["burst", "q"]);
    public static readonly Method Attack = new(["attack", "普攻", "普通攻击"]);
    public static readonly Method Charge = new(["charge", "重击"]);
    public static readonly Method Wait = new(["wait", "after", "等待"]);
    public static readonly Method Ready = new(["ready", "完成"]);
    public static readonly Method Check = new(["check", "检测"]);

    public static readonly Method Walk = new(["walk", "行走"]);
    public static readonly Method W = new(["w"]);
    public static readonly Method A = new(["a"]);
    public static readonly Method S = new(["s"]);
    public static readonly Method D = new(["d"]);

    public static readonly Method Aim = new(["aim", "r", "瞄准"]);
    public static readonly Method Dash = new(["dash", "冲刺"]);
    public static readonly Method Jump = new(["jump", "j", "跳跃"]);

    // 宏
    public static readonly Method MouseDown = new(["mousedown"]);
    public static readonly Method MouseUp = new(["mouseup"]);
    public static readonly Method Click = new(["click"]);
    public static readonly Method MoveBy = new(["moveby"]);
    public static readonly Method KeyDown = new(["keydown"]);
    public static readonly Method KeyUp = new(["keyup"]);
    public static readonly Method KeyPress = new(["keypress"]);
    public static readonly Method Scroll = new(["scroll", "verticalscroll"]);
    public static readonly Method Round = new(["round"]);
    public static readonly Method Record = new(["record"]);
    public static readonly Method Timing = new(["timing"]);
    public static readonly Method Segment = new(["segment"]);
    public static readonly Method Call = new(["call"]);
    public static readonly Method Branch = new(["branch"]);
    public static readonly Method Return = new(["return"]);
    public static readonly Method JumpTo = new(["jump"]);
    public static readonly Method Strategy = new(["strategy"]);

    public bool IsFlowControl => this == Record || this == Timing || this == Segment || this == Call ||
        this == Branch || this == Return || this == JumpTo || this == Strategy || this == Round;

    public static IEnumerable<Method> Values
    {
        get
        {
            yield return Skill;
            yield return Burst;
            yield return Attack;
            yield return Charge;
            yield return Wait;
            yield return Ready;
            yield return Check;

            yield return Walk;
            yield return W;
            yield return A;
            yield return S;
            yield return D;

            // yield return Aim;
            yield return Dash;
            yield return Jump;

            // 宏
            yield return MouseDown;
            yield return MouseUp;
            yield return Click;
            yield return MoveBy;
            yield return KeyDown;
            yield return KeyUp;
            yield return KeyPress;
            yield return Scroll;
            yield return Round;
            yield return Record;
            yield return Timing;
            yield return Segment;
            yield return Call;
            yield return Branch;
            yield return Return;
            yield return JumpTo;
            yield return Strategy;
        }
    }

    /// <summary>
    /// 别名
    /// </summary>
    public List<string> Alias { get; private set; }

    public Method(List<string> alias)
    {
        Alias = alias;
    }

    public static Method GetEnumByCode(string method)
    {
        foreach (var m in Values)
        {
            if (m.Alias.Contains(method))
            {
                return m;
            }
        }

        Logger.LogError($"战斗策略脚本中出现未知的方法：{method}");
        throw new ArgumentException($"战斗策略脚本中出现未知的方法：{method}");
    }
}
