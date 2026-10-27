#if !NETWASM
using System.Globalization;
using System.Text;

namespace Reptar.AI;

/// <summary>Raised for any syntax or runtime error inside a script.</summary>
public sealed class ScriptException(string message, int line = 0) : Exception(message)
{
    public int Line { get; } = line;

    public override string ToString() =>
        Line > 0 ? $"line {Line}: {Message}" : Message;
}

public enum ScriptTokenKind
{
    End,
    Number,
    Text,
    Identifier,
    Keyword,
    Operator,
    Punctuation
}

public readonly record struct ScriptToken(
    ScriptTokenKind Kind,
    string Text,
    double Number,
    int Line)
{
    public bool Is(string text) => string.Equals(Text, text, StringComparison.Ordinal);
}

/// <summary>
/// Tokenizer for the codemode script language. C#-flavoured on purpose: the model already knows
/// it, and it keeps the interpreter honest about the house language.
/// </summary>
public static class ScriptLexer
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "let", "const", "if", "else", "while", "for", "foreach", "in",
        "return", "break", "continue", "true", "false", "null", "function"
    };

    // Longest first: the tokenizer takes the first match, so "=>" must precede "=" and ">".
    private static readonly string[] Operators =
    [
        "=>", "==", "!=", "<=", ">=", "&&", "||", "++", "--", "+=", "-=",
        "+", "-", "*", "/", "%", "<", ">", "!", "="
    ];

    public static List<ScriptToken> Tokenize(string source)
    {
        var tokens = new List<ScriptToken>();
        var index = 0;
        var line = 1;

        while (index < source.Length)
        {
            var ch = source[index];

            if (ch == '\n')
            {
                line++;
                index++;
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                index++;
                continue;
            }

            // Comments: // to end of line, and /* */ blocks.
            if (ch == '/' && index + 1 < source.Length && source[index + 1] == '/')
            {
                while (index < source.Length && source[index] != '\n')
                    index++;
                continue;
            }

            if (ch == '/' && index + 1 < source.Length && source[index + 1] == '*')
            {
                index += 2;
                while (index + 1 < source.Length && !(source[index] == '*' && source[index + 1] == '/'))
                {
                    if (source[index] == '\n')
                        line++;
                    index++;
                }
                index = Math.Min(source.Length, index + 2);
                continue;
            }

            // Numbers
            if (char.IsDigit(ch) || (ch == '.' && index + 1 < source.Length && char.IsDigit(source[index + 1])))
            {
                var start = index;
                while (index < source.Length && (char.IsDigit(source[index]) || source[index] == '.'))
                    index++;

                var text = source[start..index];
                tokens.Add(new ScriptToken(
                    ScriptTokenKind.Number,
                    text,
                    double.Parse(text, CultureInfo.InvariantCulture),
                    line));
                continue;
            }

            // Strings: "..." and '...' with \" \' \\ \n \t escapes
            if (ch is '"' or '\'')
            {
                var quote = ch;
                var startLine = line;
                index++;
                var builder = new StringBuilder();

                while (index < source.Length && source[index] != quote)
                {
                    if (source[index] == '\\' && index + 1 < source.Length)
                    {
                        index++;
                        builder.Append(source[index] switch
                        {
                            'n' => '\n',
                            't' => '\t',
                            'r' => '\r',
                            '0' => '\0',
                            '\\' => '\\',
                            '"' => '"',
                            '\'' => '\'',
                            var other => other
                        });
                        index++;
                        continue;
                    }

                    if (source[index] == '\n')
                        line++;

                    builder.Append(source[index]);
                    index++;
                }

                if (index >= source.Length)
                    throw new ScriptException("unterminated string literal", startLine);

                index++;
                tokens.Add(new ScriptToken(ScriptTokenKind.Text, builder.ToString(), 0, startLine));
                continue;
            }

            // Identifiers and keywords
            if (char.IsLetter(ch) || ch == '_')
            {
                var start = index;
                while (index < source.Length && (char.IsLetterOrDigit(source[index]) || source[index] == '_'))
                    index++;

                var text = source[start..index];
                tokens.Add(new ScriptToken(
                    Keywords.Contains(text) ? ScriptTokenKind.Keyword : ScriptTokenKind.Identifier,
                    text,
                    0,
                    line));
                continue;
            }

            // Operators, longest match first
            var matched = false;
            foreach (var op in Operators)
            {
                if (index + op.Length > source.Length)
                    continue;
                if (!source.AsSpan(index, op.Length).SequenceEqual(op))
                    continue;

                tokens.Add(new ScriptToken(ScriptTokenKind.Operator, op, 0, line));
                index += op.Length;
                matched = true;
                break;
            }

            if (matched)
                continue;

            if ("(){}[];,.:".Contains(ch))
            {
                tokens.Add(new ScriptToken(ScriptTokenKind.Punctuation, ch.ToString(), 0, line));
                index++;
                continue;
            }

            throw new ScriptException($"unexpected character '{ch}'", line);
        }

        tokens.Add(new ScriptToken(ScriptTokenKind.End, "", 0, line));
        return tokens;
    }
}

