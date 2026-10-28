#if !NETWASM
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reptar;

public enum AiToolFormat
{
    OpenAi,
    Anthropic,
    Gemini,
    JsonSchema
}

public sealed class AiToolDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required Dictionary<string, object?> Parameters { get; init; }
    public required string Method { get; init; }
    public required string Path { get; init; }

    public Dictionary<string, object?> ToDictionary(AiToolFormat format = AiToolFormat.OpenAi)
    {
        return format switch
        {
            AiToolFormat.Anthropic => new Dictionary<string, object?>
            {
                ["name"] = Name,
                ["description"] = Description,
                ["input_schema"] = Parameters
            },
            AiToolFormat.Gemini => new Dictionary<string, object?>
            {
                ["name"] = Name,
                ["description"] = Description,
                ["parameters"] = Parameters
            },
            AiToolFormat.JsonSchema => Parameters,
            _ => new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = Name,
                    ["description"] = Description,
                    ["parameters"] = Parameters
                }
            }
        };
    }

    public string ToJson(AiToolFormat format = AiToolFormat.OpenAi, bool indented = false)
    {
        var dict = ToDictionary(format);
        return JsonSerializer.Serialize(dict, new JsonSerializerOptions
        {
            WriteIndented = indented,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });
    }
}

public sealed class AiToolResult
{
    public required bool Success { get; init; }
    public required int StatusCode { get; init; }
    public string? Content { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<ValidationIssue>? ValidationErrors { get; init; }

    public static AiToolResult Ok(int statusCode, string content) => new()
    {
        Success = true,
        StatusCode = statusCode,
        Content = content
    };

    public static AiToolResult Failure(
        int statusCode,
        string errorCode,
        string message,
        IReadOnlyList<ValidationIssue>? validationErrors = null) => new()
    {
        Success = false,
        StatusCode = statusCode,
        ErrorCode = errorCode,
        ErrorMessage = message,
        ValidationErrors = validationErrors,
        Content = message
    };

    /// <summary>
    /// Formats the tool result into a string suitable for passing directly into an LLM tool response role.
    /// If validation failed, includes the exact field constraints so the model can self-correct.
    /// </summary>
    public string ToLlmResponse()
    {
        if (Success)
            return string.IsNullOrEmpty(Content) ? "{}" : Content;

        if (ValidationErrors is { Count: > 0 } errors)
        {
            var sb = new StringBuilder();
            sb.Append("Error (").Append(ErrorCode ?? "validation_error").Append("): ");
            sb.Append(ErrorMessage ?? "Validation failed.").Append(". Issues: ");
            for (var i = 0; i < errors.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                var pathStr = errors[i].Path is { Length: > 0 } p ? string.Join(".", p) : "field";
                sb.Append(pathStr).Append(": ").Append(errors[i].Message);
            }
            return sb.ToString();
        }

        return $"Error ({StatusCode} {ErrorCode ?? "error"}): {ErrorMessage ?? Content ?? "Operation failed."}";
    }
}

public readonly record struct AiToolCall(string Id, string Name, string Arguments);

public sealed record AgentImage(string MimeType, string Base64Data);

/// <summary>
/// Provider-reported token accounting for one model response. Ported from pi's
/// <c>Usage</c>: <see cref="ReasoningTokens"/> is already counted inside
/// <see cref="OutputTokens"/>, so it must not be added again.
/// </summary>
public sealed record AgentUsage(
    int InputTokens,
    int OutputTokens,
    int CacheReadTokens = 0,
    int CacheWriteTokens = 0,
    int? ReasoningTokens = null,
    double Cost = 0)
{
    /// <summary>Everything the provider billed for this response.</summary>
    public int TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheWriteTokens;

    /// <summary>True when the provider reported nothing usable (aborted or errored turn).</summary>
    public bool IsEmpty => TotalTokens == 0;

    public AgentUsage Add(AgentUsage other) => new(
        InputTokens + other.InputTokens,
        OutputTokens + other.OutputTokens,
        CacheReadTokens + other.CacheReadTokens,
        CacheWriteTokens + other.CacheWriteTokens,
        (ReasoningTokens ?? 0) + (other.ReasoningTokens ?? 0) is var r && r == 0 ? null : r,
        Cost + other.Cost);
}

public sealed record ChatMessage(
    string Role,
    string Content,
    string? Name = null,
    string? ToolCallId = null,
    IReadOnlyList<AiToolCall>? ToolCalls = null,
    IReadOnlyList<AgentImage>? Images = null,
    AgentUsage? Usage = null,
    long Timestamp = 0,
    AI.BashExecutionDetails? Bash = null);

public sealed record AgentStep(
    string? Content,
    IReadOnlyList<AiToolCall>? ToolCalls = null,
    bool IsComplete = false);

public sealed record AgentRunResult(
    bool Success,
    string FinalContent,
    int StepsTaken,
    IReadOnlyList<ChatMessage> History);

public static class AgentHarness
{
    /// <summary>
    /// Executes an autonomous multi-turn ReAct agent loop. Dispatches tool calls directly into Reptar routes
    /// and loops until the model produces a completion or reaches maxSteps.
    /// </summary>
    public static async Task<AgentRunResult> RunAsync(
        Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<AgentStep>> model,
        List<ChatMessage> history,
        Func<string, string, Task<AiToolResult>> toolExecutor,
        int maxSteps = 10,
        Action<AgentStep>? onStep = null,
        CancellationToken cancellationToken = default)
    {
        var steps = 0;
        while (steps < maxSteps && !cancellationToken.IsCancellationRequested)
        {
            steps++;
            var step = await model(history, cancellationToken);
            onStep?.Invoke(step);

            if (step.IsComplete || step.ToolCalls is null || step.ToolCalls.Count == 0)
            {
                if (!string.IsNullOrEmpty(step.Content))
                {
                    history.Add(new ChatMessage("assistant", step.Content));
                }
                return new AgentRunResult(true, step.Content ?? "", steps, history);
            }

            history.Add(new ChatMessage("assistant", step.Content ?? "", ToolCalls: step.ToolCalls));

            foreach (var toolCall in step.ToolCalls)
            {
                var result = await toolExecutor(toolCall.Name, toolCall.Arguments);
                history.Add(new ChatMessage(
                    "tool",
                    result.ToLlmResponse(),
                    Name: toolCall.Name,
                    ToolCallId: toolCall.Id));
            }
        }

        return new AgentRunResult(false, "Maximum agent steps reached.", steps, history);
    }
}

public static class AiToolExtensions
{
    /// <summary>
    /// Coerces string-encoded primitives to the types the tool's parameter schema declares.
    /// </summary>
    /// <remarks>
    /// Only touches properties the schema marks as <c>integer</c>, <c>number</c>, or
    /// <c>boolean</c>. Everything else is left exactly as sent, because rewriting a string field
    /// that happens to contain digits would silently change user data.
    /// </remarks>
    private static void CoerceToSchema(JsonObject body, Dictionary<string, object?> schema)
    {
        if (schema.TryGetValue("properties", out var props) &&
            props is Dictionary<string, object?> properties)
        {
            foreach (var (name, propertySchema) in properties)
            {
                if (!body.TryGetPropertyValue(name, out var value) || value is null)
                    continue;

                if (propertySchema is Dictionary<string, object?> definition &&
                    Coerce(value, definition) is { } coerced)
                {
                    body[name] = coerced;
                }
            }
        }
    }

