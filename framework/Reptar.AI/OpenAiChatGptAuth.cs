#if !NETWASM
namespace Reptar.AI;

/// <summary>
/// "Sign in with ChatGPT" — a ChatGPT Plus/Pro subscription used as a provider.
/// </summary>
/// <remarks>
/// A public-client PKCE flow against <c>auth.openai.com</c>, with the resulting access token sent
/// to <c>api.openai.com</c>. No client secret exists, which is why PKCE matters: without it an
/// intercepted authorization code would be redeemable by anyone.
///
/// The client id is the literal <c>dynamic_agent_client</c>, and OpenAI performs dynamic client
/// registration — the issued client id comes back on the callback and must be used for the token
/// exchange and every later refresh. That detail is easy to miss and produces a confusing
/// "invalid_client" if the placeholder is reused.
///
/// The <c>chatgpt.tokens.use.direct</c> scope is what permits calling the API with a subscription
/// token; the grant must include it, and the code checks rather than discovering it later as a 401.
/// </remarks>
public static class OpenAiChatGptAuth
{
    /// <summary>Where the browser is sent to authorize.</summary>
    public const string AuthorizeUrl = "https://auth.openai.com/api/accounts/authorize";

    /// <summary>Where the authorization code is exchanged and refreshed.</summary>
    public const string TokenUrl = "https://auth.openai.com/api/accounts/oauth/token";

    /// <summary>Placeholder client id; OpenAI issues the real one on the callback.</summary>
    public const string PlaceholderClientId = "dynamic_agent_client";

    /// <summary>API audience the token is scoped to.</summary>
    public const string Resource = "https://api.openai.com/v1";

    /// <summary>Scope that allows the token to be used directly against the API.</summary>
    public const string DirectTokenScope = "chatgpt.tokens.use.direct";

    /// <summary>Full scope list requested at authorization.</summary>
    public const string Scope =
        "openid profile email offline_access resource.invoke " + DirectTokenScope;

    /// <summary>Fixed callback port. OpenAI requires the redirect URI to match exactly.</summary>
    public const int CallbackPort = 1455;

    /// <summary>Callback path.</summary>
    public const string CallbackPath = "/auth/callback";

    /// <summary>The redirect URI that must be registered for the client.</summary>
    public static string RedirectUri => $"http://127.0.0.1:{CallbackPort}{CallbackPath}";

    /// <summary>
    /// Runs the whole sign-in: opens the browser, waits for the loopback callback, exchanges the
    /// code, and returns tokens.
    /// </summary>
    /// <param name="onUrl">
    /// Called with the authorization URL before the browser opens, so a caller can print it for a
    /// headless session.
    /// </param>
    /// <param name="openBrowser">False to only print the URL.</param>
    public static async Task<OAuthTokens> SignInAsync(
        Action<string>? onUrl = null,
        bool openBrowser = true,
        CancellationToken cancellationToken = default)
    {
        var pkce = OAuthPkce.CreateChallenge();
        var state = OAuthPkce.CreateState();

        var url =
            $"{AuthorizeUrl}?response_type=code"
            + $"&client_id={Uri.EscapeDataString(PlaceholderClientId)}"
            + $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}"
            + $"&scope={Uri.EscapeDataString(Scope)}"
            + $"&state={Uri.EscapeDataString(state)}"
            + $"&code_challenge={Uri.EscapeDataString(pkce.Challenge)}"
            + "&code_challenge_method=S256"
            + $"&resource={Uri.EscapeDataString(Resource)}";

        onUrl?.Invoke(url);

        if (openBrowser && !OAuthPkce.TryOpenBrowser(url))
        {
            // Printing the URL is the fallback; the caller already got it through onUrl.
        }

        var callback = await OAuthPkce
            .WaitForCallbackAsync(CallbackPort, CallbackPath, state, cancellationToken)
            .ConfigureAwait(false);

        // OpenAI returns the client id it registered for this request.
        var clientId = callback.IssuedClientId is { Length: > 0 } issued
            ? issued
            : PlaceholderClientId;

        var tokens = await OAuthPkce.ExchangeAsync(
            TokenUrl, callback.Code, pkce.Verifier, RedirectUri, clientId, Resource, cancellationToken)
            .ConfigureAwait(false);

        if (!tokens.Scopes.Contains(DirectTokenScope, StringComparer.Ordinal))
        {
            throw new OAuthException(
                $"The ChatGPT grant did not include {DirectTokenScope}, so the token cannot be used "
                + "against the API. The account may not have an eligible subscription.");
        }

