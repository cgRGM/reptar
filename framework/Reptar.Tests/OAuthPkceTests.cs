using System.Net;
using System.Net.Sockets;
using System.Text;
using Reptar.AI;

namespace Reptar.Tests;

/// <summary>
/// Contract tests for the OAuth layer: PKCE shape, the loopback callback, state validation, and
/// the ChatGPT endpoint constants.
/// </summary>
public sealed class OAuthPkceTests
{
    // ------------------------------------------------------------------
    // PKCE
    // ------------------------------------------------------------------

    [Fact]
    public void a_challenge_is_a_valid_s256_of_its_verifier()
    {
        var challenge = OAuthPkce.CreateChallenge();

        // RFC 7636: 43-128 chars, base64url, no padding.
        Assert.InRange(challenge.Verifier.Length, 43, 128);
        Assert.DoesNotContain('=', challenge.Verifier);
        Assert.DoesNotContain('+', challenge.Verifier);
        Assert.DoesNotContain('/', challenge.Verifier);

        var expected = OAuthPkce.Base64Url(
            System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(challenge.Verifier)));

        Assert.Equal(expected, challenge.Challenge);
    }

    [Fact]
    public void two_challenges_differ()
    {
        Assert.NotEqual(
            OAuthPkce.CreateChallenge().Verifier,
            OAuthPkce.CreateChallenge().Verifier);
    }

    [Fact]
    public void base64url_has_no_padding_or_unsafe_characters()
    {
        var encoded = OAuthPkce.Base64Url([0xFB, 0xFF, 0xBF]);

        Assert.DoesNotContain('=', encoded);
        Assert.DoesNotContain('+', encoded);
        Assert.DoesNotContain('/', encoded);
    }

    // ------------------------------------------------------------------
    // Loopback callback
    // ------------------------------------------------------------------

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<string> RequestAsync(int port, string target)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var request = $"GET {target} HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n";
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(request));

        using var reader = new StreamReader(client.GetStream());
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task the_callback_returns_the_code_when_the_state_matches()
    {
        var port = FreePort();
        var waiting = OAuthPkce.WaitForCallbackAsync(port, "/auth/callback", "state-1");

        // Give the listener a moment to bind before connecting.
        await Task.Delay(150);
        var response = await RequestAsync(port, "/auth/callback?code=abc123&state=state-1");

        var result = await waiting;

        Assert.Equal("abc123", result.Code);
        Assert.Equal($"http://127.0.0.1:{port}/auth/callback", result.RedirectUri);
        Assert.Contains("200", response);
    }

    [Fact]
    public async Task a_mismatched_state_is_refused()
    {
        var port = FreePort();
        var waiting = OAuthPkce.WaitForCallbackAsync(port, "/auth/callback", "expected");

        await Task.Delay(150);
        _ = RequestAsync(port, "/auth/callback?code=abc123&state=wrong");

        var error = await Assert.ThrowsAsync<OAuthException>(() => waiting);
        Assert.Contains("state mismatch", error.Message);
    }

    [Fact]
    public async Task an_error_from_the_provider_is_surfaced()
    {
        var port = FreePort();
        var waiting = OAuthPkce.WaitForCallbackAsync(port, "/auth/callback", "s");

        await Task.Delay(150);
        _ = RequestAsync(port, "/auth/callback?error=access_denied&state=s");

        var error = await Assert.ThrowsAsync<OAuthException>(() => waiting);
        Assert.Contains("access_denied", error.Message);
    }

    [Fact]
    public async Task a_callback_without_a_code_is_refused()
    {
        var port = FreePort();
        var waiting = OAuthPkce.WaitForCallbackAsync(port, "/auth/callback", "s");

        await Task.Delay(150);
        _ = RequestAsync(port, "/auth/callback?state=s");

        var error = await Assert.ThrowsAsync<OAuthException>(() => waiting);
        Assert.Contains("authorization code", error.Message);
    }

    // ------------------------------------------------------------------
    // ChatGPT endpoint contract
    // ------------------------------------------------------------------

    [Fact]
    public void the_chatgpt_flow_uses_the_documented_endpoints()
    {
        Assert.Equal("https://auth.openai.com/api/accounts/authorize", OpenAiChatGptAuth.AuthorizeUrl);
        Assert.Equal("https://auth.openai.com/api/accounts/oauth/token", OpenAiChatGptAuth.TokenUrl);
        Assert.Equal("https://api.openai.com/v1", OpenAiChatGptAuth.Resource);
        Assert.Equal(1455, OpenAiChatGptAuth.CallbackPort);
        Assert.Equal("http://127.0.0.1:1455/auth/callback", OpenAiChatGptAuth.RedirectUri);
    }

    [Fact]
    public void the_direct_token_scope_is_requested()
    {
        // Without this scope the token cannot be used against the API at all.
        Assert.Contains(OpenAiChatGptAuth.DirectTokenScope, OpenAiChatGptAuth.Scope);
        Assert.Contains("offline_access", OpenAiChatGptAuth.Scope);
    }
}
