using Reptar.AI;

namespace Reptar.Tests;

/// <summary>
/// Tests for storing a ChatGPT subscription: OAuth tokens ride in the credential's field map, and
/// the refresh decision has to be made from a persisted expiry.
/// </summary>
public sealed class ChatGptSubscriptionTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"reptar-auth-{Guid.NewGuid():n}.json");

    public void Dispose()
    {
        try
        {
            if (File.Exists(_path))
                File.Delete(_path);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    [Fact]
    public void oauth_fields_survive_a_save_and_reload()
    {
        var auth = new LlmAuthFile(_path);
        auth.Set(ChatGptSubscription.ProviderKey, new ProviderCredential(
            ApiKey: "access-1",
            BaseUrl: "https://api.openai.com/v1",
            DefaultModel: "gpt-5.1-codex",
            Extra: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["access_token"] = "access-1",
                ["refresh_token"] = "refresh-1",
                ["expires_at"] = "2030-01-01T00:00:00.0000000+00:00",
                ["client_id"] = "issued-client"
            }));

        // A separate instance so the value really came off disk.
        var reloaded = new LlmAuthFile(_path).Get(ChatGptSubscription.ProviderKey);

        Assert.NotNull(reloaded);
        Assert.Equal("access-1", reloaded.Fields["access_token"]);
        Assert.Equal("refresh-1", reloaded.Fields["refresh_token"]);
        Assert.Equal("issued-client", reloaded.Fields["client_id"]);
        Assert.Equal("gpt-5.1-codex", reloaded.DefaultModel);
    }

    [Fact]
    public async Task a_fresh_token_is_returned_without_refreshing()
    {
        var auth = new LlmAuthFile(_path);
        auth.Set(ChatGptSubscription.ProviderKey, new ProviderCredential(
            ApiKey: "access-1",
            BaseUrl: null,
            DefaultModel: null,
            Extra: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["access_token"] = "access-1",
                ["refresh_token"] = "refresh-1",
                ["expires_at"] = DateTimeOffset.UtcNow.AddHours(1).ToString("O"),
                ["client_id"] = "issued-client"
            }));

        Assert.Equal("access-1", await ChatGptSubscription.GetAccessTokenAsync(auth));
    }

    [Fact]
    public async Task a_missing_credential_yields_no_token()
    {
        var auth = new LlmAuthFile(_path);
        Assert.Null(await ChatGptSubscription.GetAccessTokenAsync(auth));
    }

    [Fact]
    public void the_provider_is_registered_for_account_sign_in()
    {
        var provider = LlmProviders.Find(ChatGptSubscription.ProviderKey);

        Assert.NotNull(provider);
        Assert.Equal(ProviderAuth.Account, provider.Auth);
        Assert.False(provider.RequiresApiKey);
    }
}
