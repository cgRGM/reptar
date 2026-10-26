#if !NETWASM
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reptar.AI;

/// <summary>
/// A known LLM provider: how to detect it, where it lives, and what it defaults to.
/// Mirrors the provider registry approach used by pi (provider catalog) and
/// opencode (models.dev), scoped to the wire formats Reptar speaks natively.
/// </summary>
public sealed record ProviderInfo(
    string Key,
    string DisplayName,
    string[] EnvKeys,
    string? EnvBaseUrl,
    string DefaultBaseUrl,
    string DefaultModel,
    bool RequiresApiKey = true,
    string? KeyUrl = null,
    string? Note = null,
    bool RequiresBaseUrl = false)
{
    public bool Matches(string name) => Key.Equals(name, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Built-in provider registry. Custom OpenAI-compatible endpoints are stored in
/// the credentials file under any other key and resolved through the same path.
/// </summary>
public static class LlmProviders
{
    public const string OpenAi = "openai";
    public const string Anthropic = "anthropic";
    public const string Gemini = "gemini";
    public const string Ollama = "ollama";
    public const string OpenRouter = "openrouter";
    public const string Gateway = "gateway";
    public const string OpenCode = "opencode";
    public const string CloudflareGateway = "cloudflare-gateway";
    public const string Echo = "echo";

    public static readonly ProviderInfo[] Known =
    [
        new(OpenAi, "OpenAI",
            ["OPENAI_API_KEY"], "OPENAI_BASE_URL",
            "https://api.openai.com/v1", "gpt-4o-mini",
            KeyUrl: "platform.openai.com/api-keys"),
        new(Anthropic, "Anthropic (Claude)",
            ["ANTHROPIC_API_KEY"], "ANTHROPIC_BASE_URL",
            "https://api.anthropic.com", "claude-sonnet-4-5",
            KeyUrl: "console.anthropic.com/settings/keys"),
        new(Gemini, "Google Gemini",
            ["GEMINI_API_KEY", "GOOGLE_API_KEY"], "GEMINI_BASE_URL",
            "https://generativelanguage.googleapis.com", "gemini-2.5-flash",
            KeyUrl: "aistudio.google.com/apikey"),
        new(Ollama, "Ollama (local)",
            ["OLLAMA_HOST"], null,
            "http://localhost:11434", "llama3.2",
            RequiresApiKey: false,
            Note: "no key needed — just a running ollama serve"),
        new(OpenRouter, "OpenRouter",
            ["OPENROUTER_API_KEY"], null,
            "https://openrouter.ai/api/v1", "openai/gpt-4o-mini",
            KeyUrl: "openrouter.ai/settings/keys"),
        new(Gateway, "Vercel AI Gateway",
            ["AI_GATEWAY_API_KEY"], null,
            "https://ai-gateway.vercel.sh/v1", "openai/gpt-4o-mini",
            KeyUrl: "vercel.com/ai-gateway"),
        new(OpenCode, "OpenCode Zen",
            ["OPENCODE_API_KEY"], null,
            "https://opencode.ai/zen/v1", "anthropic/claude-sonnet-5-5",
            KeyUrl: "opencode.ai/zen"),
        new(CloudflareGateway, "Cloudflare AI Gateway",
            [], null,
            "https://gateway.ai.cloudflare.com/v1/{account_id}/{gateway_id}/{provider}",
            "openai/gpt-4o-mini",
            KeyUrl: "dash.cloudflare.com → AI Gateway",
            Note: "URL needs your account + gateway ids; the API key is the upstream provider's",
            RequiresBaseUrl: true)
    ];

    public static ProviderInfo? Find(string key) =>
        Known.FirstOrDefault(p => p.Matches(key));

    public static bool IsKnown(string key) => Find(key) is not null;

    /// <summary>
    /// Where a provider's credential currently comes from:
    /// an environment variable, the saved auth file, or nowhere.
    /// </summary>
    public static (string Source, string? Detail) CredentialSource(
        string providerKey,
        LlmAuthFile auth)
    {
        var info = Find(providerKey);

        if (info is not null)
        {
            foreach (var envKey in info.EnvKeys)
            {
                if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(envKey)))
                    return ("env", envKey);
            }
        }

        if (auth.Get(providerKey) is not null)
            return ("saved", LlmAuthFile.ActivePath);

        return ("none", null);
    }

    /// <summary>
    /// The status line Pi-style: "✓ configured" / "✓ env: OPENAI_API_KEY" / "• not connected".
    /// </summary>
    public static string FormatStatus(string providerKey, LlmAuthFile auth)
    {
        var (source, detail) = CredentialSource(providerKey, auth);

        return source switch
        {
            "env" => "✓ env: " + detail,
            "saved" => "✓ saved",
            _ => "• not connected"
        };
    }
}