// ---------------------------------------------------------------------------
// AST
// ---------------------------------------------------------------------------

public abstract record ScriptNode(int Line);

public sealed record ScriptLiteralNode(ScriptValue Value, int Line) : ScriptNode(Line);

public sealed record ScriptIdentifierNode(string Name, int Line) : ScriptNode(Line);

public sealed record ScriptArrayNode(IReadOnlyList<ScriptNode> Items, int Line) : ScriptNode(Line);

public sealed record ScriptObjectNode(
    IReadOnlyList<(string Key, ScriptNode Value)> Members, int Line) : ScriptNode(Line);

public sealed record ScriptUnaryNode(string Operator, ScriptNode Operand, int Line) : ScriptNode(Line);

public sealed record ScriptBinaryNode(
    string Operator, ScriptNode Left, ScriptNode Right, int Line) : ScriptNode(Line);

public sealed record ScriptMemberNode(ScriptNode Target, string Name, int Line) : ScriptNode(Line);

public sealed record ScriptIndexNode(ScriptNode Target, ScriptNode Index, int Line) : ScriptNode(Line);

public sealed record ScriptCallNode(
    ScriptNode Callee, IReadOnlyList<ScriptNode> Arguments, int Line) : ScriptNode(Line);

public sealed record ScriptSpawnNode(ScriptCallNode Call, int Line) : ScriptNode(Line);

public sealed record ScriptVarDeclNode(
    string Name, ScriptNode? Initializer, int Line) : ScriptNode(Line);

public sealed record ScriptAssignNode(
    ScriptNode Target, string Operator, ScriptNode Value, int Line) : ScriptNode(Line);

public sealed record ScriptIfNode(
    ScriptNode Condition, IReadOnlyList<ScriptNode> Then, IReadOnlyList<ScriptNode>? Else, int Line)
    : ScriptNode(Line);

public sealed record ScriptWhileNode(
    ScriptNode Condition, IReadOnlyList<ScriptNode> Body, int Line) : ScriptNode(Line);

public sealed record ScriptForeachNode(
    string Variable, ScriptNode Iterable, IReadOnlyList<ScriptNode> Body, int Line) : ScriptNode(Line);

public sealed record ScriptForNode(
    ScriptNode? Init, ScriptNode? Condition, ScriptNode? Step, IReadOnlyList<ScriptNode> Body, int Line)
    : ScriptNode(Line);

public sealed record ScriptReturnNode(ScriptNode? Value, int Line) : ScriptNode(Line);

public sealed record ScriptBreakNode(int Line) : ScriptNode(Line);

/// <summary>A bare <c>{ ... }</c> block, which introduces its own scope.</summary>
public sealed record ScriptBlockNode(IReadOnlyList<ScriptNode> Body, int Line) : ScriptNode(Line);

/// <summary>
/// A lambda: <c>(x) =&gt; expr</c>, <c>(x) =&gt; { ... }</c>, <c>x =&gt; expr</c>, or <c>() =&gt; expr</c>.
/// </summary>
public sealed record ScriptLambdaNode(
    IReadOnlyList<string> Parameters,
    IReadOnlyList<ScriptNode> Body,
    bool IsExpressionBody,
    int Line) : ScriptNode(Line);

public sealed record ScriptContinueNode(int Line) : ScriptNode(Line);

