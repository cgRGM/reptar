#if !NETWASM
using System.Text;

namespace Reptar.AI;

/// <summary>How a script run ended.</summary>
public enum ScriptStatus
{
    Completed,
    Failed,
    Timeout,
    StepLimit
}

/// <summary>Outcome of one <see cref="ScriptInterpreter.RunAsync"/> call.</summary>
public sealed record ScriptResult(
    ScriptStatus Status,
    ScriptValue Value,
    IReadOnlyList<string> Output,
    IReadOnlyList<string> Console,
    int ToolCalls,
    int Steps,
    double ElapsedMs,
    string? Error,
    IReadOnlyList<ScriptImage>? Images = null)
{
    public bool Ok => Status == ScriptStatus.Completed;

    /// <summary>Renders the result the way the model reads it.</summary>
    public string ToModelText()
    {
        var builder = new StringBuilder();
        builder.Append(Ok ? "Script completed" : "Script failed");
        builder.Append(" in ").Append(ElapsedMs.ToString("0")).Append("ms");

        if (ToolCalls > 0)
            builder.Append(" · ").Append(ToolCalls).Append(ToolCalls == 1 ? " tool call" : " tool calls");

        builder.AppendLine();

        var items = new List<string>(Output);
        if (!Value.IsNull && Status == ScriptStatus.Completed)
            items.Add(Value.ToDisplay());

        if (items.Count > 0)
        {
            builder.AppendLine();
            if (items.Count == 1)
            {
                builder.AppendLine(items[0]);
            }
            else
            {
                for (var i = 0; i < items.Count; i++)
                {
                    builder.AppendLine($"==> text {i + 1}/{items.Count} <==");
                    builder.AppendLine(items[i]);
                }
            }
        }

        if (Images is { Count: > 0 } images)
        {
            builder.AppendLine();
            for (var i = 0; i < images.Count; i++)
                builder.AppendLine($"[image {i + 1}/{images.Count}: {images[i].MimeType}]");
        }

        if (Console.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("<console_output>");
            foreach (var line in Console)
                builder.AppendLine(line);
            builder.AppendLine("</console_output>");
        }

        if (!Ok)
        {
            builder.AppendLine();
            builder.Append("Script error: ").AppendLine(Error ?? Status.ToString());
        }

        return builder.ToString().TrimEnd();
    }
}

/// <summary>
/// A sandboxed interpreter for model-written scripts that call tools.
/// </summary>
/// <remarks>
/// This is Reptar's answer to pi's codemode, which runs model-written JavaScript in a QuickJS VM
/// compiled to WebAssembly. Reptar targets Native AOT and NetWasm, so shipping a JS engine is not
/// an option; a small interpreter is. The capability model is identical and is the point:
/// <list type="bullet">
/// <item>A script can only reach the outside world through the injected tool delegate. No file
/// system, no network, no timers, no reflection, no dynamic code.</item>
/// <item>Only <c>text()</c>, <c>log()</c>, and the return value reach the model. A script can read
/// a 2 MB log, filter it, and hand back three lines.</item>
/// <item><c>spawn()</c> + <c>join()</c> run tool calls concurrently inside one model turn.</item>
/// <item>A step budget and a cancellation token bound the run, so a bad loop cannot hang the agent.</item>
/// </list>
/// </remarks>
public sealed class ScriptInterpreter
{
    private readonly Func<string, string, CancellationToken, Task<AiToolResult>> _toolExecutor;
    private readonly IReadOnlyList<AiToolDefinition> _tools;
    private readonly int _maxSteps;
    private readonly int _maxOutputItems;

    private readonly List<string> _output = [];
    private readonly List<string> _console = [];
    private readonly List<ScriptImage> _images = [];
    private readonly Dictionary<string, ScriptValue> _store;
    private readonly Dictionary<string, ScriptValue> _globals = new(StringComparer.Ordinal);
    private readonly List<Dictionary<string, ScriptValue>> _scopes = [];
    private readonly Dictionary<string, ScriptValue> _toolTable = new(StringComparer.Ordinal);

    private int _steps;
    private int _toolCalls;

