using Reptar.AI;

namespace Reptar.Tests;

/// <summary>
/// Contract tests for provider base-URL templating and the "what is still missing" check.
/// Cloudflare needs both: its URL contains account and gateway ids, and a missing one produces an
/// auth-looking failure rather than a configuration one.
/// </summary>
public sealed class ProviderFieldsTests
{
    private static ProviderInfo WorkersAi => LlmProviders.Find(LlmProviders.CloudflareWorkersAi)!;
    private static ProviderInfo Gateway => LlmProviders.Find(LlmProviders.CloudflareGateway)!;

    // ------------------------------------------------------------------
    // Registry
    // ------------------------------------------------------------------

    [Fact]
    public void cloudflare_workers_ai_is_registered_with_its_account_field()
    {
        Assert.NotNull(WorkersAi);
        Assert.Equal("Cloudflare Workers AI", WorkersAi.DisplayName);
        Assert.True(WorkersAi.HasUrlTemplate);

        var field = Assert.Single(WorkersAi.ExtraFields);
        Assert.Equal("account_id", field.Name);
        Assert.Equal("CLOUDFLARE_ACCOUNT_ID", field.EnvVar);
    }

    [Fact]
    public void cloudflare_ai_gateway_needs_both_ids()
    {
        Assert.Equal(2, Gateway.ExtraFields.Count);
        Assert.Contains(Gateway.ExtraFields, field => field.Name == "account_id");
        Assert.Contains(Gateway.ExtraFields, field => field.Name == "gateway_id");
    }

    [Fact]
    public void a_provider_without_placeholders_is_not_a_template()
    {
        Assert.False(LlmProviders.Find(LlmProviders.Anthropic)!.HasUrlTemplate);
    }

    // ------------------------------------------------------------------
    // URL resolution
    // ------------------------------------------------------------------

    [Fact]
    public void the_workers_ai_url_fills_in_the_account_id()
    {
        var credential = new ProviderCredential(
            ApiKey: "k", BaseUrl: null, DefaultModel: null,
            Extra: new Dictionary<string, string> { ["account_id"] = "abc123" });

        var url = LlmProviders.ResolveBaseUrl(WorkersAi, null, credential);

        Assert.Equal("https://api.cloudflare.com/client/v4/accounts/abc123/ai/v1", url);
        Assert.DoesNotContain('{', url);
    }

    [Fact]
    public void the_gateway_url_fills_in_both_ids()
    {
        var credential = new ProviderCredential(
            ApiKey: "k", BaseUrl: null, DefaultModel: null,
            Extra: new Dictionary<string, string>
            {
                ["account_id"] = "abc",
                ["gateway_id"] = "gw1"
            });

        var url = LlmProviders.ResolveBaseUrl(Gateway, null, credential);

        Assert.Contains("/v1/abc/gw1/", url);
        Assert.DoesNotContain('{', url);
    }

    [Fact]
    public void an_unfilled_placeholder_stays_visible_so_it_cannot_fail_silently()
    {
        var url = LlmProviders.ResolveBaseUrl(WorkersAi, null, null);

        Assert.Contains("{account_id}", url);
    }

    [Fact]
    public void an_explicit_url_wins_over_the_template()
    {
        var url = LlmProviders.ResolveBaseUrl(
            WorkersAi, "https://proxy.internal/v1", saved: null);

        Assert.Equal("https://proxy.internal/v1", url);
    }

    [Fact]
    public void a_plain_provider_url_passes_through()
    {
        var anthropic = LlmProviders.Find(LlmProviders.Anthropic)!;

        Assert.Equal(anthropic.DefaultBaseUrl, LlmProviders.ResolveBaseUrl(anthropic, null, null));
    }

    // ------------------------------------------------------------------
    // Requirements
    // ------------------------------------------------------------------

    [Fact]
    public void a_missing_key_is_reported()
    {
        var missing = LlmProviders.MissingRequirements(WorkersAi, saved: null, apiKey: null);

        Assert.Contains("api key", missing);
    }

    [Fact]
    public void a_missing_account_id_is_reported_even_when_the_key_is_present()
    {
        var missing = LlmProviders.MissingRequirements(WorkersAi, saved: null, apiKey: "sk-test");

        Assert.Contains("Cloudflare account id", missing);
        Assert.DoesNotContain("api key", missing);
    }

    [Fact]
    public void nothing_is_missing_once_every_field_is_supplied()
    {
        var credential = new ProviderCredential(
            ApiKey: "k", BaseUrl: null, DefaultModel: null,
            Extra: new Dictionary<string, string>
            {
                ["account_id"] = "abc",
                ["gateway_id"] = "gw"
            });

        Assert.Empty(LlmProviders.MissingRequirements(Gateway, credential, apiKey: "k"));
    }

    [Fact]
    public void a_provider_that_needs_no_key_reports_nothing_missing()
    {
        var ollama = LlmProviders.Find(LlmProviders.Ollama)!;

        Assert.Empty(LlmProviders.MissingRequirements(ollama, saved: null, apiKey: null));
    }

    // ------------------------------------------------------------------
    // End to end
    // ------------------------------------------------------------------

    [Fact]
    public void a_configured_workers_ai_provider_resolves_to_a_usable_config()
    {
        var path = Path.Combine(Path.GetTempPath(), $"reptar-auth-{Guid.NewGuid():N}.json");

        try
        {
            var auth = new LlmAuthFile(path);
            auth.Set(LlmProviders.CloudflareWorkersAi, new ProviderCredential(
                ApiKey: "sk-cf",
                BaseUrl: null,
                DefaultModel: "@cf/meta/llama-3.3-70b-instruct-fp8-fast",
                Extra: new Dictionary<string, string> { ["account_id"] = "acct" }));

            // The credential's fields drive the URL, so the resolved config is directly usable.
            var url = LlmProviders.ResolveBaseUrl(
                WorkersAi, null, auth.Get(LlmProviders.CloudflareWorkersAi));

            Assert.Contains("/accounts/acct/", url);
            Assert.DoesNotContain('{', url);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
