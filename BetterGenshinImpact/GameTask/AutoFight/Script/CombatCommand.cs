using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.Helpers;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using TimeSpan = System.TimeSpan;
using Vanara.PInvoke;

namespace BetterGenshinImpact.GameTask.AutoFight.Script;

public class CombatCommand
{
    public string Name { get; set; }

    public Method Method { get; set; }

    public List<string>? Args { get; set; }

    public Dictionary<string, string> Options { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Flags { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool HasFlag(string flag) => Flags.Contains(flag) || Args?.Contains(flag) == true;
    public bool RequiresFlow => Method.IsFlowControl || Options.Count != 0 || RoundParity != null || Flags.Count != 0 || HasFlag("refresh");

    public List<int> ActivatingRound { get; set; }
    public int? RoundParity { get; set; }
    public string? SourceFile { get; set; }
    public int SourceLine { get; set; } = 1;
    public int SourceColumn { get; set; } = 1;
    internal bool IsCompilerGenerated { get; init; }
    internal FormatException Error(string message, Exception? inner = null) =>
        SyntaxError(message, SourceLine, SourceColumn, inner, SourceFile);

    internal static FormatException SyntaxError(string message, int line, int column, Exception? inner = null, string? file = null)
    {
        var error = new FormatException($"{file ?? "策略"} 第{line}行，第{column}列：{message}", inner);
        error.Data["CombatSourceLocated"] = true;
        return error;
    }
    public bool IsActiveInRound(int round) =>
        (RoundParity == null || round % 2 == RoundParity) &&
        (ActivatingRound == null || ActivatingRound.Count == 0 || ActivatingRound.Contains(round));

    public BurstCastResult? LastBurstResult { get; private set; }

    internal CombatCommand(CombatCommand source)
    {
        Name = source.Name;
        Method = source.Method;
        Args = new(source.Args ?? []);
        ActivatingRound = new(source.ActivatingRound ?? []);
        RoundParity = source.RoundParity;
        SourceFile = source.SourceFile;
        SourceLine = source.SourceLine;
        SourceColumn = source.SourceColumn;
        IsCompilerGenerated = source.IsCompilerGenerated;
        foreach (var (key, value) in source.Options) Options.Add(key, value);
        Flags.UnionWith(source.Flags);
    }

    public CombatCommand(string name, string command)
    {
        Name = name.Trim().Normalize();
        command = command.Trim();
        var startIndex = command.IndexOf('(');
        if (startIndex > 0)
        {
            var endIndex = command.LastIndexOf(')');
            if (endIndex != command.Length - 1) throw new FormatException("命令括号不完整或结尾有多余内容");
            var method = command[..startIndex];
            method = method.Trim();
            Method = Method.GetEnumByCode(method);

            var parameters = command.Substring(startIndex + 1, endIndex - startIndex - 1);
            Args = [];
            foreach (var parameter in CombatSyntax.Split(parameters, ','))
            {
                if (parameter.Length == 0) continue;
                var equals = CombatSyntax.FindAssignment(parameter);
                if (equals > 0)
                {
                    var key = parameter[..equals].Trim();
                    var value = CombatSyntax.Unquote(parameter[(equals + 1)..].Trim());
                    if (value.Length == 0 || !Options.TryAdd(key, value))
                        throw new FormatException("参数为空或重复：" + key);
                }
                else if (CombatParameterRegistry.IsCommandFlag(parameter))
                {
                    if (!Flags.Add(parameter.ToLowerInvariant())) throw new FormatException("重复参数：" + parameter);
                }
                else Args.Add(CombatSyntax.Unquote(parameter));
            }
        }
        else
        {
            Method = Method.GetEnumByCode(command);
            Args = [];
        }

        // 保留原 jump/j 的物理跳跃；jump(片段名) 才是具名控制流转移。
        if (Method == Method.Jump && startIndex > 0 && command[..startIndex].Trim() == "jump" &&
            Args.Count == 1 && !double.TryParse(Args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            Method = Method.JumpTo;

        // 校验参数
        if (Args.Contains("refresh") &&
            (Method != Method.Skill || !Args.Contains("hold") ||
             !Args.Contains("wait") || Args.Contains("fast")))
        {
            throw new ArgumentException("裸 refresh 使用 e(hold,wait,refresh)，必须等待冷却并确认长 E 成功，不能与 fast 组合");
        }
        if (Method == Method.Walk)
        {
            AssertUtils.IsTrue(Args.Count == 2, "walk方法必须有两个入参，第一个参数是方向，第二个参数是行走时间。例：walk(s, 0.2)");
            var s = double.Parse(Args[1]);
            AssertUtils.IsTrue(s > 0, "行走时间必须大于0");
        }
        else if (Method == Method.W || Method == Method.A || Method == Method.S || Method == Method.D)
        {
            AssertUtils.IsTrue(Args.Count == 1, "w/a/s/d方法必须有一个入参，代表行走时间。例：d(0.5)");
        }
        else if (Method == Method.MoveBy)
        {
            AssertUtils.IsTrue(Args.Count == 2, "moveby方法必须有两个入参，分别是x和y。例：moveby(100, 100))");
        }
        else if (Method == Method.KeyDown || Method == Method.KeyUp || Method == Method.KeyPress)
        {
            AssertUtils.IsTrue(Args.Count == 1, $"{Method.Alias[0]}方法必须有一个入参，代表按键");
            try
            {
                User32Helper.ToVk(Args[0]);
            }
            catch
            {
                throw new ArgumentException($"{Method.Alias[0]}方法的入参必须是VirtualKeyCodes枚举中的值，当前入参 {Args[0]} 不合法");
            }
        }
        else if (Method == Method.Scroll)
        {
            AssertUtils.IsTrue(Args.Count == 1, "scroll方法必须有一个入参，代表滚动格数。例：scroll(1) 或 scroll(-1)");
            AssertUtils.IsTrue(int.TryParse(Args[0], out _), "滚动格数必须是整数");
        }
    }
    
    public override string ToString()
    {
        return $"<CombatCommand {Name}, {Method}({Args}) (rounds {ActivatingRound})>";
    }

    public bool Execute(CombatScenes combatScenes, CombatCommand? lastCommand = null)
    {
        LastBurstResult = null;
        Avatar? avatar;
        if (Name == CombatScriptParser.CurrentAvatarName)
        {
            var currentName = combatScenes.CurrentAvatar(true);
            avatar = currentName != null ? combatScenes.SelectAvatar(currentName) : combatScenes.SelectAvatar(1);
        }
        else
        {
            // 其余情况要进行角色切换
            avatar = combatScenes.SelectAvatar(Name);
            if (avatar == null)
            {
                return false;
            }

            if (lastCommand == null || lastCommand.Name != Name || combatScenes.LastActiveAvatarIndex != avatar.Index)
            {
                // 新角色块（包括首条宏指令）才确认切人；连续动作复用已确认结果。
                if (!avatar.TrySwitch(10)) return false;
            }
        }
        Execute(avatar);
        return Method != Method.Burst || !HasFlag("required") || LastBurstResult == BurstCastResult.Confirmed;
    }

    public void Execute(Avatar avatar)
    {
        if (Method.IsFlowControl || Options.Count != 0 || RoundParity != null || HasFlag("refresh") || Flags.Count != 0 && Method != Method.Burst)
            throw new InvalidOperationException("增强策略必须通过统一流程执行器运行");
        if (Method == Method.Skill)
        {
            var hold = Args != null && Args.Contains("hold");
            var wait = Args != null && Args.Contains("wait");
            var fast = Args != null && Args.Contains("fast");
            if (fast)
            {
                // 快速跳过e
                if (!avatar.IsSkillReadyFromCurrentFrame())
                {
                    return;
                }
            }
            else if (wait)
            {
                // 等待e结束,同步等待
                avatar.WaitSkillCd(avatar.Ct).GetAwaiter().GetResult();
            }

            avatar.UseSkill(hold, observeCooldown: AvatarRecognition.IsConfiguredGuardian(avatar));
        }
        else if (Method == Method.Burst)
        {
            LastBurstResult = avatar.TryUseBurst();
        }
        else if (Method == Method.Attack)
        {
            if (Args is { Count: > 0 })
            {
                var s = double.Parse(Args![0]);
                avatar.Attack((int)TimeSpan.FromSeconds(s).TotalMilliseconds);
            }
            else
            {
                avatar.Attack();
            }
        }
        else if (Method == Method.Charge)
        {
            if (Args is { Count: > 0 })
            {
                var s = double.Parse(Args![0]);
                avatar.Charge((int)TimeSpan.FromSeconds(s).TotalMilliseconds);
            }
            else
            {
                avatar.Charge();
            }
        }
        else if (Method == Method.Walk)
        {
            var s = double.Parse(Args![1]);
            avatar.Walk(Args![0], (int)TimeSpan.FromSeconds(s).TotalMilliseconds);
        }
        else if (Method == Method.W)
        {
            var s = double.Parse(Args![0]);
            avatar.Walk("w", (int)TimeSpan.FromSeconds(s).TotalMilliseconds);
        }
        else if (Method == Method.A)
        {
            var s = double.Parse(Args![0]);
            avatar.Walk("a", (int)TimeSpan.FromSeconds(s).TotalMilliseconds);
        }
        else if (Method == Method.S)
        {
            var s = double.Parse(Args![0]);
            avatar.Walk("s", (int)TimeSpan.FromSeconds(s).TotalMilliseconds);
        }
        else if (Method == Method.D)
        {
            var s = double.Parse(Args![0]);
            avatar.Walk("d", (int)TimeSpan.FromSeconds(s).TotalMilliseconds);
        }
        else if (Method == Method.Wait)
        {
            var s = double.Parse(Args![0]);
            avatar.Wait((int)TimeSpan.FromSeconds(s).TotalMilliseconds);
        }
        else if (Method == Method.Ready)
        {
            avatar.Ready();
        }
        else if (Method == Method.Check)
        {
            // check动作在AutoFightTask主循环中处理，此处不做任何操作
        }
        else if (Method == Method.Aim)
        {
            throw new NotImplementedException();
        }
        else if (Method == Method.Dash)
        {
            if (Args is { Count: > 0 })
            {
                var s = double.Parse(Args![0]);
                avatar.Dash((int)TimeSpan.FromSeconds(s).TotalMilliseconds);
            }
            else
            {
                avatar.Dash();
            }
        }
        else if (Method == Method.Jump)
        {
            avatar.Jump();
        }
        // 宏
        else if (Method == Method.MouseDown)
        {
            if (Args is { Count: > 0 })
            {
                avatar.MouseDown(Args![0]);
            }
            else
            {
                avatar.MouseDown();
            }
        }
        else if (Method == Method.MouseUp)
        {
            if (Args is { Count: > 0 })
            {
                avatar.MouseUp(Args![0]);
            }
            else
            {
                avatar.MouseUp();
            }
        }
        else if (Method == Method.Click)
        {
            if (Args is { Count: > 0 })
            {
                avatar.Click(Args![0]);
            }
            else
            {
                avatar.Click();
            }
        }
        else if (Method == Method.MoveBy)
        {
            if (Args is { Count: 2 })
            {
                var x = int.Parse(Args![0]);
                var y = int.Parse(Args[1]);
                avatar.MoveBy(x, y);
            }
            else
            {
                throw new ArgumentException("moveby方法必须有两个入参，分别是x和y。例：moveby(100, 100)");
            }
        }
        else if (Method == Method.KeyDown)
        {
            avatar.KeyDown(Args![0]);
        }
        else if (Method == Method.KeyUp)
        {
            avatar.KeyUp(Args![0]);
            TryTriggerESkillCdCheck(avatar, Args![0]);
        }
        else if (Method == Method.KeyPress)
        {
            avatar.KeyPress(Args![0]);
            TryTriggerESkillCdCheck(avatar, Args![0]);
        }
        else if (Method == Method.Scroll)
        {
            avatar.Scroll(int.Parse(Args![0]));
        }
        else if (Method == Method.Round)
        {
            // 作为回合标记使用，不做任何操作
        }
        else
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// KeyUp/KeyPress 为 E 键时，触发 E 技能 CD 检测（由调度层处理，不入侵按键层）。
    /// 内联实现：最多重试 4 次 OCR 截屏检测，防抖由 ESkillCdTracker.TriggerECheck 处理。
    /// </summary>
    private static void TryTriggerESkillCdCheck(Avatar avatar, string key)
    {
        try
        {
            if (User32Helper.ToVk(key) != User32.VK.VK_E) return;
        }
        catch
        {
            return;
        }

        avatar.QueueSkillCooldownObservation();
    }
}
