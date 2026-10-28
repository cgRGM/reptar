#if !NETWASM
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reptar.AI;

/// <summary>
/// A known LLM provider: how to detect it, where it lives, and what it defaults to.
/// Mirrors the provider registry approach used by pi (provider catalog) and
/// opencode (models.dev), scoped to the wire formats Reptar speaks natively.
/// </summary>
/// <summary>
/// One additional value a provider needs beyond its API key, such as a Cloudflare account id.
/// </summary>
/// <param name="Name">Placeholder name, substituted into <see cref="ProviderInfo.DefaultBaseUrl"/>.</param>
/// <param name="Label">What to ask the user for.</param>
/// <param name="EnvVar">Environment variable that can supply it instead.</param>
/// <param name="Required">Whether the provider cannot work without it.</param>
public sealed record ProviderField(string Name, string Label, string EnvVar, bool Required = true);

/// <summary>How a provider can be signed in to.</summary>
public enum ProviderAuth
{
    /// <summary>An API key or token, entered by the user.</summary>
    ApiKey,

    /// <summary>A coding subscription signed in through the browser (OAuth device flow).</summary>
    Account,

    /// <summary>Either method works.</summary>
    Both
}

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
    bool RequiresBaseUrl = false,
    IReadOnlyList<ProviderField>? Fields = null,
    ProviderAuth Auth = ProviderAuth.ApiKey)
{
    public bool Matches(string name) => Key.Equals(name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Extra values this provider needs, if any.</summary>
    public IReadOnlyList<ProviderField> ExtraFields => Fields ?? [];

    /// <summary>True when the base URL has <c>{placeholder}</c> segments to fill in.</summary>
    public bool HasUrlTemplate => DefaultBaseUrl.Contains('{', StringComparison.Ordinal);

    /// <summary>True when this provider can be signed in to with a coding subscription.</summary>
    public bool SupportsAccountSignIn => Auth is ProviderAuth.Account or ProviderAuth.Both;
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
    public const string CloudflareWorkersAi = "cloudflare-workers-ai";

    /// <summary>A ChatGPT Plus/Pro subscription, signed in through the browser.</summary>
    public const string OpenAiChatGpt = "openai-chatgpt";
    public const string Echo = "echo";

    public static readonly ProviderInfo[] Known =
    [
        new(OpenAi, "OpenAI",
            ["OPENAI_API_KEY"], "OPENAI_BASE_URL",
            "https://api.openai.com/v1", "gpt-4o-mini",
            KeyUrl: "platform.openai.com/api-keys",
            Auth: ProviderAuth.Both),
        new(Anthropic, "Anthropic (Claude)",
            ["ANTHROPIC_API_KEY"], "ANTHROPIC_BASE_URL",
            "https://api.anthropic.com", "claude-sonnet-4-5",
            KeyUrl: "console.anthropic.com/settings/keys",
            Auth: ProviderAuth.Both),
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
            KeyUrl: "openrouter.ai/settings/keys",
            Auth: ProviderAuth.Both),
        new(Gateway, "Vercel AI Gateway",
            ["AI_GATEWAY_API_KEY"], null,
            "https://ai-gateway.vercel.sh/v1", "openai/gpt-4o-mini",
            KeyUrl: "vercel.com/ai-gateway"),
        new(OpenCode, "OpenCode Zen",
            ["OPENCODE_API_KEY"], null,
            "https://opencode.ai/zen/v1", "anthropic/claude-sonnet-5-5",
            KeyUrl: "opencode.ai/zen",
            Auth: ProviderAuth.Both),
        new(OpenAiChatGpt, "OpenAI (ChatGPT subscription)",
            [], null,
            "https://api.openai.com/v1",
            "gpt-5.1-codex",
            RequiresApiKey: false,
            KeyUrl: "chatgpt.com",
            Note: "Sign in with your ChatGPT account — no API key needed",
            Auth: ProviderAuth.Account),
        new(CloudflareWorkersAi, "Cloudflare Workers AI",
            ["CLOUDFLARE_API_KEY"], null,
            "https://api.cloudflare.com/client/v4/accounts/{account_id}/ai/v1",
            "@cf/meta/llama-3.3-70b-instruct-fp8-fast",
            KeyUrl: "dash.cloudflare.com/profile/api-tokens",
            Note: "Runs @cf/* models on Cloudflare's edge; needs your account id",
            Fields:
            [
                new("account_id", "Cloudflare account id", "CLOUDFLARE_ACCOUNT_ID")
            ]),
        new(CloudflareGateway, "Cloudflare AI Gateway",
            ["CLOUDFLARE_API_KEY"], null,
            "https://gateway.ai.cloudflare.com/v1/{account_id}/{gateway_id}/compat/chat/completions",
            "openai/gpt-4o-mini",
            KeyUrl: "dash.cloudflare.com → AI Gateway",
            Note: "Proxies any provider through your gateway; needs account + gateway ids",
            Fields:
            [
                new("account_id", "Cloudflare account id", "CLOUDFLARE_ACCOUNT_ID"),
                new("gateway_id", "AI Gateway id", "CLOUDFLARE_GATEWAY_ID")
            ])
    ];

    /// <summary>
    /// Resolves a provider's base URL, substituting any <c>{placeholder}</c> segments from the
    /// saved credential or the environment.
    /// </summary>
    /// <remarks>
    /// Cloudflare needs this: Workers AI lives at
    /// <c>.../accounts/{account_id}/ai/v1</c> and AI Gateway at
    /// <c>.../v1/{account_id}/{gateway_id}/…</c>. Without the substitution the request goes to a
    /// URL containing a literal <c>{account_id}</c>, which fails in a way that looks like an auth
    /// error rather than a configuration one.
    /// </remarks>
    public static string ResolveBaseUrl(
        ProviderInfo info,
        string? explicitUrl,
        ProviderCredential? saved)
    {
        var template = explicitUrl
            ?? saved?.BaseUrl
            ?? info.DefaultBaseUrl;

        if (!template.Contains('{', StringComparison.Ordinal))
            return template;

        var resolved = template;

        foreach (var field in info.ExtraFields)
        {
            var value = FirstNonEmpty(
                saved?.Fields.GetValueOrDefault(field.Name),
                Environment.GetEnvironmentVariable(field.EnvVar));

            if (value is { Length: > 0 })
                resolved = resolved.Replace("{" + field.Name + "}", value, StringComparison.Ordinal);
        }

        return resolved;
    }

    /// <summary>
    /// Values still missing for a provider to work: a required key, or a required placeholder
    /// with no value in the credential or the environment.
    /// </summary>
    public static IReadOnlyList<string> MissingRequirements(
        ProviderInfo info,
        ProviderCredential? saved,
        string? apiKey)
    {
        var missing = new List<string>();

        if (info.RequiresApiKey && string.IsNullOrEmpty(apiKey) && string.IsNullOrEmpty(saved?.ApiKey))
            missing.Add("api key");

        foreach (var field in info.ExtraFields)
        {
            if (!field.Required)
                continue;

            var value = FirstNonEmpty(
                saved?.Fields.GetValueOrDefault(field.Name),
                Environment.GetEnvironmentVariable(field.EnvVar));

            if (value is null or { Length: 0 })
                missing.Add(field.Label);
        }

        return missing;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

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
    /// True when a provider has a usable credential.
    /// </summary>
    /// <remarks>
    /// "Connected" means a stored credential or an environment variable — nothing else. A provider
    /// that does not require a key is still not connected until it has been chosen, because
    /// otherwise it appears in model pickers it cannot serve. Lists of <em>options</em> filter on
    /// this; lists of <em>status</em> do not, since showing what is missing is their whole job.
    /// </remarks>
    public static bool IsConnected(string providerKey, LlmAuthFile auth)
    {
        var (source, detail) = CredentialSource(providerKey, auth);

        if (source == "none")
            return false;

        // A credential that is present but incomplete cannot serve a request — a gateway URL with
        // no account id, for instance. Treating that as connected offers a provider that fails on
        // first use, which is the failure this check exists to prevent.
        var info = Find(providerKey);
        if (info is null)
            return true;

        // CredentialSource reports the environment variable's *name*, so read its value: passing
        // null here would report the api key as missing for a provider configured from the
        // environment.
        var fromEnvironment = source == "env" && detail is not null
            ? Environment.GetEnvironmentVariable(detail)
            : null;

        return MissingRequirements(info, auth.Get(providerKey), fromEnvironment).Count == 0;
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
    string? DefaultModel,
    IReadOnlyDictionary<string, string>? Extra = null)
{
    /// <summary>Extra values such as an account id, keyed by <see cref="ProviderField.Name"/>.</summary>
    public IReadOnlyDictionary<string, string> Fields => Extra ?? new Dictionary<string, string>();
}

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
                    DefaultModel: value["defaultModel"]?.GetValue<string>(),
                    Extra: value["extra"] is JsonObject extra
                        ? extra.ToDictionary(
                            entry => entry.Key,
                            entry => entry.Value?.GetValue<string>() ?? "",
                            StringComparer.Ordinal)
                        : null);
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
                var entry = new JsonObject
                {
                    ["apiKey"] = credential.ApiKey,
                    ["baseUrl"] = credential.BaseUrl,
                    ["defaultModel"] = credential.DefaultModel
                };

                // OAuth tokens and provider-specific ids (account id, client id) ride here.
                if (credential.Fields.Count > 0)
                {
                    var extra = new JsonObject();
                    foreach (var (field, fieldValue) in credential.Fields)
                        extra[field] = fieldValue;
                    entry["extra"] = extra;
                }

                root[key] = entry;
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
