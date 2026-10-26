using System.Text;
using Reptar;
using Reptar.AI;
using Xunit;

namespace Reptar.Tests;

/// <summary>
/// Security regression tests for the 2026-10 hardening pass. Each test pins a
/// specific fix from plans/security-deep-dive-2026-10.md so it cannot silently
/// regress:
///   - OAuth state validation (built-in callback)
///   - Site-relative OAuth callback URLs
///   - Unverified-email account linking gate
///   - Oversized-JWT / long-password crash resistance (bounded buffers)
///   - CookieJar header-injection rejection
///   - Redirect control-character rejection
///   - Peer-keyed login throttling (built-in + BetterAuth) that ignores
///     spoofed forwarding headers, with a bounded failure map
/// </summary>
[Collection("AuthStaticTests")]
public sealed class SecurityRegressions
{
    private static ReptarRequest Json(
        string method,
        string path,
        string json,
        string? remoteIp = null) => new()
    {
        Method = method,
        Path = path,
        RemoteIp = remoteIp,
        Headers = new Dictionary<string, string> { ["content-type"] = "application/json" },
        Body = Encoding.UTF8.GetBytes(json),
    };

    private static string? CookieValue(ReptarResponse response, string name)
    {
        var prefix = $"{name}=";

        foreach (var header in response.SetCookies)
        {
            if (header.StartsWith(prefix, StringComparison.Ordinal))
                return header[prefix.Length..].Split(';')[0];
        }

        return null;
    }

    private sealed record FakeProvider(bool EmailVerified) : IOAuthProvider
    {
        public string ProviderName => "fake";

        public string BuildAuthorizationUrl(string state, string codeChallenge, string redirectUri) =>
            $"https://provider.example/auth?state={state}&code_challenge={codeChallenge}&redirect_uri={redirectUri}";

        public Task<OAuthUserInfo> ExchangeCodeAsync(string code, string codeVerifier, string redirectUri) =>
            Task.FromResult(new OAuthUserInfo(
                "fake",
                "fake-puid-1",
                "oauth.user@example.com",
                "OAuth User",
                null,
                EmailVerified));
    }

    private static (ReptarApp App, AuthOptions Options) CreateAuthApp(IOAuthProvider provider)
    {
        var authOpts = new AuthOptions();
        authOpts.OAuthProviders.Add(provider);
        Auth.Configure(authOpts);

        var app = ReptarApp.Create(new ReptarConfig
        {
            Database = Db.Sqlite(":memory:"),
            Auth = authOpts,
        });
        app.Route("/auth", Auth.Routes());

        return (app, authOpts);
    }

    private static async Task<string> StartSocialFlowAsync(ReptarApp app, string callbackUrl = "/after")
    {
        var socialRes = await app.HandleAsync(Json(
            "POST",
            "/auth/social",
            $$"""{"provider":"fake","callbackUrl":"{{callbackUrl}}"}"""));

        Assert.Equal(200, socialRes.Status);

        var stateCookie = CookieValue(socialRes, "reptar_oauth_pkce_fake");
        Assert.NotNull(stateCookie);
        Assert.Contains('|', stateCookie);

        return stateCookie!;
    }

    [Fact]
    public async Task OAuth_BuiltIn_Callback_RejectsWrongOrMissingState()
    {
        var (app, _) = CreateAuthApp(new FakeProvider(EmailVerified: true));
        await app.EnsureCreatedAsync();

        var stateCookie = await StartSocialFlowAsync(app);
        var state = stateCookie.Split('|')[0];
        Assert.False(string.IsNullOrEmpty(state));

        var cookieHeaders = new Dictionary<string, string>
        {
            ["reptar_oauth_pkce_fake"] = stateCookie,
        };

        // Missing state -> rejected
        var missing = await app.HandleAsync(new ReptarRequest
        {
            Method = "GET",
            Path = "/auth/callback/fake",
            Query = "code=abc",
            Cookies = cookieHeaders,
        });
        Assert.Equal(400, missing.Status);

        // Wrong state -> rejected
        var wrong = await app.HandleAsync(new ReptarRequest
        {
            Method = "GET",
            Path = "/auth/callback/fake",
            Query = "code=abc&state=attacker-forged-state",
            Cookies = cookieHeaders,
        });
        Assert.Equal(400, wrong.Status);

        // Correct state -> completes with the bound callback redirect
        var ok = await app.HandleAsync(new ReptarRequest
        {
            Method = "GET",
            Path = "/auth/callback/fake",
            Query = $"code=abc&state={state}",
            Cookies = cookieHeaders,
        });
        Assert.Equal(302, ok.Status);
        Assert.Equal("/after", ok.Headers["Location"]);
        Assert.NotNull(CookieValue(ok, "reptar_session"));
    }

