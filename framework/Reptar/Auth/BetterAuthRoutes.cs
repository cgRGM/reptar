using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using static Reptar.ReptarApi;

namespace Reptar;

public sealed record BetterAuthSignInInput(string Email, string Password);
public sealed record BetterAuthSignUpInput(string Email, string Password, string Name);
public sealed record BetterAuthSocialInput(string Provider, string? CallbackURL = null);

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
public sealed record BetterAuthUser(
    string Id,
    string Name,
    string Email,
    string? Image,
    bool EmailVerified,
    string CreatedAt,
    string UpdatedAt);

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
public sealed record BetterAuthSession(
    string Id,
    string UserId,
    string Token,
    string ExpiresAt,
    string CreatedAt,
    string UpdatedAt,
    string? IpAddress = null,
    string? UserAgent = null);

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
public sealed record BetterAuthSessionResponse(
    BetterAuthSession Session,
    BetterAuthUser User);

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
public sealed record BetterAuthSignInResponse(
    BetterAuthUser User,
    string Token);

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
public sealed record BetterAuthSocialResponse(
    string Url);

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
public sealed record BetterAuthSessionQueryResult(
    string SessionId,
    string UserId,
    string ExpiresAt,
    string SessionCreatedAt,
    string Id,
    string Email,
    string Name,
    string? Image,
    int EmailVerified,
    string UserCreatedAt);

public static class BetterAuthProtocol
{
    private const string BetterAuthCookie = "better-auth.session_token";
    private const string StateCookie = "reptar_oauth_state";

    private static readonly Validator<BetterAuthSignInInput> SignInEmailSchema = Rule<BetterAuthSignInInput>()
        .Prop(x => x.Email, rules => rules.Email())
        .Prop(x => x.Password, rules => rules.Min(1).Max(512));

