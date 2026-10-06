using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Dcms.Plugins.DynamicApps.Automation;

/// <summary>An expression or template the language refuses, with where.</summary>
public sealed class ExpressionException(string message, int position = -1)
    : Exception(position >= 0 ? $"{message} (at {position})" : message);

/// <summary>
/// The language tenant-authored flows are written in (ADR 0021): a small CEL-like expression
/// language over JSON values. Deterministic and closed: literals, variables, member and index
/// access, arithmetic, comparison, boolean logic, <c>in</c>, a conditional, and a fixed set of
/// pure functions. No reflection, no I/O, no loops; every evaluation is bounded in steps and
/// output size, and every parse in length and nodes.
/// </summary>
/// <example><c>row.amount &gt;= 10000 &amp;&amp; "status" in changedFields</c></example>
public static class Expressions
{
    public const int MaxSourceChars = 2_000;
    public const int MaxNodes = 500;
    public const int MaxDepth = 64;
    public const int MaxSteps = 10_000;
    public const int MaxStringChars = 65_536;

    /// <summary>The function names, so a validator can report an unknown one before anything runs.</summary>
    public static readonly IReadOnlySet<string> Functions = new HashSet<string>(StringComparer.Ordinal)
    {
        "len", "lower", "upper", "trim", "contains", "startsWith", "endsWith", "coalesce", "string", "number",
        "round", "now", "today", "addDays", "addHours", "join",
    };

    /// <exception cref="ExpressionException">It does not parse, or is too large.</exception>
    public static Node Parse(string source)
    {
        if (source.Length > MaxSourceChars)
        {
            throw new ExpressionException($"An expression is at most {MaxSourceChars} characters.");
        }
        return new Parser(source).ParseAll();
    }

    /// <summary>The variables an expression reads, top-level names only (<c>row</c>, <c>steps</c>…).</summary>
    public static IEnumerable<string> Variables(Node node) => node switch
    {
        Var v => [v.Name],
        Member m => Variables(m.Target),
        Index i => Variables(i.Target).Concat(Variables(i.Key)),
        Call c => c.Args.SelectMany(Variables),
        Unary u => Variables(u.Operand),
        Binary b => Variables(b.Left).Concat(Variables(b.Right)),
        Conditional c => Variables(c.Test).Concat(Variables(c.Then)).Concat(Variables(c.Else)),
        ListLit l => l.Items.SelectMany(Variables),
        _ => [],
    };

    /// <summary>The value, detached from the scope it may have been read out of.</summary>
    public static JsonNode? Evaluate(Node node, IReadOnlyDictionary<string, JsonNode?> scope, DateTimeOffset now) =>
        new Evaluator(scope, now).Eval(node, 0)?.DeepClone();

    /// <summary>Evaluates a string expression in one call.</summary>
    public static JsonNode? Evaluate(string source, IReadOnlyDictionary<string, JsonNode?> scope, DateTimeOffset now) =>
        Evaluate(Parse(source), scope, now);

    /// <summary>Whether a value counts as true in a condition: not null, false, 0, "" or an empty list.</summary>
    public static bool Truthy(JsonNode? value) => value switch
    {
        null => false,
        JsonArray a => a.Count > 0,
        JsonObject => true,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when TryNumber(v, out var d) => d != 0,
        JsonValue v when v.TryGetValue<string>(out var s) => s.Length > 0,
        _ => true,
    };

    /// <summary>
    /// A JSON number as a decimal, whatever backs it: parsed JSON, or a value built in code from an
    /// int, a long or a double — <c>TryGetValue&lt;decimal&gt;</c> alone reads only the first kind.
    /// </summary>
    public static bool TryNumber(JsonNode? node, out decimal number)
    {
        number = 0;
        if (node is not JsonValue v)
        {
            return false;
        }
        if (v.TryGetValue<decimal>(out number))
        {
            return true;
        }
        if (v.TryGetValue<long>(out var l)) { number = l; return true; }
        if (v.TryGetValue<int>(out var i)) { number = i; return true; }
        if (v.TryGetValue<double>(out var d) && double.IsFinite(d) && Math.Abs(d) < 7.9e28) { number = (decimal)d; return true; }
        return false;
    }