    [Fact]
    public async Task OAuth_BuiltIn_Social_RejectsNonRelativeCallbackUrl()
    {
        var (app, _) = CreateAuthApp(new FakeProvider(EmailVerified: true));
        await app.EnsureCreatedAsync();

        var absolute = await app.HandleAsync(Json(
            "POST",
            "/auth/social",
            """{"provider":"fake","callbackUrl":"https://evil.example/phish"}"""));
        Assert.Equal(400, absolute.Status);

        var protocolRelative = await app.HandleAsync(Json(
            "POST",
            "/auth/social",
            """{"provider":"fake","callbackUrl":"//evil.example/phish"}"""));
        Assert.Equal(400, protocolRelative.Status);

        var crlf = await app.HandleAsync(Json(
            "POST",
            "/auth/social",
            "{\"provider\":\"fake\",\"callbackUrl\":\"/ok\\r\\nSet-Cookie: evil=1\"}"));
        Assert.Equal(400, crlf.Status);

        // Site-relative still works
        var valid = await app.HandleAsync(Json(
            "POST",
            "/auth/social",
            """{"provider":"fake","callbackUrl":"/dashboard?next=/home"}"""));
        Assert.Equal(200, valid.Status);
    }

    [Fact]
    public async Task OAuth_BuiltIn_DoesNotLinkUnverifiedProviderEmail()
    {
        var (app, authOpts) = CreateAuthApp(new FakeProvider(EmailVerified: false));
        await app.EnsureCreatedAsync();

        // Victim already owns this email as a password account.
        var signup = await app.HandleAsync(Json(
            "POST",
            "/auth/signup",
            """{"email":"oauth.user@example.com","password":"password123","name":"Legit User"}"""));
        Assert.Equal(201, signup.Status);

        var stateCookie = await StartSocialFlowAsync(app);
        var state = stateCookie.Split('|')[0];

        // An attacker-controlled provider identity that merely claims the
        // victim's email must not be attached to the victim's account.
        var callback = await app.HandleAsync(new ReptarRequest
        {
            Method = "GET",
            Path = "/auth/callback/fake",
            Query = $"code=abc&state={state}",
            Cookies = new Dictionary<string, string> { ["reptar_oauth_pkce_fake"] = stateCookie },
        });

        Assert.Equal(409, callback.Status);
        Assert.Null(CookieValue(callback, authOpts.CookieName));
    }

    [Fact]
    public async Task OAuth_BuiltIn_LinksVerifiedProviderEmail()
    {
        var (app, _) = CreateAuthApp(new FakeProvider(EmailVerified: true));
        await app.EnsureCreatedAsync();

        var signup = await app.HandleAsync(Json(
            "POST",
            "/auth/signup",
            """{"email":"oauth.user@example.com","password":"password123","name":"Legit User"}"""));
        Assert.Equal(201, signup.Status);

        var stateCookie = await StartSocialFlowAsync(app);
        var state = stateCookie.Split('|')[0];

        var callback = await app.HandleAsync(new ReptarRequest
        {
            Method = "GET",
            Path = "/auth/callback/fake",
            Query = $"code=abc&state={state}",
            Cookies = new Dictionary<string, string> { ["reptar_oauth_pkce_fake"] = stateCookie },
        });

        Assert.Equal(302, callback.Status);
        Assert.Equal("/after", callback.Headers["Location"]);
    }

