using System;
using System.Collections.Generic;
using System.Linq;

namespace BetterGenshinImpact.GameTask.AutoFight.Script;

public partial class ConditionEvaluator
{
    /// <summary>复用现有词法/AST，在战斗外编译，运行时保留三值语义。</summary>
    public static CompiledCondition Compile(string expression, IEnumerable<string>? names = null)
    {
        if (string.IsNullOrWhiteSpace(expression)) return new(_ => true);
        var identifiers = new HashSet<string>(FunctionNames, StringComparer.OrdinalIgnoreCase);
        if (names != null) identifiers.UnionWith(names);
        var tokens = Tokenize(expression, identifiers);
        var position = 0;
        var ast = ParseOrExpr(tokens, ref position);
        if (tokens[position].Type != TokenType.End) throw new FormatException("条件表达式结尾有多余内容");
        Validate(ast);
        return new(resolve => EvaluateNode(ast, resolve), Array.AsReadOnly(References(ast).ToArray()),
            Functions(ast).ToHashSet(StringComparer.OrdinalIgnoreCase), Array.AsReadOnly(References(ast, recordsOnly: false).ToArray()));
    }

    private static IEnumerable<string> Functions(AstNode node)
    {
        if (node is FuncCallNode call)
        {
            if (!call.IsLiteral && (call.IsInvocation || FunctionNames.Contains(call.Name))) yield return call.Name;
            foreach (var argument in call.Args)
                foreach (var function in Functions(argument)) yield return function;
        }
        else if (node is BinaryOpNode binary)
        {
            foreach (var function in Functions(binary.Left)) yield return function;
            foreach (var function in Functions(binary.Right)) yield return function;
        }
        else if (node is UnaryOpNode unary)
            foreach (var function in Functions(unary.Operand)) yield return function;
    }

    internal sealed record ConditionReference(string Function, string Name);

    private static IEnumerable<ConditionReference> References(AstNode node, bool recordsOnly = true)
    {
        if (node is FuncCallNode { IsInvocation: true } call)
        {
            if (call.Name.StartsWith("record-", StringComparison.OrdinalIgnoreCase) || !recordsOnly &&
                (call.Name.Equals("succeeded", StringComparison.OrdinalIgnoreCase) || call.Name.Equals("call-index", StringComparison.OrdinalIgnoreCase)))
            {
                if (call.Args.Count != 1 || call.Args[0] is not FuncCallNode { IsInvocation: false } name)
                    throw new FormatException("记录条件需要一个显式记录名：" + call.Name);
                yield return new(call.Name.ToLowerInvariant(), name.Name);
            }
            foreach (var argument in call.Args)
                foreach (var reference in References(argument, recordsOnly)) yield return reference;
        }
        else if (node is BinaryOpNode binary)
        {
            foreach (var reference in References(binary.Left, recordsOnly)) yield return reference;
            foreach (var reference in References(binary.Right, recordsOnly)) yield return reference;
        }
        else if (node is UnaryOpNode unary)
            foreach (var reference in References(unary.Operand, recordsOnly)) yield return reference;
    }

    private static void Validate(AstNode node)
    {
        switch (node)
        {
            case FuncCallNode call:
                if (!call.IsLiteral && (call.IsInvocation || FunctionNames.Contains(call.Name)))
                {
                    ConditionFunctionRegistry.Validate(call.Name, call.Args.Count);
                    if (call.Name.StartsWith("record-", StringComparison.OrdinalIgnoreCase) ||
                        call.Name.Equals("call-index", StringComparison.OrdinalIgnoreCase) || call.Name.Equals("succeeded", StringComparison.OrdinalIgnoreCase))
                    {
                        if (call.Args[0] is not FuncCallNode { IsInvocation: false } selector)
                            throw new FormatException("条件需要一个显式名称：" + call.Name);
                        if (string.IsNullOrWhiteSpace(selector.Name)) throw new FormatException("条件中的名称不能为空：" + call.Name);
                        if (selector.Name.StartsWith('$')) throw new FormatException("条件不能寻址编译器的保留命名空间");
                    }
                }
                foreach (var argument in call.Args) Validate(argument);
                break;
            case BinaryOpNode binary: Validate(binary.Left); Validate(binary.Right); break;
            case UnaryOpNode unary: Validate(unary.Operand); break;
        }
    }

