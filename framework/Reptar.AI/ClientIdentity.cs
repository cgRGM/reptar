#if !NETWASM
namespace Reptar.AI;

/// <summary>
/// How requests identify the application to a provider. Ported from pi's
/// <c>ClientAttribution</c>.
/// </summary>
/// <param name="Name">Application name, shown in provider dashboards and rankings.</param>
/// <param name="Url">Application URL. OpenRouter uses it as <c>HTTP-Referer</c>.</param>
/// <param name="Categories">
/// Free-form categories. OpenRouter groups apps by these, so <c>cli-agent</c> is what puts an
/// agent in the agent leaderboard rather than the generic bucket.
/// </param>
public sealed record ClientAttribution(
    string Name,
    string? Url = null,
    IReadOnlyList<string>? Categories = null);

/// <summary>
/// Builds the provider-specific attribution headers for a request.
/// </summary>
/// <remarks>
/// This is not decoration. OpenRouter only credits an application in its rankings when the
/// request carries <c>HTTP-Referer</c> and <c>X-OpenRouter-Title</c>; without them every call is
/// attributed to "unknown" and the app never appears. NVIDIA NIM bills against an origin string
/// and rejects requests without it on some plans. The header names differ per provider, which is
/// the whole reason this is a lookup rather than one constant.
/// </remarks>
public static class ClientIdentity
{
    /// <summary>Reptar's own attribution, used unless a host overrides it.</summary>
    public static readonly ClientAttribution Reptar =
        new("reptar", "https://github.com/RocktownLabs/reptar", ["cli-agent"]);

    private const string OpenRouterHost = "openrouter.ai";
    private const string OpenCodeHost = "opencode.ai";
    private const string NvidiaHost = "nvidia.com";

    /// <summary>
    /// Attribution headers for a request target, or an empty map when attribution is off.
    /// </summary>
    /// <param name="providerKey">Provider registry key.</param>
    /// <param name="baseUrl">Resolved base URL, used to recognise gateways by host.</param>
    /// <param name="attribution">Attribution to send. Null disables it.</param>
    /// <param name="sessionId">
    /// Provider-facing session id. OpenCode routes per-conversation with it, which is how its
    /// prompt cache stays warm across turns.
    /// </param>
    public static Dictionary<string, string> Headers(
        string providerKey,
        string? baseUrl,
        ClientAttribution? attribution,
        string? sessionId = null)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var isOpenRouter = Matches(providerKey, LlmProviders.OpenRouter) || HostMatches(baseUrl, OpenRouterHost);
        var isOpenCode = Matches(providerKey, LlmProviders.OpenCode) || HostMatches(baseUrl, OpenCodeHost);
        var isNvidia = HostMatches(baseUrl, NvidiaHost);

        if (attribution is not null)
        {
            if (isOpenRouter)
            {
                if (attribution.Url is { Length: > 0 } url)
                    headers["HTTP-Referer"] = url;

                headers["X-OpenRouter-Title"] = attribution.Name;

                if (attribution.Categories is { Count: > 0 } categories)
                    headers["X-OpenRouter-Categories"] = string.Join(',', categories);
            }
            else if (isNvidia)
            {
                // NVIDIA expects a capitalised origin, for example "Reptar".
                headers["X-BILLING-INVOKE-ORIGIN"] = char.ToUpperInvariant(attribution.Name[0]) + attribution.Name[1..];
            }
            else if (isOpenCode)
            {
                headers["x-opencode-client"] = attribution.Name;
            }
        }

        // The session header is independent of attribution: it is routing, not identity.
        if (isOpenCode && sessionId is { Length: > 0 })
            headers["x-opencode-session"] = sessionId;

        return headers;
    }

    /// <summary>Merges attribution headers into a request's header map.</summary>
    public static void Apply(
        IDictionary<string, string> target,
        string providerKey,
        string? baseUrl,
        ClientAttribution? attribution,
        string? sessionId = null)
    {
        foreach (var (key, value) in Headers(providerKey, baseUrl, attribution, sessionId))
            target[key] = value;
    }

    private static bool Matches(string providerKey, string expected) =>
        string.Equals(providerKey, expected, StringComparison.OrdinalIgnoreCase);

    private static bool HostMatches(string? baseUrl, string host)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return false;

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
            return false;

        return uri.Host.EndsWith(host, StringComparison.OrdinalIgnoreCase);
    }
}
#endif
