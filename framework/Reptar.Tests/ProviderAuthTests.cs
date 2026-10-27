using Reptar.AI;

namespace Reptar.Tests;

/// <summary>
/// Contract tests for the auth-method split. pi offers two provider lists — subscription-backed
/// and API-key — and they are different sets, so the capability has to be recorded per provider.
/// </summary>
public sealed class ProviderAuthTests
{
    [Fact]
    public void providers_that_support_account_sign_in_are_marked()
    {
        foreach (var key in new[]
                 {
                     LlmProviders.Anthropic,
                     LlmProviders.OpenAi,
                     LlmProviders.OpenRouter,
                     LlmProviders.OpenCode
                 })
        {
            Assert.True(LlmProviders.Find(key)!.SupportsAccountSignIn, key);
        }
    }

    [Fact]
    public void api_key_only_providers_are_not_offered_account_sign_in()
    {
        foreach (var key in new[]
                 {
                     LlmProviders.Gemini,
                     LlmProviders.Ollama,
                     LlmProviders.CloudflareWorkersAi,
                     LlmProviders.CloudflareGateway
                 })
        {
            Assert.False(LlmProviders.Find(key)!.SupportsAccountSignIn, key);
        }
    }

    [Fact]
    public void the_account_list_is_a_strict_subset_of_the_api_key_list()
    {
        var account = LlmProviders.Known.Where(provider => provider.SupportsAccountSignIn).ToList();

        Assert.NotEmpty(account);
        Assert.True(account.Count < LlmProviders.Known.Length,
            "the two lists should not be identical, or the split is pointless");

        foreach (var provider in account)
            Assert.Contains(provider.Key, LlmProviders.Known.Select(known => known.Key));
    }

    [Fact]
    public void a_provider_defaults_to_api_key_auth()
    {
        var custom = new ProviderInfo(
            "custom", "Custom", [], null, "https://example.test/v1", "model");

        Assert.Equal(ProviderAuth.ApiKey, custom.Auth);
        Assert.False(custom.SupportsAccountSignIn);
    }

    [Fact]
    public void both_means_either_method_works()
    {
        var both = new ProviderInfo(
            "either", "Either", [], null, "https://example.test/v1", "model",
            Auth: ProviderAuth.Both);

        Assert.True(both.SupportsAccountSignIn);
    }

    [Fact]
    public void account_only_providers_still_support_account_sign_in()
    {
        var accountOnly = new ProviderInfo(
            "sub", "Subscription", [], null, "https://example.test/v1", "model",
            Auth: ProviderAuth.Account);

        Assert.True(accountOnly.SupportsAccountSignIn);
    }
}
