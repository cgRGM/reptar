using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Reptar;
using static Reptar.ReptarApi;

namespace Reptar.AI.Gateway
{
public static class AiGateway
{
    // ── Wire protocol constants ──────────────────────────────────────────────

    private const string ContentTypeSse = "text/event-stream; charset=utf-8";
    private const string ContentTypeDataStream = "text/plain; charset=utf-8";

    // ── Prompt cache (SWR-style: TTL + bounded entries) ─────────────────────

    private sealed record PromptCacheEntry(NormalizedCompletion Completion, string Provider, long CreatedTicks);

    private static readonly ConcurrentDictionary<string, PromptCacheEntry> PromptCache =
        new(StringComparer.Ordinal);

    // G12 — stream continuation tokens → prompt-cache key, so an interrupted
    // client can reconnect (?continuation=... + Last-Event-ID / ?last_event=)
    // and resume the same cached completion from the frame after the last
    // one it received.
    private static readonly ConcurrentDictionary<string, string> Continuations =
        new(StringComparer.Ordinal);

    // G13 — resolved A2A agent cards: endpoint → message-send URL (cached per
    // gateway instance).
    private static readonly ConcurrentDictionary<string, string> A2aAgentUrls =
        new(StringComparer.Ordinal);

    private static readonly object CacheEvictionLock = new();

    private static int CacheTtlSeconds(IEnvironment env) =>
        ParseInt(env.Get("GATEWAY_CACHE_TTL_SECONDS"), 120);

    private static int CacheMaxEntries(IEnvironment env) =>
        ParseInt(env.Get("GATEWAY_CACHE_MAX_ENTRIES"), 4_096);

    // ── Provider telemetry SQL (stable constant, group-commit friendly) ──────

    private const string LogInsertSql =
        "INSERT INTO \"gateway_logs\" " +
        "(\"id\", \"model\", \"prompt_tokens\", \"completion_tokens\", \"latency_ms\", \"cached\") " +
        "VALUES (@id, @model, @prompt_tokens, @completion_tokens, @latency_ms, @cached)";

    private const string StatsSql =
        "SELECT COUNT(*) AS \"TotalRequests\", " +
        "COALESCE(SUM(\"prompt_tokens\"), 0) AS \"PromptTokens\", " +
        "COALESCE(SUM(\"completion_tokens\"), 0) AS \"CompletionTokens\", " +
        "COALESCE(AVG(\"latency_ms\"), 0) AS \"AvgLatencyMs\", " +
        "COALESCE(SUM(CASE WHEN \"cached\" = '1' THEN 1 ELSE 0 END), 0) AS \"CacheHits\" " +
        "FROM \"gateway_logs\"";

    private sealed record GatewayStatsRow(
        long TotalRequests,
        long PromptTokens,
        long CompletionTokens,
        double AvgLatencyMs,
        long CacheHits);

    // ── Route registration ───────────────────────────────────────────────────

    public static void Configure(ReptarApp app)
    {
        app.Get("/health", ctx =>
            ReptarResponse.Json(JsonBytes(writer =>
            {
                writer.WriteStartObject();
                writer.WriteBoolean("ok", true);
                writer.WriteString("service", "reptar-ai-gateway");
                writer.WriteNumber("uptime_seconds", (long)(DateTimeOffset.UtcNow - ProcessStartTime).TotalSeconds);
                writer.WriteEndObject();
            })));

        app.Get("/v1/models", ctx =>
            ReptarResponse.Json(JsonBytes(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("object", "list");
                writer.WriteStartArray("data");
                foreach (var model in ModelCatalog)
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", model);
                    writer.WriteString("object", "model");
                    writer.WriteString("owned_by", "reptar-gateway");
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            })));

        // The universal proxy endpoint. OpenAI-compatible in, OpenAI-compatible
        // SSE / Vercel Data Stream / buffered JSON out, with routing, failover,
        // caching, and telemetry.
        app.Post("/v1/chat/completions", ChatCompletionsAsync);

        app.Get("/gateway/stats", async ctx =>
        {
            var row = await ctx.Db.QuerySingleOrDefaultAsync<GatewayStatsRow>(StatsSql);
            return ReptarResponse.Json(JsonBytes(writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("total_requests", row?.TotalRequests ?? 0);
                writer.WriteNumber("prompt_tokens", row?.PromptTokens ?? 0);
                writer.WriteNumber("completion_tokens", row?.CompletionTokens ?? 0);
                writer.WriteNumber("cache_hits", row?.CacheHits ?? 0);
                writer.WriteNumber("cache_hit_rate", row is { TotalRequests: > 0 }
                    ? Math.Round(row.CacheHits * 100.0 / row.TotalRequests, 2)
                    : 0);
                writer.WriteNumber("avg_latency_ms", Math.Round(row?.AvgLatencyMs ?? 0, 3));
                writer.WriteNumber("prompt_cache_entries", PromptCache.Count);
                writer.WriteEndObject();
            }));
        });

