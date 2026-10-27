#if !NETWASM
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reptar.AI;

/// <summary>
/// A value inside a <see cref="ScriptInterpreter"/> run. Deliberately small: JSON plus a
/// promise handle, which is everything a tool-calling script needs.
/// </summary>
public abstract record ScriptValue
{
    public static readonly ScriptValue Null = new ScriptNull();
    public static readonly ScriptValue True = new ScriptBool(true);
    public static readonly ScriptValue False = new ScriptBool(false);

    public static ScriptValue Of(bool value) => value ? True : False;
    public static ScriptValue Of(double value) => new ScriptNumber(value);
    public static ScriptValue Of(string value) => new ScriptText(value);

    public static ScriptValue Of(IEnumerable<ScriptValue> values) => new ScriptArray([.. values]);

    /// <summary>True for null and for absent object members.</summary>
    public bool IsNull => this is ScriptNull;

    /// <summary>Truthiness: null and false are false; 0, "" and empty arrays are false.</summary>
    public virtual bool Truthy => true;

    /// <summary>String form used by <c>text()</c> and <c>+</c> concatenation.</summary>
    public abstract string ToDisplay();

    /// <summary>Converts to JSON, the shape a tool result or script return takes.</summary>
    public abstract JsonNode ToJson();

    // ------------------------------------------------------------------
    // Accessors
    // ------------------------------------------------------------------

    public virtual ScriptValue Member(string name) => Null;

    public virtual ScriptValue Index(ScriptValue index) => Null;

    public virtual double AsNumber() => this switch
    {
        ScriptNumber number => number.Value,
        ScriptText text when double.TryParse(text.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) => parsed,
        ScriptBool boolean => boolean.Value ? 1 : 0,
        _ => 0
    };

    public virtual string AsText() => ToDisplay();

    public virtual int Length => this switch
    {
        ScriptArray array => array.Items.Count,
        ScriptText text => text.Value.Length,
        ScriptObject obj => obj.Members.Count,
        _ => 0
    };

    // ------------------------------------------------------------------
    // JSON bridge
    // ------------------------------------------------------------------

    /// <summary>Parses JSON text into a script value. Invalid JSON yields null.</summary>
    public static ScriptValue FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Null;

        try
        {
            return FromNode(JsonNode.Parse(json));
        }
        catch (JsonException)
        {
            return Null;
        }
    }

    /// <summary>Adapts a <see cref="JsonNode"/> into a script value.</summary>
    public static ScriptValue FromNode(JsonNode? node)
    {
        if (node is null)
            return Null;

        return node switch
        {
            JsonObject obj => new ScriptObject(
                obj.ToDictionary(kv => kv.Key, kv => FromNode(kv.Value), StringComparer.Ordinal)),
            JsonArray array => new ScriptArray([.. array.Select(FromNode)]),
            JsonValue value => FromJsonValue(value),
            _ => Null
        };
    }

    private static ScriptValue FromJsonValue(JsonValue value)
    {
        if (value.TryGetValue<bool>(out var boolean))
            return Of(boolean);
        if (value.TryGetValue<double>(out var number))
            return Of(number);
        if (value.TryGetValue<string>(out var text))
            return Of(text);
        return Null;
    }

    /// <summary>Serializes to compact JSON text.</summary>
    public string ToJsonText() => ToJson().ToJsonString();
}

public sealed record ScriptNull : ScriptValue
{
    public override bool Truthy => false;
    public override string ToDisplay() => "null";
    public override JsonNode ToJson() => JsonValue.Create((string?)null)!;
}

public sealed record ScriptBool(bool Value) : ScriptValue
{
    public override bool Truthy => Value;
    public override string ToDisplay() => Value ? "true" : "false";
    public override JsonNode ToJson() => JsonValue.Create(Value);
}

public sealed record ScriptNumber(double Value) : ScriptValue
{
    public override bool Truthy => Value != 0 && !double.IsNaN(Value);
    public override string ToDisplay() =>
        Math.Abs(Value - Math.Round(Value)) < double.Epsilon && Math.Abs(Value) < 1e15
            ? ((long)Value).ToString(CultureInfo.InvariantCulture)
            : Value.ToString("0.############", CultureInfo.InvariantCulture);
    public override JsonNode ToJson() => JsonValue.Create(Value);
}

