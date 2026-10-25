#if !NETWASM
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reptar.AI;

/// <summary>
/// LLM providers supported by the built-in Reptar agent engine.
/// </summary>
public enum LlmProvider
{
    OpenAi,
    Anthropic,
    Gemini,
    Ollama,
    Echo
}

/// <summary>
/// Provider-agnostic configuration for an LLM conversation.
/// </summary>
public sealed record LlmConfig
{
    public LlmProvider Provider { get; init; } = LlmProvider.OpenAi;

    /// <summary>
    /// Canonical registry key: openai, anthropic, gemini, ollama, openrouter,
    /// gateway, echo, or a custom provider id registered via /connect.
    /// </summary>
    public string ProviderKey { get; init; } = "";

    public string Model { get; init; } = "gpt-4o-mini";
    public string? ApiKey { get; init; }
    public string? BaseUrl { get; init; }
    public double Temperature { get; init; } = 0.2;
    public int MaxTokens { get; init; } = 4096;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);

    public string DisplayName =>
        $"{(ProviderKey.Length > 0 ? ProviderKey : Provider.ToString().ToLowerInvariant())}/{Model}";

    /// <summary>Human name from the registry, or the custom id itself.</summary>
    public string ProviderName =>
        LlmProviders.Find(ProviderKey)?.DisplayName ??
        (ProviderKey.Length > 0 ? ProviderKey : Provider.ToString().ToLowerInvariant());

    public static LlmConfig Echo(string model = "echo") => new()
    {
        Provider = LlmProvider.Echo,
        Model = model
    };

    /// <summary>
    /// Resolves a provider and model for the agent. Precedence (pi/opencode style):
    ///   1. explicit provider/model arguments (CLI flags, /model command)
    ///   2. REPTAR_AGENT_PROVIDER / REPTAR_AGENT_MODEL env vars
    ///   3. provider API keys in the environment (OPENAI_API_KEY, ...)
    ///   4. credentials saved by /connect in ~/.reptar/agent/auth.json
    ///   5. the offline Echo provider
    /// Env wins over saved auth so project .env files keep working; the
    /// provider status display always shows which source is in use.
    /// </summary>
    public static LlmConfig FromEnvironment(
        string? provider = null,
        string? model = null,
        string? baseUrl = null)
    {
        var requestedProvider = FirstNonEmpty(
            provider,
            Environment.GetEnvironmentVariable("REPTAR_AGENT_PROVIDER"));

        var requestedModel = FirstNonEmpty(
            model,
            Environment.GetEnvironmentVariable("REPTAR_AGENT_MODEL"));

        var requestedBaseUrl = FirstNonEmpty(
            baseUrl,
            Environment.GetEnvironmentVariable("REPTAR_AGENT_BASE_URL"));

        var auth = LlmAuthFile.LoadActive();
        var providerKey = ResolveProviderKey(requestedProvider, auth);

        return providerKey switch
        {
            LlmProviders.OpenAi => BuildOpenAiCompatible(
                LlmProviders.OpenAi, requestedModel, requestedBaseUrl, auth,
                Environment.GetEnvironmentVariable("REPTAR_AGENT_API_KEY")),
            LlmProviders.OpenRouter => BuildOpenAiCompatible(
                LlmProviders.OpenRouter, requestedModel, requestedBaseUrl, auth,
                Environment.GetEnvironmentVariable("REPTAR_AGENT_API_KEY")),
            LlmProviders.Gateway => BuildOpenAiCompatible(
                LlmProviders.Gateway, requestedModel, requestedBaseUrl, auth,
                Environment.GetEnvironmentVariable("REPTAR_AGENT_API_KEY")),
            LlmProviders.Anthropic => new LlmConfig
            {
                ProviderKey = LlmProviders.Anthropic,
                Provider = LlmProvider.Anthropic,
                Model = requestedModel
                    ?? auth.Get(LlmProviders.Anthropic)?.DefaultModel
                    ?? LlmProviders.Find(LlmProviders.Anthropic)!.DefaultModel,
                ApiKey = FirstNonEmpty(
                    Environment.GetEnvironmentVariable("REPTAR_AGENT_API_KEY"),
                    Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"),
                    auth.Get(LlmProviders.Anthropic)?.ApiKey),
                BaseUrl = requestedBaseUrl
                    ?? Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL")
                    ?? auth.Get(LlmProviders.Anthropic)?.BaseUrl
                    ?? "https://api.anthropic.com"
            },
            LlmProviders.Gemini => new LlmConfig
            {
                ProviderKey = LlmProviders.Gemini,
                Provider = LlmProvider.Gemini,
                Model = requestedModel
                    ?? auth.Get(LlmProviders.Gemini)?.DefaultModel
                    ?? LlmProviders.Find(LlmProviders.Gemini)!.DefaultModel,
                ApiKey = FirstNonEmpty(
                    Environment.GetEnvironmentVariable("REPTAR_AGENT_API_KEY"),
                    Environment.GetEnvironmentVariable("GEMINI_API_KEY"),
                    Environment.GetEnvironmentVariable("GOOGLE_API_KEY"),
                    auth.Get(LlmProviders.Gemini)?.ApiKey),
                BaseUrl = requestedBaseUrl
                    ?? Environment.GetEnvironmentVariable("GEMINI_BASE_URL")
                    ?? auth.Get(LlmProviders.Gemini)?.BaseUrl
                    ?? "https://generativelanguage.googleapis.com"
            },
            LlmProviders.Ollama => new LlmConfig
            {
                ProviderKey = LlmProviders.Ollama,
                Provider = LlmProvider.Ollama,
                Model = requestedModel
                    ?? auth.Get(LlmProviders.Ollama)?.DefaultModel
                    ?? LlmProviders.Find(LlmProviders.Ollama)!.DefaultModel,
                ApiKey = Environment.GetEnvironmentVariable("REPTAR_AGENT_API_KEY")
                    ?? auth.Get(LlmProviders.Ollama)?.ApiKey,
                BaseUrl = requestedBaseUrl
                    ?? Environment.GetEnvironmentVariable("OLLAMA_HOST")
                    ?? auth.Get(LlmProviders.Ollama)?.BaseUrl
                    ?? "http://localhost:11434"
            },
            LlmProviders.Echo => new LlmConfig
            {
                ProviderKey = LlmProviders.Echo,
                Provider = LlmProvider.Echo,
                Model = requestedModel ?? "echo"
            },
            // A custom OpenAI-compatible endpoint registered through /connect → Other.
            var customKey => BuildCustom(customKey, requestedModel, requestedBaseUrl, auth)
        };
    }

    /// <summary>
    /// Maps a requested provider name to a provider key. Unknown names that
    /// match a saved custom provider are accepted; everything else fails fast
    /// with the supported list.
    /// </summary>
    public static string ResolveProviderKey(string? requestedProvider, LlmAuthFile auth)
    {
        var name = requestedProvider?.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(name))
            return DetectProviderKey(auth);

        return name switch
        {
            "openai" or "open-ai" => LlmProviders.OpenAi,
            "anthropic" or "claude" => LlmProviders.Anthropic,
            "gemini" or "google" => LlmProviders.Gemini,
            "ollama" or "local" => LlmProviders.Ollama,
            "openrouter" => LlmProviders.OpenRouter,
            "gateway" or "ai-gateway" or "vercel" => LlmProviders.Gateway,
            "echo" or "offline" or "mock" => LlmProviders.Echo,
            _ when auth.Get(name) is not null => name,
            _ => throw new ArgumentException(
                $"Unknown LLM provider '{name}'. Use openai, anthropic, gemini, ollama, openrouter, gateway, " +
                "a custom provider registered via /connect, or echo.")
        };
    }

    private static string DetectProviderKey(LlmAuthFile auth)
    {
        // Environment first (project .env files keep working), then saved auth.
        if (HasEnv("OPENAI_API_KEY")) return LlmProviders.OpenAi;
        if (HasEnv("AI_GATEWAY_API_KEY")) return LlmProviders.Gateway;
        if (HasEnv("OPENROUTER_API_KEY")) return LlmProviders.OpenRouter;
        if (HasEnv("ANTHROPIC_API_KEY")) return LlmProviders.Anthropic;
        if (HasEnv("GEMINI_API_KEY") || HasEnv("GOOGLE_API_KEY")) return LlmProviders.Gemini;
        if (HasEnv("OLLAMA_HOST")) return LlmProviders.Ollama;

        // Saved credentials: last /connect wins.
        var defaultProvider = auth.DefaultProvider;
        if (defaultProvider is not null)
            return defaultProvider;

        return LlmProviders.Echo;
    }

    private static bool HasEnv(string name) =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name));

    private static LlmConfig BuildOpenAiCompatible(
        string providerKey,
        string? model,
        string? baseUrl,
        LlmAuthFile auth,
        string? genericKey)
    {
        var info = LlmProviders.Find(providerKey)!;
        var saved = auth.Get(providerKey);

        var apiKey = providerKey switch
        {
            LlmProviders.OpenAi => FirstNonEmpty(
                genericKey,
                Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
                saved?.ApiKey),
            LlmProviders.OpenRouter => FirstNonEmpty(
                genericKey,
                Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"),
                saved?.ApiKey),
            LlmProviders.Gateway => FirstNonEmpty(
                genericKey,
                Environment.GetEnvironmentVariable("AI_GATEWAY_API_KEY"),
                saved?.ApiKey),
            _ => FirstNonEmpty(genericKey, saved?.ApiKey)
        };

        var resolvedBaseUrl = baseUrl
            ?? (providerKey == LlmProviders.OpenAi
                ? Environment.GetEnvironmentVariable("OPENAI_BASE_URL")
                : null)
            ?? saved?.BaseUrl
            ?? info.DefaultBaseUrl;

        return new LlmConfig
        {
            ProviderKey = providerKey,
            Provider = LlmProvider.OpenAi,
            Model = model ?? saved?.DefaultModel ?? info.DefaultModel,
            ApiKey = apiKey,
            BaseUrl = resolvedBaseUrl
        };
    }

    private static LlmConfig BuildCustom(
        string providerKey,
        string? model,
        string? baseUrl,
        LlmAuthFile auth)
    {
        var saved = auth.Get(providerKey)
            ?? throw new ArgumentException(
                $"Provider '{providerKey}' is not registered. Run /connect and choose 'Other' to add it.");

        return new LlmConfig
        {
            ProviderKey = providerKey,
            Provider = LlmProvider.OpenAi,
            Model = model ?? saved.DefaultModel
                ?? throw new ArgumentException(
                    $"Custom provider '{providerKey}' has no default model. Pass --model {providerKey}/<model>."),
            ApiKey = saved.ApiKey,
            BaseUrl = baseUrl ?? saved.BaseUrl
        };
    }

    internal static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }
}

