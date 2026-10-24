using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Reptar;

public sealed record OAuthUserInfo(
    string Provider,
    string ProviderUserId,
    string Email,
    string Name,
    string? Image = null);

public interface IOAuthProvider
{
    string ProviderName { get; }
    string BuildAuthorizationUrl(string state, string codeChallenge, string redirectUri);
    Task<OAuthUserInfo> ExchangeCodeAsync(string code, string codeVerifier, string redirectUri);
}

public static class Pkce
{
    public static string GenerateCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64UrlEncode(bytes);
    }

    public static string GenerateCodeChallenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64UrlEncode(hash);
    }

    public static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}

public sealed class GoogleOAuthProvider : IOAuthProvider
{
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>>? _httpHandler;
    private static readonly HttpClient _defaultHttp = new();

    public GoogleOAuthProvider(
        string clientId,
        string clientSecret,
        Func<HttpRequestMessage, Task<HttpResponseMessage>>? httpHandler = null)
    {
        _clientId = clientId;
        _clientSecret = clientSecret;
        _httpHandler = httpHandler;
    }

    public string ProviderName => "google";

    public string BuildAuthorizationUrl(string state, string codeChallenge, string redirectUri) =>
        "https://accounts.google.com/o/oauth2/v2/auth?" +
        $"client_id={Uri.EscapeDataString(_clientId)}&" +
        $"redirect_uri={Uri.EscapeDataString(redirectUri)}&" +
        "response_type=code&" +
        "scope=openid%20email%20profile&" +
        $"state={Uri.EscapeDataString(state)}&" +
        $"code_challenge={Uri.EscapeDataString(codeChallenge)}&" +
        "code_challenge_method=S256&" +
        "access_type=offline&prompt=consent";

    public async Task<OAuthUserInfo> ExchangeCodeAsync(string code, string codeVerifier, string redirectUri)
    {
        var tokenReq = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["client_secret"] = _clientSecret,
                ["code"] = code,
                ["code_verifier"] = codeVerifier,
                ["grant_type"] = "authorization_code",
                ["redirect_uri"] = redirectUri,
            })
        };

        var tokenRes = _httpHandler != null
            ? await _httpHandler(tokenReq)
            : await _defaultHttp.SendAsync(tokenReq);

        tokenRes.EnsureSuccessStatusCode();
        var tokenJson = await tokenRes.Content.ReadAsStringAsync();
        using var tokenDoc = JsonDocument.Parse(tokenJson);
        var accessToken = tokenDoc.RootElement.GetProperty("access_token").GetString()!;

        var userReq = new HttpRequestMessage(HttpMethod.Get, "https://openidconnect.googleapis.com/v1/userinfo");
        userReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var userRes = _httpHandler != null
            ? await _httpHandler(userReq)
            : await _defaultHttp.SendAsync(userReq);

        userRes.EnsureSuccessStatusCode();
        var userJson = await userRes.Content.ReadAsStringAsync();
        using var userDoc = JsonDocument.Parse(userJson);
        var root = userDoc.RootElement;

        var sub = root.GetProperty("sub").GetString()!;
        var email = root.GetProperty("email").GetString()!;
        var name = root.TryGetProperty("name", out var np) ? np.GetString() ?? "" : "";
        var picture = root.TryGetProperty("picture", out var pp) ? pp.GetString() : null;

        return new OAuthUserInfo("google", sub, email, name, picture);
    }
}

public sealed class GitHubOAuthProvider : IOAuthProvider
{
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>>? _httpHandler;
    private static readonly HttpClient _defaultHttp = new();

    public GitHubOAuthProvider(
        string clientId,
        string clientSecret,
        Func<HttpRequestMessage, Task<HttpResponseMessage>>? httpHandler = null)
    {
        _clientId = clientId;
        _clientSecret = clientSecret;
        _httpHandler = httpHandler;
    }

    public string ProviderName => "github";

    public string BuildAuthorizationUrl(string state, string codeChallenge, string redirectUri) =>
        "https://github.com/login/oauth/authorize?" +
        $"client_id={Uri.EscapeDataString(_clientId)}&" +
        $"redirect_uri={Uri.EscapeDataString(redirectUri)}&" +
        "scope=read:user%20user:email&" +
        $"state={Uri.EscapeDataString(state)}";

    public async Task<OAuthUserInfo> ExchangeCodeAsync(string code, string codeVerifier, string redirectUri)
    {
        var tokenReq = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["client_secret"] = _clientSecret,
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
            })
        };
        tokenReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var tokenRes = _httpHandler != null
            ? await _httpHandler(tokenReq)
            : await _defaultHttp.SendAsync(tokenReq);

        tokenRes.EnsureSuccessStatusCode();
        var tokenJson = await tokenRes.Content.ReadAsStringAsync();
        using var tokenDoc = JsonDocument.Parse(tokenJson);
        var accessToken = tokenDoc.RootElement.GetProperty("access_token").GetString()!;

        var userReq = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        userReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        userReq.Headers.UserAgent.Add(new ProductInfoHeaderValue("ReptarApp", "1.0"));

        var userRes = _httpHandler != null
            ? await _httpHandler(userReq)
            : await _defaultHttp.SendAsync(userReq);

        userRes.EnsureSuccessStatusCode();
        var userJson = await userRes.Content.ReadAsStringAsync();
        using var userDoc = JsonDocument.Parse(userJson);
        var root = userDoc.RootElement;

        var id = root.GetProperty("id").GetInt64().ToString();
        var login = root.GetProperty("login").GetString()!;
        var name = root.TryGetProperty("name", out var np) && np.ValueKind == JsonValueKind.String
            ? np.GetString() ?? login
            : login;
        var avatar = root.TryGetProperty("avatar_url", out var ap) ? ap.GetString() : null;
        var email = root.TryGetProperty("email", out var ep) && ep.ValueKind == JsonValueKind.String
            ? ep.GetString()
            : null;

        if (string.IsNullOrEmpty(email))
        {
            email = $"{login}@users.noreply.github.com";
        }

        return new OAuthUserInfo("github", id, email, name, avatar);
    }
}