    // ------------------------------------------------------------------ syntax tree

    public abstract record Node(int Position);
    public sealed record Literal(JsonNode? Value, int Position) : Node(Position);
    public sealed record Var(string Name, int Position) : Node(Position);
    public sealed record Member(Node Target, string Name, int Position) : Node(Position);
    public sealed record Index(Node Target, Node Key, int Position) : Node(Position);
    public sealed record Call(string Name, IReadOnlyList<Node> Args, int Position) : Node(Position);
    public sealed record Unary(string Op, Node Operand, int Position) : Node(Position);
    public sealed record Binary(string Op, Node Left, Node Right, int Position) : Node(Position);
    public sealed record Conditional(Node Test, Node Then, Node Else, int Position) : Node(Position);
    public sealed record ListLit(IReadOnlyList<Node> Items, int Position) : Node(Position);

    // ------------------------------------------------------------------ parser

    private sealed class Parser(string source)
    {
        private int _pos;
        private int _nodes;
        private int _depth;

        public Node ParseAll()
        {
            var node = Expression();
            Space();
            if (_pos < source.Length)
            {
                throw new ExpressionException($"Unexpected '{source[_pos]}'", _pos);
            }
            return node;
        }

        private T Made<T>(T node) where T : Node
        {
            if (++_nodes > MaxNodes)
            {
                throw new ExpressionException($"An expression has at most {MaxNodes} parts.");
            }
            return node;
        }

        private Node Expression()
        {
            if (++_depth > MaxDepth)
            {
                throw new ExpressionException($"An expression nests at most {MaxDepth} deep.", _pos);
            }
            try
            {
                var test = Or();
                if (!Eat("?"))
                {
                    return test;
                }
                var then = Expression();
                Expect(":");
                return Made(new Conditional(test, then, Expression(), test.Position));
            }
            finally
            {
                _depth--;
            }
        }

        private Node Or() => Chain(And, "||");
        private Node And() => Chain(Equality, "&&");
        private Node Equality() => Chain(Relational, "==", "!=");
        private Node Relational() => Chain(Additive, "<=", ">=", "<", ">", "in");
        private Node Additive() => Chain(Multiplicative, "+", "-");
        private Node Multiplicative() => Chain(UnaryOp, "*", "/", "%");

        private Node Chain(Func<Node> next, params string[] ops)
        {
            var left = next();
            while (true)
            {
                Space();
                var op = ops.FirstOrDefault(o => At(o) && (o != "in" || !IsIdentChar(Peek(2))));
                if (op is null)
                {
                    return left;
                }
                var at = _pos;
                _pos += op.Length;
                left = Made(new Binary(op, left, next(), at));
            }
        }

        private Node UnaryOp()
        {
            Space();
            var at = _pos;
            if (Eat("!"))
            {
                return Made(new Unary("!", UnaryOp(), at));
            }
            if (Peek(0) == '-' && !char.IsDigit(Peek(1)))
            {
                _pos++;
                return Made(new Unary("-", UnaryOp(), at));
            }
            return Postfix();
        }

        private Node Postfix()
        {
            var node = Primary();
            while (true)
            {
                Space();
                var at = _pos;
                if (Eat("."))
                {
                    node = Made(new Member(node, Identifier(), at));
                }
                else if (Eat("["))
                {
                    var key = Expression();
                    Expect("]");
                    node = Made(new Index(node, key, at));
                }
                else
                {
                    return node;
                }
            }
        }

