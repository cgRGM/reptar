#if !NETWASM
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Reptar.AI;

/// <summary>A PKCE verifier and its S256 challenge.</summary>
public sealed record PkceChallenge(string Verifier, string Challenge);

/// <summary>An OAuth token set, with the client id the provider issued.</summary>
public sealed record OAuthTokens(
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset ExpiresAt,
    string? ClientId,
    IReadOnlyList<string> Scopes)
{
    /// <summary>True when the access token is within <paramref name="margin"/> of expiring.</summary>
    public bool ExpiresWithin(TimeSpan margin) => DateTimeOffset.UtcNow >= ExpiresAt - margin;
}

/// <summary>
/// Authorization-code OAuth with PKCE, a loopback callback, and refresh.
/// </summary>
/// <remarks>
/// This is the flow a desktop tool has to use: it is a public client, so there is no secret, and
/// PKCE is what stops an intercepted authorization code from being redeemed. The callback is a
/// loopback listener on an ephemeral port rather than a hosted URL, so nothing has to be
/// registered or reachable from outside the machine.
///
/// A raw <see cref="TcpListener"/> rather than <see cref="HttpListener"/>: HttpListener needs a URL
/// ACL registered on Windows and behaves inconsistently elsewhere, while reading one request line
/// and writing one response is all this needs.
/// </remarks>
public static class OAuthPkce
{
    /// <summary>Creates a verifier and its S256 challenge.</summary>
    public static PkceChallenge CreateChallenge()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);

        var verifier = Base64Url(bytes);
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));

        return new PkceChallenge(verifier, Base64Url(hash));
    }

    /// <summary>URL-safe base64 with no padding, as RFC 7636 requires.</summary>
    public static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>A random opaque value for the <c>state</c> parameter.</summary>
    public static string CreateState()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url(bytes);
    }

    /// <summary>
    /// Opens <paramref name="url"/> in the default browser. Best-effort: the caller should always
    /// also print the URL, because a headless session has no browser.
    /// </summary>
    public static bool TryOpenBrowser(string url)
    {
        try
        {
            var startInfo = OperatingSystem.IsMacOS()
                ? new ProcessStartInfo("open", url)
                : OperatingSystem.IsWindows()
                    ? new ProcessStartInfo(url) { UseShellExecute = true }
                    : new ProcessStartInfo("xdg-open", url);

            startInfo.UseShellExecute = !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux();
            using var process = Process.Start(startInfo);
            return process is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Listens on loopback for the authorization redirect and returns the full callback URL.
    /// </summary>
    /// <param name="port">Preferred port. Pass 0 for any free port.</param>
    /// <param name="path">Callback path, for example <c>/auth/callback</c>.</param>
    /// <param name="expectedState">The <c>state</c> the callback must echo.</param>
    public static async Task<OAuthCallbackResult> WaitForCallbackAsync(
        int port,
        string path,
        string expectedState,
        Action<string>? onListening = null,
        CancellationToken cancellationToken = default)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);

        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            throw new OAuthException(
                $"Could not listen on 127.0.0.1:{port} for the sign-in callback ({ex.Message}). "
                + "Close whatever is using that port, or paste the callback URL manually.");
        }

        try
        {
            var actualPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            var redirectUri = $"http://127.0.0.1:{actualPort}{path}";

            // Announce only once the socket is accepting. Opening the browser first races the
            // bind: on a session that needs no interaction the redirect can arrive before the
            // listener exists, and the browser reports a connection failure.
            onListening?.Invoke(redirectUri);

            var mismatches = 0;

            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                using var stream = client.GetStream();

                var requestLine = await ReadRequestLineAsync(stream, cancellationToken).ConfigureAwait(false);
                if (requestLine is null)
                    continue;

                // "GET /auth/callback?code=... HTTP/1.1"
                var parts = requestLine.Split(' ');
                if (parts.Length < 2)
                    continue;

                var target = parts[1];
                if (!target.StartsWith(path, StringComparison.Ordinal))
                {
                    await RespondAsync(stream, 404, "Not found", cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var callbackUrl = $"http://127.0.0.1:{actualPort}{target}";
                var query = System.Web.HttpUtility.ParseQueryString(
                    target.Contains('?') ? target[(target.IndexOf('?') + 1)..] : "");

                if (query["error"] is { Length: > 0 } error)
                {
                    await RespondAsync(stream, 400, $"Sign-in failed: {error}", cancellationToken)
                        .ConfigureAwait(false);
                    throw new OAuthException($"The provider refused the sign-in: {error}");
                }

                if (!string.Equals(query["state"], expectedState, StringComparison.Ordinal))
                {
                    // A stale tab, a cached redirect, or another client's callback can land here
                    // while a legitimate sign-in is still pending. Answer it and keep listening
                    // rather than aborting a flow that is about to succeed.
                    mismatches++;
                    await RespondAsync(
                        stream,
                        400,
                        "This sign-in link is no longer valid. Return to the terminal and start again.",
                        cancellationToken).ConfigureAwait(false);

                    if (mismatches >= MaxMismatches)
                    {
                        throw new OAuthException(
                            $"The sign-in callback did not match this request after {mismatches} attempts. "
                            + "Another sign-in may be holding port "
                            + $"{actualPort}; cancel it and try again.");
                    }

                    continue;
                }

                if (query["code"] is not { Length: > 0 } code)
                {
                    await RespondAsync(stream, 400, "Missing authorization code", cancellationToken)
                        .ConfigureAwait(false);
                    throw new OAuthException("The sign-in callback had no authorization code.");
                }

                await RespondAsync(
                    stream,
                    200,
                    "Signed in. You can close this window and return to the terminal.",
                    cancellationToken).ConfigureAwait(false);

                return new OAuthCallbackResult(code, redirectUri, query["client_id"]);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>Exchanges an authorization code for tokens.</summary>
    public static async Task<OAuthTokens> ExchangeAsync(
        string tokenUrl,
        string code,
        string verifier,
        string redirectUri,
        string clientId,
        string? resource = null,
        CancellationToken cancellationToken = default)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "authorization_code"),
            new("client_id", clientId),
            new("code", code),
            new("code_verifier", verifier),
            new("redirect_uri", redirectUri)
        };

        if (resource is { Length: > 0 })
            form.Add(new("resource", resource));

        return await PostTokenAsync(tokenUrl, form, clientId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Refreshes an expired access token.</summary>
    public static async Task<OAuthTokens> RefreshAsync(
        string tokenUrl,
        string refreshToken,
        string clientId,
        string? resource = null,
        CancellationToken cancellationToken = default)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "refresh_token"),
            new("client_id", clientId),
            new("refresh_token", refreshToken)
        };

        if (resource is { Length: > 0 })
            form.Add(new("resource", resource));

        return await PostTokenAsync(tokenUrl, form, clientId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<OAuthTokens> PostTokenAsync(
        string tokenUrl,
        IReadOnlyList<KeyValuePair<string, string>> form,
        string clientId,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(form);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            "application/x-www-form-urlencoded");

        using var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl) { Content = content };
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await LlmHttp.Client
            .SendAsync(request, cancellationToken).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new OAuthException(
                $"Token exchange failed ({(int)response.StatusCode}): {Clip(body)}");
        }

        if (JsonNode.Parse(body) is not JsonObject json)
            throw new OAuthException("Token endpoint returned something that is not a JSON object.");

        var access = json["access_token"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(access))
            throw new OAuthException("Token endpoint returned no access_token.");

        var expiresIn = json["expires_in"] is JsonValue value && value.TryGetValue<double>(out var seconds)
            ? seconds
            : 3600;

        var scopes = (json["scope"]?.GetValue<string>() ?? "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return new OAuthTokens(
            access,
            json["refresh_token"]?.GetValue<string>(),
            // Expire a little early so a request never starts with a token about to lapse.
            DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn) - 180),
            clientId,
            scopes);
    }

    private static async Task<string?> ReadRequestLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        var builder = new StringBuilder();

        while (builder.Length < 2048)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return builder.Length > 0 ? builder.ToString() : null;

            var ch = (char)buffer[0];
            if (ch == '\n')
                return builder.ToString().TrimEnd('\r');

            builder.Append(ch);
        }

        return builder.ToString();
    }

    private const int MaxMismatches = 5;

    private static async Task RespondAsync(
        Stream stream, int status, string message, CancellationToken cancellationToken)
    {
        var html = $"""
            <!doctype html><meta charset="utf-8">
            <title>Reptar agent</title>
            <body style="font:16px/1.5 -apple-system,system-ui,sans-serif;margin:4rem auto;max-width:32rem;color:#ddd;background:#16181d">
            <p>{WebUtility.HtmlEncode(message)}</p></body>
            """;

        var reason = status == 200 ? "OK" : status == 404 ? "Not Found" : "Bad Request";
        var bytes = Encoding.UTF8.GetBytes(html);
        var header =
            $"HTTP/1.1 {status} {reason}\r\n"
            + "Content-Type: text/html; charset=utf-8\r\n"
            + $"Content-Length: {bytes.Length}\r\n"
            + "Connection: close\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Clip(string value) =>
        value.Length <= 300 ? value : value[..300] + "…";
}

/// <summary>The callback parameters extracted from the loopback redirect.</summary>
public sealed record OAuthCallbackResult(string Code, string RedirectUri, string? IssuedClientId);

/// <summary>Raised when an OAuth sign-in cannot complete.</summary>
public sealed class OAuthException(string message) : Exception(message);
#endif