    [Fact]
    public async Task Jwt_VerifyRejectsOversizedTokenWithoutCrashing()
    {
        // Pre-fix: the verify path stackalloc'd a buffer sized by token
        // length, so an oversized token crashed the process with an
        // uncatchable StackOverflowException. It must fail closed instead.
        var segment = new string('A', 512 * 1024);
        var oversizedToken = $"{segment}.{segment}.{segment}";

        var payload = await Jwt.VerifyAsync(
            new DevCrypto(),
            new JwtOptions { Secret = "regression-test-secret-0123456789" },
            oversizedToken);

        Assert.Null(payload);

        // Sanity: a well-formed reject path still works (bad signature).
        var header = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9";
        var body = "eyJzdWIiOiJ1c2VyMSIsImlhdCI6MTcwMDAwMDAwMH0";
        var badSignature = "c2lnbmF0dXJl";
        Assert.Null(await Jwt.VerifyAsync(
            new DevCrypto(),
            new JwtOptions { Secret = "regression-test-secret-0123456789" },
            $"{header}.{body}.{badSignature}"));
    }

    [Fact]
    public async Task DevCrypto_LongPassword_RoundTripsAfterNormalization()
    {
        var crypto = new DevCrypto();

        // Long passwords are pre-hashed instead of crashing the verify path.
        var longPassword = new string('x', 512 * 1024);
        var hash = await crypto.HashPassword(longPassword);

        Assert.True(await crypto.VerifyPassword(longPassword, hash));
        Assert.False(await crypto.VerifyPassword(longPassword + "y", hash));

        // Short passwords keep the original derivation (existing hashes stay
        // verifiable).
        var shortHash = await crypto.HashPassword("password123");
        Assert.True(await crypto.VerifyPassword("password123", shortHash));
        Assert.False(await crypto.VerifyPassword("password124", shortHash));

        // Corrupt stored hashes fail closed instead of throwing.
        Assert.False(await crypto.VerifyPassword("password123", "pbkdf2-sha256$100000$not-base64$$$"));
        Assert.False(await crypto.VerifyPassword(
            "password123",
            $"pbkdf2-sha256$999999999999${Convert.ToBase64String(new byte[16])}${Convert.ToBase64String(new byte[32])}"));
    }

    [Fact]
    public async Task CookieJar_RejectsHeaderInjectionValues()
    {
        var app = ReptarApp.Create(new ReptarConfig());

        app.Get("/cookie-inject", ctx =>
        {
            ctx.Cookies.Set("reptar_session", "v\r\nSet-Cookie: evil=1");
            return ctx.Status(200);
        });
        app.Get("/cookie-bad-name", ctx =>
        {
            ctx.Cookies.Set("bad name", "v");
            return ctx.Status(200);
        });
        app.Get("/cookie-ok", ctx =>
        {
            ctx.Cookies.Set("reptar_session", "safe-token_value");
            return ctx.Status(200);
        });

        // CR/LF in a cookie value is the Set-Cookie injection primitive: it
        // must throw (surfacing as a 500 from the pipeline) rather than emit.
        var inject = await app.HandleAsync(new ReptarRequest { Method = "GET", Path = "/cookie-inject" });
        Assert.Equal(500, inject.Status);
        Assert.Empty(inject.SetCookies);

        var invalidName = await app.HandleAsync(new ReptarRequest
        {
            Method = "GET",
            Path = "/cookie-bad-name",
        });
        Assert.Equal(500, invalidName.Status);
        Assert.Empty(invalidName.SetCookies);

        var ok = await app.HandleAsync(new ReptarRequest { Method = "GET", Path = "/cookie-ok" });
        Assert.Equal(200, ok.Status);
        Assert.Contains("reptar_session=safe-token_value", ok.SetCookies.Single());
    }

    [Fact]
    public async Task Redirect_RejectsControlCharacterLocations()
    {
        var app = ReptarApp.Create(new ReptarConfig());

        app.Get("/redirect-bad", ctx => ctx.Redirect("/ok\r\nSet-Cookie: x=1"));
        app.Get("/redirect-ok", ctx => ctx.Redirect("/dashboard"));

        var bad = await app.HandleAsync(new ReptarRequest { Method = "GET", Path = "/redirect-bad" });
        Assert.Equal(500, bad.Status);
        Assert.False(bad.Headers.ContainsKey("Location"));

        var ok = await app.HandleAsync(new ReptarRequest { Method = "GET", Path = "/redirect-ok" });
        Assert.Equal(302, ok.Status);
        Assert.Equal("/dashboard", ok.Headers["Location"]);
    }