        private Node Primary()
        {
            Space();
            var at = _pos;
            if (_pos >= source.Length)
            {
                throw new ExpressionException("The expression ends too soon", _pos);
            }
            var c = source[_pos];
            if (Eat("("))
            {
                var inner = Expression();
                Expect(")");
                return inner;
            }
            if (Eat("["))
            {
                var items = new List<Node>();
                Space();
                if (!Eat("]"))
                {
                    do
                    {
                        items.Add(Expression());
                    }
                    while (Eat(","));
                    Expect("]");
                }
                return Made(new ListLit(items, at));
            }
            if (c is '"' or '\'')
            {
                return Made(new Literal(StringLiteral(c), at));
            }
            if (char.IsDigit(c) || (c == '-' && char.IsDigit(Peek(1))))
            {
                return Made(new Literal(NumberLiteral(), at));
            }
            if (IsIdentStart(c))
            {
                var name = Identifier();
                switch (name)
                {
                    case "true": return Made(new Literal(true, at));
                    case "false": return Made(new Literal(false, at));
                    case "null": return Made(new Literal(null, at));
                }
                Space();
                if (Eat("("))
                {
                    if (!Functions.Contains(name))
                    {
                        throw new ExpressionException($"Unknown function '{name}'", at);
                    }
                    var args = new List<Node>();
                    Space();
                    if (!Eat(")"))
                    {
                        do
                        {
                            args.Add(Expression());
                        }
                        while (Eat(","));
                        Expect(")");
                    }
                    return Made(new Call(name, args, at));
                }
                return Made(new Var(name, at));
            }
            throw new ExpressionException($"Unexpected '{c}'", _pos);
        }

        private string StringLiteral(char quote)
        {
            var start = _pos++;
            var text = new StringBuilder();
            while (_pos < source.Length && source[_pos] != quote)
            {
                var c = source[_pos++];
                if (c == '\\' && _pos < source.Length)
                {
                    var e = source[_pos++];
                    text.Append(e switch { 'n' => '\n', 't' => '\t', _ => e });
                }
                else
                {
                    text.Append(c);
                }
            }
            if (_pos >= source.Length)
            {
                throw new ExpressionException("A string is not closed", start);
            }
            _pos++;
            return text.ToString();
        }

        private decimal NumberLiteral()
        {
            var start = _pos;
            if (source[_pos] == '-')
            {
                _pos++;
            }
            while (_pos < source.Length && (char.IsDigit(source[_pos]) || source[_pos] == '.'))
            {
                _pos++;
            }
            return decimal.TryParse(source[start.._pos], NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out var number)
                ? number
                : throw new ExpressionException($"'{source[start.._pos]}' is not a number", start);
        }

        private string Identifier()
        {
            Space();
            var start = _pos;
            if (_pos >= source.Length || !IsIdentStart(source[_pos]))
            {
                throw new ExpressionException("A name is expected", _pos);
            }
            while (_pos < source.Length && IsIdentChar(source[_pos]))
            {
                _pos++;
            }
            return source[start.._pos];
        }

        private static bool IsIdentStart(char c) => char.IsAsciiLetter(c) || c == '_';
        private static bool IsIdentChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

        private char Peek(int offset) => _pos + offset < source.Length ? source[_pos + offset] : '\0';

        private bool At(string token) => string.CompareOrdinal(source, _pos, token, 0, token.Length) == 0;

        private bool Eat(string token)
        {
            Space();
            if (!At(token))
            {
                return false;
            }
            _pos += token.Length;
            return true;
        }

        private void Expect(string token)
        {
            if (!Eat(token))
            {
                throw new ExpressionException($"'{token}' expected", _pos);
            }
        }

        private void Space()
        {
            while (_pos < source.Length && char.IsWhiteSpace(source[_pos]))
            {
                _pos++;
            }
        }
    }

    // ------------------------------------------------------------------ evaluator

    private sealed class Evaluator(IReadOnlyDictionary<string, JsonNode?> scope, DateTimeOffset now)
    {
        private int _steps;

