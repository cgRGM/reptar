using Reptar.AI;

namespace Reptar.Tests;

/// <summary>
/// Contract tests for provider attribution headers. These are not decoration: OpenRouter only
/// credits an application in its rankings when they are present.
/// </summary>
public sealed class ClientIdentityTests
{
    private static readonly ClientAttribution Attribution =
        new("reptar", "https://example.test", ["cli-agent", "devtool"]);

    [Fact]
    public void openrouter_gets_referer_title_and_categories()
    {
        var headers = ClientIdentity.Headers(LlmProviders.OpenRouter, null, Attribution);

        Assert.Equal("https://example.test", headers["HTTP-Referer"]);
        Assert.Equal("reptar", headers["X-OpenRouter-Title"]);
        Assert.Equal("cli-agent,devtool", headers["X-OpenRouter-Categories"]);
    }

    [Fact]
    public void openrouter_is_recognised_by_host_even_under_a_custom_provider_key()
    {
        var headers = ClientIdentity.Headers(
            "my-custom", "https://openrouter.ai/api/v1", Attribution);

        Assert.True(headers.ContainsKey("HTTP-Referer"));
    }

    [Fact]
    public void categories_are_omitted_when_there_are_none()
    {
        var headers = ClientIdentity.Headers(
            LlmProviders.OpenRouter, null, new ClientAttribution("app", "https://x.test"));

        Assert.False(headers.ContainsKey("X-OpenRouter-Categories"));
    }

    [Fact]
    public void the_referer_is_omitted_when_no_url_is_configured()
    {
        var headers = ClientIdentity.Headers(
            LlmProviders.OpenRouter, null, new ClientAttribution("app"));

        Assert.False(headers.ContainsKey("HTTP-Referer"));
        Assert.Equal("app", headers["X-OpenRouter-Title"]);
    }

    [Fact]
    public void nvidia_gets_a_capitalised_origin()
    {
        var headers = ClientIdentity.Headers(
            "custom", "https://integrate.api.nvidia.com/v1", new ClientAttribution("reptar"));

        Assert.Equal("Reptar", headers["X-BILLING-INVOKE-ORIGIN"]);
    }

    [Fact]
    public void opencode_gets_its_client_header()
    {
        var headers = ClientIdentity.Headers(LlmProviders.OpenCode, null, Attribution);

        Assert.Equal("reptar", headers["x-opencode-client"]);
    }

    [Fact]
    public void opencode_session_routing_is_independent_of_attribution()
    {
        var headers = ClientIdentity.Headers(
            LlmProviders.OpenCode, null, attribution: null, sessionId: "sess_123");

        Assert.Equal("sess_123", headers["x-opencode-session"]);
        Assert.False(headers.ContainsKey("x-opencode-client"));
    }

    [Fact]
    public void no_attribution_means_no_identity_headers()
    {
        var headers = ClientIdentity.Headers(LlmProviders.OpenRouter, null, attribution: null);

        Assert.Empty(headers);
    }

    [Fact]
    public void a_plain_provider_gets_no_attribution_headers()
    {
        var headers = ClientIdentity.Headers(LlmProviders.OpenAi, null, Attribution);

        Assert.Empty(headers);
    }

    [Fact]
    public void apply_merges_into_an_existing_header_map()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["authorization"] = "Bearer sk-test"
        };

        ClientIdentity.Apply(headers, LlmProviders.OpenRouter, null, Attribution);

        Assert.Equal("Bearer sk-test", headers["authorization"]);
        Assert.True(headers.ContainsKey("HTTP-Referer"));
    }

    [Fact]
    public void the_default_attribution_names_reptar_as_a_cli_agent()
    {
        Assert.Equal("reptar", ClientIdentity.Reptar.Name);
        Assert.Contains("cli-agent", ClientIdentity.Reptar.Categories!);
    }
}
