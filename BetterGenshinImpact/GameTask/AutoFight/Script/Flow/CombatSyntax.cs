using System;
using System.Collections.Generic;
using System.Linq;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

/// <summary>共用词法边界：只在括号和引号外分隔，不解释条件表达式。</summary>
internal static class CombatSyntax
{
    internal readonly record struct Part(string Text, int Offset);

    public static bool HasBlockSyntax(string text) => SplitBlocksLocated(text).Any(part => part.Text is "{" or "}");

    public static List<Part> SplitBlocksLocated(string text)
    {
        List<Part> parts = [];
        var depth = 0;
        var quote = '\0';
        var start = 0;
        void Add(int end)
        {
            var offset = start;
            while (offset < end && char.IsWhiteSpace(text[offset])) offset++;
            if (offset < end) parts.Add(new(text[offset..end].TrimEnd(), offset));
        }
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0')
            {
                if (c == '\\' && i + 1 < text.Length) { i++; continue; }
                if (c == quote) quote = '\0';
                continue;
            }
            if (c is '\'' or '"') { quote = c; continue; }
            if (c == '(') depth++;
            if (c == ')' && --depth < 0) throw LexicalError("多余的右括号", i);
            if (depth != 0) continue;
            if (c == '#' || c == '/' && i + 1 < text.Length && text[i + 1] == '/') { Add(i); return parts; }
            if (c is '{' or '}' or ';')
            {
                Add(i);
                if (c != ';') parts.Add(new(c.ToString(), i));
                start = i + 1;
            }
        }
        if (depth != 0 || quote != '\0') throw LexicalError("括号或引号未配对", start);
        Add(text.Length);
        return parts;
    }

    public static List<string> Split(string text, char separator) => SplitLocated(text, separator).Select(part => part.Text).ToList();

    public static List<Part> SplitLocated(string text, char separator)
    {
        List<Part> parts = [];
        var depth = 0;
        var start = 0;
        var quote = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0')
            {
                if (c == '\\' && i + 1 < text.Length) { i++; continue; }
                if (c == quote) quote = '\0';
                continue;
            }
            if (c is '\'' or '"') { quote = c; continue; }
            if (c == '(') depth++;
            if (c == ')' && --depth < 0) throw LexicalError("多余的右括号", i);
            if (c == separator && depth == 0)
            {
                AddPart(i);
                start = i + 1;
            }
        }
        if (depth != 0 || quote != '\0') throw LexicalError("括号或引号未配对", start);
        AddPart(text.Length);
        return parts;

        void AddPart(int end)
        {
            var offset = start;
            while (offset < end && char.IsWhiteSpace(text[offset])) offset++;
            parts.Add(new(text[offset..end].TrimEnd(), offset));
        }
    }

    private static FormatException LexicalError(string message, int offset)
    {
        var error = new FormatException(message);
        error.Data["CombatColumn"] = offset + 1;
        return error;
    }

    public static string Unquote(string value)
    {
        if (value.Length < 2 || value[0] != value[^1] || value[0] is not ('\'' or '"')) return value.Trim().Normalize();
        var result = new System.Text.StringBuilder();
        for (var i = 1; i < value.Length - 1; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length - 1 && (value[i + 1] == value[0] || value[i + 1] == '\\')) i++;
            result.Append(value[i]);
        }
        return result.ToString().Trim().Normalize();
    }

    public static int FindAssignment(string parameter)
    {
        var depth = 0;
        var quote = '\0';
        for (var i = 0; i < parameter.Length; i++)
        {
            var c = parameter[i];
            if (quote != '\0')
            {
                if (c == '\\' && i + 1 < parameter.Length) { i++; continue; }
                if (c == quote) quote = '\0';
                continue;
            }
            if (c is '\'' or '"') { quote = c; continue; }
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == '=' && depth == 0) return i;
        }
        return -1;
    }
}