        public JsonNode? Eval(Node node, int depth)
        {
            if (++_steps > MaxSteps)
            {
                throw new ExpressionException($"The expression took more than {MaxSteps} steps.");
            }
            return node switch
            {
                // Reads hand back the node itself, never a copy: a record can be 256K, and an
                // expression may name it hundreds of times. A value is copied only where it is put
                // into a new container (a list here, a rendered input in Render).
                Literal l => l.Value,
                Var v => scope.TryGetValue(v.Name, out var value) ? value : throw new ExpressionException($"Unknown name '{v.Name}'", v.Position),
                Member m => Eval(m.Target, depth + 1) is JsonObject o ? o[m.Name] : null,
                Index i => IndexOf(Eval(i.Target, depth + 1), Eval(i.Key, depth + 1)),
                ListLit l => new JsonArray(l.Items.Select(x => Eval(x, depth + 1)?.DeepClone()).ToArray()),
                Unary { Op: "!" } u => !Truthy(Eval(u.Operand, depth + 1)),
                Unary u => Number(Eval(u.Operand, depth + 1), u) is { } n ? -n : null,
                Conditional c => Truthy(Eval(c.Test, depth + 1)) ? Eval(c.Then, depth + 1) : Eval(c.Else, depth + 1),
                Binary { Op: "&&" } b => Truthy(Eval(b.Left, depth + 1)) && Truthy(Eval(b.Right, depth + 1)),
                Binary { Op: "||" } b => Truthy(Eval(b.Left, depth + 1)) || Truthy(Eval(b.Right, depth + 1)),
                Binary b => BinaryOp(b, Eval(b.Left, depth + 1), Eval(b.Right, depth + 1)),
                Call c => CallOf(c, c.Args.Select(a => Eval(a, depth + 1)).ToList()),
                _ => throw new ExpressionException("Unsupported expression", node.Position),
            };
        }

        private static JsonNode? IndexOf(JsonNode? target, JsonNode? key) => (target, key) switch
        {
            (JsonArray a, JsonValue k) when TryNumber(k, out var i) && i >= 0 && i < a.Count && i == decimal.Truncate(i) => a[(int)i],
            (JsonObject o, JsonValue k) when k.TryGetValue<string>(out var name) => o[name],
            _ => null,
        };

        private JsonNode? BinaryOp(Binary b, JsonNode? left, JsonNode? right)
        {
            switch (b.Op)
            {
                case "==": return Same(left, right);
                case "!=": return !Same(left, right);
                case "in":
                    return right switch
                    {
                        JsonArray list => list.Any(item => Same(item, left)),
                        JsonObject obj => Text(left) is { } key && obj.ContainsKey(key),
                        _ => Text(right) is { } text && Text(left) is { } part && text.Contains(part, StringComparison.Ordinal),
                    };
                case "+" when left is JsonValue lv && lv.TryGetValue<string>(out _) || right is JsonValue rv && rv.TryGetValue<string>(out _):
                    return Bounded(Display(left) + Display(right));
                case "<" or "<=" or ">" or ">=":
                    var order = Compare(left, right);
                    return order is null ? false : b.Op switch
                    {
                        "<" => order < 0,
                        "<=" => order <= 0,
                        ">" => order > 0,
                        _ => order >= 0,
                    };
            }
            if (Number(left, b) is not { } x || Number(right, b) is not { } y)
            {
                return null; // arithmetic with a missing value is missing
            }
            return b.Op switch
            {
                "+" => x + y,
                "-" => x - y,
                "*" => x * y,
                "/" => y == 0 ? throw new ExpressionException("Division by zero", b.Position) : x / y,
                _ => y == 0 ? throw new ExpressionException("Division by zero", b.Position) : x % y,
            };
        }