public sealed record ScriptText(string Value) : ScriptValue
{
    public override bool Truthy => Value.Length > 0;
    public override string ToDisplay() => Value;
    public override string AsText() => Value;
    public override JsonNode ToJson() => JsonValue.Create(Value);
    public override int Length => Value.Length;

    public override ScriptValue Member(string name) => name switch
    {
        "length" => Of(Value.Length),
        "upper" => new ScriptNativeFunction("upper", 0, (_, _) => Task.FromResult<ScriptValue>(Of(Value.ToUpperInvariant()))),
        "lower" => new ScriptNativeFunction("lower", 0, (_, _) => Task.FromResult<ScriptValue>(Of(Value.ToLowerInvariant()))),
        "trim" => new ScriptNativeFunction("trim", 0, (_, _) => Task.FromResult<ScriptValue>(Of(Value.Trim()))),
        "split" => new ScriptNativeFunction("split", 1, (args, _) =>
            Task.FromResult<ScriptValue>(Of(Value
                .Split(args[0].AsText())
                .Select(Of)))),
        "includes" => new ScriptNativeFunction("includes", 1, (args, _) =>
            Task.FromResult(Of(Value.Contains(args[0].AsText(), StringComparison.Ordinal)))),
        "startsWith" => new ScriptNativeFunction("startsWith", 1, (args, _) =>
            Task.FromResult(Of(Value.StartsWith(args[0].AsText(), StringComparison.Ordinal)))),
        "endsWith" => new ScriptNativeFunction("endsWith", 1, (args, _) =>
            Task.FromResult(Of(Value.EndsWith(args[0].AsText(), StringComparison.Ordinal)))),
        "slice" => new ScriptNativeFunction("slice", 2, (args, _) =>
        {
            var start = (int)Math.Clamp(args[0].AsNumber(), 0, Value.Length);
            var end = args.Count > 1 ? (int)Math.Clamp(args[1].AsNumber(), start, Value.Length) : Value.Length;
            return Task.FromResult<ScriptValue>(Of(Value[start..end]));
        }),
        _ => Null
    };
}

public sealed record ScriptArray(List<ScriptValue> Items) : ScriptValue
{
    public override bool Truthy => Items.Count > 0;
    public override string ToDisplay() => ToJsonText();
    public override JsonNode ToJson() => new JsonArray([.. Items.Select(item => item.ToJson())]);
    public override int Length => Items.Count;

    public override ScriptValue Index(ScriptValue index)
    {
        var position = (int)index.AsNumber();
        if (position < 0)
            position += Items.Count;
        return position >= 0 && position < Items.Count ? Items[position] : Null;
    }

    public override ScriptValue Member(string name) => name switch
    {
        "length" => Of(Items.Count),
        "push" => new ScriptNativeFunction("push", 1, (args, _) =>
        {
            Items.Add(args[0]);
            return Task.FromResult<ScriptValue>(Of(Items.Count));
        }),
        "join" => new ScriptNativeFunction("join", 1, (args, _) =>
            Task.FromResult<ScriptValue>(Of(string.Join(args[0].AsText(), Items.Select(i => i.ToDisplay()))))),
        "filter" => new ScriptNativeFunction("filter", 1, (args, ct) =>
            FilterAsync(args[0], ct)),
        "map" => new ScriptNativeFunction("map", 1, (args, ct) =>
            MapAsync(args[0], ct)),
        "find" => new ScriptNativeFunction("find", 1, (args, ct) =>
            FindAsync(args[0], ct)),
        _ => Null
    };

    private async Task<ScriptValue> FilterAsync(ScriptValue predicate, CancellationToken ct)
    {
        var kept = new List<ScriptValue>();
        foreach (var item in Items)
        {
            ct.ThrowIfCancellationRequested();
            if ((await ScriptInterpreter.InvokeAsync(predicate, [item], ct).ConfigureAwait(false)).Truthy)
                kept.Add(item);
        }
        return new ScriptArray(kept);
    }

