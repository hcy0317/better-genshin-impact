using System;
using System.Collections.Generic;
using System.Linq;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

namespace BetterGenshinImpact.GameTask.AutoFight.Script;

public partial class CombatScriptParser
{
    private static string NormalizePunctuation(string text) => text.Replace('（', '(').Replace('）', ')').Replace('，', ',')
        .Replace('｛', '{').Replace('｝', '}');

    private static CombatScript ParseContextCore(string context, bool validate, string? defaultAvatarName,
        int firstLine, int firstColumn)
    {
        var lines = context.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        List<CombatCommand> commands = [];
        HashSet<string> names = [];
        var braces = new Stack<(CombatCommand Start, int Depth, string? Record)>();
        CombatCommand? pending = null;
        var depth = 0;
        for (var index = 0; index < lines.Length; index++)
        {
            var lineNumber = firstLine + index;
            var baseColumn = index == 0 ? firstColumn : 1;
            var line = NormalizePunctuation(lines[index]);
            try
            {
                foreach (var part in CombatSyntax.SplitBlocksLocated(line))
                {
                    var column = baseColumn + part.Offset;
                    if (part.Text == "{")
                    {
                        if (pending?.Args?.FirstOrDefault() is not { Length: > 0 } name)
                            throw CombatCommand.SyntaxError("{ 前需要 segment(片段名[,define])", lineNumber, column);
                        if (pending.Options.ContainsKey("name")) throw pending.Error("片段名已在首个参数中给出，不再填写 name=");
                        pending.Args[0] = "start";
                        pending.Options.Add("name", name);
                        pending.Options.Remove("record", out var record);
                        braces.Push((pending, ++depth, record));
                        pending = null;
                        continue;
                    }
                    if (pending != null) throw pending.Error("具名 segment 后需要 { ... } 片段体");
                    if (part.Text == "}")
                    {
                        if (braces.Count == 0 || braces.Peek().Depth != depth)
                            throw CombatCommand.SyntaxError("} 没有匹配的片段 {，或混用了旧 segment(end)", lineNumber, column);
                        var block = braces.Pop();
                        var end = new CombatCommand("", "segment(end)") { SourceLine = lineNumber, SourceColumn = column };
                        if (block.Record != null) end.Options.Add("record", block.Record);
                        commands.Add(end);
                        depth--;
                        continue;
                    }
                    var statement = part.Text;
                    var commaPrefix = statement.Length - statement.TrimStart(',').Length;
                    statement = statement[commaPrefix..];
                    if (string.IsNullOrWhiteSpace(statement)) continue;
                    var whitespace = statement.Length - statement.TrimStart().Length;
                    foreach (var command in ParseLine(statement.Trim(), names, validate, defaultAvatarName,
                                 lineNumber, column + commaPrefix + whitespace))
                    {
                        if (pending != null) throw pending.Error("具名 segment 后需要 { ... } 片段体");
                        if (command.Method == Method.Segment)
                        {
                            var marker = command.Args?.FirstOrDefault();
                            if (marker == "start") depth++;
                            else if (marker == "end")
                            {
                                if (braces.Count != 0 && braces.Peek().Depth == depth)
                                    throw command.Error("花括号片段使用 } 结束，不混用 segment(end)");
                                depth--;
                            }
                            else pending = command;
                        }
                        commands.Add(command);
                    }
                }
            }
            catch (Exception exception) when (exception.Data["CombatSourceLocated"] is not true)
            {
                throw CombatCommand.SyntaxError(exception.Message, lineNumber,
                    baseColumn + (exception.Data["CombatColumn"] is int offset ? offset - 1 : 0), exception);
            }
        }
        if (pending != null) throw pending.Error("具名 segment 后需要 { ... } 片段体");
        if (braces.Count != 0) throw braces.Peek().Start.Error("片段缺少结束的 }");
        return new(names, commands);
    }
}