        private JsonNode? CallOf(Call call, IReadOnlyList<JsonNode?> args)
        {
            JsonNode? Arg(int i) => i < args.Count ? args[i] : null;
            void Arity(int min, int max)
            {
                if (args.Count < min || args.Count > max)
                {
                    throw new ExpressionException($"{call.Name}() takes {(min == max ? $"{min}" : $"{min} to {max}")} argument(s)", call.Position);
                }
            }
            switch (call.Name)
            {
                case "len":
                    Arity(1, 1);
                    return Arg(0) switch
                    {
                        JsonArray a => (decimal)a.Count,
                        JsonObject o => (decimal)o.Count,
                        null => 0m,
                        var v => (decimal)Display(v).Length,
                    };
                case "lower": Arity(1, 1); return Text(Arg(0))?.ToLowerInvariant();
                case "upper": Arity(1, 1); return Text(Arg(0))?.ToUpperInvariant();
                case "trim": Arity(1, 1); return Text(Arg(0))?.Trim();
                case "contains": Arity(2, 2); return Text(Arg(0)) is { } s && Text(Arg(1)) is { } p && s.Contains(p, StringComparison.OrdinalIgnoreCase);
                case "startsWith": Arity(2, 2); return Text(Arg(0)) is { } s1 && Text(Arg(1)) is { } p1 && s1.StartsWith(p1, StringComparison.OrdinalIgnoreCase);
                case "endsWith": Arity(2, 2); return Text(Arg(0)) is { } s2 && Text(Arg(1)) is { } p2 && s2.EndsWith(p2, StringComparison.OrdinalIgnoreCase);
                case "coalesce":
                    Arity(1, 16);
                    return args.FirstOrDefault(a => a is not null)?.DeepClone();
                case "string": Arity(1, 1); return Arg(0) is null ? null : Bounded(Display(Arg(0)));
                case "number":
                    Arity(1, 1);
                    return Arg(0) switch
                    {
                        JsonValue v when TryNumber(v, out var d) => d,
                        JsonValue v when v.TryGetValue<string>(out var numeric) && decimal.TryParse(numeric, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) => parsed,
                        _ => null,
                    };
                case "round":
                    Arity(1, 2);
                    var digits = Arg(1) is null ? 0 : (int)Math.Clamp(Number(Arg(1), call) ?? 0, 0, 10);
                    return Number(Arg(0), call) is { } r ? Math.Round(r, digits, MidpointRounding.AwayFromZero) : null;
                case "now": Arity(0, 0); return now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
                case "today": Arity(0, 0); return now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                case "addDays":
                case "addHours":
                {
                    Arity(2, 2);
                    var amount = (double)(Number(Arg(1), call) ?? 0);
                    var text = Text(Arg(0));
                    if (text is { Length: 10 } && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    {
                        return call.Name == "addDays"
                            ? date.AddDays((int)amount).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                            : throw new ExpressionException("addHours() needs a date and time, not a date", call.Position);
                    }
                    if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant))
                    {
                        return null;
                    }
                    var moved = call.Name == "addDays" ? instant.AddDays(amount) : instant.AddHours(amount);
                    return moved.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
                }
                case "join":
                    Arity(1, 2);
                    return Arg(0) is JsonArray items ? Bounded(string.Join(Text(Arg(1)) ?? ", ", items.Select(Display))) : null;
                default:
                    throw new ExpressionException($"Unknown function '{call.Name}'", call.Position);
            }
        }

        private static decimal? Number(JsonNode? value, Node at) => value switch
        {
            null => null,
            JsonValue v when TryNumber(v, out var d) => d,
            _ => throw new ExpressionException($"{value.ToJsonString()} is not a number", at.Position),
        };

        private static string? Text(JsonNode? value) => value is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

        private static string Display(JsonNode? value) => value switch
        {
            null => "",
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            JsonValue v when TryNumber(v, out var d) => d.ToString(CultureInfo.InvariantCulture),
            _ => value.ToJsonString(),
        };

        private static JsonNode Bounded(string text) =>
            text.Length <= MaxStringChars ? text : throw new ExpressionException($"A string is longer than {MaxStringChars} characters.");

        private static bool Same(JsonNode? a, JsonNode? b) =>
            (a, b) switch
            {
                (null, null) => true,
                (JsonValue x, JsonValue y) when TryNumber(x, out var m) && TryNumber(y, out var n) => m == n,
                _ => JsonNode.DeepEquals(a, b),
            };

