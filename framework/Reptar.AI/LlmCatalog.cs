#if !NETWASM
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reptar.AI;

public sealed record CatalogResult(
    bool Ok,
    List<string> Models,
    string? Error = null)
{
    public static CatalogResult Failure(string error) => new(false, [], error);
}

/// <summary>
/// Live model catalogs and connection checks, per provider:
///   OpenAI-compatible (openai/openrouter/gateway/custom): GET {base}/models
///   Anthropic:  GET {base}/v1/models
///   Gemini:     GET {base}/v1beta/models
///   Ollama:     GET {host}/api/tags
/// A successful listing doubles as the connection test (opencode /connect behavior).
/// </summary>
public static class LlmCatalog
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    public static async Task<CatalogResult> ListAsync(LlmConfig config, int timeoutMs = 20_000)
    {
        try
        {
            using var cancellation = new CancellationTokenSource(timeoutMs);
            var (url, headers, parser) = Endpoint(config);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            foreach (var (name, value) in headers)
                request.Headers.TryAddWithoutValidation(name, value);

            using var response = await Client.SendAsync(request, cancellation.Token);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return CatalogResult.Failure(
                    $"HTTP {(int)response.StatusCode} from {url}" +
                    (body.Length > 0 && body.Length < 200 ? $": {Trim(body)}" : ""));
            }

            var models = parser(body)
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            models.Sort(CompareByRecency);
            return new CatalogResult(true, models);
        }
        catch (OperationCanceledException)
        {
            return CatalogResult.Failure($"Timed out after {timeoutMs} ms");
        }
        catch (HttpRequestException ex)
        {
            return CatalogResult.Failure(ex.Message);
        }
        catch (JsonException ex)
        {
            return CatalogResult.Failure("Malformed model list: " + ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            // Invalid base URLs surface as request-building failures, not crashes.
            return CatalogResult.Failure(ex.Message);
        }
    }

    /// <summary>
    /// Orders model ids newest first.
    /// </summary>
    /// <remarks>
    /// Provider catalogs are in no useful order, and sorting them alphabetically is worse than
    /// leaving them alone: it puts <c>gpt-4o</c> above <c>gpt-5.6</c>, so the model someone just
    /// heard about is buried under the whole back catalogue.
    ///
    /// The rule is a heuristic, not a version scheme: compare the numeric runs in the id
    /// left-to-right, descending, so <c>gpt-6-sol</c> beats <c>gpt-5.6</c> and a dated snapshot
    /// beats the alias it snapshots. Ties fall back to the id itself, descending, which keeps the
    /// order stable. Models whose families are not comparable (<c>o3</c> versus <c>gpt-4</c>) are
    /// ordered by the same numbers; there is no correct answer for that case, only a stable one.
    /// </remarks>
    public static int CompareByRecency(string a, string b)
    {
        var left = VersionParts(a);
        var right = VersionParts(b);

        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            if (left[i] != right[i])
                return right[i].CompareTo(left[i]);
        }

        // More parts means more specific, and a dated snapshot is newer than its alias.
        if (left.Length != right.Length)
            return right.Length.CompareTo(left.Length);

        return string.CompareOrdinal(b, a);
    }

    private static int[] VersionParts(string id)
    {
        var parts = new List<int>(4);
        var current = 0;
        var inNumber = false;

        foreach (var character in id)
        {
            if (character is >= '0' and <= '9')
            {
                // Clamp rather than overflow on a long date-like run.
                current = current > 9_999_999 ? 9_999_999 : (current * 10) + (character - '0');
                inNumber = true;
            }
            else if (inNumber)
            {
                parts.Add(current);
                current = 0;
                inNumber = false;
            }
        }

        if (inNumber)
            parts.Add(current);

        return [.. parts];
    }

    // ------------------------------------------------------------------
    // Curated catalog (models.dev — the catalog opencode uses). Works with
    // zero credentials, so /models can browse a provider before connecting.
    // ------------------------------------------------------------------

    public sealed record CuratedModel(
        string Id,
        string Name,
        long ContextTokens,
        double InputCostPerMillion = 0,
        double OutputCostPerMillion = 0,
        bool Reasoning = false,
        bool Vision = false,
        bool ToolCall = true,
        IReadOnlyList<ThinkingLevel>? Thinking = null,
        string? ReasoningKind = null,
        int MaxOutputTokens = 0)
    {
        /// <summary>
        /// Levels this model actually accepts. Empty when it does not reason. Derived from
        /// models.dev's <c>reasoning_options</c>, which is the only authoritative source: a model
        /// can be a reasoning model and still reject <c>xhigh</c>.
        /// </summary>
        public IReadOnlyList<ThinkingLevel> SupportedThinking =>
            Thinking ?? (Reasoning ? ThinkingLevels.Supported(true) : [ThinkingLevel.Off]);
    }

    /// <summary>
    /// Per-million-token pricing and context limit for a model, from models.dev.
    /// Handles "openai/gpt-4o"-style gateway ids by falling back to the last
    /// path segment.
    /// </summary>
    public static async Task<CuratedModel?> GetModelInfoAsync(string providerKey, string modelId)
    {
        var curated = await ListCuratedAsync(providerKey);

        var match = curated.FirstOrDefault(m =>
                       string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase))
                   ?? curated.FirstOrDefault(m =>
                       string.Equals(m.Id.Split('/')[^1], modelId.Split('/')[^1],
                           StringComparison.OrdinalIgnoreCase));

        return match;
    }

    private static readonly Dictionary<string, string> ModelsDevProviderIds = new(StringComparer.OrdinalIgnoreCase)
    {
        [LlmProviders.OpenAi] = "openai",
        // A ChatGPT subscription serves the same model family as the API, so it browses the same
        // catalog. Without this the provider lists only its default and looks broken.
        [LlmProviders.OpenAiChatGpt] = "openai",
        [LlmProviders.Anthropic] = "anthropic",
        [LlmProviders.Gemini] = "google",
        [LlmProviders.Ollama] = "ollama",
        [LlmProviders.OpenRouter] = "openrouter",
        // Gateways deliberately have no curated entry. A gateway routes to many providers and its
        // ids carry a routing prefix (`openai/gpt-5.6`), so borrowing OpenAI's catalog would list
        // ids that cannot be routed. They fall back to a live listing instead.
        [LlmProviders.CloudflareWorkersAi] = "cloudflare-workers-ai"
    };

    public static bool HasCuratedModels(string providerKey) =>
        ModelsDevProviderIds.ContainsKey(providerKey);

    private static JsonObject? _modelsDevCache;

    public static async Task<List<CuratedModel>> ListCuratedAsync(string providerKey)
    {
        try
        {
            if (!ModelsDevProviderIds.TryGetValue(providerKey, out var modelsDevId))
                return [];

            _modelsDevCache ??= await FetchModelsDevAsync();
            if (_modelsDevCache is null)
                return [];

            if (_modelsDevCache[modelsDevId] is not JsonObject provider ||
                provider["models"] is not JsonObject models)
                return [];

            var result = new List<CuratedModel>();
            foreach (var (modelId, node) in models)
            {
                if (node is not JsonObject model)
                    continue;

                var name = model["name"]?.GetValue<string>() ?? modelId;
                var context = model["limit"]?["context"]?.GetValue<long>() ?? 0;
                var maxOutput = model["limit"]?["output"]?.GetValue<long>() ?? 0;
                var inputCost = model["cost"]?["input"]?.GetValue<double>() ?? 0;
                var outputCost = model["cost"]?["output"]?.GetValue<double>() ?? 0;

                // models.dev puts these at the top level. Read there first, then fall back to a
                // `capabilities` object in case the catalog generation moves them.
                var caps = model["capabilities"] as JsonObject;
                var reasoning = ReadBool(model, "reasoning") || (caps is not null && ReadBool(caps, "reasoning"));

                var inputs = model["modalities"]?["input"] as JsonArray;
                var vision = inputs is not null &&
                    inputs.Any(node => string.Equals(node?.GetValue<string>(), "image", StringComparison.OrdinalIgnoreCase));

                var toolCall = ReadBool(model, "tool_call",
                    caps is not null ? ReadBool(caps, "tool_call", fallback: true) : true);

                var (levels, kind) = ParseReasoningOptions(model["reasoning_options"], reasoning);

                result.Add(new CuratedModel(
                    modelId, name, context, inputCost, outputCost,
                    reasoning, vision, toolCall, levels, kind,
                    (int)Math.Min(int.MaxValue, maxOutput)));
            }

            // Newest first, matching the live listing. Sorting by display name put "GPT-4o"
            // above "GPT-5.6" for the same reason alphabetical sorting did.
            result.Sort((a, b) => CompareByRecency(a.Id, b.Id));
            return result;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Turns models.dev's <c>reasoning_options</c> into the levels a model accepts, mirroring pi's
    /// <c>getEffortThinkingLevelMap</c>.
    /// </summary>
    /// <remarks>
    /// models.dev describes reasoning in three shapes:
    /// <list type="bullet">
    /// <item><c>toggle</c> — on or off, so the only levels are <c>off</c> and <c>high</c>.</item>
    /// <item><c>effort</c> — an explicit list of accepted effort names. <c>off</c> is only offered
    /// when the list contains <c>none</c>, because a model that cannot disable thinking would
    /// otherwise silently ignore the request.</item>
    /// <item><c>budget_tokens</c> — a numeric range, which maps onto the standard ladder.</item>
    /// </list>
    /// </remarks>
    private static (IReadOnlyList<ThinkingLevel>? Levels, string? Kind) ParseReasoningOptions(
        JsonNode? node,
        bool reasoning)
    {
        if (!reasoning)
            return ([ThinkingLevel.Off], null);

        if (node is not JsonArray options || options.Count == 0)
            return (null, null);

        foreach (var entry in options)
        {
            if (entry is not JsonObject option)
                continue;

            var type = option["type"]?.GetValue<string>();

            if (type == "toggle")
                return ([ThinkingLevel.Off, ThinkingLevel.High], "toggle");

            if (type == "budget_tokens")
                return (ThinkingLevels.Supported(true), "budget_tokens");

            if (type != "effort")
                continue;

            var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (option["values"] is JsonArray array)
            {
                foreach (var value in array)
                {
                    if (value is JsonValue scalar && scalar.TryGetValue<string>(out var name))
                        values.Add(name);
                }
            }

            if (values.Count == 0)
                continue;

            var levels = new List<ThinkingLevel>();

            // "off" is only selectable when the provider accepts an explicit "none".
            if (values.Contains("none"))
                levels.Add(ThinkingLevel.Off);

            foreach (var level in ThinkingLevels.All)
            {
                if (level == ThinkingLevel.Off)
                    continue;

                if (values.Contains(ThinkingLevels.Name(level)))
                    levels.Add(level);
            }

            return (levels.Count > 0 ? levels : null, "effort");
        }

        return (null, null);
    }

    private static bool ReadBool(JsonObject node, string key, bool fallback = false) =>
        node[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : fallback;

    private static async Task<JsonObject?> FetchModelsDevAsync()
    {
        try
        {
            using var response = await Client.GetAsync("https://models.dev/api.json");
            if (!response.IsSuccessStatusCode)
                return null;

            return JsonNode.Parse(await response.Content.ReadAsStringAsync()) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Fetch the catalog and return the first usable model (pi's post-login model sync).</summary>
    public static async Task<CatalogResult> PickDefaultAsync(LlmConfig config)
    {
        var listing = await ListAsync(config);

        if (!listing.Ok)
            return listing;

        var preferred = listing.Models.FirstOrDefault(m =>
                          m.Contains(config.Model, StringComparison.OrdinalIgnoreCase) &&
                          m.Length > config.Model.Length)
                      ?? listing.Models.FirstOrDefault(m =>
                          string.Equals(m, config.Model, StringComparison.OrdinalIgnoreCase));

        return preferred is null
            ? listing
            : new CatalogResult(true, [preferred]);
    }

    private static (string Url, (string, string)[] Headers, Func<string, List<string>> Parser) Endpoint(
        LlmConfig config) => config.Provider switch
    {
        LlmProvider.OpenAi => OpenAiCompatible(config),
        LlmProvider.Anthropic => (
            $"{TrimEnd(config.BaseUrl ?? "https://api.anthropic.com")}/v1/models",
            [
                ("x-api-key", config.ApiKey ?? ""),
                ("anthropic-version", "2023-06-01")
            ],
            ParseAnthropicModels),
        LlmProvider.Gemini => (
            $"{TrimEnd(config.BaseUrl ?? "https://generativelanguage.googleapis.com")}/v1beta/models",
            [("x-goog-api-key", config.ApiKey ?? "")],
            ParseGeminiModels),
        LlmProvider.Ollama => (
            $"{TrimEnd(config.BaseUrl ?? "http://localhost:11434")}/api/tags",
            [],
            ParseOllamaModels),
        _ => OpenAiCompatible(config)
    };

    private static (string Url, (string, string)[] Headers, Func<string, List<string>> Parser) OpenAiCompatible(
        LlmConfig config)
    {
        (string, string)[] headers = string.IsNullOrEmpty(config.ApiKey)
            ? []
            : [("authorization", "Bearer " + config.ApiKey)];

        return (
            $"{TrimEnd(config.BaseUrl ?? "https://api.openai.com/v1")}/models",
            headers,
            ParseOpenAiModels);
    }

    private static string TrimEnd(string value) => value.TrimEnd('/');

    private static string Trim(string value) =>
        value.Length > 200 ? value[..200] + "…" : value;

    // ---- parsers (unit-testable, JsonNode-only: AOT clean) ----

    public static List<string> ParseOpenAiModels(string json) =>
        ParseIds(json, "data");

    public static List<string> ParseAnthropicModels(string json) =>
        ParseIds(json, "data");

    private static List<string> ParseIds(string json, string arrayKey)
    {
        var result = new List<string>();
        if (ParseObject(json) is not { } root ||
            root[arrayKey] is not JsonArray data)
            return result;

        foreach (var item in data)
        {
            if (item?["id"] is { } id)
                result.Add(id.GetValue<string>());
        }

        return result;
    }

    private static JsonObject? ParseObject(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static List<string> ParseGeminiModels(string json)
    {
        var result = new List<string>();
        if (ParseObject(json) is not { } root ||
            root["models"] is not JsonArray models)
            return result;

        foreach (var item in models)
        {
            var name = item?["name"] is { } n ? n.GetValue<string>() : null;
            if (name is { Length: > 0 })
                result.Add(name.StartsWith("models/", StringComparison.Ordinal)
                    ? name["models/".Length..]
                    : name);
        }

        return result;
    }

    public static List<string> ParseOllamaModels(string json)
    {
        var result = new List<string>();
        if (ParseObject(json) is not { } root ||
            root["models"] is not JsonArray models)
            return result;

        foreach (var item in models)
        {
            if (item?["name"] is { } name)
                result.Add(name.GetValue<string>());
        }

        return result;
    }
}
#endif
