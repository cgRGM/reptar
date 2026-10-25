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

    public static async Task<CatalogResult> ListAsync(LlmConfig config)
    {
        try
        {
            var (url, headers, parser) = Endpoint(config);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            foreach (var (name, value) in headers)
                request.Headers.TryAddWithoutValidation(name, value);

            using var response = await Client.SendAsync(request);
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

            models.Sort(StringComparer.OrdinalIgnoreCase);
            return new CatalogResult(true, models);
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