public abstract record LlmStreamEvent
{
    public sealed record TextDelta(string Text) : LlmStreamEvent;
    public sealed record ReasoningDelta(string Text) : LlmStreamEvent;
    public sealed record ToolCallDelta(int Index, string? Id, string? Name, string ArgumentsJson) : LlmStreamEvent;
    public sealed record ToolCall(string Id, string Name, string ArgumentsJson) : LlmStreamEvent;
    public sealed record Usage(int InputTokens, int OutputTokens) : LlmStreamEvent;
    public sealed record Completed(string? FinishReason) : LlmStreamEvent;
    public sealed record Failed(string Message) : LlmStreamEvent;
}

public sealed class LlmException(int statusCode, string message, string? responseBody = null)
    : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string? ResponseBody { get; } = responseBody;
}

public interface ILlmClient
{
    LlmConfig Config { get; }

    IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<AiToolDefinition> tools,
        CancellationToken cancellationToken = default);
}

public static class LlmClientFactory
{
    public static ILlmClient Create(LlmConfig config) => config.Provider switch
    {
        LlmProvider.OpenAi => new OpenAiLlmClient(config),
        LlmProvider.Anthropic => new AnthropicLlmClient(config),
        LlmProvider.Gemini => new GeminiLlmClient(config),
        LlmProvider.Ollama => new OllamaLlmClient(config),
        _ => new EchoLlmClient(config)
    };