    [Fact]
    public async Task BetterAuth_SignIn_IsThrottled_AndIgnoresSpoofedHeaders()
    {
        var authOpts = new AuthOptions { RateLimitMaxAttempts = 3 };

        var app = ReptarApp.Create(new ReptarConfig
        {
            Database = Db.Sqlite(":memory:"),
            Auth = authOpts,
        });
        app.Route("/ba", BetterAuthProtocol.Routes(authOpts));
        await app.EnsureCreatedAsync();

        var signUp = await app.HandleAsync(Json(
            "POST",
            "/ba/sign-up/email",
            """{"email":"ba.user@example.com","password":"password12345","name":"BA User"}"""));
        Assert.Equal(201, signUp.Status);

        // Invalid emails now fail validation before any DB/crypto work.
        var invalidEmail = await app.HandleAsync(Json(
            "POST",
            "/ba/sign-in/email",
            """{"email":"not-an-email","password":"password12345"}""",
            remoteIp: "203.0.113.171"));
        Assert.Equal(400, invalidEmail.Status);

        // Failed attempts from one peer IP with rotating spoofed forwarding
        // headers must share a single throttle bucket.
        var spoofed = new[]
        {
            new Dictionary<string, string> { ["CF-Connecting-IP"] = "198.51.100.11" },
            new Dictionary<string, string> { ["X-Forwarded-For"] = "198.51.100.12" },
            new Dictionary<string, string>
            {
                ["CF-Connecting-IP"] = "198.51.100.13",
                ["X-Forwarded-For"] = "198.51.100.14",
            },
        };

        for (var i = 0; i < 3; i++)
        {
            var res = await app.HandleAsync(new ReptarRequest
            {
                Method = "POST",
                Path = "/ba/sign-in/email",
                RemoteIp = "203.0.113.170",
                Body = Encoding.UTF8.GetBytes("""{"email":"ba.user@example.com","password":"wrongpassword"}"""),
                Headers = new Dictionary<string, string>(spoofed[i])
                {
                    ["content-type"] = "application/json",
                }
            });
            Assert.Equal(401, res.Status);
        }

        var blocked = await app.HandleAsync(new ReptarRequest
        {
            Method = "POST",
            Path = "/ba/sign-in/email",
            RemoteIp = "203.0.113.170",
            Body = Encoding.UTF8.GetBytes("""{"email":"ba.user@example.com","password":"wrongpassword"}"""),
            Headers = new Dictionary<string, string> { ["content-type"] = "application/json" }
        });
        Assert.Equal(429, blocked.Status);

        // Successful sign-in from a different peer is unaffected.
        var success = await app.HandleAsync(Json(
            "POST",
            "/ba/sign-in/email",
            """{"email":"ba.user@example.com","password":"password12345"}""",
            remoteIp: "203.0.113.172"));
        Assert.Equal(200, success.Status);
    }

    [Fact]
    public void HttpUrlPolicy_BlocksPrivateAndMetadataTargets()
    {
        var blocked = new[]
        {
            "http://localhost/admin",
            "http://api.localhost/x",
            "http://minio.local/x",
            "http://metadata/latest",
            "http://metadata.google.internal/computeMetadata/v1/",
            "http://127.0.0.1:8787/secret",
            "http://10.0.0.1/",
            "http://172.16.0.9/",
            "http://172.31.255.255/",
            "http://192.168.1.10/",
            "http://169.254.169.254/latest/meta-data/",
            "http://100.64.1.1/",
            "http://224.0.0.1/",
            "http://[::1]/",
            "http://[fe80::1]/",
            "http://[fd00::1]/",
            "http://[fc00::1]/",
            "http://[::ffff:127.0.0.1]/",
            // Userinfo tricks do not smuggle private targets past the host check
            "https://api.openai.com@169.254.169.254/latest",
            "not a url",
            "ftp://api.openai.com/x",
        };

        foreach (var url in blocked)
            Assert.False(HttpUrlPolicy.IsAllowed(url), url);

        var allowed = new[]
        {
            "https://api.openai.com/v1/chat/completions",
            "https://oauth2.googleapis.com/token",
            "http://api.github.com/user",
        };

        foreach (var url in allowed)
            Assert.True(HttpUrlPolicy.IsAllowed(url), url);

        Assert.Throws<InvalidOperationException>(
            () => HttpUrlPolicy.EnsureAllowed("http://169.254.169.254/latest/meta-data/"));
    }