    private static JsonNode? Coerce(JsonNode value, Dictionary<string, object?> definition)
    {
        var type = definition.TryGetValue("type", out var declared) ? declared as string : null;

        switch (type)
        {
            case "integer" when value is JsonValue integerValue &&
                                integerValue.TryGetValue<string>(out var integerText):
                return long.TryParse(integerText, out var integer) ? JsonValue.Create(integer) : null;

            case "number" when value is JsonValue numberValue &&
                              numberValue.TryGetValue<string>(out var numberText):
                return double.TryParse(
                    numberText,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var number)
                    ? JsonValue.Create(number)
                    : null;

            case "boolean" when value is JsonValue booleanValue &&
                               booleanValue.TryGetValue<string>(out var booleanText):
                return bool.TryParse(booleanText, out var flag) ? JsonValue.Create(flag) : null;

            case "object" when value is JsonObject nested &&
                              definition.TryGetValue("properties", out var nestedProperties) &&
                              nestedProperties is Dictionary<string, object?> nestedSchema:
                foreach (var (name, propertySchema) in nestedSchema)
                {
                    if (nested.TryGetPropertyValue(name, out var child) && child is not null &&
                        propertySchema is Dictionary<string, object?> childDefinition &&
                        Coerce(child, childDefinition) is { } coercedChild)
                    {
                        nested[name] = coercedChild;
                    }
                }
                return null;

            case "array" when value is JsonArray array &&
                             definition.TryGetValue("items", out var itemSchema) &&
                             itemSchema is Dictionary<string, object?> itemDefinition:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is { } element && Coerce(element, itemDefinition) is { } coercedElement)
                        array[i] = coercedElement;
                }
                return null;

