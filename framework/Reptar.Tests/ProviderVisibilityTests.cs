using Reptar.AI;

namespace Reptar.Tests;

/// <summary>
/// Which providers may appear in a list of options. A provider that cannot serve a request must not
/// be offered: picking it produces a 401 that reads like a bug in the model rather than a missing
/// credential.
/// </summary>
public sealed class ProviderVisibilityTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"reptar-vis-{Guid.NewGuid():n}.json");

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
    public void an_unconnected_provider_is_not_connected()
    {
        var auth = new LlmAuthFile(_path);

        Assert.False(LlmProviders.IsConnected(LlmProviders.OpenAi, auth));
    }

    [Fact]
    public void a_saved_credential_connects_a_provider()
    {
        var auth = new LlmAuthFile(_path);
        auth.Set(LlmProviders.OpenAi, new ProviderCredential("sk-1", null, "gpt-5.6"));

        Assert.True(LlmProviders.IsConnected(LlmProviders.OpenAi, auth));
    }

    [Fact]
    public void a_provider_that_needs_no_key_is_still_not_connected_until_chosen()
    {
        var auth = new LlmAuthFile(_path);

        // The subscription requires no API key, which is exactly why the old rule leaked it into
        // the picker while signed out.
        var provider = LlmProviders.Find(LlmProviders.OpenAiChatGpt);
        Assert.NotNull(provider);
        Assert.False(provider.RequiresApiKey);

        Assert.False(LlmProviders.IsConnected(LlmProviders.OpenAiChatGpt, auth));

        auth.Set(LlmProviders.OpenAiChatGpt, new ProviderCredential("token", null, "gpt-5.1-codex"));
        Assert.True(LlmProviders.IsConnected(LlmProviders.OpenAiChatGpt, auth));
    }

    [Fact]
    public void a_local_provider_becomes_connected_once_recorded()
    {
        var auth = new LlmAuthFile(_path);

        Assert.False(LlmProviders.IsConnected(LlmProviders.Ollama, auth));

        // Choosing it in /connect stores a credential even with no key, so it then counts.
        auth.Set(LlmProviders.Ollama, new ProviderCredential(null, "http://127.0.0.1:11434", "llama3.1:8b"));

        Assert.True(LlmProviders.IsConnected(LlmProviders.Ollama, auth));
    }

    [Fact]
    public void an_environment_variable_connects_a_provider_without_a_saved_credential()
    {
        var auth = new LlmAuthFile(_path);
        var previous = Environment.GetEnvironmentVariable("OPENAI_API_KEY");

        try
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", "sk-from-env");

            Assert.True(LlmProviders.IsConnected(LlmProviders.OpenAi, auth));
            Assert.Equal("env", LlmProviders.CredentialSource(LlmProviders.OpenAi, auth).Source);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", previous);
        }
    }

    [Fact]
    public void a_credential_missing_required_fields_is_not_connected()
    {
        var auth = new LlmAuthFile(_path);

        // A gateway URL is built from an account id and a gateway id. A credential without them
        // cannot serve a request, so it must not be offered as an option even though it exists.
        auth.Set(LlmProviders.CloudflareGateway, new ProviderCredential(
            "token", null, "gpt-5.1", new Dictionary<string, string>()));

        Assert.Equal("saved", LlmProviders.CredentialSource(LlmProviders.CloudflareGateway, auth).Source);
        Assert.False(LlmProviders.IsConnected(LlmProviders.CloudflareGateway, auth));
    }

    [Fact]
    public void a_complete_credential_is_connected()
    {
        var auth = new LlmAuthFile(_path);

        auth.Set(LlmProviders.CloudflareGateway, new ProviderCredential(
            "token",
            null,
            "gpt-5.1",
            new Dictionary<string, string>
            {
                ["account_id"] = "acc",
                ["gateway_id"] = "gw"
            }));

        Assert.True(LlmProviders.IsConnected(LlmProviders.CloudflareGateway, auth));
    }

    [Fact]
    public void a_disconnected_provider_still_reports_its_status()
    {
        // Status lists keep showing what is missing; only option lists filter.
        var auth = new LlmAuthFile(_path);

        Assert.Equal("• not connected", LlmProviders.FormatStatus(LlmProviders.OpenAi, auth));
    }
}