    public static ILlmClient FromEnvironment(string? provider = null, string? model = null) =>
        Create(LlmConfig.FromEnvironment(provider, model));
}

internal static class LlmHttp
{
    internal static readonly HttpClient Client = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    public static async Task<HttpResponseMessage> PostJsonAsync(
        string url,
        JsonObject body,
        IReadOnlyDictionary<string, string> headers,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };

        foreach (var (name, value) in headers)
        {
            if (!request.Headers.TryAddWithoutValidation(name, value))
                request.Content.Headers.TryAddWithoutValidation(name, value);
        }

        var response = await Client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeoutCts.Token).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
            return response;

        var responseBody = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        response.Dispose();

        throw new LlmException(
            status,
            $"LLM request failed with HTTP {status} ({ReasonPhrase(status)}).",
            responseBody);
    }

    private static string ReasonPhrase(int status) => status switch
    {
        401 => "unauthorized - check your API key",
        403 => "forbidden",
        404 => "model or endpoint not found",
        429 => "rate limited",
        _ => ((HttpStatusCode)status).ToString()
    };

    public static JsonObject ParseObject(string json)
    {
        var node = JsonNode.Parse(json);
        return node as JsonObject
            ?? throw new LlmException(502, "LLM returned a non-object JSON payload.", json);
    }

    public static string? Str(JsonNode? node) =>
        node is null
            ? null
            : node.GetValueKind() == JsonValueKind.String
                ? node.GetValue<string>()
                : node.ToJsonString();

    public static JsonArray BuildOpenAiTools(IReadOnlyList<AiToolDefinition> tools) =>
        ToArray(tools, AiToolFormat.OpenAi);

    public static JsonArray BuildAnthropicTools(IReadOnlyList<AiToolDefinition> tools) =>
        ToArray(tools, AiToolFormat.Anthropic);

    public static JsonArray BuildGeminiTools(IReadOnlyList<AiToolDefinition> tools)
    {
        var declarations = ToArray(tools, AiToolFormat.Gemini);
        return [new JsonObject { ["functionDeclarations"] = declarations }];
    }

    private static JsonArray ToArray(IReadOnlyList<AiToolDefinition> tools, AiToolFormat format)
    {
        var array = new JsonArray();
        foreach (var tool in tools)
            array.Add(ToNode(tool.ToDictionary(format)));
        return array;
    }

    /// <summary>
    /// Converts parameter-schema values (strings, primitives, nested
    /// dictionaries, string lists) into JsonNode without reflection, so tool
    /// declarations serialize identically under Native AOT.
    /// </summary>
    public static JsonNode? ToNode(object? value)
    {
        return value switch
        {
            null => null,
            JsonNode node => node,
            string text => text,
            bool flag => flag,
            int number => number,
            long bigNumber => bigNumber,
            double real => real,
            float floatReal => floatReal,
            decimal money => money,
            Dictionary<string, object?> map => new JsonObject(
                map.Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, ToNode(pair.Value)))),
            List<string> strings => new JsonArray(
                strings.Select(item => (JsonNode?)item).ToArray()),
            IEnumerable<object?> items => new JsonArray(
                items.Select(ToNode).ToArray()),
            _ => value.ToString()
        };
    }

    public static string NormalizeArguments(JsonNode? node)
    {
        if (node is null)
            return "{}";

        if (node.GetValueKind() == JsonValueKind.String)
            return node.GetValue<string>() is { Length: > 0 } text ? text : "{}";

        return node.ToJsonString();
    }
}