        // Demo/test helper: wipe the prompt cache and the telemetry table.
        app.Post("/gateway/reset", async ctx =>
        {
            PromptCache.Clear();
            await ctx.Db.DeleteWhereAsync(GatewaySchema.GatewayLogs, "1=1");
            return ReptarResponse.Json(JsonBytes(writer =>
            {
                writer.WriteStartObject();
                writer.WriteBoolean("ok", true);
                writer.WriteEndObject();
            }));
        });
    }

    // ── Main handler ─────────────────────────────────────────────────────────

    private static async Task<ReptarResponse> ChatCompletionsAsync(Ctx ctx)
    {
        var started = Stopwatch.GetTimestamp();
        var env = ctx.Environment;

        var body = ctx.Req.BodyText;
        if (string.IsNullOrWhiteSpace(body))
            return OpenAiError(400, "invalid_request_error", "Empty request body.");

        ParsedRequest request;
        try
        {
            request = ParseRequest(body);
        }
        catch (JsonException)
        {
            return OpenAiError(400, "invalid_request_error", "Request body is not valid JSON.");
        }

        // G12 — stream resume: ?continuation=<token> replays the cached
        // completion from frame (Last-Event-ID + 1), letting interrupted
        // clients reconnect without re-paying the upstream token cost.
        var continuationToken = ctx.Req.QueryValue("continuation");
        if (continuationToken is not null)
        {
            if (!Continuations.TryGetValue(continuationToken, out var resumeKey) ||
                !TryGetCache(resumeKey, env, out var resumeEntry))
            {
                return OpenAiError(404, "invalid_request_error",
                    "Unknown or expired continuation token.");
            }

            var lastEvent = ParseInt(
                ctx.Req.Header("last-event-id") ?? ctx.Req.QueryValue("last_event"), 0);
            var resumeRequest = new ParsedRequest(
                string.IsNullOrWhiteSpace(request.Model) ? "resumed" : request.Model,
                [], null, null, true, "[]");

            ctx.SetHeader("X-Reptar-Cache", "HIT");
            ctx.SetHeader("X-Reptar-Provider", resumeEntry.Provider);
            ctx.SetHeader("X-Reptar-Continuation", continuationToken);
            _ = LogAsync(ctx, resumeRequest, resumeEntry.Completion, started, cached: true);

            var resumeProtocol = ResolveProtocol(ctx, wantsStream: true);
            return Respond(
                ctx, resumeRequest, resumeProtocol, resumeEntry.Completion,
                resumeEntry.Provider, tokenDelayMs: 0,
                cacheKey: resumeKey, skipFrames: lastEvent + 1);
        }

        if (request.Messages.Count == 0)
            return OpenAiError(400, "invalid_request_error", "'messages' must be a non-empty array.");

        if (string.IsNullOrWhiteSpace(request.Model))
            return OpenAiError(400, "invalid_request_error", "'model' is required.");

        // ── Optional model allowlist (cost control) ───────────────────────────
        // GATEWAY_ALLOWED_MODELS="gpt-4o-mini,llama-3.3-70b-versatile" — when
        // set, completions for any other model are rejected instead of being
        // routed (and billed) to providers with server-held credentials.
        var allowedModels = env.Get("GATEWAY_ALLOWED_MODELS");
        if (!string.IsNullOrWhiteSpace(allowedModels))
        {
            var permitted = allowedModels.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (!permitted.Contains(request.Model, StringComparer.Ordinal))
            {
                return OpenAiError(
                    400,
                    "invalid_request_error",
                    $"'{request.Model}' is not in GATEWAY_ALLOWED_MODELS.");
            }
        }

        var protocol = ResolveProtocol(ctx, request.Stream);
        var bypassCache = ctx.Req.QueryValue("no_cache") == "1" ||
                          ctx.Req.QueryValue("cache") == "0";

        // ── Prompt cache: SHA-256(model + canonical messages JSON) ───────────
        var cacheKey = await ctx.Crypto.Sha256(
            request.Model + "\n" + request.CanonicalMessagesJson);

        if (!bypassCache && TryGetCache(cacheKey, env, out var cached))
        {
            ctx.SetHeader("X-Reptar-Cache", "HIT");
            ctx.SetHeader("X-Reptar-Provider", cached.Provider);
            _ = LogAsync(ctx, request, cached.Completion, started, cached: true);
            return Respond(ctx, request, protocol, cached.Completion, cached.Provider,
                tokenDelayMs: 0, cacheKey: cacheKey);
        }

        // ── Provider routing + automatic failover (same HTTP connection) ─────
        var chain = BuildProviderChain(request.Model, env);
        NormalizedCompletion? completion = null;
        var usedProvider = "";
        var attempts = new List<string>();

        foreach (var provider in chain)
        {
            if (provider == "mock")
            {
                completion = MockComplete(request, env);
                usedProvider = "mock";
                attempts.Add("mock:200");
                break;
            }

            if (provider == "workers-ai")
            {
                try
                {
                    var prompt = FlattenPrompt(request);
                    var result = await ctx.AI.GenerateAsync(new AiRequest(request.Model, prompt));
                    var text = result.Text ?? "";
                    completion = new NormalizedCompletion(
                        Tokenize(text),
                        [],
                        EstimateTokens(prompt),
                        EstimateTokens(text),
                        "stop");
                    usedProvider = "workers-ai";
                    attempts.Add("workers-ai:200");
                    break;
                }
                catch (Exception)
                {
                    attempts.Add("workers-ai:error");
                    continue;
                }
            }

            // G13 — A2A provider: agents as upstreams. Route with
            // model "a2a/<agent-url>"; the agent card is resolved once,
            // the message sent via JSON-RPC, and long-running tasks polled
            // until completion. Card/poll failures fail over like any other
            // provider.
            if (provider == "a2a")
            {
                try
                {
                    var a2aText = await A2aCompleteAsync(
                        ctx.Http, UpstreamModel(request.Model), request, env);
                    var promptText = FlattenPrompt(request);
                    completion = new NormalizedCompletion(
                        Tokenize(a2aText),
                        [],
                        EstimateTokens(promptText),
                        EstimateTokens(a2aText),
                        "stop");
                    usedProvider = "a2a";
                    attempts.Add("a2a:200");
                    break;
                }
                catch (A2aException ex)
                {
                    attempts.Add($"a2a:{ex.Message}");
                    continue;
                }
                catch (Exception)
                {
                    attempts.Add("a2a:error");
                    continue;
                }
            }

            ProviderCall call;
            try
            {
                call = BuildCall(provider, request, env);
            }
            catch (InvalidOperationException ex)
            {
                // Missing API key / misconfigured provider: record and fail over.
                attempts.Add($"{provider}:config({ex.Message})");
                continue;
            }

            try
            {
                var response = await ctx.Http.FetchAsync(call.Url, "POST", call.Headers, call.Body);
                attempts.Add($"{provider}:{response.Status}");

                if (response.Status == 429 || response.Status >= 500)
                    continue;

                if (response.Status != 200)
                    return OpenAiError(502, "upstream_error",
                        $"Upstream provider '{provider}' returned {response.Status}.", attempts);

                var parsed = ParseUpstream(provider, response.Body);
                if (parsed is null)
                {
                    attempts.Add($"{provider}:unparsable");
                    continue;
                }

                completion = parsed;
                usedProvider = provider;
                break;
            }
            catch (Exception)
            {
                attempts.Add($"{provider}:error");
            }
        }

        if (completion is null)
            return OpenAiError(502, "upstream_error",
                "All providers failed or returned retryable errors.", attempts);

        ctx.SetHeader("X-Reptar-Cache", "MISS");
        ctx.SetHeader("X-Reptar-Provider", usedProvider);
        StoreCache(cacheKey, completion, usedProvider, env);
        _ = LogAsync(ctx, request, completion, started, cached: false);

        return Respond(ctx, request, protocol, completion, usedProvider,
            TokenDelayMs(env), cacheKey: cacheKey);
    }

    // ── Response emission ─────────────────────────────────────────────────────

    private static ReptarResponse Respond(
        Ctx ctx,
        ParsedRequest request,
        string protocol,
        NormalizedCompletion completion,
        string provider,
        int tokenDelayMs,
        string? cacheKey = null,
        int skipFrames = 0)
    {
        if (protocol == "json")
            return BufferedCompletion(request, completion);

        var id = "chatcmpl-" + Ids.New("gw").ToLowerInvariant();
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var (contentType, frames) = protocol == "data-stream"
            ? (ContentTypeDataStream, BuildDataStreamFrames(completion))
            : (ContentTypeSse, BuildSseFrames(id, request.Model, created, completion));

        // G12 — hand the client a continuation token so a dropped connection
        // can resume from the cached completion (SSE Last-Event-ID semantics).
        if (cacheKey is not null)
        {
            var continuation = Ids.New("cont").ToLowerInvariant();
            Continuations[continuation] = cacheKey;
            ctx.SetHeader("X-Reptar-Continuation", continuation);
        }

        var response = ctx.Stream(
            cancellationToken => EmitFramesAsync(frames, tokenDelayMs, skipFrames, cancellationToken),
            contentType);

        response.Headers["Cache-Control"] = "no-cache, no-transform";
        response.Headers["X-Accel-Buffering"] = "no";
        return response;
    }

    private static ReptarResponse BufferedCompletion(ParsedRequest request, NormalizedCompletion completion)
    {
        var id = "chatcmpl-" + Ids.New("gw").ToLowerInvariant();
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var text = string.Concat(completion.TextTokens);

        var body = JsonBytes(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("id", id);
            writer.WriteString("object", "chat.completion");
            writer.WriteNumber("created", created);
            writer.WriteString("model", request.Model);
            writer.WriteStartArray("choices");
            writer.WriteStartObject();
            writer.WriteNumber("index", 0);
            writer.WriteStartObject("message");
            writer.WriteString("role", "assistant");
            writer.WriteString("content", text);
            if (completion.ToolCalls.Count > 0)
            {
                writer.WriteStartArray("tool_calls");
                foreach (var toolCall in completion.ToolCalls)
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", toolCall.Id);
                    writer.WriteString("type", "function");
                    writer.WriteStartObject("function");
                    writer.WriteString("name", toolCall.Name);
                    writer.WriteString("arguments", toolCall.ArgsJson);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
            writer.WriteString("finish_reason", completion.FinishReason);
            writer.WriteEndObject();
            writer.WriteEndArray();
            WriteUsage(writer, completion);
            writer.WriteEndObject();
        });

        return ReptarResponse.Json(body);
    }

    private static async IAsyncEnumerable<byte[]> EmitFramesAsync(
        List<byte[]> frames,
        int tokenDelayMs,
        int skipFrames,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (var index = skipFrames; index < frames.Count; index++)
        {
            if (tokenDelayMs > 0)
                await Task.Delay(tokenDelayMs, cancellationToken);
            yield return frames[index];
        }
    }

    // ── Wire protocol frame builders ─────────────────────────────────────────

    private static List<byte[]> BuildSseFrames(
        string id,
        string model,
        long created,
        NormalizedCompletion completion)
    {
        var frames = new List<byte[]>(completion.TextTokens.Count + completion.ToolCalls.Count + 4);

        foreach (var token in completion.TextTokens)
        {
            frames.Add(Encoding.UTF8.GetBytes(
                "data: {\"id\":" + JsonEscape(id) +
                ",\"object\":\"chat.completion.chunk\"" +
                ",\"created\":" + created +
                ",\"model\":" + JsonEscape(model) +
                ",\"choices\":[{\"index\":0,\"delta\":{\"content\":" +
                JsonEscape(token) + "},\"finish_reason\":null}]}\n\n"));
        }

        for (var index = 0; index < completion.ToolCalls.Count; index++)
        {
            var toolCall = completion.ToolCalls[index];
            frames.Add(Encoding.UTF8.GetBytes(
                "data: {\"id\":" + JsonEscape(id) +
                ",\"object\":\"chat.completion.chunk\"" +
                ",\"created\":" + created +
                ",\"model\":" + JsonEscape(model) +
                ",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{" +
                "\"index\":" + index +
                ",\"id\":" + JsonEscape(toolCall.Id) +
                ",\"type\":\"function\"," +
                "\"function\":{\"name\":" + JsonEscape(toolCall.Name) +
                ",\"arguments\":" + JsonEscape(toolCall.ArgsJson) +
                "}}]},\"finish_reason\":null}]}\n\n"));
        }

        frames.Add(Encoding.UTF8.GetBytes(
            "data: {\"id\":" + JsonEscape(id) +
            ",\"object\":\"chat.completion.chunk\"" +
            ",\"created\":" + created +
            ",\"model\":" + JsonEscape(model) +
            ",\"choices\":[{\"index\":0,\"delta\":{}," +
            "\"finish_reason\":" + JsonEscape(completion.FinishReason) + "}]}\n\n"));

        frames.Add(Encoding.UTF8.GetBytes(
            "data: {\"id\":" + JsonEscape(id) +
            ",\"object\":\"chat.completion.chunk\"" +
            ",\"created\":" + created +
            ",\"model\":" + JsonEscape(model) +
            ",\"choices\":[]," +
            "\"usage\":{\"prompt_tokens\":" + completion.PromptTokens +
            ",\"completion_tokens\":" + completion.CompletionTokens +
            ",\"total_tokens\":" + (completion.PromptTokens + completion.CompletionTokens) +
            "}}\n\n"));

        frames.Add(Encoding.UTF8.GetBytes("data: [DONE]\n\n"));

        // G12 — SSE event ids on every frame so an interrupted client can
        // resume with Last-Event-ID (the frame index IS the event id).
        for (var index = 0; index < frames.Count; index++)
        {
            frames[index] = Encoding.UTF8.GetBytes(
                $"id: {index}\n" + Encoding.UTF8.GetString(frames[index]));
        }

        return frames;
    }

    // Vercel AI SDK Data Stream Protocol:
    //   0:"text"                          → text delta
    //   9:{"toolCallId","toolName","args"} → tool invocation
    //   d:{"finishReason","usage"}         → finish + usage
    private static List<byte[]> BuildDataStreamFrames(NormalizedCompletion completion)
    {
        var frames = new List<byte[]>(completion.TextTokens.Count + completion.ToolCalls.Count + 1);

        foreach (var token in completion.TextTokens)
        {
            frames.Add(Encoding.UTF8.GetBytes(
                "0:" + JsonEscape(token) + "\n"));
        }

        foreach (var toolCall in completion.ToolCalls)
        {
            frames.Add(Encoding.UTF8.GetBytes(
                "9:{\"toolCallId\":" + JsonEscape(toolCall.Id) +
                ",\"toolName\":" + JsonEscape(toolCall.Name) +
                ",\"args\":" + (toolCall.ArgsJson.Length == 0 ? "{}" : toolCall.ArgsJson) +
                "}\n"));
        }

        frames.Add(Encoding.UTF8.GetBytes(
            "d:{\"finishReason\":" + JsonEscape(completion.FinishReason) +
            ",\"usage\":{\"promptTokens\":" + completion.PromptTokens +
            ",\"completionTokens\":" + completion.CompletionTokens + "}}\n"));

        return frames;
    }

    // ── Wire protocol resolution (Accept header or stream_protocol query) ────

    private static string ResolveProtocol(Ctx ctx, bool wantsStream)
    {
        if (!wantsStream)
            return "json";

        var queryProtocol = ctx.Req.QueryValue("stream_protocol");
        if (queryProtocol is "data-stream" or "ai" or "vercel")
            return "data-stream";
        if (queryProtocol is "sse" or "openai")
            return "sse";

        var accept = ctx.Req.Header("accept") ?? "";
        if (accept.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
            return "sse";
        if (accept.Contains("ai-stream") ||
            accept.Contains("data-stream") ||
            accept.Contains("application/vnd.ai", StringComparison.OrdinalIgnoreCase))
            return "data-stream";

        return "sse";
    }

    // ── Request parsing (JsonDocument — zero reflection) ─────────────────────

    private static ParsedRequest ParseRequest(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Root must be a JSON object.");

        var model = root.TryGetProperty("model", out var modelElement) &&
                    modelElement.ValueKind == JsonValueKind.String
            ? modelElement.GetString() ?? ""
            : "";

        var temperature = root.TryGetProperty("temperature", out var temperatureElement) &&
                          temperatureElement.ValueKind == JsonValueKind.Number
            ? temperatureElement.GetDouble()
            : (double?)null;

        var maxTokens = root.TryGetProperty("max_tokens", out var maxTokensElement) &&
                        maxTokensElement.ValueKind == JsonValueKind.Number
            ? temperatureElement.GetInt32()
            : (int?)null;

        var stream = root.TryGetProperty("stream", out var streamElement) &&
                     streamElement.ValueKind == JsonValueKind.True;

        var messages = new List<ChatMessage>();
        if (root.TryGetProperty("messages", out var messagesElement) &&
            messagesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var messageElement in messagesElement.EnumerateArray())
            {
                var role = messageElement.TryGetProperty("role", out var roleElement)
                    ? roleElement.GetString() ?? "user"
                    : "user";
                var content = messageElement.TryGetProperty("content", out var contentElement)
                    ? ExtractContent(contentElement)
                    : "";
                messages.Add(new ChatMessage(role, content));
            }
        }

        var canonicalJson = JsonBytes(writer =>
        {
            writer.WriteStartArray();
            foreach (var message in messages)
            {
                writer.WriteStartObject();
                writer.WriteString("role", message.Role);
                writer.WriteString("content", message.Content);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        });

        return new ParsedRequest(
            model,
            messages,
            temperature,
            maxTokens,
            stream,
            Encoding.UTF8.GetString(canonicalJson));
    }

    private static string ExtractContent(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? "",
        JsonValueKind.Array => string.Concat(element.EnumerateArray()
            .Where(part => part.ValueKind == JsonValueKind.Object &&
                           part.TryGetProperty("text", out var text) &&
                           text.ValueKind == JsonValueKind.String)
            .Select(part => part.GetProperty("text").GetString() ?? "")),
        _ => "",
    };

    private static string FlattenPrompt(ParsedRequest request) => string.Concat(
        request.Messages.Select(message => $"[{message.Role}] {message.Content}\n"));

    // ── Provider routing & failover chain ────────────────────────────────────

    private static readonly string[] ModelCatalog =
    [
        "mock-mini",
        "mock-large",
        "gpt-4o",
        "gpt-4o-mini",
        "openai/gpt-4o-mini",
        "openrouter/auto",
        "claude-sonnet-4-20250514",
        "anthropic/claude-sonnet-4-20250514",
        "gemini-2.0-flash",
        "llama-3.3-70b-versatile",
        "groq/llama-3.3-70b-versatile",
        "ollama/llama3.2",
        "a2a/https://remote-agent.example.com",
    ];

    private static List<string> BuildProviderChain(string model, IEnvironment env)
    {
        var primary = RouteProvider(model, env);

        var chain = new List<string> { primary };
        var fallbacks = env.Get("GATEWAY_FALLBACKS") ?? "mock";
        foreach (var fallback in fallbacks.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!chain.Contains(fallback))
                chain.Add(fallback);
        }

        return chain;
    }

    private static string RouteProvider(string model, IEnvironment env)
    {
        // Portkey-style explicit "provider/model" syntax wins.
        var separatorIndex = model.IndexOf('/');
        if (separatorIndex > 0)
        {
            var prefix = model[..separatorIndex];
            if (prefix is "openai" or "openrouter" or "groq" or "anthropic" or "gemini" or "ollama" or "mock" or "workers-ai" or "a2a")
                return prefix;
        }

        if (model.StartsWith("mock", StringComparison.OrdinalIgnoreCase))
            return "mock";
        if (model.StartsWith("gpt", StringComparison.OrdinalIgnoreCase) ||
            model.StartsWith("o1") || model.StartsWith("o3") || model.StartsWith("o4"))
            return "openai";
        if (model.StartsWith("claude", StringComparison.OrdinalIgnoreCase))
            return "anthropic";
        if (model.StartsWith("gemini", StringComparison.OrdinalIgnoreCase))
            return "gemini";
        if (model.StartsWith("llama", StringComparison.OrdinalIgnoreCase) ||
            model.StartsWith("mixtral", StringComparison.OrdinalIgnoreCase) ||
            model.StartsWith("qwen", StringComparison.OrdinalIgnoreCase))
            return "groq";

        return env.Get("GATEWAY_DEFAULT_PROVIDER") ?? "mock";
    }

    private static string UpstreamModel(string model) => model.Contains('/') ? model[(model.IndexOf('/') + 1)..] : model;

    // ── Upstream request translation ─────────────────────────────────────────

    private sealed record ProviderCall(string Url, Dictionary<string, string> Headers, string Body);

    private static ProviderCall BuildCall(string provider, ParsedRequest request, IEnvironment env)
    {
        switch (provider)
        {
            case "openai" or "openrouter" or "groq":
            {
                var apiKey = provider switch
                {
                    "openai" => env.Get("OPENAI_API_KEY"),
                    "openrouter" => env.Get("OPENROUTER_API_KEY"),
                    _ => env.Get("GROQ_API_KEY"),
                };
                if (string.IsNullOrWhiteSpace(apiKey))
                    throw new InvalidOperationException($"Missing API key for {provider}");

                var baseUrl = provider switch
                {
                    "openai" => env.Get("OPENAI_BASE_URL") ?? "https://api.openai.com/v1",
                    "openrouter" => env.Get("OPENROUTER_BASE_URL") ?? "https://openrouter.ai/api/v1",
                    _ => env.Get("GROQ_BASE_URL") ?? "https://api.groq.com/openai/v1",
                };

                return new ProviderCall(
                    baseUrl.TrimEnd('/') + "/chat/completions",
                    new Dictionary<string, string>
                    {
                        ["Authorization"] = $"Bearer {apiKey}",
                        ["Content-Type"] = "application/json",
                    },
                    OpenAiBody(UpstreamModel(request.Model), request, stream: false));
            }

            case "anthropic":
            {
                var apiKey = env.Get("ANTHROPIC_API_KEY");
                if (string.IsNullOrWhiteSpace(apiKey))
                    throw new InvalidOperationException("Missing API key for anthropic");

                var baseUrl = (env.Get("ANTHROPIC_BASE_URL") ?? "https://api.anthropic.com").TrimEnd('/');

                var body = JsonBytes(writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("model", UpstreamModel(request.Model));
                    writer.WriteNumber("max_tokens", request.MaxTokens ?? 1_024);

                    var system = string.Concat(request.Messages
                        .Where(message => message.Role == "system")
                        .Select(message => message.Content + "\n"));
                    if (system.Length > 0)
                        writer.WriteString("system", system.TrimEnd('\n'));

                    writer.WriteStartArray("messages");
                    foreach (var message in request.Messages)
                    {
                        if (message.Role == "system")
                            continue;
                        writer.WriteStartObject();
                        writer.WriteString("role", message.Role is "assistant" or "user" ? message.Role : "user");
                        writer.WriteString("content", message.Content);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();

                    if (request.Temperature.HasValue)
                        writer.WriteNumber("temperature", request.Temperature.Value);

                    writer.WriteEndObject();
                });

                return new ProviderCall(
                    baseUrl + "/v1/messages",
                    new Dictionary<string, string>
                    {
                        ["x-api-key"] = apiKey,
                        ["anthropic-version"] = "2023-06-01",
                        ["Content-Type"] = "application/json",
                    },
                    Encoding.UTF8.GetString(body));
            }

            case "gemini":
            {
                var apiKey = env.Get("GEMINI_API_KEY");
                if (string.IsNullOrWhiteSpace(apiKey))
                    throw new InvalidOperationException("Missing API key for gemini");

                var baseUrl = (env.Get("GEMINI_BASE_URL") ?? "https://generativelanguage.googleapis.com/v1beta").TrimEnd('/');

                var body = JsonBytes(writer =>
                {
                    writer.WriteStartObject();

                    var system = string.Concat(request.Messages
                        .Where(message => message.Role == "system")
                        .Select(message => message.Content + "\n"));
                    if (system.Length > 0)
                    {
                        writer.WriteStartObject("systemInstruction");
                        writer.WriteStartArray("parts");
                        writer.WriteStartObject();
                        writer.WriteString("text", system.TrimEnd('\n'));
                        writer.WriteEndObject();
                        writer.WriteEndArray();
                        writer.WriteEndObject();
                    }

                    writer.WriteStartArray("contents");
                    foreach (var message in request.Messages)
                    {
                        if (message.Role == "system")
                            continue;
                        writer.WriteStartObject();
                        writer.WriteString("role", message.Role == "assistant" ? "model" : "user");
                        writer.WriteStartArray("parts");
                        writer.WriteStartObject();
                        writer.WriteString("text", message.Content);
                        writer.WriteEndObject();
                        writer.WriteEndArray();
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();

                    writer.WriteStartObject("generationConfig");
                    if (request.Temperature.HasValue)
                        writer.WriteNumber("temperature", request.Temperature.Value);
                    if (request.MaxTokens.HasValue)
                        writer.WriteNumber("maxOutputTokens", request.MaxTokens.Value);
                    writer.WriteEndObject();

                    writer.WriteEndObject();
                });

                return new ProviderCall(
                    $"{baseUrl}/models/{UpstreamModel(request.Model)}:generateContent",
                    new Dictionary<string, string>
                    {
                        ["x-goog-api-key"] = apiKey,
                        ["Content-Type"] = "application/json",
                    },
                    Encoding.UTF8.GetString(body));
            }

            case "ollama":
            {
                var baseUrl = (env.Get("OLLAMA_BASE_URL") ?? "http://localhost:11434").TrimEnd('/');

                var body = JsonBytes(writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("model", UpstreamModel(request.Model));
                    writer.WriteStartArray("messages");
                    foreach (var message in request.Messages)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("role", message.Role);
                        writer.WriteString("content", message.Content);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                    writer.WriteBoolean("stream", false);

                    if (request.Temperature.HasValue)
                    {
                        writer.WriteStartObject("options");
                        writer.WriteNumber("temperature", request.Temperature.Value);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndObject();
                });

                return new ProviderCall(
                    baseUrl + "/api/chat",
                    new Dictionary<string, string> { ["Content-Type"] = "application/json" },
                    Encoding.UTF8.GetString(body));
            }

            default:
                throw new InvalidOperationException($"Unknown provider '{provider}'");
        }
    }

    private static string OpenAiBody(string model, ParsedRequest request, bool stream)
    {
        var body = JsonBytes(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("model", model);
            writer.WriteStartArray("messages");
            foreach (var message in request.Messages)
            {
                writer.WriteStartObject();
                writer.WriteString("role", message.Role);
                writer.WriteString("content", message.Content);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            if (request.Temperature.HasValue)
                writer.WriteNumber("temperature", request.Temperature.Value);
            if (request.MaxTokens.HasValue)
                writer.WriteNumber("max_tokens", request.MaxTokens.Value);
            writer.WriteBoolean("stream", stream);
            writer.WriteEndObject();
        });
        return Encoding.UTF8.GetString(body);
    }

    // ── Upstream response normalization (buffered upstream → deltas) ─────────

    private static NormalizedCompletion? ParseUpstream(string provider, string body)
    {
        try
        {
            return provider switch
            {
                "openai" or "openrouter" or "groq" => ParseOpenAi(body),
                "anthropic" => ParseAnthropic(body),
                "gemini" => ParseGemini(body),
                "ollama" => ParseOllama(body),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static NormalizedCompletion? ParseOpenAi(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        var choices = root.TryGetProperty("choices", out var choicesElement) &&
                      choicesElement.ValueKind == JsonValueKind.Array &&
                      choicesElement.GetArrayLength() > 0
            ? choicesElement[0]
            : throw new JsonException("Missing choices");

        var text = "";
        if (choices.TryGetProperty("message", out var message) &&
            message.TryGetProperty("content", out var content))
        {
            text = ExtractContent(content);
        }

        var toolCalls = new List<ToolCall>();
        if (message.TryGetProperty("tool_calls", out var toolCallsElement) &&
            toolCallsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var toolCallElement in toolCallsElement.EnumerateArray())
            {
                toolCalls.Add(new ToolCall(
                    toolCallElement.GetProperty("id").GetString() ?? "",
                    toolCallElement.GetProperty("function").GetProperty("name").GetString() ?? "",
                    toolCallElement.GetProperty("function").GetProperty("arguments").GetString() ?? "{}"));
            }
        }

        var finishReason = choices.TryGetProperty("finish_reason", out var finishElement) &&
                           finishElement.ValueKind == JsonValueKind.String
            ? finishElement.GetString() ?? "stop"
            : "stop";

        var promptTokens = 0;
        var completionTokens = 0;
        if (root.TryGetProperty("usage", out var usage))
        {
            promptTokens = ReadInt(usage, "prompt_tokens");
            completionTokens = ReadInt(usage, "completion_tokens");
        }

        if (promptTokens == 0 && text.Length > 0)
            promptTokens = EstimateTokens(string.Concat(text));
        if (completionTokens == 0)
            completionTokens = Math.Max(1, EstimateTokens(text));

        return new NormalizedCompletion(Tokenize(text), toolCalls, promptTokens, completionTokens, finishReason);
    }

    private static NormalizedCompletion? ParseAnthropic(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        var textParts = new List<string>();
        var toolCalls = new List<ToolCall>();

        if (root.TryGetProperty("content", out var content) &&
            content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                var type = block.TryGetProperty("type", out var typeElement)
                    ? typeElement.GetString()
                    : null;
                if (type == "text")
                {
                    if (block.TryGetProperty("text", out var textElement))
                        textParts.Add(textElement.GetString() ?? "");
                }
                else if (type == "tool_use")
                {
                    toolCalls.Add(new ToolCall(
                        block.GetProperty("id").GetString() ?? "",
                        block.GetProperty("name").GetString() ?? "",
                        block.TryGetProperty("input", out var input)
                            ? input.GetRawText()
                            : "{}"));
                }
            }
        }
        else
        {
            return null;
        }

        var text = string.Concat(textParts);
        var promptTokens = 0;
        var completionTokens = 0;
        if (root.TryGetProperty("usage", out var usage))
        {
            promptTokens = ReadInt(usage, "input_tokens");
            completionTokens = ReadInt(usage, "output_tokens");
        }
        if (promptTokens == 0)
            promptTokens = EstimateTokens(text);
        if (completionTokens == 0)
            completionTokens = Math.Max(1, EstimateTokens(text));

        var finishReason = root.TryGetProperty("stop_reason", out var stopReason) &&
                           stopReason.ValueKind == JsonValueKind.String
            ? stopReason.GetString() switch
            {
                "tool_use" => "tool-calls",
                _ => "stop",
            }
            : "stop";

        return new NormalizedCompletion(Tokenize(text), toolCalls, promptTokens, completionTokens, finishReason);
    }

    private static NormalizedCompletion? ParseGemini(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        var textParts = new List<string>();
        if (root.TryGetProperty("candidates", out var candidates) &&
            candidates.ValueKind == JsonValueKind.Array &&
            candidates.GetArrayLength() > 0 &&
            candidates[0].TryGetProperty("content", out var content) &&
            content.TryGetProperty("parts", out var parts) &&
            parts.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var textElement))
                    textParts.Add(textElement.GetString() ?? "");
            }
        }
        else
        {
            return null;
        }

        var text = string.Concat(textParts);
        var promptTokens = 0;
        var completionTokens = 0;
        if (root.TryGetProperty("usageMetadata", out var usage))
        {
            promptTokens = ReadInt(usage, "promptTokenCount");
            completionTokens = ReadInt(usage, "candidatesTokenCount");
        }
        if (promptTokens == 0)
            promptTokens = EstimateTokens(text);
        if (completionTokens == 0)
            completionTokens = Math.Max(1, EstimateTokens(text));

        return new NormalizedCompletion(
            Tokenize(text),
            [],
            promptTokens,
            completionTokens,
            "stop");
    }

    private static NormalizedCompletion? ParseOllama(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        if (!root.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content))
            return null;

        var text = content.GetString() ?? "";
        var promptTokens = ReadInt(root, "prompt_eval_count");
        var completionTokens = ReadInt(root, "eval_count");
        if (promptTokens == 0)
            promptTokens = EstimateTokens(text);
        if (completionTokens == 0)
            completionTokens = Math.Max(1, EstimateTokens(text));

        return new NormalizedCompletion(
            Tokenize(text),
            [],
            promptTokens,
            completionTokens,
            "stop");
    }

    // ── Mock provider (local, deterministic — the benchmark backend) ─────────

    private static readonly string[] MockWordPool =
    [
        "The", "Reptar", "gateway", "streams", "tokens", "with", "zero", "overhead,",
        "routing", "every", "request", "through", "failover", "chains", "and", "SHA-256",
        "prompt", "caches", "at", "sub-millisecond", "latency.",
    ];

    private static NormalizedCompletion MockComplete(ParsedRequest request, IEnvironment env)
    {
        var tokenCount = ParseInt(env.Get("GATEWAY_MOCK_TOKENS"), 48);

        var tokens = new List<string>(tokenCount);
        for (var index = 0; index < tokenCount; index++)
            tokens.Add(MockWordPool[index % MockWordPool.Length] + (index == tokenCount - 1 ? "" : " "));

        var promptTokens = EstimateTokens(request.CanonicalMessagesJson);
        return new NormalizedCompletion(tokens, [], promptTokens, tokenCount, "stop");
    }

    private static int TokenDelayMs(IEnvironment env) =>
        ParseInt(env.Get("GATEWAY_TOKEN_DELAY_MS"), 0);

    // ── Prompt cache (SWR-style: TTL freshness + bounded eviction) ────────────

    private static bool TryGetCache(string key, IEnvironment env, out PromptCacheEntry entry)
    {
        if (PromptCache.TryGetValue(key, out entry!))
        {
            var ageSeconds = (Stopwatch.GetTimestamp() - entry.CreatedTicks) / (double)Stopwatch.Frequency;
            if (ageSeconds < CacheTtlSeconds(env))
                return true;

            PromptCache.TryRemove(key, out _);
        }

        return false;
    }

    private static void StoreCache(string key, NormalizedCompletion completion, string provider, IEnvironment env)
    {
        var maxEntries = CacheMaxEntries(env);
        if (PromptCache.Count >= maxEntries)
        {
            lock (CacheEvictionLock)
            {
                if (PromptCache.Count >= maxEntries)
                {
                    // Coarse bounded eviction: drop the oldest ~25% to amortize
                    // the sweep. Good enough for a hot-key gateway workload.
                    var toEvict = PromptCache.Count / 4;
                    var evicted = 0;
                    foreach (var pair in PromptCache)
                    {
                        if (evicted >= toEvict)
                            break;
                        if (PromptCache.TryRemove(pair.Key, out _))
                            evicted++;
                    }
                }
            }
        }

        PromptCache[key] = new PromptCacheEntry(completion, provider, Stopwatch.GetTimestamp());
    }

    // ── Fire-and-forget telemetry (flows through SQLite group-commit) ────────

    private static async Task LogAsync(
        Ctx ctx,
        ParsedRequest request,
        NormalizedCompletion completion,
        long startedTicks,
        bool cached)
    {
        try
        {
            if (ctx.Environment.Get("GATEWAY_LOGGING") == "0")
                return;

            var latencyMs = Math.Round(
                (Stopwatch.GetTimestamp() - startedTicks) * 1000.0 / Stopwatch.Frequency,
                3);

            // Not awaited by the handler: the insert is enqueued into the
            // SQLite group-commit channel batcher, so response latency is
            // decoupled from disk I/O.
            await ctx.Db.InsertAsync(GatewaySchema.GatewayLogs, new Dictionary<string, object?>
            {
                ["id"] = Ids.New("gl"),
                ["model"] = request.Model,
                ["prompt_tokens"] = completion.PromptTokens,
                ["completion_tokens"] = completion.CompletionTokens,
                ["latency_ms"] = latencyMs,
                ["cached"] = cached ? "1" : "0",
            });
        }
        catch
        {
            // Telemetry is best-effort; never fail a response over logging.
        }
    }

    // ── Small helpers ─────────────────────────────────────────────────────────

    // ── G13: A2A (Agent-to-Agent) upstream provider ────────────────────────────

    private sealed class A2aException(string message) : Exception(message);

    /// <summary>
    /// Minimal A2A flow: resolve the agent card once (cached), send the
    /// message as JSON-RPC <c>message/send</c>, poll <c>tasks/get</c> until the
    /// task completes, and return the task's text artifacts. Any failure
    /// throws <see cref="A2aException"/> so the failover chain can move on.
    /// </summary>
    private static async Task<string> A2aCompleteAsync(
        IHttpClient http,
        string agentEndpoint,
        ParsedRequest request,
        IEnvironment env)
    {
        var endpoint = agentEndpoint.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? agentEndpoint.TrimEnd('/')
            : $"https://{agentEndpoint.TrimEnd('/')}";

        if (!A2aAgentUrls.TryGetValue(endpoint, out var sendUrl))
        {
            sendUrl = await ResolveA2aSendUrlAsync(http, endpoint);
            A2aAgentUrls[endpoint] = sendUrl;
        }

        return await A2aSendAsync(http, sendUrl, request, env);
    }

    /// <summary>
    /// Resolves the JSON-RPC endpoint from the agent card; falls back to the
    /// endpoint root so plain HTTP+JSON A2A servers work without a card.
    /// </summary>
    private static async Task<string> ResolveA2aSendUrlAsync(IHttpClient http, string endpoint)
    {
        foreach (var wellKnown in new[] { "/.well-known/agent-card.json", "/.well-known/agent.json" })
        {
            try
            {
                var cardResponse = await http.FetchAsync(endpoint + wellKnown, "GET");
                if (cardResponse.Status != 200)
                    continue;

                using var document = JsonDocument.Parse(cardResponse.Body);
                if (document.RootElement.TryGetProperty("url", out var urlElement) &&
                    urlElement.ValueKind == JsonValueKind.String &&
                    urlElement.GetString() is { Length: > 0 } url)
                {
                    return url;
                }
            }
            catch
            {
                // Try the next well-known path, then the endpoint root.
            }
        }

        return endpoint;
    }

    private static async Task<string> A2aSendAsync(
        IHttpClient http, string sendUrl, ParsedRequest request, IEnvironment env)
    {
        var sendBody = JsonBytes(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteNumber("id", 1);
            writer.WriteString("method", "message/send");
            writer.WriteStartObject("params");
            writer.WriteStartObject("message");
            writer.WriteString("kind", "message");
            writer.WriteString("role", "user");
            writer.WriteString("messageId", Ids.New("a2a").ToLowerInvariant());
            writer.WriteStartArray("parts");
            writer.WriteStartObject();
            writer.WriteString("kind", "text");
            writer.WriteString("text", FlattenPrompt(request));
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        });

        var response = await http.FetchAsync(
            sendUrl, "POST",
            new Dictionary<string, string> { ["Content-Type"] = "application/json" },
            Encoding.UTF8.GetString(sendBody));

        if (response.Status == 429 || response.Status >= 500)
            throw new A2aException($"retryable:{response.Status}");
        if (response.Status != 200)
            throw new A2aException($"status:{response.Status}");

        using var document = JsonDocument.Parse(response.Body);
        if (document.RootElement.TryGetProperty("error", out var error) &&
            error.ValueKind == JsonValueKind.Object)
        {
            throw new A2aException("jsonrpc-error");
        }

        var task = document.RootElement.TryGetProperty("result", out var result)
            ? result
            : document.RootElement;
        if (!task.TryGetProperty("id", out var idElement) ||
            idElement.GetString() is not { Length: > 0 } taskId)
        {
            throw new A2aException("no-task-id");
        }

        // Synchronously-completed tasks: use the artifacts from the send
        // response itself (some A2A servers never re-attach them in
        // tasks/get).
        if (A2aTaskState(task) == "completed" && A2aTaskText(task) is { Length: > 0 } inline)
        {
            return inline;
        }

        return await A2aAwaitTaskAsync(http, sendUrl, taskId, env);
    }

    private static async Task<string> A2aAwaitTaskAsync(
        IHttpClient http, string sendUrl, string taskId, IEnvironment env)
    {
        var pollAttempts = ParseInt(env.Get("A2A_POLL_ATTEMPTS"), 60);
        var pollIntervalMs = ParseInt(env.Get("A2A_POLL_INTERVAL_MS"), 500);

        for (var attempt = 0; attempt < pollAttempts; attempt++)
        {
            var body = JsonBytes(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("jsonrpc", "2.0");
                writer.WriteNumber("id", 1);
                writer.WriteString("method", "tasks/get");
                writer.WriteStartObject("params");
                writer.WriteString("id", taskId);
                writer.WriteEndObject();
                writer.WriteEndObject();
            });

            var response = await http.FetchAsync(
                sendUrl, "POST",
                new Dictionary<string, string> { ["Content-Type"] = "application/json" },
                Encoding.UTF8.GetString(body));

            if (response.Status != 200)
                throw new A2aException($"poll-status:{response.Status}");

            using var document = JsonDocument.Parse(response.Body);
            if (document.RootElement.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.Object)
            {
                throw new A2aException("jsonrpc-error");
            }

            var task = document.RootElement.TryGetProperty("result", out var result)
                ? result
                : document.RootElement;

            var state = A2aTaskState(task);
            if (state == "completed")
            {
                return A2aTaskText(task);
            }

            if (state is "failed" or "canceled" or "rejected")
            {
                throw new A2aException($"task-{state}");
            }

            await Task.Delay(pollIntervalMs);
        }

        throw new A2aException("task-timeout");
    }

    private static string? A2aTaskState(JsonElement task)
    {
        if (task.TryGetProperty("status", out var status) &&
            status.TryGetProperty("state", out var stateElement) &&
            stateElement.ValueKind == JsonValueKind.String)
        {
            return stateElement.GetString();
        }

        return null;
    }

    private static string A2aTaskText(JsonElement task)
    {
        var texts = new List<string>();

        if (task.TryGetProperty("artifacts", out var artifacts) &&
            artifacts.ValueKind == JsonValueKind.Array)
        {
            foreach (var artifact in artifacts.EnumerateArray())
            {
                CollectA2aParts(artifact, texts);
            }
        }

        if (texts.Count == 0 &&
            task.TryGetProperty("history", out var history) &&
            history.ValueKind == JsonValueKind.Array)
        {
            foreach (var message in history.EnumerateArray())
            {
                CollectA2aParts(message, texts);
            }
        }

        return string.Concat(texts);
    }

    private static void CollectA2aParts(JsonElement parent, List<string> texts)
    {
        if (!parent.TryGetProperty("parts", out var parts) ||
            parts.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("text", out var text) &&
                text.ValueKind == JsonValueKind.String)
            {
                texts.Add(text.GetString() ?? "");
            }
        }
    }

    private static readonly DateTimeOffset ProcessStartTime = DateTimeOffset.UtcNow;

    private static byte[] JsonBytes(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            write(writer);
        }
        return stream.ToArray();
    }

    /// <summary>
    /// Minimal JSON string escaper (quotes, backslash, control characters).
    /// Hand-rolled so the streaming frame path never touches System.Text.Json's
    /// reflection-annotated serializer overloads (IL2026/IL3050-clean under
    /// Native AOT and NetWasm trimming).
    /// </summary>
    private static string JsonEscape(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        builder.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case < (char)0x20:
                    builder.Append($"\\u{(int)character:x4}");
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }
        builder.Append('"');
        return builder.ToString();
    }

    private static void WriteUsage(Utf8JsonWriter writer, NormalizedCompletion completion)
    {
        writer.WriteStartObject("usage");
        writer.WriteNumber("prompt_tokens", completion.PromptTokens);
        writer.WriteNumber("completion_tokens", completion.CompletionTokens);
        writer.WriteNumber("total_tokens", completion.PromptTokens + completion.CompletionTokens);
        writer.WriteEndObject();
    }

    private static ReptarResponse OpenAiError(
        int status,
        string type,
        string message,
        List<string>? attempts = null)
    {
        var body = JsonBytes(writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartObject("error");
            writer.WriteString("message", message);
            writer.WriteString("type", type);
            writer.WriteNull("param");
            writer.WriteNull("code");
            if (attempts is { Count: > 0 })
            {
                writer.WriteStartArray("attempts");
                foreach (var attempt in attempts)
                    writer.WriteStringValue(attempt);
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        });

        return new ReptarResponse
        {
            Status = status,
            ContentType = "application/json; charset=utf-8",
            Body = body,
        };
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        foreach (var character in text)
        {
            current.Append(character);
            if (character == ' ')
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length > 0)
            tokens.Add(current.ToString());
        return tokens;
    }

    private static int EstimateTokens(string text) =>
        text.Length == 0 ? 1 : (text.Length + 3) / 4;

    private static int ReadInt(JsonElement parent, string property)
    {
        if (parent.TryGetProperty(property, out var element) &&
            element.ValueKind == JsonValueKind.Number &&
            element.TryGetInt32(out var value))
            return value;
        return 0;
    }

    private static int ParseInt(string? value, int fallback)
    {
        if (value is null)
            return fallback;
        if (int.TryParse(value.Trim(), out var parsed))
            return parsed;
        return fallback;
    }
}