    public static ReptarApp Routes(AuthOptions? options = null)
    {
        var authOptions = options ?? new AuthOptions();
        var app = ReptarApp.Create();
        app.RegisterSchema(Auth.Users, Auth.Sessions, Auth.OAuthAccounts);

        app.Post("/sign-in/email", SignInEmailSchema, async (ctx, input) =>
        {
            // Same login throttling as the built-in routes, keyed on the socket
            // peer address (or trusted proxy headers when explicitly enabled).
            var throttleKey = authOptions.EnableRateLimiting
                ? LoginThrottle.ClientKey(ctx, authOptions)
                : null;
            var window = TimeSpan.FromMinutes(authOptions.RateLimitWindowMinutes);

            if (throttleKey is not null &&
                LoginThrottle.IsLimited(throttleKey, authOptions.RateLimitMaxAttempts, window))
            {
                return ctx.Error(
                    429,
                    "rate_limited",
                    "Too many failed login attempts. Please try again later.");
            }

            var email = input.Email.Trim().ToLowerInvariant();

            var user = await ctx.Db.QuerySingleOrDefaultAsync<Auth.UserRow>(
                "SELECT id, email, name, password_hash as PasswordHash, is_anonymous as IsAnonymous, role as Role, created_at as CreatedAt, image as Image, email_verified as EmailVerified FROM users WHERE email = @email",
                new Dictionary<string, object?> { ["email"] = email });

            if (user is null || string.IsNullOrEmpty(user.PasswordHash) ||
                !await ctx.Crypto.VerifyPassword(input.Password, user.PasswordHash))
            {
                if (throttleKey is not null)
                    LoginThrottle.RecordFailure(throttleKey, authOptions.RateLimitMaxAttempts, window);
                return ctx.Unauthorized("Invalid email or password.");
            }

            if (throttleKey is not null)
                LoginThrottle.Reset(throttleKey);

            var token = ctx.Crypto.RandomToken(32);
            var expiresAt = ctx.Clock.IsoIn(TimeSpan.FromDays(authOptions.SessionDays));
            var sessionId = Ids.New("ses");

            await ctx.Db.InsertAsync(Auth.Sessions, new Auth.SessionRow(
                Id: sessionId,
                UserId: user.Id,
                TokenHash: await ctx.Crypto.Sha256(token),
                ExpiresAt: expiresAt,
                CreatedAt: ctx.Clock.IsoNow()));

            SetSessionCookie(ctx, authOptions, token, expiresAt);

            var bUser = new BetterAuthUser(
                user.Id,
                user.Name,
                user.Email,
                user.Image,
                user.EmailVerified == 1,
                user.CreatedAt,
                user.CreatedAt);

            return ctx.Json(new BetterAuthSignInResponse(bUser, token));
        })
        .Produces<BetterAuthSignInResponse>(200)
        .Produces<ValidationError>(400)
        .Produces<ErrorBody>(401)
        .Produces<ErrorBody>(429);

        app.Post<BetterAuthSignUpInput>("/sign-up/email", async (ctx, input) =>
        {
            var email = input.Email.Trim().ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
                return ctx.BadRequest("Valid email address required.");

            if (string.IsNullOrEmpty(input.Password) || input.Password.Length < 8)
                return ctx.BadRequest("Password must be at least 8 characters.");

            if (await ctx.Db.ExistsAsync(Auth.Users, "email = @email", new Dictionary<string, object?> { ["email"] = email }))
                return ctx.Conflict("Email already in use.");

            var userId = Ids.New("usr");
            var now = ctx.Clock.IsoNow();
            var passwordHash = await ctx.Crypto.HashPassword(input.Password);

            var user = new Auth.UserRow(
                Id: userId,
                Email: email,
                Name: string.IsNullOrWhiteSpace(input.Name) ? email.Split('@')[0] : input.Name.Trim(),
                PasswordHash: passwordHash,
                IsAnonymous: 0,
                Role: authOptions.BootstrapAdmins.Contains(email, StringComparer.OrdinalIgnoreCase) ? "admin" : "user",
                CreatedAt: now,
                Image: null,
                EmailVerified: 0);

            await ctx.Db.InsertAsync(Auth.Users, user);

            var token = ctx.Crypto.RandomToken(32);
            var expiresAt = ctx.Clock.IsoIn(TimeSpan.FromDays(authOptions.SessionDays));
            var sessionId = Ids.New("ses");

            await ctx.Db.InsertAsync(Auth.Sessions, new Auth.SessionRow(
                Id: sessionId,
                UserId: userId,
                TokenHash: await ctx.Crypto.Sha256(token),
                ExpiresAt: expiresAt,
                CreatedAt: now));

            SetSessionCookie(ctx, authOptions, token, expiresAt);

            var bUser = new BetterAuthUser(
                userId,
                user.Name,
                email,
                null,
                false,
                now,
                now);

            return ctx.Json(new BetterAuthSignInResponse(bUser, token), 201);
        })
        .Produces<BetterAuthSignInResponse>(201)
        .Produces<ErrorBody>(400)
        .Produces<ErrorBody>(409);

        app.Get("/get-session", async ctx =>
        {
            var token = ctx.Cookies.Get(BetterAuthCookie)
                ?? ctx.Cookies.Get(authOptions.CookieName);

            if (string.IsNullOrEmpty(token))
            {
                var authHeader = ctx.Req.Header("Authorization");
                if (authHeader != null && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    token = authHeader[7..].Trim();
                }
            }

            if (string.IsNullOrEmpty(token))
            {
                return ctx.JsonRaw("null");
            }

            var hash = await ctx.Crypto.Sha256(token);
            var now = ctx.Clock.IsoNow();

            var rows = await ctx.Db.QueryAsync<BetterAuthSessionQueryResult>(
                """
                SELECT s.id as SessionId, s.user_id as UserId, s.expires_at as ExpiresAt, s.created_at as SessionCreatedAt,
                       u.id as Id, u.email as Email, u.name as Name, u.image as Image, u.email_verified as EmailVerified,
                       u.created_at as UserCreatedAt
                FROM sessions s
                JOIN users u ON u.id = s.user_id
                WHERE s.token_hash = @hash AND s.expires_at > @now
                LIMIT 1
                """,
                new Dictionary<string, object?> { ["hash"] = hash, ["now"] = now });

            if (rows.Count == 0)
            {
                return ctx.JsonRaw("null");
            }

            var row = rows[0];

            var bSession = new BetterAuthSession(
                row.SessionId,
                row.UserId,
                token,
                row.ExpiresAt,
                row.SessionCreatedAt,
                row.SessionCreatedAt);

            var bUser = new BetterAuthUser(
                row.Id,
                row.Name,
                row.Email,
                row.Image,
                row.EmailVerified == 1,
                row.UserCreatedAt,
                row.UserCreatedAt);

            return ctx.Json(new BetterAuthSessionResponse(bSession, bUser));
        });

        app.Post("/sign-out", async ctx =>
        {
            var token = ctx.Cookies.Get(BetterAuthCookie)
                ?? ctx.Cookies.Get(authOptions.CookieName);

            if (!string.IsNullOrEmpty(token))
            {
                var hash = await ctx.Crypto.Sha256(token);
                await ctx.Db.DeleteWhereAsync(
                    Auth.Sessions,
                    "token_hash = @hash",
                    new Dictionary<string, object?> { ["hash"] = hash });
            }

            ctx.Cookies.Delete(BetterAuthCookie, domain: null, secure: authOptions.SecureCookies);
            if (!string.Equals(authOptions.CookieName, BetterAuthCookie, StringComparison.Ordinal))
            {
                ctx.Cookies.Delete(
                    authOptions.CookieName,
                    domain: null,
                    secure: authOptions.SecureCookies);
            }

            return ctx.Json(new Dictionary<string, bool> { ["success"] = true });
        });

        app.Post<BetterAuthSocialInput>("/sign-in/social", async (Ctx ctx, BetterAuthSocialInput input) =>
        {
            var provider = authOptions.OAuthProviders.FirstOrDefault(p =>
                string.Equals(p.ProviderName, input.Provider, StringComparison.OrdinalIgnoreCase));

            if (provider is null)
            {
                return ctx.BadRequest($"OAuth provider '{input.Provider}' is not configured.");
            }

            var state = ctx.Crypto.RandomToken(16);
            var verifier = Pkce.GenerateCodeVerifier();
            var challenge = Pkce.GenerateCodeChallenge(verifier);

            var redirectUri = BuildRedirectUri(ctx, authOptions, provider.ProviderName);
            var targetCallback = input.CallbackURL ?? "/dashboard";

            // Post-auth redirect targets must be site-relative paths: an
            // attacker-supplied absolute URL would turn the callback into an
            // open redirect (and its raw value into a header-injection probe).
            if (!Auth.IsSiteRelativePath(targetCallback))
            {
                return ctx.BadRequest("Callback URL must be a site-relative path starting with '/'.");
            }

            var statePayload = $"{state}|{verifier}|{targetCallback}";
            ctx.Cookies.Set(
                StateCookie,
                statePayload,
                ctx.Clock.IsoIn(TimeSpan.FromMinutes(10)),
                domain: null,
                secure: authOptions.SecureCookies);

            var authUrl = provider.BuildAuthorizationUrl(state, challenge, redirectUri);
            return ctx.Json(new BetterAuthSocialResponse(authUrl));
        });

        app.Get("/callback/:provider", async ctx =>
        {
            var providerName = ctx.Req.Param("provider");
            var code = ctx.Req.QueryValue("code");
            var state = ctx.Req.QueryValue("state");

            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
            {
                return ctx.BadRequest("Missing OAuth authorization code or state.");
            }

            var stateCookie = ctx.Cookies.Get(StateCookie);
            if (string.IsNullOrEmpty(stateCookie))
            {
                return ctx.BadRequest("OAuth state expired or missing.");
            }

            var parts = stateCookie.Split('|', 3);
            if (parts.Length < 3 || !string.Equals(parts[0], state, StringComparison.Ordinal))
            {
                return ctx.BadRequest("Invalid OAuth state verification.");
            }

            var verifier = parts[1];
            var destination = parts[2];

            var provider = authOptions.OAuthProviders.FirstOrDefault(p =>
                string.Equals(p.ProviderName, providerName, StringComparison.OrdinalIgnoreCase));

            if (provider is null)
            {
                return ctx.BadRequest($"OAuth provider '{providerName}' is not configured.");
            }

            var redirectUri = BuildRedirectUri(ctx, authOptions, provider.ProviderName);
            var userInfo = await provider.ExchangeCodeAsync(code, verifier, redirectUri);

            var accounts = await ctx.Db.QueryAsync<Auth.OAuthAccountRow>(
                "SELECT id, user_id as UserId, provider, provider_user_id as ProviderUserId, created_at as CreatedAt FROM oauth_accounts WHERE provider = @provider AND provider_user_id = @puid LIMIT 1",
                new Dictionary<string, object?> { ["provider"] = userInfo.Provider, ["puid"] = userInfo.ProviderUserId });

            string userId;

            if (accounts.Count > 0)
            {
                userId = accounts[0].UserId;
            }
            else
            {
                var existingUsers = await ctx.Db.QueryAsync<Auth.UserRow>(
                    "SELECT id, email, name, password_hash as PasswordHash, is_anonymous as IsAnonymous, role as Role, created_at as CreatedAt, image as Image, email_verified as EmailVerified FROM users WHERE email = @email LIMIT 1",
                    new Dictionary<string, object?> { ["email"] = userInfo.Email.ToLowerInvariant() });

                if (existingUsers.Count > 0)
                {
                    // Never attach an OAuth identity to a password account on a
                    // provider email the IdP has not verified — that is the
                    // unverified-email account-takeover path.
                    if (!userInfo.EmailVerified)
                    {
                        return ctx.Conflict("Provider email is not verified. Sign in with your password, then link this provider from account settings.");
                    }

                    userId = existingUsers[0].Id;
                }
                else
                {
                    userId = Ids.New("usr");
                    var now = ctx.Clock.IsoNow();
                    var newUser = new Auth.UserRow(
                        Id: userId,
                        Email: userInfo.Email.ToLowerInvariant(),
                        Name: userInfo.Name,
                        PasswordHash: null,
                        IsAnonymous: 0,
                        Role: "user",
                        CreatedAt: now,
                        Image: userInfo.Image,
                        EmailVerified: userInfo.EmailVerified ? 1 : 0);

                    await ctx.Db.InsertAsync(Auth.Users, newUser);
                }

                await ctx.Db.InsertAsync(Auth.OAuthAccounts, new Auth.OAuthAccountRow(
                    Id: Ids.New("oac"),
                    UserId: userId,
                    Provider: userInfo.Provider,
                    ProviderUserId: userInfo.ProviderUserId,
                    CreatedAt: ctx.Clock.IsoNow()));
            }

            var sessionToken = ctx.Crypto.RandomToken(32);
            var sessionExpires = ctx.Clock.IsoIn(TimeSpan.FromDays(authOptions.SessionDays));

            await ctx.Db.InsertAsync(Auth.Sessions, new Auth.SessionRow(
                Id: Ids.New("ses"),
                UserId: userId,
                TokenHash: await ctx.Crypto.Sha256(sessionToken),
                ExpiresAt: sessionExpires,
                CreatedAt: ctx.Clock.IsoNow()));

            SetSessionCookie(ctx, authOptions, sessionToken, sessionExpires);
            ctx.Cookies.Delete(StateCookie, domain: null, secure: authOptions.SecureCookies);

            return ctx.Redirect(destination);
        });

        return app;
    }