    public ScriptInterpreter(
        Func<string, string, CancellationToken, Task<AiToolResult>> toolExecutor,
        IReadOnlyList<AiToolDefinition> tools,
        int maxSteps = 5_000,
        int maxOutputItems = 100,
        Dictionary<string, ScriptValue>? store = null)
    {
        _store = store ?? [];
        _toolExecutor = toolExecutor;
        _tools = tools;
        _maxSteps = maxSteps;
        _maxOutputItems = maxOutputItems;

        foreach (var tool in tools)
            _toolTable[Identifier(tool.Name)] = ScriptValue.Of(tool.Name);

        InstallBuiltins();
    }

    /// <summary>
    /// Runs a script body. Never throws for script errors: a syntax error, a failed tool call, a
    /// timeout, and a step-limit trip all come back as a <see cref="ScriptResult"/>.
    /// </summary>
    public async Task<ScriptResult> RunAsync(string source, CancellationToken cancellationToken = default)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        _steps = 0;
        _toolCalls = 0;
        _output.Clear();
        _console.Clear();
        _images.Clear();
        _scopes.Clear();

        _scopes.Add([]);

        List<ScriptNode> program;
        try
        {
            program = ScriptParser.Parse(source);
        }
        catch (ScriptException ex)
        {
            watch.Stop();
            return new ScriptResult(
                ScriptStatus.Failed, ScriptValue.Null, [], [], 0, 0,
                watch.Elapsed.TotalMilliseconds, ex.ToString());
        }

