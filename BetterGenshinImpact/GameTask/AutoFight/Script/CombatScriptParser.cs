using BetterGenshinImpact.GameTask.AutoFight.Config;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.AutoFight.Script;

public partial class CombatScriptParser
{
    private static readonly ILogger Logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    public static string CurrentAvatarName = "当前角色";
    
    public static CombatScriptBag ReadAndParse(string path)
    {
        if (File.Exists(path))
        {
            return new CombatScriptBag(Parse(path));
        }
        else if (Directory.Exists(path))
        {
            var files = Directory.GetFiles(path, "*", SearchOption.AllDirectories)
                .Where(file => file.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)).ToArray();
            // 文件系统枚举顺序不稳定；专属 00-* 策略必须先于通用策略参与四人匹配。
            Array.Sort(files, (left, right) =>
            {
                var priority = (Path.GetFileName(left).StartsWith("00-", StringComparison.Ordinal) ? 0 : 1)
                    .CompareTo(Path.GetFileName(right).StartsWith("00-", StringComparison.Ordinal) ? 0 : 1);
                if (priority != 0) return priority;
                return StringComparer.Ordinal.Compare(left, right);
            });
            if (files.Length == 0)
            {
                Logger.LogError("战斗脚本文件不存在：{Path}", path);
                throw new Exception("战斗脚本文件不存在");
            }

            var combatScripts = new List<CombatScript>();
            foreach (var file in files)
            {
                try
                {
                    combatScripts.Add(Parse(file));
                }
                catch (Exception e)
                {
                    Logger.LogWarning("解析战斗脚本文件失败：{Path} , {Msg} ", file, e.Message);
                }
            }

            return new CombatScriptBag(combatScripts);
        }
        else
        {
            Logger.LogError("战斗脚本文件不存在：{Path}", path);
            throw new Exception("战斗脚本文件不存在");
        }
    }

    public static CombatScript Parse(string path)
    {
        var script = File.ReadAllText(path);
        CombatScript combatScript;
        try { combatScript = ParseContext(script); }
        catch (FormatException exception)
        {
            var located = new FormatException($"{path}: {exception.Message}", exception);
            located.Data["CombatSourceLocated"] = true;
            throw located;
        }
        combatScript.Path = path;
        combatScript.Name = Path.GetFileNameWithoutExtension(path);
        foreach (var command in combatScript.CombatCommands) command.SourceFile = path;
        return combatScript;
    }

    public static CombatScript ParseContext(string context, bool validate = true, string? defaultAvatarName = null)
        => ParseContextCore(context, validate, defaultAvatarName, 1, 1);

    private static List<CombatCommand> ParseLine(string line, HashSet<string> combatAvatarNames, bool validate = true,
        string? defaultAvatarName = null, int sourceLine = 1, int sourceColumn = 1)
    {
        line = line.Trim();
        var oneLineCombatCommands = new List<CombatCommand>();
        // 以空格分隔角色和指令 截取第一个空格前的内容为角色名称，后面的为指令
        // 20241116更新 不输入角色名称时，直接以当前角色为准
        // 用括号嵌套深度查找角色分隔符：深度为 0 时的空格才是角色名和指令的分隔
        // 无角色前缀时（如 walk(s, 0.2)），所有空格都在括号内，separatorIndex 保持 -1
        var depth = 0;
        var separatorIndex = -1;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '(') depth++;
            else if (line[i] == ')') depth--;
            else if (line[i] == ' ' && depth == 0)
            {
                separatorIndex = i;
                break;
            }
        }

        var character = defaultAvatarName ?? CurrentAvatarName;
        var commands = line;
        var firstToken = line.Split('(', ' ', ',')[0];
        var controlLine = Method.Values.Any(method => method.IsFlowControl && method.Alias.Contains(firstToken));
        if (controlLine) separatorIndex = -1;
        if (separatorIndex > 0)
        {
            character = line[..separatorIndex];
            character = DefaultAutoFightConfig.AvatarAliasToStandardName(character);
            commands = line[(separatorIndex + 1)..];
            sourceColumn += separatorIndex + 1;
        }
        else
        {
            // 无显式前缀时，若提供了 defaultAvatarName 则对其标准化；否则走验证失败
            if (defaultAvatarName != null)
            {
                character = DefaultAutoFightConfig.AvatarAliasToStandardName(defaultAvatarName);
            }
            else if (validate && !controlLine)
            {
                Logger.LogError("战斗脚本格式错误，必须以空格分隔角色和指令");
                throw new Exception("战斗脚本格式错误，必须以空格分隔角色和指令");
            }
        }

        oneLineCombatCommands.AddRange(ParseLineCommands(commands, character, sourceLine, sourceColumn));
        if (oneLineCombatCommands.Any(command => !command.Method.IsFlowControl)) combatAvatarNames.Add(character);
        return oneLineCombatCommands;
    }

    public static List<int> ParseRoundCommand(CombatCommand roundCommand) {
        // 解析round命令的入参，返回一个整数列表，代表在哪些回合执行后续指令
        // 支持Round(1)、Round(1,3,5)、Round(2-4)、Round(1,3-5)等格式
        var activatingRounds = new List<int>();
        if (roundCommand.Args == null || roundCommand.Args.Count == 0) {
            Logger.LogError("round方法必须有入参，代表在哪些回合执行后续指令，例：round(1)、round(1,3-5)");
            throw new ArgumentException("round方法必须有入参，代表在哪些回合执行后续指令，例：round(1)、round(1,3-5)");
        }
        foreach (var arg in roundCommand.Args) {
            if (arg.Contains('-')) {
                // 范围
                var parts = arg.Split('-', StringSplitOptions.TrimEntries);
                if (parts.Length != 2) {
                    Logger.LogError("round方法的入参格式错误，例：round(1-3)");
                    throw new ArgumentException("round方法的入参格式错误，例：round(1-3)");
                }
                var start = int.Parse(parts[0]);
                var end = int.Parse(parts[1]);
                if (start > end || start <= 0) {
                    Logger.LogError("round方法的入参格式错误，起始回合必须小于等于结束回合且大于0，例：round(1-3)");
                    throw new ArgumentException("round方法的入参格式错误，起始回合必须小于等于结束回合且大于0，例：round(1-3)");
                }
                for (int i = start; i <= end; i++) {
                    activatingRounds.Add(i);
                }
            } else {
                // 单个回合
                var round = int.Parse(arg);
                if (round <= 0) {
                    Logger.LogError("round方法的入参格式错误，回合数必须大于0，例：round(1)");
                    throw new ArgumentException("round方法的入参格式错误，回合数必须大于0，例：round(1)");
                }
                activatingRounds.Add(round);
            }
        }
        return activatingRounds;
    }

    public static List<CombatCommand> ParseLineCommands(string lineWithoutAvatar, string avatarName, int sourceLine = 1, int sourceColumn = 1) {
        lineWithoutAvatar = NormalizePunctuation(lineWithoutAvatar);
        if (CombatSyntax.HasBlockSyntax(lineWithoutAvatar))
            return ParseContextCore(lineWithoutAvatar, false, avatarName, sourceLine, sourceColumn).CombatCommands;
        var parts = CombatSyntax.SplitLocated(lineWithoutAvatar, '|').Where(part => part.Text.Length != 0);
        var fullCombatCommands = new List<CombatCommand>();
        (List<int> Rounds, int? Parity, CombatCommand Command)? pendingRound = null;
        foreach (var part in parts)
        {
            var combatCommands = ParseLinePart(part.Text, avatarName, sourceLine, sourceColumn + part.Offset);
            var declarations = combatCommands.TakeWhile(command => command.Method == Method.Strategy || command.Method == Method.Timing).Count();
            if (combatCommands.Count > declarations && combatCommands[declarations].Method == Method.Round) {
                // 遇到round指令，作为回合分隔符使用，不加入最终指令列表
                var roundCommand = combatCommands[declarations];
                var parity = roundCommand.Args is { Count: 1 } && roundCommand.Args[0] is "odd" or "even"
                    ? (int?)(roundCommand.Args[0] == "odd" ? 1 : 0) : null;
                var activatingRounds = parity == null ? ParseRoundCommand(roundCommand) : [];
                combatCommands.RemoveAt(declarations);
                if (combatCommands.Count == declarations)
                {
                    fullCombatCommands.AddRange(combatCommands);
                    pendingRound = (activatingRounds, parity, roundCommand);
                    continue;
                }
                foreach (var combatCommand in combatCommands.Skip(declarations)) {
                    
                    combatCommand.ActivatingRound = activatingRounds;
                    combatCommand.RoundParity = parity;
                }
            }
            else if (pendingRound is { } filter)
                foreach (var command in combatCommands.Skip(declarations))
                {
                    command.ActivatingRound = filter.Rounds;
                    command.RoundParity = filter.Parity;
                }
            pendingRound = null;
            fullCombatCommands.AddRange(combatCommands);
        }
        if (pendingRound is { } dangling) throw dangling.Command.Error("round 过滤器后需要动作或 call");
        // foreach (var combatCommand in fullCombatCommands)
        // {
        //     Logger.LogDebug("解析战斗脚本命令：{cmd}", combatCommand.ToString());
        // }
        return fullCombatCommands;
    }

    public static List<CombatCommand> ParseLinePart(string lineWithoutAvatar, string avatarName, int sourceLine = 1, int sourceColumn = 1)
    {
        List<CombatCommand> result = [];
        foreach (var part in CombatSyntax.SplitLocated(lineWithoutAvatar, ',').Where(part => part.Text.Length != 0))
        {
            try
            {
                result.Add(new(avatarName, part.Text) { SourceLine = sourceLine, SourceColumn = sourceColumn + part.Offset });
            }
            catch (Exception exception) when (exception.Data["CombatSourceLocated"] is not true)
            {
                throw CombatCommand.SyntaxError(exception.Message, sourceLine, sourceColumn + part.Offset, exception);
            }
        }
        return result;
    }
}