    [Fact]
    public void HttpUrlPolicy_Allowlist_Overrides_Blocklist_For_Explicit_Hosts()
    {
        var allow = new[] { "minio.internal", "Corp.example.com" };

        Assert.True(HttpUrlPolicy.IsAllowed("http://minio.internal:9000/bucket", allow));
        Assert.True(HttpUrlPolicy.IsAllowed("http://s3.minio.internal/bucket", allow));
        Assert.True(HttpUrlPolicy.IsAllowed("https://api.corp.example.com/v1", allow));

        // Unlisted public hosts are rejected once an allowlist is configured
        Assert.False(HttpUrlPolicy.IsAllowed("https://api.openai.com", allow));
        // Suffix-match tricks do not count as subdomain matches
        Assert.False(HttpUrlPolicy.IsAllowed("https://evilminio.internal.example.com", allow));
        // Private hosts are still rejected when not on the allowlist
        Assert.False(HttpUrlPolicy.IsAllowed("http://localhost/x", allow));
    }

    [Fact]
    public void StorageKey_RejectsTraversalAndControlChars()
    {
        // Pins the shared normalizer the Wasm R2Client now applies to every
        // operation (parity with LocalStorage/S3), so the guard cannot be
        // dropped from any provider silently.
        Assert.Equal("a/b.png", StorageKey.Normalize("/a/b.png"));

        Assert.False(StorageKey.IsValid("../escape.png"));
        Assert.False(StorageKey.IsValid("a/../../escape.png"));
        Assert.False(StorageKey.IsValid("a/./b"));
        Assert.False(StorageKey.IsValid("a\rb"));
        Assert.False(StorageKey.IsValid(new string('a', StorageKey.MaxLength + 1)));
    }