        try
        {
            var value = ScriptValue.Null;

            foreach (var statement in program)
            {
                var outcome = await ExecuteAsync(statement, cancellationToken).ConfigureAwait(false);

                if (outcome.Flow == ScriptFlow.Return)
                {
                    value = outcome.Value;
                    break;
                }
            }

            watch.Stop();
            return new ScriptResult(
                ScriptStatus.Completed, value, [.. _output], [.. _console], _toolCalls, _steps,
                watch.Elapsed.TotalMilliseconds, null, [.. _images]);
        }
        catch (OperationCanceledException)
        {
            watch.Stop();
            return new ScriptResult(
                ScriptStatus.Timeout, ScriptValue.Null, [.. _output], [.. _console], _toolCalls, _steps,
                watch.Elapsed.TotalMilliseconds, "script exceeded its time limit");
        }
        catch (ScriptStepLimitException ex)
        {
            watch.Stop();
            return new ScriptResult(
                ScriptStatus.StepLimit, ScriptValue.Null, [.. _output], [.. _console], _toolCalls, _steps,
                watch.Elapsed.TotalMilliseconds, ex.Message);
        }
        catch (ScriptException ex)
        {
            watch.Stop();
            return new ScriptResult(
                ScriptStatus.Failed, ScriptValue.Null, [.. _output], [.. _console], _toolCalls, _steps,
                watch.Elapsed.TotalMilliseconds, ex.ToString());
        }
        catch (Exception ex)
        {
            watch.Stop();
            return new ScriptResult(
                ScriptStatus.Failed, ScriptValue.Null, [.. _output], [.. _console], _toolCalls, _steps,
                watch.Elapsed.TotalMilliseconds, ex.Message);
        }
    }

    /// <summary>
    /// Invokes a script callable with the given arguments. Used by array methods
    /// (<c>filter</c>/<c>map</c>/<c>find</c>) so a lambda-shaped value can be applied.
    /// </summary>
    public static Task<ScriptValue> InvokeAsync(
        ScriptValue callable,
        IReadOnlyList<ScriptValue> arguments,
        CancellationToken cancellationToken)
    {
        return callable switch
        {
            ScriptNativeFunction native => native.Invoke(arguments, cancellationToken),
            ScriptClosure closure => InvokeClosureAsync(closure, arguments, cancellationToken),
            _ => Task.FromResult(ScriptValue.Null)
        };
    }

    /// <summary>
    /// Adds a host function to the global scope.
    /// </summary>
    /// <remarks>
    /// Extensions need this: the registration functions (<c>register_tool</c>, <c>on</c>) exist
    /// only while loading, and the built-in set is fixed at construction.
    /// </remarks>
    public void AddGlobal(string name, ScriptValue value) => _globals[name] = value;

    /// <summary>
    /// Runs a registered handler on this interpreter, keeping its globals and tool executor.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="InvokeAsync"/>: that one builds a throwaway interpreter for array
    /// callbacks, which is fine for <c>map</c>/<c>filter</c> but wrong for an extension handler —
    /// the handler needs the host functions and tool access it was defined against, and a stub
    /// executor would make every tool call inside it return null.
    /// </remarks>
    public async Task<ScriptValue> InvokeHandlerAsync(
        ScriptValue callable,
        IReadOnlyList<ScriptValue> arguments,
        CancellationToken cancellationToken = default)
    {
        if (callable is ScriptNativeFunction native)
            return await native.Invoke(arguments, cancellationToken).ConfigureAwait(false);

        if (callable is not ScriptClosure closure)
            return ScriptValue.Null;

        // Each invocation gets a fresh step budget; otherwise a long-lived extension would exhaust
        // the limit over many turns and start failing mid-session.
        _steps = 0;

        var saved = _scopes.ToList();
        _scopes.Clear();

        try
        {
            foreach (var scope in closure.Captured)
                _scopes.Add(new Dictionary<string, ScriptValue>(scope, StringComparer.Ordinal));

            if (_scopes.Count == 0)
                _scopes.Add([]);

            var frame = new Dictionary<string, ScriptValue>(StringComparer.Ordinal);
            for (var i = 0; i < closure.Parameters.Count; i++)
            {
                frame[closure.Parameters[i]] = i < arguments.Count ? arguments[i] : ScriptValue.Null;
            }

            _scopes.Add(frame);

            foreach (var statement in closure.Body)
            {
                var outcome = await ExecuteAsync(statement, cancellationToken).ConfigureAwait(false);
                if (outcome.Flow == ScriptFlow.Return)
                    return outcome.Value;
            }

            return ScriptValue.Null;
        }
        finally
        {
            _scopes.Clear();
            _scopes.AddRange(saved);
        }
    }

    /// <summary>
    /// Runs a user lambda. Static so array methods (<c>filter</c>/<c>map</c>/<c>find</c>) can
    /// apply it without owning an interpreter instance.
    /// </summary>
    private static async Task<ScriptValue> InvokeClosureAsync(
        ScriptClosure closure,
        IReadOnlyList<ScriptValue> arguments,
        CancellationToken cancellationToken)
    {
        // Rebuild the captured chain, then bind the parameters in a fresh innermost scope.
        var interpreter = new ScriptInterpreter(
            (_, _, _) => Task.FromResult(AiToolResult.Ok(200, "null")),
            []);

        interpreter._scopes.Clear();
        foreach (var scope in closure.Captured)
            interpreter._scopes.Add(new Dictionary<string, ScriptValue>(scope, StringComparer.Ordinal));

        if (interpreter._scopes.Count == 0)
            interpreter._scopes.Add([]);

        var frame = new Dictionary<string, ScriptValue>(StringComparer.Ordinal);
        for (var i = 0; i < closure.Parameters.Count; i++)
            frame[closure.Parameters[i]] = i < arguments.Count ? arguments[i] : ScriptValue.Null;

        interpreter._scopes.Add(frame);

        foreach (var statement in closure.Body)
        {
            var outcome = await interpreter.ExecuteAsync(statement, cancellationToken).ConfigureAwait(false);
            if (outcome.Flow == ScriptFlow.Return)
                return outcome.Value;
        }

        return ScriptValue.Null;
    }

    // ------------------------------------------------------------------
    // Built-ins
    // ------------------------------------------------------------------

    private void InstallBuiltins()
    {
        _globals["text"] = NativeSync("text", 1, args =>
        {
            AddOutput(args[0].ToDisplay());
            return ScriptValue.Null;
        });

        _globals["log"] = NativeSync("log", 1, args =>
        {
            AddConsole(string.Join(' ', args.Select(a => a.ToDisplay())));
            return ScriptValue.Null;
        });

        _globals["image"] = NativeSync("image", 1, args =>
        {
            var value = args[0];

            // Accept a data: URL, an { image_url } object, or an image block.
            var dataUrl = value switch
            {
                ScriptText text => text.Value,
                ScriptObject obj => obj.Member("image_url") is ScriptText url
                    ? url.Value
                    : obj.Member("data") is ScriptText raw
                        ? $"data:{obj.Member("mimeType").AsText()};base64,{raw.Value}"
                        : "",
                _ => ""
            };

            var image = ScriptImage.FromDataUrl(dataUrl);
            if (image is null)
                throw new ScriptException(
                    "image() needs a base64 data: URL or an { type: \"image\", data, mimeType } block; "
                    + "remote URLs are not supported");

            if (_images.Count >= _maxOutputItems)
                throw new ScriptException($"script produced more than {_maxOutputItems} output items");

            _images.Add(image);
            return ScriptValue.Null;
        });

        _globals["store"] = NativeSync("store", 2, args =>
        {
            var key = args[0].AsText();
            if (args[1].IsNull)
                _store.Remove(key);
            else
                _store[key] = args[1];
            return ScriptValue.Null;
        });

        _globals["load"] = NativeSync("load", 1, args =>
            _store.TryGetValue(args[0].AsText(), out var stored) ? stored : ScriptValue.Null);

        _globals["len"] = NativeSync("len", 1, args => ScriptValue.Of(args[0].Length));

        _globals["str"] = NativeSync("str", 1, args => ScriptValue.Of(args[0].ToDisplay()));
        _globals["num"] = NativeSync("num", 1, args => ScriptValue.Of(args[0].AsNumber()));

        _globals["json"] = new ScriptObject(new Dictionary<string, ScriptValue>(StringComparer.Ordinal)
        {
            ["parse"] = NativeSync("json.parse", 1, args => ScriptValue.FromJson(args[0].AsText())),
            ["stringify"] = NativeSync("json.stringify", 1, args => ScriptValue.Of(args[0].ToJsonText()))
        });

        _globals["tools"] = new ScriptObject(new Dictionary<string, ScriptValue>(StringComparer.Ordinal));
        _globals["ALL_TOOLS"] = ScriptValue.Of(_tools.Select(tool => (ScriptValue)new ScriptObject(
            new Dictionary<string, ScriptValue>(StringComparer.Ordinal)
            {
                ["name"] = ScriptValue.Of(tool.Name),
                ["description"] = ScriptValue.Of(tool.Description)
            })));

        // join([handle, ...]) or join(handle) waits for deferred tool calls.
        _globals["join"] = Native("join", 1, (args, ct) => JoinAsync(args[0], ct));

        // sleep is deliberately absent: no timers, matching pi's sandbox.
    }

    private static ScriptNativeFunction Native(
        string name,
        int arity,
        Func<IReadOnlyList<ScriptValue>, CancellationToken, Task<ScriptValue>> body) =>
        new(name, arity, body);

    private static ScriptNativeFunction NativeSync(
        string name,
        int arity,
        Func<IReadOnlyList<ScriptValue>, ScriptValue> body) =>
        new(name, arity, (args, _) => Task.FromResult(body(args)));

    private static async Task<ScriptValue> JoinAsync(ScriptValue value, CancellationToken cancellationToken)
    {
        switch (value)
        {
            case ScriptHandle handle:
                return await handle.Pending.WaitAsync(cancellationToken).ConfigureAwait(false);

            case ScriptArray array:
            {
                var results = new List<ScriptValue>(array.Items.Count);
                foreach (var item in array.Items)
                    results.Add(await JoinAsync(item, cancellationToken).ConfigureAwait(false));
                return new ScriptArray(results);
            }

            default:
                return value;
        }
    }

    private void AddOutput(string text)
    {
        if (_output.Count >= _maxOutputItems)
            throw new ScriptException($"script produced more than {_maxOutputItems} output items");
        _output.Add(text);
    }

    private void AddConsole(string text)
    {
        if (_console.Count >= _maxOutputItems)
            throw new ScriptException($"script produced more than {_maxOutputItems} console lines");
        _console.Add(text);
    }

    // ------------------------------------------------------------------
    // Scopes
    // ------------------------------------------------------------------

    private void PushScope() => _scopes.Add([]);

    private void PopScope()
    {
        if (_scopes.Count > 1)
            _scopes.RemoveAt(_scopes.Count - 1);
    }

    private ScriptValue Lookup(string name)
    {
        for (var i = _scopes.Count - 1; i >= 0; i--)
        {
            if (_scopes[i].TryGetValue(name, out var value))
                return value;
        }

        return _globals.TryGetValue(name, out var global) ? global : ScriptValue.Null;
    }

    private void Declare(string name, ScriptValue value) =>
        _scopes[^1][name] = value;

    private void Assign(string name, ScriptValue value)
    {
        for (var i = _scopes.Count - 1; i >= 0; i--)
        {
            if (!_scopes[i].ContainsKey(name))
                continue;
            _scopes[i][name] = value;
            return;
        }

        if (_globals.ContainsKey(name) && name != "tools")
        {
            _globals[name] = value;
            return;
        }

        throw new ScriptException($"unknown variable '{name}'");
    }

    // ------------------------------------------------------------------
    // Execution
    // ------------------------------------------------------------------

    private enum ScriptFlow
    {
        Next,
        Break,
        Continue,
        Return
    }

    private readonly record struct ScriptOutcome(ScriptFlow Flow, ScriptValue Value);

    private sealed class ScriptStepLimitException(string message) : Exception(message);

    private void Tick()
    {
        if (++_steps > _maxSteps)
            throw new ScriptStepLimitException($"script exceeded its budget of {_maxSteps} steps");
    }

    private async Task<ScriptOutcome> ExecuteAsync(ScriptNode node, CancellationToken cancellationToken)
    {
        Tick();
        cancellationToken.ThrowIfCancellationRequested();

        switch (node)
        {
            case ScriptVarDeclNode declaration:
            {
                var value = declaration.Initializer is null
                    ? ScriptValue.Null
                    : await EvaluateAsync(declaration.Initializer, cancellationToken).ConfigureAwait(false);
                Declare(declaration.Name, value);
                return new ScriptOutcome(ScriptFlow.Next, ScriptValue.Null);
            }

            case ScriptAssignNode assignment:
            {
                var value = await EvaluateAsync(assignment.Value, cancellationToken).ConfigureAwait(false);

                if (assignment.Target is ScriptIdentifierNode identifier)
                {
                    if (assignment.Operator == "+=")
                        value = Add(Lookup(identifier.Name), value);
                    else if (assignment.Operator == "-=")
                        value = ScriptValue.Of(Lookup(identifier.Name).AsNumber() - value.AsNumber());

                    Assign(identifier.Name, value);
                    return new ScriptOutcome(ScriptFlow.Next, value);
                }

                if (assignment.Target is ScriptIndexNode index)
                {
                    var container = await EvaluateAsync(index.Target, cancellationToken).ConfigureAwait(false);
                    var indexValue = await EvaluateAsync(index.Index, cancellationToken).ConfigureAwait(false);

                    if (container is ScriptArray array)
                    {
                        var position = (int)indexValue.AsNumber();
                        if (position < 0)
                            position += array.Items.Count;
                        if (position >= 0 && position < array.Items.Count)
                            array.Items[position] = value;
                        return new ScriptOutcome(ScriptFlow.Next, value);
                    }

                    if (container is ScriptObject obj)
                    {
                        obj.Members[indexValue.AsText()] = value;
                        return new ScriptOutcome(ScriptFlow.Next, value);
                    }
                }

                throw new ScriptException("cannot assign to this expression", assignment.Line);
            }

            case ScriptIfNode conditional:
            {
                var condition = await EvaluateAsync(conditional.Condition, cancellationToken).ConfigureAwait(false);
                var branch = condition.Truthy ? conditional.Then : conditional.Else;

                if (branch is null)
                    return new ScriptOutcome(ScriptFlow.Next, ScriptValue.Null);

                PushScope();
                try
                {
                    foreach (var statement in branch)
                    {
                        var outcome = await ExecuteAsync(statement, cancellationToken).ConfigureAwait(false);
                        if (outcome.Flow != ScriptFlow.Next)
                            return outcome;
                    }
                }
                finally
                {
                    PopScope();
                }

                return new ScriptOutcome(ScriptFlow.Next, ScriptValue.Null);
            }

            case ScriptWhileNode loop:
            {
                while (true)
                {
                    Tick();
                    cancellationToken.ThrowIfCancellationRequested();

                    var condition = await EvaluateAsync(loop.Condition, cancellationToken).ConfigureAwait(false);
                    if (!condition.Truthy)
                        break;

                    PushScope();
                    try
                    {
                        var flow = ScriptFlow.Next;
                        foreach (var statement in loop.Body)
                        {
                            var outcome = await ExecuteAsync(statement, cancellationToken).ConfigureAwait(false);
                            if (outcome.Flow == ScriptFlow.Break)
                            {
                                flow = ScriptFlow.Break;
                                break;
                            }
                            if (outcome.Flow == ScriptFlow.Continue)
                                break;
                            if (outcome.Flow == ScriptFlow.Return)
                                return outcome;
                        }
                        if (flow == ScriptFlow.Break)
                            break;
                    }
                    finally
                    {
                        PopScope();
                    }
                }

                return new ScriptOutcome(ScriptFlow.Next, ScriptValue.Null);
            }

            case ScriptForeachNode loop:
            {
                var iterable = await EvaluateAsync(loop.Iterable, cancellationToken).ConfigureAwait(false);
                var items = iterable switch
                {
                    ScriptArray array => array.Items,
                    ScriptText text => [.. text.Value.Select(c => (ScriptValue)ScriptValue.Of(c.ToString()))],
                    ScriptObject obj => [.. obj.Members.Keys.Select(ScriptValue.Of)],
                    _ => []
                };

                foreach (var item in items)
                {
                    Tick();
                    cancellationToken.ThrowIfCancellationRequested();

                    PushScope();
                    try
                    {
                        Declare(loop.Variable, item);

                        var flow = ScriptFlow.Next;
                        foreach (var statement in loop.Body)
                        {
                            var outcome = await ExecuteAsync(statement, cancellationToken).ConfigureAwait(false);
                            if (outcome.Flow == ScriptFlow.Break)
                            {
                                flow = ScriptFlow.Break;
                                break;
                            }
                            if (outcome.Flow == ScriptFlow.Continue)
                                break;
                            if (outcome.Flow == ScriptFlow.Return)
                                return outcome;
                        }
                        if (flow == ScriptFlow.Break)
                            break;
                    }
                    finally
                    {
                        PopScope();
                    }
                }

                return new ScriptOutcome(ScriptFlow.Next, ScriptValue.Null);
            }

            case ScriptForNode loop:
            {
                PushScope();
                try
                {
                    if (loop.Init is not null)
                        await ExecuteAsync(loop.Init, cancellationToken).ConfigureAwait(false);

                    while (true)
                    {
                        Tick();
                        cancellationToken.ThrowIfCancellationRequested();

                        if (loop.Condition is not null)
                        {
                            var condition = await EvaluateAsync(loop.Condition, cancellationToken).ConfigureAwait(false);
                            if (!condition.Truthy)
                                break;
                        }

                        var flow = ScriptFlow.Next;
                        foreach (var statement in loop.Body)
                        {
                            var outcome = await ExecuteAsync(statement, cancellationToken).ConfigureAwait(false);
                            if (outcome.Flow == ScriptFlow.Break)
                            {
                                flow = ScriptFlow.Break;
                                break;
                            }
                            if (outcome.Flow == ScriptFlow.Continue)
                                break;
                            if (outcome.Flow == ScriptFlow.Return)
                                return outcome;
                        }

                        if (flow == ScriptFlow.Break)
                            break;

                        if (loop.Step is not null)
                            await ExecuteAsync(loop.Step, cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    PopScope();
                }

                return new ScriptOutcome(ScriptFlow.Next, ScriptValue.Null);
            }

            case ScriptReturnNode ret:
            {
                var value = ret.Value is null
                    ? ScriptValue.Null
                    : await EvaluateAsync(ret.Value, cancellationToken).ConfigureAwait(false);
                return new ScriptOutcome(ScriptFlow.Return, value);
            }

            case ScriptBlockNode block:
            {
                PushScope();
                try
                {
                    foreach (var statement in block.Body)
                    {
                        var outcome = await ExecuteAsync(statement, cancellationToken).ConfigureAwait(false);
                        if (outcome.Flow != ScriptFlow.Next)
                            return outcome;
                    }
                }
                finally
                {
                    PopScope();
                }
                return new ScriptOutcome(ScriptFlow.Next, ScriptValue.Null);
            }

            case ScriptBreakNode:
                return new ScriptOutcome(ScriptFlow.Break, ScriptValue.Null);

            case ScriptContinueNode:
                return new ScriptOutcome(ScriptFlow.Continue, ScriptValue.Null);

            default:
                // Expression statement: evaluate for effect.
                await EvaluateAsync(node, cancellationToken).ConfigureAwait(false);
                return new ScriptOutcome(ScriptFlow.Next, ScriptValue.Null);
        }
    }

    private async Task<ScriptValue> EvaluateAsync(ScriptNode node, CancellationToken cancellationToken)
    {
        Tick();
        cancellationToken.ThrowIfCancellationRequested();

        switch (node)
        {
            case ScriptLiteralNode literal:
                return literal.Value;

            case ScriptIdentifierNode identifier:
                return Lookup(identifier.Name);

            case ScriptArrayNode array:
            {
                var items = new List<ScriptValue>(array.Items.Count);
                foreach (var item in array.Items)
                    items.Add(await EvaluateAsync(item, cancellationToken).ConfigureAwait(false));
                return new ScriptArray(items);
            }

            case ScriptObjectNode obj:
            {
                var members = new Dictionary<string, ScriptValue>(StringComparer.Ordinal);
                foreach (var (key, valueNode) in obj.Members)
                    members[key] = await EvaluateAsync(valueNode, cancellationToken).ConfigureAwait(false);
                return new ScriptObject(members);
            }

            case ScriptUnaryNode unary:
            {
                var operand = await EvaluateAsync(unary.Operand, cancellationToken).ConfigureAwait(false);
                return unary.Operator switch
                {
                    "!" => ScriptValue.Of(!operand.Truthy),
                    "-" => ScriptValue.Of(-operand.AsNumber()),
                    "+" => ScriptValue.Of(operand.AsNumber()),
                    _ => operand
                };
            }

            case ScriptBinaryNode binary:
                return await EvaluateBinaryAsync(binary, cancellationToken).ConfigureAwait(false);

            case ScriptMemberNode member:
            {
                var target = await EvaluateAsync(member.Target, cancellationToken).ConfigureAwait(false);
                return target.Member(member.Name);
            }

            case ScriptIndexNode index:
            {
                var target = await EvaluateAsync(index.Target, cancellationToken).ConfigureAwait(false);
                var key = await EvaluateAsync(index.Index, cancellationToken).ConfigureAwait(false);
                return target.Index(key);
            }

            case ScriptCallNode call:
                return await EvaluateCallAsync(call, cancellationToken).ConfigureAwait(false);

            case ScriptLambdaNode lambda:
            {
                // Snapshot the visible scopes so the closure keeps working after the block exits.
                var captured = new List<Dictionary<string, ScriptValue>>(_scopes.Count);
                foreach (var scope in _scopes)
                    captured.Add(new Dictionary<string, ScriptValue>(scope, StringComparer.Ordinal));

                return new ScriptClosure(lambda.Parameters, lambda.Body, captured);
            }

            case ScriptSpawnNode spawn:
                // Start the tool call without awaiting so several can overlap.
                return new ScriptHandle(EvaluateCallAsync(spawn.Call, cancellationToken));

            default:
                await ExecuteAsync(node, cancellationToken).ConfigureAwait(false);
                return ScriptValue.Null;
        }
    }

    private async Task<ScriptValue> EvaluateBinaryAsync(
        ScriptBinaryNode binary,
        CancellationToken cancellationToken)
    {
        // Short-circuit before evaluating the right side.
        if (binary.Operator == "&&")
        {
            var left = await EvaluateAsync(binary.Left, cancellationToken).ConfigureAwait(false);
            return left.Truthy
                ? await EvaluateAsync(binary.Right, cancellationToken).ConfigureAwait(false)
                : left;
        }

        if (binary.Operator == "||")
        {
            var left = await EvaluateAsync(binary.Left, cancellationToken).ConfigureAwait(false);
            return left.Truthy
                ? left
                : await EvaluateAsync(binary.Right, cancellationToken).ConfigureAwait(false);
        }

        var lhs = await EvaluateAsync(binary.Left, cancellationToken).ConfigureAwait(false);
        var rhs = await EvaluateAsync(binary.Right, cancellationToken).ConfigureAwait(false);

        return binary.Operator switch
        {
            "+" => Add(lhs, rhs),
            "-" => ScriptValue.Of(lhs.AsNumber() - rhs.AsNumber()),
            "*" => ScriptValue.Of(lhs.AsNumber() * rhs.AsNumber()),
            "/" => ScriptValue.Of(rhs.AsNumber() == 0 ? 0 : lhs.AsNumber() / rhs.AsNumber()),
            "%" => ScriptValue.Of(rhs.AsNumber() == 0 ? 0 : lhs.AsNumber() % rhs.AsNumber()),
            "==" => ScriptValue.Of(Equals(lhs, rhs)),
            "!=" => ScriptValue.Of(!Equals(lhs, rhs)),
            "<" => ScriptValue.Of(lhs.AsNumber() < rhs.AsNumber()),
            "<=" => ScriptValue.Of(lhs.AsNumber() <= rhs.AsNumber()),
            ">" => ScriptValue.Of(lhs.AsNumber() > rhs.AsNumber()),
            ">=" => ScriptValue.Of(lhs.AsNumber() >= rhs.AsNumber()),
            _ => ScriptValue.Null
        };
    }

    private static ScriptValue Add(ScriptValue left, ScriptValue right)
    {
        // String concatenation wins when either side is text, like JS.
        if (left is ScriptText || right is ScriptText)
            return ScriptValue.Of(left.ToDisplay() + right.ToDisplay());

        if (left is ScriptArray leftArray && right is ScriptArray rightArray)
            return new ScriptArray([.. leftArray.Items, .. rightArray.Items]);

        return ScriptValue.Of(left.AsNumber() + right.AsNumber());
    }

    private async Task<ScriptValue> EvaluateCallAsync(
        ScriptCallNode call,
        CancellationToken cancellationToken)
    {
        // tools.<name>(args) is the only capability, so special-case it before evaluating.
        if (call.Callee is ScriptMemberNode { Target: ScriptIdentifierNode { Name: "tools" } } member)
            return await CallToolAsync(member.Name, call, cancellationToken).ConfigureAwait(false);

        var callee = await EvaluateAsync(call.Callee, cancellationToken).ConfigureAwait(false);

        var arguments = new List<ScriptValue>(call.Arguments.Count);
        foreach (var argument in call.Arguments)
            arguments.Add(await EvaluateAsync(argument, cancellationToken).ConfigureAwait(false));

        return callee switch
        {
            ScriptNativeFunction native => await native.Invoke(arguments, cancellationToken).ConfigureAwait(false),
            _ => throw new ScriptException(
                $"'{DescribeCallee(call.Callee)}' is not callable", call.Line)
        };
    }

    private static string DescribeCallee(ScriptNode node) => node switch
    {
        ScriptIdentifierNode identifier => identifier.Name,
        ScriptMemberNode member => $"{DescribeCallee(member.Target)}.{member.Name}",
        _ => "expression"
    };

    private async Task<ScriptValue> CallToolAsync(
        string name,
        ScriptCallNode call,
        CancellationToken cancellationToken)
    {
        var identifier = Identifier(name);

        if (!_toolTable.TryGetValue(identifier, out var actualName))
            throw new ScriptException($"unknown tool '{name}'", call.Line);

        // Arguments: either one object, or a single positional object built from key/value pairs.
        var arguments = new Dictionary<string, ScriptValue>(StringComparer.Ordinal);

        if (call.Arguments.Count == 1)
        {
            var single = await EvaluateAsync(call.Arguments[0], cancellationToken).ConfigureAwait(false);
            if (single is ScriptObject obj)
            {
                foreach (var (key, value) in obj.Members)
                    arguments[key] = value;
            }
            else if (!single.IsNull)
            {
                throw new ScriptException(
                    $"tools.{name}() takes one object argument, for example tools.{name}({{ path: \"a.txt\" }})",
                    call.Line);
            }
        }
        else if (call.Arguments.Count > 1)
        {
            throw new ScriptException(
                $"tools.{name}() takes one object argument, but {call.Arguments.Count} were given",
                call.Line);
        }

        var json = new ScriptObject(arguments).ToJsonText();
        _toolCalls++;

        var result = await _toolExecutor(actualName.AsText(), json, cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            throw new ScriptException(
                $"tools.{name}() failed: {result.ToLlmResponse()}",
                call.Line);
        }

        return ScriptValue.FromJson(result.Content);
    }

    /// <summary>
    /// Maps a tool name to a script identifier the way pi does: characters that are not valid in
    /// an identifier become <c>_</c>, so <c>my-tool</c> is <c>tools.my_tool</c>.
    /// </summary>
    public static string Identifier(string toolName)
    {
        var builder = new StringBuilder(toolName.Length);
        foreach (var ch in toolName)
            builder.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        return builder.ToString();
    }
}
#endif