        return tokens;
    }

    /// <summary>Refreshes an expired ChatGPT access token.</summary>
    public static Task<OAuthTokens> RefreshAsync(
        OAuthTokens tokens,
        CancellationToken cancellationToken = default)
    {
        if (tokens.RefreshToken is not { Length: > 0 } refresh)
            throw new OAuthException("No refresh token is stored; sign in again.");

        return OAuthPkce.RefreshAsync(
            TokenUrl,
            refresh,
            tokens.ClientId ?? PlaceholderClientId,
            Resource,
            cancellationToken);
    }
}

/// <summary>
/// Sign-in and token handling for "Sign in with ChatGPT".
/// </summary>
/// <remarks>
/// A subscription token is short-lived and refreshes on a different schedule from an API key, so
/// it is stored with its expiry and refreshed before use rather than being treated as a static
/// credential. The refresh token rotates: the new one must be persisted or the next refresh fails.
/// </remarks>
public static class ChatGptSubscription
{
    /// <summary>Provider key used for a ChatGPT subscription.</summary>
    public const string ProviderKey = "openai-chatgpt";

    private const string AccessField = "access_token";
    private const string RefreshField = "refresh_token";
    private const string ExpiresField = "expires_at";
    private const string ClientIdField = "client_id";

    /// <summary>
    /// Runs the browser sign-in and stores the result. Returns the provider key.
    /// </summary>
    public static async Task<ProviderCredential> SignInAsync(
        LlmAuthFile auth,
        Action<string>? onUrl = null,
        Action<string>? onStatus = null,
        CancellationToken cancellationToken = default)
    {
        onStatus?.Invoke("opening the browser to sign in with ChatGPT…");

        var tokens = await OpenAiChatGptAuth
            .SignInAsync(onUrl, openBrowser: true, cancellationToken)
            .ConfigureAwait(false);

        var credential = new ProviderCredential(
            ApiKey: tokens.AccessToken,
            BaseUrl: OpenAiChatGptAuth.Resource,
            DefaultModel: "gpt-5.1-codex",
            Extra: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AccessField] = tokens.AccessToken,
                [RefreshField] = tokens.RefreshToken ?? "",
                [ExpiresField] = tokens.ExpiresAt.ToString("O"),
                [ClientIdField] = tokens.ClientId ?? OpenAiChatGptAuth.PlaceholderClientId
            });

        auth.Set(ProviderKey, credential);
        onStatus?.Invoke("signed in.");
        return credential;
    }

    /// <summary>
    /// Returns a usable access token, refreshing and re-persisting when it is close to expiring.
    /// </summary>
    public static async Task<string?> GetAccessTokenAsync(
        LlmAuthFile auth,
        CancellationToken cancellationToken = default)
    {
        var credential = auth.Get(ProviderKey);
        if (credential is null)
            return null;

        var access = credential.Fields.GetValueOrDefault(AccessField) ?? credential.ApiKey;
        if (access is not { Length: > 0 })
            return null;

        var expires = DateTimeOffset.TryParse(
            credential.Fields.GetValueOrDefault(ExpiresField), out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

        // Refresh a few minutes early so a request never starts with a token about to lapse.
        if (DateTimeOffset.UtcNow < expires - TimeSpan.FromMinutes(3))
            return access;

        var refresh = credential.Fields.GetValueOrDefault(RefreshField);
        var clientId = credential.Fields.GetValueOrDefault(ClientIdField)
            ?? OpenAiChatGptAuth.PlaceholderClientId;

        if (refresh is not { Length: > 0 })
            return access;

        try
        {
            var tokens = await OAuthPkce.RefreshAsync(
                OpenAiChatGptAuth.TokenUrl,
                refresh,
                clientId,
                OpenAiChatGptAuth.Resource,
                cancellationToken).ConfigureAwait(false);

            var fields = new Dictionary<string, string>(credential.Fields, StringComparer.Ordinal)
            {
                [AccessField] = tokens.AccessToken,
                // The refresh token rotates; keeping the old one breaks the next refresh.
                [RefreshField] = tokens.RefreshToken ?? refresh,
                [ExpiresField] = tokens.ExpiresAt.ToString("O")
            };

            auth.Set(ProviderKey, credential with { ApiKey = tokens.AccessToken, Extra = fields });
            return tokens.AccessToken;
        }
        catch (OAuthException)
        {
            // A failed refresh means the grant is gone; the caller will surface a 401 and the
            // user can sign in again.
            return access;
        }
    }
}
#endif