/// <summary>
/// Recursive-descent parser for the codemode script language. Produces an AST the interpreter
/// walks; there is no code generation, so nothing is ever compiled or JIT'd.
/// </summary>
public sealed class ScriptParser
{
    private readonly List<ScriptToken> _tokens;
    private int _position;

    private ScriptParser(List<ScriptToken> tokens) => _tokens = tokens;

    /// <summary>Parses a script body into a statement list.</summary>
    /// <exception cref="ScriptException">Syntax error, with the line number.</exception>
    public static List<ScriptNode> Parse(string source) =>
        new ScriptParser(ScriptLexer.Tokenize(source)).ParseProgram();

    private ScriptToken Current => _tokens[Math.Min(_position, _tokens.Count - 1)];

    private ScriptToken Peek(int offset = 1) =>
        _tokens[Math.Min(_position + offset, _tokens.Count - 1)];

    private ScriptToken Advance() => _tokens[Math.Min(_position++, _tokens.Count - 1)];

    private bool Match(string text)
    {
        if (!Current.Is(text))
            return false;
        _position++;
        return true;
    }

    private ScriptToken Expect(string text)
    {
        if (!Current.Is(text))
            throw new ScriptException($"expected '{text}' but found '{Current.Text}'", Current.Line);
        return Advance();
    }

    private List<ScriptNode> ParseProgram()
    {
        var statements = new List<ScriptNode>();
        while (Current.Kind != ScriptTokenKind.End)
            statements.Add(ParseStatement());
        return statements;
    }

    private List<ScriptNode> ParseBlock()
    {
        Expect("{");
        var statements = new List<ScriptNode>();
        while (!Current.Is("}") && Current.Kind != ScriptTokenKind.End)
            statements.Add(ParseStatement());
        Expect("}");
        return statements;
    }

    private ScriptNode ParseStatement()
    {
        var token = Current;

        switch (token.Text)
        {
            case "let" or "const":
            {
                Advance();
                if (Current.Kind != ScriptTokenKind.Identifier)
                    throw new ScriptException("expected a variable name", token.Line);
                var name = Advance().Text;

                ScriptNode? initializer = null;
                if (Match("="))
                    initializer = ParseExpression();
                Match(";");
                return new ScriptVarDeclNode(name, initializer, token.Line);
            }

            case "if":
            {
                Advance();
                Expect("(");
                var condition = ParseExpression();
                Expect(")");
                var then = ParseBlock();
                List<ScriptNode>? otherwise = null;
                if (Match("else"))
                    otherwise = Current.Is("if") ? [ParseStatement()] : ParseBlock();
                return new ScriptIfNode(condition, then, otherwise, token.Line);
            }

            case "while":
            {
                Advance();
                Expect("(");
                var condition = ParseExpression();
                Expect(")");
                return new ScriptWhileNode(condition, ParseBlock(), token.Line);
            }

            case "foreach":
            {
                Advance();
                Expect("(");

                // Accept both `foreach (x in xs)` and `foreach (let x in xs)`.
                if (Current.Is("let") || Current.Is("const"))
                    Advance();

                if (Current.Kind != ScriptTokenKind.Identifier)
                    throw new ScriptException("expected a loop variable", token.Line);

                var variable = Advance().Text;
                Expect("in");
                var iterable = ParseExpression();
                Expect(")");
                return new ScriptForeachNode(variable, iterable, ParseBlock(), token.Line);
            }

            case "for":
            {
                Advance();
                Expect("(");
                ScriptNode? init = null;
                if (!Current.Is(";"))
                {
                    if (Current.Is("let") || Current.Is("const"))
                        init = ParseStatement();
                    else
                        init = new ScriptAssignNode(ParsePostfix(), "=", ParseExpression(), token.Line);
                }
                Match(";");

                ScriptNode? condition = null;
                if (!Current.Is(";"))
                    condition = ParseExpression();
                Match(";");

                ScriptNode? step = null;
                if (!Current.Is(")"))
                    step = new ScriptAssignNode(ParsePostfix(), "=", ParseExpression(), token.Line);
                Expect(")");

                return new ScriptForNode(init, condition, step, ParseBlock(), token.Line);
            }

            case "return":
            {
                Advance();
                ScriptNode? value = null;
                if (!Current.Is(";") && !Current.Is("}") && Current.Kind != ScriptTokenKind.End)
                    value = ParseExpression();
                Match(";");
                return new ScriptReturnNode(value, token.Line);
            }

            case "break":
                Advance();
                Match(";");
                return new ScriptBreakNode(token.Line);

            case "continue":
                Advance();
                Match(";");
                return new ScriptContinueNode(token.Line);

            case "{":
                return new ScriptBlockNode(ParseBlock(), token.Line);
        }

        var expression = ParseExpression();

        // Assignment
        if (Current.Kind == ScriptTokenKind.Operator &&
            Current.Text is "=" or "+=" or "-=")
        {
            var op = Advance().Text;
            var value = ParseExpression();
            Match(";");
            return new ScriptAssignNode(expression, op, value, token.Line);
        }

        Match(";");
        return expression;
    }

