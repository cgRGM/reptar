#if !NETWASM
using System.Text;
using System.Text.Json;

namespace Reptar.AI;

/// <summary>
/// Outcome of a typed generation run: the parsed value when validation
/// succeeded, plus retry diagnostics either way.
/// </summary>
public sealed record StructuredOutputResult<T>(
    T? Value,
    string RawText,
    bool Succeeded,
    int Attempts,
    IReadOnlyList<ValidationIssue> Issues,
    IReadOnlyList<ChatMessage> History) where T : class;

/// <summary>
/// Notification delivered before each automatic retry so callers can log,
/// meter, or debug the correction loop.
/// </summary>
public sealed record StructuredOutputRetry(
    string RawResponse,
    IReadOnlyList<ValidationIssue> Issues,
    int AttemptNumber);

/// <summary>
/// Structured output with typed validation and an automatic retry loop.
///
/// The same <see cref="Validator{T>"/> that guards inbound routes is the single
/// source of truth for both the JSON shape described to the model and the
/// validation applied to its reply. When the model returns malformed JSON or a
/// payload that fails <c>Rule&lt;T&gt;</c>, the validation issues are appended to the
/// conversation as feedback and the model is asked again, up to
/// <paramref name="maxRetries"/> times.
///
/// AOT notes: the schema prompt is rendered from <see cref="IValidatorMeta"/>
/// (materialized when the validator is constructed — no runtime type inspection
/// in this path). Response parsing uses <c>JsonSerializer</c> with the passed
/// <c>jsonOptions</c>; for Native AOT / NetWasm pass options backed by a
/// source-generated <c>JsonSerializerContext</c>. The default
/// <see cref="ReptarJson.Options"/> works in the dev host.
/// </summary>
public static class AgentStructuredOutput
{
    private const string DefaultSystemPrompt = """
        You are a structured data extraction engine.
        Respond with a single JSON object and nothing else — no markdown, no code fences, no commentary.
        The JSON object must conform to this JSON Schema:

        {schema}

        Every schema constraint is a hard requirement. Violating any of them
        makes your reply useless. Return ONLY the JSON object.
        """;

    private const string RetryFeedbackPrompt = """
        Your previous reply was rejected. It was:

        {response}

        It failed validation with these issues:

        {issues}

        Respond again with ONLY a corrected JSON object that satisfies every
        schema constraint. No markdown, no code fences, no commentary.
        """;

    /// <summary>
    /// Runs a typed generation against the model with a fresh conversation.
    /// </summary>
    public static Task<StructuredOutputResult<T>> GenerateAsync<T>(
        this ILlmClient client,
        string prompt,
        Validator<T> validator,
        int maxRetries = 2,
        string? systemPrompt = null,
        JsonSerializerOptions? jsonOptions = null,
        Action<StructuredOutputRetry>? onRetry = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var history = new List<ChatMessage>();
        return client.GenerateAsync(
            history,
            prompt,
            validator,
            maxRetries,
            systemPrompt,
            jsonOptions,
            onRetry,
            cancellationToken);
    }

    /// <summary>
    /// Runs a typed generation, appending the whole exchange (schema system
    /// message, prompt, model replies, validation feedback) to
    /// <paramref name="history"/> so it can be persisted into an agent session.
    /// </summary>
    public static async Task<StructuredOutputResult<T>> GenerateAsync<T>(
        this ILlmClient client,
        List<ChatMessage> history,
        string prompt,
        Validator<T> validator,
        int maxRetries = 2,
        string? systemPrompt = null,
        JsonSerializerOptions? jsonOptions = null,
        Action<StructuredOutputRetry>? onRetry = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var options = jsonOptions ?? ReptarJson.Options;
        var schemaJson = RenderSchema(validator);
        var system = (systemPrompt is null ? DefaultSystemPrompt : systemPrompt)
            .Replace("{schema}", schemaJson, StringComparison.Ordinal);

        history.Add(new ChatMessage("system", system));
        history.Add(new ChatMessage("user", prompt));

        var totalAttempts = Math.Max(1, maxRetries) + 1;
        var issues = new List<ValidationIssue>();
        var lastRaw = "";

        for (var attempt = 1; attempt <= totalAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lastRaw = await client.CompleteAsync(history, cancellationToken).ConfigureAwait(false);
            var value = ParseCandidate<T>(lastRaw, options, issues);

            if (value is { } parsed && issues.Count == 0)
            {
                issues.AddRange(validator.Validate(parsed));
            }

            if (value is { } valid && issues.Count == 0)
            {
                history.Add(new ChatMessage("assistant", lastRaw));
                return new StructuredOutputResult<T>(
                    valid,
                    lastRaw,
                    Succeeded: true,
                    Attempts: attempt,
                    issues,
                    history);
            }

            if (value is null && issues.Count == 0)
            {
                issues.Add(new ValidationIssue(
                    "The reply could not be parsed. Return only a JSON object matching the schema.",
                    null));
            }

            onRetry?.Invoke(new StructuredOutputRetry(lastRaw, [.. issues], attempt));
            history.Add(new ChatMessage("assistant", lastRaw));

            if (attempt < totalAttempts)
            {
                history.Add(new ChatMessage("user", BuildFeedback(lastRaw, issues)));
            }
        }

        return new StructuredOutputResult<T>(
            null,
            lastRaw,
            Succeeded: false,
            Attempts: totalAttempts,
            issues,
            history);
    }