internal static class SseParser
{
    public sealed record Frame(string? Event, string Data);

    public static async IAsyncEnumerable<Frame> ReadAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 8192,
            leaveOpen: true);

        string? eventName = null;
        var data = new StringBuilder();

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
                break;

            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    yield return new Frame(eventName, data.ToString());
                    data.Clear();
                }

                eventName = null;
                continue;
            }

            if (line[0] == ':')
                continue;

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = line[6..].Trim();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0)
                    data.Append('\n');
                data.Append(line[5..].TrimStart());
            }
        }

        if (data.Length > 0)
            yield return new Frame(eventName, data.ToString());
    }
}

/// <summary>
/// Deterministic offline provider used when no API key is configured. It keeps the
/// CLI, tests, and demos runnable without network access.
/// </summary>
internal sealed class EchoLlmClient(LlmConfig config) : ILlmClient
{
    public LlmConfig Config { get; } = config;

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<AiToolDefinition> tools,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var prompt = messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";
        var response = $"""
            **Reptar Agent is running in offline echo mode.**

            No LLM API key was detected. Set one of `OPENAI_API_KEY`, `ANTHROPIC_API_KEY`,
            `GEMINI_API_KEY`, or `OLLAMA_HOST` to connect a real model.

            You said: {prompt}

            The workspace tools are available and can be tested with `--self-test`.
            """;

        foreach (var word in response.Split(' '))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new LlmStreamEvent.TextDelta(word + " ");
            await Task.Delay(8, cancellationToken).ConfigureAwait(false);
        }

        yield return new LlmStreamEvent.Completed("stop");
    }
}
#endif