    private ScriptNode ParseExpression() => ParseLogicalOr();

    private ScriptNode ParseLogicalOr()
    {
        var left = ParseLogicalAnd();
        while (Current.Is("||"))
        {
            var line = Advance().Line;
            left = new ScriptBinaryNode("||", left, ParseLogicalAnd(), line);
        }
        return left;
    }

    private ScriptNode ParseLogicalAnd()
    {
        var left = ParseEquality();
        while (Current.Is("&&"))
        {
            var line = Advance().Line;
            left = new ScriptBinaryNode("&&", left, ParseEquality(), line);
        }
        return left;
    }

    private ScriptNode ParseEquality()
    {
        var left = ParseComparison();
        while (Current.Is("==") || Current.Is("!="))
        {
            var op = Advance();
            left = new ScriptBinaryNode(op.Text, left, ParseComparison(), op.Line);
        }
        return left;
    }

    private ScriptNode ParseComparison()
    {
        var left = ParseAdditive();
        while (Current.Is("<") || Current.Is("<=") || Current.Is(">") || Current.Is(">="))
        {
            var op = Advance();
            left = new ScriptBinaryNode(op.Text, left, ParseAdditive(), op.Line);
        }
        return left;
    }

    private ScriptNode ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (Current.Is("+") || Current.Is("-"))
        {
            var op = Advance();
            left = new ScriptBinaryNode(op.Text, left, ParseMultiplicative(), op.Line);
        }
        return left;
    }

    private ScriptNode ParseMultiplicative()
    {
        var left = ParseUnary();
        while (Current.Is("*") || Current.Is("/") || Current.Is("%"))
        {
            var op = Advance();
            left = new ScriptBinaryNode(op.Text, left, ParseUnary(), op.Line);
        }
        return left;
    }

    private ScriptNode ParseUnary()
    {
        if (Current.Is("!") || Current.Is("-") || Current.Is("+"))
        {
            var op = Advance();
            return new ScriptUnaryNode(op.Text, ParseUnary(), op.Line);
        }

        // spawn(call) captures an unevaluated call so several can run at once.
        if (Current.Is("spawn") && Peek().Is("("))
        {
            var line = Advance().Line;
            Expect("(");
            var call = ParsePostfix();
            Expect(")");

            if (call is not ScriptCallNode callNode)
                throw new ScriptException("spawn() requires a tool call, for example spawn(tools.read({ path: \"a\" }))", line);

            return new ScriptSpawnNode(callNode, line);
        }

        return ParsePostfix();
    }

    private ScriptNode ParsePostfix()
    {
        var node = ParsePrimary();

        while (true)
        {
            if (Current.Is("."))
            {
                var line = Advance().Line;
                if (Current.Kind is not (ScriptTokenKind.Identifier or ScriptTokenKind.Keyword))
                    throw new ScriptException("expected a member name after '.'", line);
                node = new ScriptMemberNode(node, Advance().Text, line);
                continue;
            }

            if (Current.Is("["))
            {
                var line = Advance().Line;
                var index = ParseExpression();
                Expect("]");
                node = new ScriptIndexNode(node, index, line);
                continue;
            }

            if (Current.Is("("))
            {
                var line = Advance().Line;
                var arguments = new List<ScriptNode>();
                if (!Current.Is(")"))
                {
                    do
                    {
                        arguments.Add(ParseExpression());
                    }
                    while (Match(","));
                }
                Expect(")");
                node = new ScriptCallNode(node, arguments, line);
                continue;
            }

            return node;
        }
    }

    /// <summary>
    /// Tries to read <c>(params) =&gt; body</c> starting at the current position. Returns null and
    /// restores the position when the parentheses turn out to be a grouping expression.
    /// </summary>
    private ScriptNode? TryParseLambda()
    {
        var start = _position;
        var line = Current.Line;

        Advance(); // (

        var parameters = new List<string>();
        if (!Current.Is(")"))
        {
            while (true)
            {
                if (Current.Is("let") || Current.Is("const"))
                    Advance();

                if (Current.Kind != ScriptTokenKind.Identifier)
                {
                    _position = start;
                    return null;
                }

                parameters.Add(Advance().Text);

                if (!Match(","))
                    break;
            }
        }

        if (!Current.Is(")"))
        {
            _position = start;
            return null;
        }

        Advance(); // )

        if (!Current.Is("=>"))
        {
            _position = start;
            return null;
        }

        Advance(); // =>
        return FinishLambda(parameters, line);
    }

    private ScriptNode FinishLambda(IReadOnlyList<string> parameters, int line)
    {
        if (Current.Is("{"))
        {
            var body = ParseBlock();
            return new ScriptLambdaNode(parameters, body, IsExpressionBody: false, line);
        }

        // Expression body: wrap it in an implicit return.
        var expression = ParseExpression();
        return new ScriptLambdaNode(parameters, [new ScriptReturnNode(expression, line)], true, line);
    }

    private ScriptNode ParsePrimary()
    {
        var token = Current;

        // Single-parameter lambda shorthand (x => ...). Must come before the switch, because the
        // Identifier case consumes the token and returns.
        if (token.Kind == ScriptTokenKind.Identifier && Peek().Is("=>"))
        {
            Advance();
            Expect("=>");
            return FinishLambda([token.Text], token.Line);
        }

        switch (token.Kind)
        {
            case ScriptTokenKind.Number:
                Advance();
                return new ScriptLiteralNode(ScriptValue.Of(token.Number), token.Line);

            case ScriptTokenKind.Text:
                Advance();
                return new ScriptLiteralNode(ScriptValue.Of(token.Text), token.Line);

            case ScriptTokenKind.Identifier:
                Advance();
                return new ScriptIdentifierNode(token.Text, token.Line);

            case ScriptTokenKind.Keyword:
                switch (token.Text)
                {
                    case "true":
                        Advance();
                        return new ScriptLiteralNode(ScriptValue.True, token.Line);
                    case "false":
                        Advance();
                        return new ScriptLiteralNode(ScriptValue.False, token.Line);
                    case "null":
                        Advance();
                        return new ScriptLiteralNode(ScriptValue.Null, token.Line);
                }
                throw new ScriptException($"unexpected keyword '{token.Text}'", token.Line);
        }

        if (token.Is("(") && TryParseLambda() is { } lambda)
            return lambda;

        if (token.Is("("))
        {
            Advance();
            var inner = ParseExpression();
            Expect(")");
            return inner;
        }

        if (token.Is("["))
        {
            Advance();
            var items = new List<ScriptNode>();
            if (!Current.Is("]"))
            {
                do
                {
                    items.Add(ParseExpression());
                }
                while (Match(","));
            }
            Expect("]");
            return new ScriptArrayNode(items, token.Line);
        }

        if (token.Is("{"))
        {
            Advance();
            var members = new List<(string Key, ScriptNode Value)>();
            if (!Current.Is("}"))
            {
                do
                {
                    if (Current.Kind is not (ScriptTokenKind.Identifier or ScriptTokenKind.Text or ScriptTokenKind.Keyword))
                        throw new ScriptException("expected an object key", Current.Line);

                    var key = Advance().Text;
                    Expect(":");
                    members.Add((key, ParseExpression()));
                }
                while (Match(","));
            }
            Expect("}");
            return new ScriptObjectNode(members, token.Line);
        }

        throw new ScriptException($"unexpected token '{token.Text}'", token.Line);
    }
}
#endif