    public sealed class CompiledCondition
    {
        private readonly Func<Func<string, IReadOnlyList<object?>, object?>, object?> _evaluate;
        internal IReadOnlyList<ConditionReference> References { get; }
        internal IReadOnlyList<ConditionReference> NamedReferences { get; }
        private readonly IReadOnlySet<string>? _functions;
        public bool UsesFunction(string name) => _functions?.Contains(name) == true;
        internal CompiledCondition(Func<Func<string, IReadOnlyList<object?>, object?>, object?> evaluate,
            IReadOnlyList<ConditionReference>? references = null, IReadOnlySet<string>? functions = null,
            IReadOnlyList<ConditionReference>? namedReferences = null)
        {
            _evaluate = evaluate;
            References = references ?? Array.Empty<ConditionReference>();
            NamedReferences = namedReferences ?? References;
            _functions = functions;
        }
        public object? Evaluate(Func<string, IReadOnlyList<object?>, object?> resolve) => _evaluate(resolve);
        public bool? EvaluateBoolean(Func<string, IReadOnlyList<object?>, object?> resolve) => Truth(_evaluate(resolve));
    }

    public static bool? Truth(object? value) => value switch
    {
        bool flag => flag,
        double number when !double.IsNaN(number) => number > 0,
        int number => number != 0,
        _ => null
    };

    private static double? Numeric(object? value) => value switch
    {
        double number when !double.IsNaN(number) => number,
        int number => number,
        bool flag => flag ? 1 : 0,
        _ => null
    };

    private static object? EvaluateNode(AstNode node, Func<string, IReadOnlyList<object?>, object?> resolve)
    {
        switch (node)
        {
            case NumberNode number: return number.Value;
            case BoolNode flag: return flag.Value;
            case FuncCallNode function:
                if (function.IsLiteral || !function.IsInvocation && !FunctionNames.Contains(function.Name)) return function.Name;
                var args = function.Args.Select(argument => EvaluateNode(argument, resolve)).ToArray();
                var name = function.Name.ToLowerInvariant();
                if (name == "odd") return args.Length == 1 && Numeric(args[0]) is { } integer ? integer % 2 != 0 : null;
                if (name is "min" or "max")
                {
                    var values = args.Select(Numeric).ToArray();
                    return values.Length == 0 || values.Any(value => value == null) ? null
                        : name == "min" ? values.Min() : values.Max();
                }
                return resolve(name, args);
            case UnaryOpNode unary:
                var operand = EvaluateNode(unary.Operand, resolve);
                return unary.Op == "!" ? Truth(operand) is { } truth ? !truth : null
                    : Numeric(operand) is { } unaryNumber ? -unaryNumber : null;
            case BinaryOpNode binary:
                var left = EvaluateNode(binary.Left, resolve);
                if (binary.Op == "&&" && Truth(left) == false) return false;
                if (binary.Op == "||" && Truth(left) == true) return true;
                var right = EvaluateNode(binary.Right, resolve);
                if (binary.Op == "&&") return Truth(right) == false ? false : Truth(left) == true ? Truth(right) : null;
                if (binary.Op == "||") return Truth(right) == true ? true : Truth(left) == false ? Truth(right) : null;
                if (Numeric(left) is not { } a || Numeric(right) is not { } b) return null;
                return binary.Op switch
                {
                    ">" => a > b, "<" => a < b, "=" => Math.Abs(a - b) < .0001,
                    "+" => a + b, "-" => a - b, "*" => a * b, "/" => b == 0 ? null : a / b,
                    _ => null
                };
            default: return null;
        }
    }
}