    [Fact]
    public async Task WorkspaceTools_GateRemotePeers_UnlessExplicitlyAuthorized()
    {
        var root = Path.Combine(Path.GetTempPath(), $"reptar-ws-gate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "hello.txt"), "line one\n");

        try
        {
            // Default (no Authorize): remote peers are 403, loopback and
            // in-process dispatch pass the gate.
            var app = ReptarApp.Create();
            app.UseWorkspaceTools(root);

            var remote = await app.HandleAsync(new ReptarRequest
            {
                Method = "GET",
                Path = "/workspace/read_file",
                Query = "path=hello.txt",
                RemoteIp = "203.0.113.99",
            });
            Assert.Equal(403, remote.Status);

            var loopback = await app.HandleAsync(new ReptarRequest
            {
                Method = "GET",
                Path = "/workspace/read_file",
                Query = "path=hello.txt",
                RemoteIp = "127.0.0.1",
            });
            // Gate passed; the file exists so the read succeeds.
            Assert.Equal(200, loopback.Status);

            var inProcess = await app.ExecuteAiToolAsync(
                "read_file",
                """{"path": "hello.txt"}""");
            Assert.True(inProcess.Success, inProcess.ToLlmResponse());

            // Explicit Authorize gate decides access itself.
            var gated = ReptarApp.Create();
            gated.UseWorkspaceTools(root, options =>
            {
                options.Authorize = ctx => ctx.Req.Header("X-Agent-Token") == "secret";
            });

            var badToken = await gated.HandleAsync(new ReptarRequest
            {
                Method = "GET",
                Path = "/workspace/read_file",
                Query = "path=hello.txt",
                RemoteIp = "127.0.0.1",
                Headers = new Dictionary<string, string> { ["X-Agent-Token"] = "wrong" },
            });
            Assert.Equal(403, badToken.Status);

            var goodToken = await gated.HandleAsync(new ReptarRequest
            {
                Method = "GET",
                Path = "/workspace/read_file",
                Query = "path=hello.txt",
                RemoteIp = "203.0.113.99",
                Headers = new Dictionary<string, string> { ["X-Agent-Token"] = "secret" },
            });
            Assert.Equal(200, goodToken.Status);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { /* best-effort temp cleanup */ }
        }
    }

    [Fact]
    public async Task Csrf_Middleware_RequiresDoubleSubmitToken_OnUnsafeMethods()
    {
        var app = ReptarApp.Create(new ReptarConfig());
        app.Use(Middlewares.Csrf(new CsrfOptions { SecureCookie = false }));
        app.Get("/value", ctx => ctx.Json(new { ok = true }));
        app.Post("/submit", ctx => ctx.Json(new { ok = true }));

        // 1. GET issues the JS-readable token cookie (double-submit token
        // must not be HttpOnly)
        var get = await app.HandleAsync(new ReptarRequest { Method = "GET", Path = "/value" });
        Assert.Equal(200, get.Status);
        var cookie = CookieValue(get, "reptar_csrf");
        Assert.NotNull(cookie);
        Assert.DoesNotContain("HttpOnly", get.SetCookies.Single());

        // 2. POST without the header -> 403
        var noHeader = await app.HandleAsync(new ReptarRequest
        {
            Method = "POST",
            Path = "/submit",
            Cookies = new Dictionary<string, string> { ["reptar_csrf"] = cookie! },
        });
        Assert.Equal(403, noHeader.Status);

        // 3. POST with a mismatched header -> 403
        var wrong = await app.HandleAsync(new ReptarRequest
        {
            Method = "POST",
            Path = "/submit",
            Cookies = new Dictionary<string, string> { ["reptar_csrf"] = cookie! },
            Headers = new Dictionary<string, string> { ["x-csrf-token"] = "forged" },
        });
        Assert.Equal(403, wrong.Status);

        // 4. POST echoing the cookie in the header -> 200
        var ok = await app.HandleAsync(new ReptarRequest
        {
            Method = "POST",
            Path = "/submit",
            Cookies = new Dictionary<string, string> { ["reptar_csrf"] = cookie! },
            Headers = new Dictionary<string, string> { ["x-csrf-token"] = cookie! },
        });
        Assert.Equal(200, ok.Status);

        // 5. First-visit POST (no cookie yet) -> rejected, token issued for
        // the client's retry
        var firstVisit = await app.HandleAsync(new ReptarRequest { Method = "POST", Path = "/submit" });
        Assert.Equal(403, firstVisit.Status);
        Assert.NotNull(CookieValue(firstVisit, "reptar_csrf"));

        // 6. Exempt path prefixes bypass the check (third-party webhooks)
        var exemptApp = ReptarApp.Create(new ReptarConfig());
        exemptApp.Use(Middlewares.Csrf(new CsrfOptions { ExemptPaths = ["/hooks"] }));
        exemptApp.Post("/hooks/stripe", ctx => ctx.Json(new { ok = true }));

        var webhook = await exemptApp.HandleAsync(new ReptarRequest
        {
            Method = "POST",
            Path = "/hooks/stripe",
        });
        Assert.Equal(200, webhook.Status);
    }

    [Fact]
    public async Task LoginThrottle_SweepsExpiredWindows_UnderDistinctKeyFlood()
    {
        // Flood the map past its bound with keys whose windows expire quickly,
        // then verify expired windows are swept and fresh keys still record.
        var shortWindow = TimeSpan.FromMilliseconds(20);

        for (var i = 0; i < 11_000; i++)
            LoginThrottle.RecordFailure($"sweep-regression-{i}", 1, shortWindow);

        await Task.Delay(60);

        LoginThrottle.RecordFailure("post-sweep-key", 1, shortWindow);

        Assert.True(LoginThrottle.IsLimited("post-sweep-key", 1, shortWindow));
        LoginThrottle.Reset("post-sweep-key");
        Assert.False(LoginThrottle.IsLimited("post-sweep-key", 1, shortWindow));
    }
}