    /// <summary>
    /// Buffered completion helper: drains the provider stream into a single
    /// text response. Throws if the provider signals a failure.
    /// </summary>
    public static async Task<string> CompleteAsync(
        this ILlmClient client,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        var builder = new StringBuilder();

        await foreach (var streamEvent in client.StreamAsync(messages, [], cancellationToken).ConfigureAwait(false))
        {
            switch (streamEvent)
            {
                case LlmStreamEvent.TextDelta text:
                    builder.Append(text.Text);
                    break;
                case LlmStreamEvent.Failed failed:
                    throw new LlmException(502, $"LLM stream failed: {failed.Message}");
                default:
                    break;
            }
        }

        return builder.ToString();
    }

    // ── Internals ────────────────────────────────────────────────────────────

    private static T? ParseCandidate<T>(
        string raw,
        JsonSerializerOptions options,
        List<ValidationIssue> issues) where T : class
    {
        issues.Clear();

        try
        {
            return JsonSerializer.Deserialize<T>(ExtractJson(raw), options);
        }
        catch (JsonException ex)
        {
            issues.Add(new ValidationIssue(
                $"The reply was not valid JSON ({ex.Message}). Return only a JSON object matching the schema.",
                null));
            return null;
        }
    }

    /// <summary>
    /// Renders the validator as the JSON Schema fragment embedded in the system
    /// prompt. Uses the same generator the OpenAPI export uses, so the model
    /// sees exactly what the API surface advertises.
    /// </summary>
    private static string RenderSchema<T>(Validator<T> validator) where T : class
    {
        var schema = validator.Properties.Count > 0
            ? Reptar.OpenApiGenerator.GenerateValidatorSchema(validator, typeof(T))
            : Reptar.OpenApiGenerator.GenerateTypeSchema(typeof(T));

        var node = LlmHttp.ToNode(schema);
        return node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "{}";
    }

    private static string BuildFeedback(string rawResponse, IReadOnlyList<ValidationIssue> issues)
    {
        var issuesText = new StringBuilder();
        for (var index = 0; index < issues.Count; index++)
        {
            var issue = issues[index];
            issuesText.Append("  - ");
            if (issue.Path is { Length: > 0 } path)
            {
                issuesText.Append(string.Join("/", path)).Append(": ");
            }
            issuesText.AppendLine(issue.Message);
        }

        return RetryFeedbackPrompt
            .Replace("{response}", rawResponse, StringComparison.Ordinal)
            .Replace("{issues}", issuesText.ToString().TrimEnd(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Extracts the JSON payload from a model reply, tolerating markdown code
    /// fences and short preambles or postambles around the object.
    /// </summary>
    private static string ExtractJson(string text)
    {
        var trimmed = text.Trim();

        // Strip ``` / ```json fences.
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLineEnd = trimmed.IndexOf('\n');
            if (firstLineEnd > 0)
            {
                trimmed = trimmed[(firstLineEnd + 1)..].TrimStart();
            }

            var fenceClose = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (fenceClose >= 0)
            {
                trimmed = trimmed[..fenceClose].TrimEnd();
            }
        }

        // Tolerate prose around the object: keep the outermost braces.
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            trimmed = trimmed[start..(end + 1)];
        }

        return trimmed;
    }
}
#endif