    private static void SetSessionCookie(
        Ctx ctx,
        AuthOptions options,
        string token,
        string expiresAt)
    {
        ctx.Cookies.Set(
            BetterAuthCookie,
            token,
            expiresAt,
            options.CookieDomain,
            options.SecureCookies);

        // Avoid a duplicate Set-Cookie header when the app configures its
        // cookie name to the Better Auth wire name.
        if (!string.Equals(options.CookieName, BetterAuthCookie, StringComparison.Ordinal))
        {
            ctx.Cookies.Set(
                options.CookieName,
                token,
                expiresAt,
                options.CookieDomain,
                options.SecureCookies);
        }
    }

    private static string BuildRedirectUri(Ctx ctx, AuthOptions options, string providerName)
    {
        // A configured public origin wins: X-Forwarded-*/Host headers are
        // client-spoofable whenever the app is not behind a trusted proxy.
        var publicOrigin = options.PublicBaseUrl?.TrimEnd('/');
        if (!string.IsNullOrEmpty(publicOrigin))
        {
            if (!publicOrigin.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                publicOrigin = "https://" + publicOrigin;
            return $"{publicOrigin}/api/auth/callback/{providerName}";
        }

        var scheme = ctx.Req.Header("X-Forwarded-Proto") ?? "http";
        var host = ctx.Req.Header("X-Forwarded-Host") ?? ctx.Req.Header("Host") ?? "localhost";
        return $"{scheme}://{host}/api/auth/callback/{providerName}";
    }
}