/// <summary>
/// One saved provider credential: `~/.reptar/agent/auth.json`
/// (opencode-style flat map, pi-style 0600 permissions).
/// </summary>
public sealed record ProviderCredential(
    string? ApiKey,
    string? BaseUrl,
    string? DefaultModel);

/// <summary>
/// File-backed credential store. One credential per provider key, flat JSON map:
///
/// {
///   "anthropic": { "apiKey": "sk-…", "baseUrl": null, "defaultModel": "claude-sonnet-4-5" },
///   "ollama":    { "apiKey": null, "baseUrl": "http://localhost:11434", "defaultModel": "llama3.2" },
///   "_default":  "anthropic"
/// }
///
/// The `_default` key marks the provider of the last successful /connect so
/// plain `bun reptar agent` uses it when no env var points elsewhere.
/// </summary>
public sealed class LlmAuthFile
{
    /// <summary>Override the location with REPTAR_AGENT_AUTH (used by tests and sandboxes).</summary>
    public static string ActivePath
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("REPTAR_AGENT_AUTH");
            if (!string.IsNullOrWhiteSpace(configured))
                return configured;

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return System.IO.Path.Combine(home, ".reptar", "agent", "auth.json");
        }
    }

    public string Path { get; }
    private readonly Dictionary<string, ProviderCredential> _credentials = new(StringComparer.OrdinalIgnoreCase);
    private string? _default;

    public LlmAuthFile(string? path = null)
    {
        Path = path ?? ActivePath;
        Load();
    }

    public static LlmAuthFile LoadActive() => new(null);

    public IReadOnlyDictionary<string, ProviderCredential> Credentials => _credentials;

    /// <summary>Directory holding auth.json, used by the status display.</summary>
    public static string ActiveDirectory => System.IO.Path.GetDirectoryName(ActivePath) ?? ".";

    public string? DefaultProvider => _credentials.ContainsKey(_default ?? "") ? _default : FirstStored();

    public ProviderCredential? Get(string providerKey) =>
        _credentials.TryGetValue(providerKey, out var credential) ? credential : null;

    public void Set(string providerKey, ProviderCredential credential, bool makeDefault = true)
    {
        _credentials[providerKey] = credential;
        if (makeDefault || _default is null)
            _default = providerKey;
        Save();
    }

    public void SetDefault(string providerKey)
    {
        if (!_credentials.ContainsKey(providerKey))
            return;
        _default = providerKey;
        Save();
    }

    public bool Clear(string providerKey)
    {
        if (!_credentials.Remove(providerKey))
            return false;

        if (string.Equals(_default, providerKey, StringComparison.OrdinalIgnoreCase))
            _default = FirstStored();

        Save();
        return true;
    }

    private string? FirstStored()
    {
        foreach (var key in _credentials.Keys)
        {
            if (!key.StartsWith('_'))
                return key;
        }
        return null;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(Path))
                return;

            var root = JsonNode.Parse(File.ReadAllText(Path)) as JsonObject;
            if (root is null)
                return;

            _default = root["_default"]?.GetValue<string>();

            foreach (var (key, value) in root)
            {
                if (key.StartsWith('_') || value is not JsonObject)
                    continue;

                _credentials[key] = new ProviderCredential(
                    ApiKey: value["apiKey"]?.GetValue<string>(),
                    BaseUrl: value["baseUrl"]?.GetValue<string>(),
                    DefaultModel: value["defaultModel"]?.GetValue<string>());
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable auth file must never stop the agent from booting.
        }
    }

    private void Save()
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var root = new JsonObject();

            foreach (var (key, credential) in _credentials)
            {
                root[key] = new JsonObject
                {
                    ["apiKey"] = credential.ApiKey,
                    ["baseUrl"] = credential.BaseUrl,
                    ["defaultModel"] = credential.DefaultModel
                };
            }

            if (_default is not null)
                root["_default"] = _default;

            File.WriteAllText(Path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            TryRestrictPermissions();
        }
        catch (IOException)
        {
            // Read-only home or sandboxed FS: the credential simply won't persist.
        }
    }

    /// <summary>Best-effort chmod 600 (pi-style) so keys stay private on unix.</summary>
    private void TryRestrictPermissions()
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(Path,
                    System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite);
            }
        }
        catch
        {
            // Platform without unix file modes (Windows) or restricted FS.
        }
    }
}
#endif