    private async Task<ScriptValue> MapAsync(ScriptValue transform, CancellationToken ct)
    {
        var mapped = new List<ScriptValue>(Items.Count);
        foreach (var item in Items)
        {
            ct.ThrowIfCancellationRequested();
            mapped.Add(await ScriptInterpreter.InvokeAsync(transform, [item], ct).ConfigureAwait(false));
        }
        return new ScriptArray(mapped);
    }

    private async Task<ScriptValue> FindAsync(ScriptValue predicate, CancellationToken ct)
    {
        foreach (var item in Items)
        {
            ct.ThrowIfCancellationRequested();
            if ((await ScriptInterpreter.InvokeAsync(predicate, [item], ct).ConfigureAwait(false)).Truthy)
                return item;
        }
        return Null;
    }
}

public sealed record ScriptObject(Dictionary<string, ScriptValue> Members) : ScriptValue
{
    public override bool Truthy => Members.Count > 0;
    public override string ToDisplay() => ToJsonText();
    public override JsonNode ToJson() =>
        new JsonObject(Members.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value.ToJson())));
    public override int Length => Members.Count;

    public override ScriptValue Member(string name) =>
        Members.TryGetValue(name, out var value) ? value : Null;

    public override ScriptValue Index(ScriptValue index) => Member(index.AsText());
}

/// <summary>
/// An image a script surfaced with <c>image()</c>. Carried separately from text so the host can
/// attach it as a vision block rather than base64 in the transcript.
/// </summary>
public sealed record ScriptImage(string MimeType, string Base64Data) : ScriptValue
{
    public override string ToDisplay() => $"[image {MimeType}]";
    public override JsonNode ToJson() => new JsonObject
    {
        ["type"] = "image",
        ["mimeType"] = MimeType,
        ["data"] = Base64Data
    };

    /// <summary>
    /// Parses a <c>data:</c> URL. Remote URLs are refused, matching pi: a script must not be able
    /// to make the host fetch arbitrary addresses.
    /// </summary>
    public static ScriptImage? FromDataUrl(string value)
    {
        if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return null;

        var comma = value.IndexOf(',');
        if (comma < 0)
            return null;

        var header = value[5..comma];
        var data = value[(comma + 1)..];

        if (!header.Contains(";base64", StringComparison.OrdinalIgnoreCase))
            return null;

        var mime = header.Split(';')[0];
        if (mime.Length == 0)
            mime = "image/png";

        return data.Length == 0 ? null : new ScriptImage(mime, data);
    }
}

/// <summary>A deferred tool call produced by <c>spawn()</c>, awaited by <c>join()</c>.</summary>
public sealed record ScriptHandle(Task<ScriptValue> Pending) : ScriptValue
{
    public override string ToDisplay() => "<pending>";
    public override JsonNode ToJson() => JsonValue.Create("<pending>")!;
}

/// <summary>
/// A user-defined lambda together with the scopes it captured at definition time. Cloning the
/// scope chain is what makes a closure outlive the block that created it.
/// </summary>
public sealed record ScriptClosure(
    IReadOnlyList<string> Parameters,
    IReadOnlyList<ScriptNode> Body,
    IReadOnlyList<Dictionary<string, ScriptValue>> Captured) : ScriptValue
{
    public override string ToDisplay() => "<function>";
    public override JsonNode ToJson() => JsonValue.Create("<function>")!;
}

/// <summary>
/// A callable implemented by the interpreter rather than by the script. Used for built-ins and
/// for array/string methods, which keeps the method table in one place.
/// </summary>
public sealed record ScriptNativeFunction(
    string Name,
    int Arity,
    Func<IReadOnlyList<ScriptValue>, CancellationToken, Task<ScriptValue>> Invoke) : ScriptValue
{
    public override string ToDisplay() => $"<function {Name}>";
    public override JsonNode ToJson() => JsonValue.Create($"<function {Name}>")!;
}
#endif