            default:
                return null;
        }
    }

    /// <summary>
    /// Exports registered API routes as AI agent tool definitions compatible with OpenAI, Claude, or Gemini.
    /// </summary>
    public static List<AiToolDefinition> GetAiTools(
        this ReptarApp app,
        AiToolFormat format = AiToolFormat.OpenAi,
        // Safe default: only routes explicitly marked `.AsAiTool(...)` are
        // exposed. Exposing every HTTP route to an LLM must be opt-in.
        bool onlyExplicit = true)
    {
        var tools = new List<AiToolDefinition>();

        foreach (var route in app.RouteMetadata)
        {
            if (onlyExplicit && !route.IsAiTool)
                continue;

            // Skip internal/docs/framework routes
            if (route.Path.StartsWith("/openapi") ||
                route.Path.StartsWith("/reference") ||
                route.Path.StartsWith("/swagger") ||
                route.Path.StartsWith("/__bench") ||
                route.Path.StartsWith("/api/ai/tools") ||
                route.Path == "/health")
            {
                continue;
            }

            var toolName = GetToolName(route);
            var description = route.AiToolDescription ??
                              route.Description ??
                              route.Summary ??
                              $"{route.Method.ToUpperInvariant()} {route.Path}";

            var (translatedPath, pathParams) = OpenApiGenerator.TranslatePath(route.Path);
            var parameters = BuildParametersSchema(route, pathParams);

            tools.Add(new AiToolDefinition
            {
                Name = toolName,
                Description = description,
                Parameters = parameters,
                Method = route.Method.ToUpperInvariant(),
                Path = route.Path
            });
        }

        return tools;
    }

    /// <summary>
    /// Serializes the AI agent tools as a JSON string in the specified LLM format.
    /// </summary>
    public static string GetAiToolsJson(
        this ReptarApp app,
        AiToolFormat format = AiToolFormat.OpenAi,
        bool onlyExplicit = true,
        bool indented = true)
    {
        var tools = app.GetAiTools(format, onlyExplicit);

        // Reflection-free serialization: tool parameter schemas are nested
        // dictionaries, which JsonSerializer.Serialize would refuse under
        // Native AOT. Build the JsonArray directly instead.
        var array = new System.Text.Json.Nodes.JsonArray();
        foreach (var tool in tools)
        {
            var dict = tool.ToDictionary(format);
            var obj = new System.Text.Json.Nodes.JsonObject();
            foreach (var (key, value) in dict)
                obj[key] = AI.LlmHttp.ToNode(value);
            array.Add((JsonNode)obj);
        }

        return array.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = indented
        });
    }

    /// <summary>
    /// Registers an HTTP endpoint (e.g. GET /api/ai/tools) that serves the tool definitions JSON.
    /// </summary>
    public static ReptarApp MapAiTools(
        this ReptarApp app,
        string path = "/api/ai/tools",
        AiToolFormat format = AiToolFormat.OpenAi,
        bool onlyExplicit = true)
    {
        app.Get(path, ctx =>
        {
            var json = app.GetAiToolsJson(format, onlyExplicit, indented: false);
            return Task.FromResult(ctx.JsonRaw(json));
        });

        return app;
    }

    /// <summary>
    /// Executes an AI tool call in-process against the matching Reptar route, running all middleware,
    /// route validation, and database operations without an external HTTP hop.
    /// </summary>
    public static async Task<AiToolResult> ExecuteAiToolAsync(
        this ReptarApp app,
        string toolName,
        string argumentsJson,
        Ctx? parentCtx = null,
        string? authorization = null)
    {
        var tool = app.ResolveAiTool(toolName);

        if (tool is null)
        {
            return AiToolResult.Failure(404, "tool_not_found", $"AI tool '{toolName}' was not found.");
        }

        // Parse arguments JSON to extract path/query/body parameters
        JsonNode? rootNode = null;
        if (!string.IsNullOrWhiteSpace(argumentsJson))
        {
            try
            {
                rootNode = JsonNode.Parse(argumentsJson);
            }
            catch (JsonException ex)
            {
                return AiToolResult.Failure(400, "invalid_json", $"Arguments JSON is invalid: {ex.Message}");
            }
        }

        var (_, pathParams) = OpenApiGenerator.TranslatePath(tool.Path);
        var resolvedPath = tool.Path;
        var queryParams = new List<string>();
        var rootObj = rootNode as JsonObject;

        // Models routinely send numbers and booleans as strings ("wait_ms": "10000"). Coerce them
        // against the tool's own schema before dispatch, so a route that binds an int does not
        // fail on a value the model meant perfectly well. Schema-guided, not blanket: a string
        // field whose value happens to be numeric must stay a string.
        if (rootObj is not null)
            CoerceToSchema(rootObj, tool.Parameters);

        // Replace route path parameters with values from arguments
        foreach (var param in pathParams)
        {
            var placeholder = ":" + param;
            string? paramValue = null;

            if (rootObj is not null && rootObj.TryGetPropertyValue(param, out var nodeVal) && nodeVal is not null)
            {
                paramValue = nodeVal.ToString();
                rootObj.Remove(param); // Remove path param from request body
            }

            if (!string.IsNullOrEmpty(paramValue))
            {
                resolvedPath = resolvedPath.Replace(placeholder, Uri.EscapeDataString(paramValue));
            }
        }

        byte[]? bodyBytes = null;
        if (tool.Method is "GET" or "HEAD")
        {
            // For GET requests, convert remaining arguments to query parameters
            if (rootObj is not null)
            {
                foreach (var kvp in rootObj)
                {
                    if (kvp.Value is not null)
                    {
                        queryParams.Add($"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value.ToString())}");
                    }
                }
            }
        }
        else
        {
            // For POST/PUT/PATCH/DELETE, pass remaining arguments as JSON body
            if (rootObj is not null && rootObj.Count > 0)
            {
                bodyBytes = Encoding.UTF8.GetBytes(rootObj.ToJsonString());
            }
            else if (rootNode is not null && rootNode is not JsonObject)
            {
                bodyBytes = Encoding.UTF8.GetBytes(rootNode.ToJsonString());
            }
        }

        var queryString = queryParams.Count > 0 ? string.Join("&", queryParams) : "";
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["content-type"] = "application/json"
        };

        if (parentCtx is not null)
        {
            foreach (var (k, v) in parentCtx.Req.Headers)
            {
                headers[k] = v;
            }
        }

        if (!string.IsNullOrEmpty(authorization))
        {
            headers["authorization"] = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? authorization
                : "Bearer " + authorization;
        }

        var request = new ReptarRequest
        {
            Method = tool.Method,
            Path = resolvedPath,
            Query = queryString,
            Headers = headers,
            Body = bodyBytes
        };

        var response = await app.HandleAsync(request);
        var bodyString = response.Body is not null
            ? Encoding.UTF8.GetString(response.Body)
            : string.Empty;

        var isSuccess = response.Status is >= 200 and < 300;
        if (isSuccess)
        {
            return AiToolResult.Ok(response.Status, bodyString);
        }

        // Try parsing structured validation errors if returned
        string errorCode = "error";
        string errorMessage = bodyString;
        List<ValidationIssue>? issues = null;

        try
        {
            using var doc = JsonDocument.Parse(bodyString);
            if (doc.RootElement.TryGetProperty("code", out var codeEl))
                errorCode = codeEl.GetString() ?? errorCode;
            if (doc.RootElement.TryGetProperty("message", out var msgEl))
                errorMessage = msgEl.GetString() ?? errorMessage;

            if (doc.RootElement.TryGetProperty("issues", out var issuesEl) && issuesEl.ValueKind == JsonValueKind.Array)
            {
                issues = new List<ValidationIssue>();
                foreach (var item in issuesEl.EnumerateArray())
                {
                    string[]? pathArray = null;
                    if (item.TryGetProperty("path", out var pe))
                    {
                        if (pe.ValueKind == JsonValueKind.Array)
                        {
                            pathArray = pe.EnumerateArray()
                                .Select(x => x.GetString() ?? "")
                                .Where(x => !string.IsNullOrEmpty(x))
                                .ToArray();
                        }
                        else if (pe.ValueKind == JsonValueKind.String)
                        {
                            pathArray = [pe.GetString() ?? ""];
                        }
                    }

                    var m = item.TryGetProperty("message", out var me) ? me.GetString() ?? "" : "";
                    issues.Add(new ValidationIssue(m, pathArray));
                }
            }
        }
        catch
        {
            // Non-JSON error body fallback
        }

        return AiToolResult.Failure(response.Status, errorCode, errorMessage, issues);
    }

        // Tool definitions are static after startup, so each app caches a
    // case-insensitive index. ConditionalWeakTable keeps test apps
    // collectable and avoids adding mutable state to ReptarApp itself.
    private sealed class ToolIndexBox
    {
        public Dictionary<string, AiToolDefinition> Index = [];
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ReptarApp, ToolIndexBox>
        ToolIndexCache = new();

    /// <summary>
    /// Resolves a tool definition by name. The first call builds a case-insensitive
    /// index over the route table; subsequent agent-loop dispatches are dictionary
    /// hits instead of full route scans. The index rebuilds if routes change.
    /// </summary>
    public static AiToolDefinition? ResolveAiTool(this ReptarApp app, string toolName)
    {
        if (string.IsNullOrEmpty(toolName))
            return null;

        var box = ToolIndexCache.GetValue(app, static _ => new ToolIndexBox());
        if (box.Index.Count != app.RouteMetadata.Count)
        {
            var rebuilt = new Dictionary<string, AiToolDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var definition in app.GetAiTools(AiToolFormat.OpenAi, onlyExplicit: true))
                rebuilt[definition.Name] = definition;
            box.Index = rebuilt;
        }

        return box.Index.TryGetValue(toolName, out var tool) ? tool : null;
    }

    private static string GetToolName(RouteMeta route)
    {
        if (!string.IsNullOrWhiteSpace(route.AiToolName))
            return route.AiToolName!;

        if (!string.IsNullOrWhiteSpace(route.Name))
            return ToSnakeCase(route.Name!);

        // Derive semantic snake_case tool name from method + path
        // e.g. POST /api/tasks -> create_task
        //      GET  /api/tasks/:id -> get_task
        //      PUT  /api/tasks/:id -> update_task
        //      DELETE /api/tasks/:id -> delete_task
        var segments = route.Path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var cleanSegments = segments
            .Where(s => !s.StartsWith(':') && !s.Equals("api", StringComparison.OrdinalIgnoreCase) && !s.Equals("v1", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var entity = cleanSegments.Count > 0 ? cleanSegments[^1] : "item";
        // singularize simple plurals
        if (entity.EndsWith("s", StringComparison.OrdinalIgnoreCase) && entity.Length > 1)
            entity = entity[..^1];

        var action = route.Method.ToUpperInvariant() switch
        {
            "POST" => "create",
            "GET" => segments.Any(s => s.StartsWith(':')) ? "get" : "list",
            "PUT" => "update",
            "PATCH" => "patch",
            "DELETE" => "delete",
            _ => route.Method.ToLowerInvariant()
        };

        return $"{action}_{entity}";
    }

    private static Dictionary<string, object?> BuildParametersSchema(RouteMeta route, List<string> pathParams)
    {
        var schema = new Dictionary<string, object?>
        {
            ["type"] = "object"
        };

        // An explicitly declared schema wins: the route parses its own body, so there is no
        // input type to derive properties from.
        if (route.AiToolParameters is { } declared)
        {
            var merged = new Dictionary<string, object?>(declared, StringComparer.Ordinal);

            if (pathParams.Count > 0)
            {
                var declaredProps = declared.TryGetValue("properties", out var existing) &&
                                    existing is Dictionary<string, object?> map
                    ? new Dictionary<string, object?>(map, StringComparer.Ordinal)
                    : new Dictionary<string, object?>(StringComparer.Ordinal);

                foreach (var p in pathParams)
                {
                    declaredProps[p] = new Dictionary<string, object?>
                    {
                        ["type"] = "string",
                        ["description"] = $"Route parameter {p}"
                    };
                }

                merged["properties"] = declaredProps;
            }

            return merged;
        }

        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        var required = new List<string>();

        // Add path parameters
        foreach (var p in pathParams)
        {
            properties[p] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = $"Route parameter {p}"
            };
            required.Add(p);
        }

        // Add validator / input type schema
        if (route.Validator is IValidatorMeta validatorMeta && route.InputType is not null)
        {
            var bodySchema = OpenApiGenerator.GenerateValidatorSchema(validatorMeta, route.InputType);
            if (bodySchema.TryGetValue("properties", out var propsObj) && propsObj is Dictionary<string, object?> bodyProps)
            {
                foreach (var (k, v) in bodyProps)
                {
                    properties[k] = v;
                }
            }

            if (bodySchema.TryGetValue("required", out var reqObj) && reqObj is List<string> bodyReq)
            {
                foreach (var r in bodyReq)
                {
                    if (!required.Contains(r))
                        required.Add(r);
                }
            }
        }
        else if (route.InputType is not null)
        {
            var bodySchema = OpenApiGenerator.GenerateTypeSchema(route.InputType);
            if (bodySchema.TryGetValue("properties", out var propsObj) && propsObj is Dictionary<string, object?> bodyProps)
            {
                foreach (var (k, v) in bodyProps)
                {
                    properties[k] = v;
                }
            }
        }

        schema["properties"] = properties;
        if (required.Count > 0)
            schema["required"] = required;

        return schema;
    }

    private static string ToSnakeCase(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var sb = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsUpper(c))
            {
                if (i > 0) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
#endif