        private static int? Compare(JsonNode? a, JsonNode? b)
        {
            if (a is JsonValue x && b is JsonValue y)
            {
                if (TryNumber(x, out var m) && TryNumber(y, out var n))
                {
                    return m.CompareTo(n);
                }
                // Dates and times are stored in fixed-width ISO forms, so text order is time order.
                if (x.TryGetValue<string>(out var s) && y.TryGetValue<string>(out var t))
                {
                    return string.CompareOrdinal(s, t);
                }
            }
            return null;
        }
    }

    // ------------------------------------------------------------------ templates

    /// <summary>
    /// Fills a step's input: a string that is exactly <c>{{ expr }}</c> becomes the expression's
    /// value (any type); any other string has each <c>{{ expr }}</c> replaced by its text.
    /// Objects and arrays are filled recursively.
    /// </summary>
    /// <summary>The most one rendered input may come to, in serialized characters.</summary>
    public const int MaxRenderedChars = 256_000;

    public static JsonNode? Render(JsonNode? template, IReadOnlyDictionary<string, JsonNode?> scope, DateTimeOffset now)
    {
        var budget = new[] { MaxRenderedChars };
        return Render(template, scope, now, budget);
    }

    private static JsonNode? Render(JsonNode? template, IReadOnlyDictionary<string, JsonNode?> scope, DateTimeOffset now, int[] budget)
    {
        var rendered = template switch
        {
            JsonObject o => new JsonObject(o.Select(p => KeyValuePair.Create(p.Key, Render(p.Value, scope, now, budget)))),
            JsonArray a => new JsonArray(a.Select(i => Render(i, scope, now, budget)).ToArray()),
            JsonValue v when v.TryGetValue<string>(out var s) && s.Contains("{{", StringComparison.Ordinal) => Spend(RenderString(s, scope, now), budget),
            _ => template?.DeepClone(),
        };
        return rendered;
    }

    /// <summary>Counts what a hole produced against the input's budget, so many holes cannot add up to an unbounded input.</summary>
    private static JsonNode? Spend(JsonNode? value, int[] budget)
    {
        budget[0] -= value switch
        {
            null => 0,
            JsonValue v when v.TryGetValue<string>(out var s) => s.Length,
            _ => value.ToJsonString().Length,
        };
        return budget[0] >= 0
            ? value
            : throw new ExpressionException($"A step's input would be larger than {MaxRenderedChars:N0} characters.");
    }

    /// <summary>Every expression inside a template, for validation.</summary>
    public static IEnumerable<string> TemplateExpressions(JsonNode? template) => template switch
    {
        JsonObject o => o.SelectMany(p => TemplateExpressions(p.Value)),
        JsonArray a => a.SelectMany(TemplateExpressions),
        JsonValue v when v.TryGetValue<string>(out var s) => Holes(s).Select(h => h.Expression),
        _ => [],
    };

    private static JsonNode? RenderString(string text, IReadOnlyDictionary<string, JsonNode?> scope, DateTimeOffset now)
    {
        var holes = Holes(text).ToList();
        if (holes.Count == 1 && holes[0].Start == 0 && holes[0].End == text.Length)
        {
            return Evaluate(holes[0].Expression, scope, now);
        }
        var result = new StringBuilder();
        var at = 0;
        foreach (var hole in holes)
        {
            result.Append(text, at, hole.Start - at);
            var value = Evaluate(hole.Expression, scope, now);
            result.Append(value switch
            {
                null => "",
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                _ => value.ToJsonString(),
            });
            at = hole.End;
            if (result.Length > MaxStringChars)
            {
                throw new ExpressionException($"A string is longer than {MaxStringChars} characters.");
            }
        }
        result.Append(text, at, text.Length - at);
        return result.ToString();
    }

    private static IEnumerable<(int Start, int End, string Expression)> Holes(string text)
    {
        var at = 0;
        while ((at = text.IndexOf("{{", at, StringComparison.Ordinal)) >= 0)
        {
            var end = text.IndexOf("}}", at + 2, StringComparison.Ordinal);
            if (end < 0)
            {
                throw new ExpressionException("A '{{' is not closed with '}}'", at);
            }
            yield return (at, end + 2, text[(at + 2)..end].Trim());
            at = end + 2;
        }
    }
}