// ── Data shapes ──────────────────────────────────────────────────────────────

internal sealed record ChatMessage(string Role, string Content);

internal sealed record ParsedRequest(
    string Model,
    List<ChatMessage> Messages,
    double? Temperature,
    int? MaxTokens,
    bool Stream,
    string CanonicalMessagesJson);

internal sealed record ToolCall(string Id, string Name, string ArgsJson);

internal sealed record NormalizedCompletion(
    List<string> TextTokens,
    List<ToolCall> ToolCalls,
    int PromptTokens,
    int CompletionTokens,
    string FinishReason);

public static class GatewaySchema
{
    // High-throughput token telemetry. Rows are written fire-and-forget so
    // each request flows through Reptar's SQLite group-commit channel
    // batcher instead of blocking the response on disk I/O.
    public static readonly TableDef GatewayLogs = DefineTable("gateway_logs",
        Col.Text("id").PrimaryKey(),
        Col.Text("model").NotNull(),
        Col.Int("prompt_tokens"),
        Col.Int("completion_tokens"),
        Col.Real("latency_ms"),
        Col.Text("cached").NotNull(),
        Col.Text("created_at").DefaultSql("CURRENT_TIMESTAMP")
    );

    public static readonly TableDef[] All = [GatewayLogs];
}
}
