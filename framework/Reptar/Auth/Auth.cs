using static Reptar.ReptarApi;

namespace Reptar;

public static class Auth
{
    private static AuthOptions _options = new();

    public static void Configure(AuthOptions options) => _options = options;

    public static readonly TableDef Users = DefineTable("users",
        Col.Text("id").PrimaryKey(),
        Col.Text("email").NotNull().Unique(),
        Col.Text("name").NotNull(),
        Col.Text("password_hash"),
        Col.Text("image"),
        Col.Int("email_verified").NotNull().Default(0),
        Col.Int("is_anonymous").NotNull().Default(0),
        Col.Text("role").NotNull().Default("user"),
        Col.Text("created_at").DefaultSql("CURRENT_TIMESTAMP")
    );

    public static readonly TableDef Sessions = DefineTable("sessions",
        Col.Text("id").PrimaryKey(),
        Col.Text("user_id").NotNull().References("users", "id", onDelete: "cascade"),
        Col.Text("token_hash").NotNull().Unique(),
        Col.Text("expires_at").NotNull(),
        Col.Text("created_at").DefaultSql("CURRENT_TIMESTAMP")
    );

    public static readonly TableDef OAuthAccounts = DefineTable("oauth_accounts",
        Col.Text("id").PrimaryKey(),
        Col.Text("user_id").NotNull().References("users", "id", onDelete: "cascade"),
        Col.Text("provider").NotNull(),
        Col.Text("provider_user_id").NotNull(),
        Col.Text("created_at").DefaultSql("CURRENT_TIMESTAMP")
    );

    public static readonly TableDef ApiKeys = DefineTable("api_keys",
        Col.Text("id").PrimaryKey(),
        Col.Text("user_id").NotNull().References("users", "id", onDelete: "cascade"),
        Col.Text("name").NotNull(),
        Col.Text("key_hash").NotNull().Unique(),
        Col.Text("prefix").NotNull(),
        Col.Text("expires_at"),
        Col.Text("created_at").DefaultSql("CURRENT_TIMESTAMP")
    );

    [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.All)]
    public sealed record UserRow(
        string Id,
        string Email,
        string Name,
        string? PasswordHash,
        int IsAnonymous,
        string Role,
        string CreatedAt,
        string? Image = null,
        int EmailVerified = 0);

    [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.All)]
    public sealed record SessionRow(
        string Id,
        string UserId,
        string TokenHash,
        string ExpiresAt,
        string CreatedAt);

    [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.All)]
    public sealed record OAuthAccountRow(
        string Id,
        string UserId,
        string Provider,
        string ProviderUserId,
        string CreatedAt);

    [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.All)]
    public sealed record ApiKeyRow(
        string Id,
        string UserId,
        string Name,
        string KeyHash,
        string Prefix,
        string? ExpiresAt,
        string CreatedAt);

    public sealed record SignUpInput(string Email, string Password, string Name);
    public sealed record SignInInput(string Email, string Password);
    public sealed record BuiltInSocialInput(string Provider, string CallbackUrl);
    public sealed record SessionInfo(string Id, string CreatedAt, string ExpiresAt, bool IsCurrent);
    public sealed record RevokeSessionInput(string SessionId);
    public sealed record ChangePasswordInput(string CurrentPassword, string NewPassword);
    public sealed record CreateApiKeyInput(string Name, int? ExpiresInDays = null);
    public sealed record ApiKeyResponse(string Id, string Name, string Key, string Prefix, string? ExpiresAt, string CreatedAt);
    public sealed record ApiKeyInfo(string Id, string Name, string Prefix, string? ExpiresAt, string CreatedAt);

    // Concrete response shapes for the small auth mutations. Anonymous objects
    // cannot be registered in a JsonSerializerContext and therefore throw
    // under Native AOT.
    public sealed record AuthErrorResponse(string Error);
    public sealed record AuthUrlResponse(string Url);
    public sealed record AuthSuccessResponse(bool Success);

    public static readonly Validator<SignUpInput> SignUpSchema = Rule<SignUpInput>()
        .Prop(x => x.Email, rules => rules.Email())
        .Prop(x => x.Password, rules => rules.Min(8).Max(128))
        .Prop(x => x.Name, rules => rules.Min(1).Max(80));

    public static readonly Validator<SignInInput> SignInSchema = Rule<SignInInput>()
        .Prop(x => x.Email, rules => rules.Email())
        .Prop(x => x.Password, rules => rules.Min(1).Max(128));

    public static readonly Validator<ChangePasswordInput> ChangePasswordSchema = Rule<ChangePasswordInput>()
        .Prop(x => x.CurrentPassword, rules => rules.Min(1))
        .Prop(x => x.NewPassword, rules => rules.Min(8).Max(128));

    public static readonly Validator<CreateApiKeyInput> CreateApiKeySchema = Rule<CreateApiKeyInput>()
        .Prop(x => x.Name, rules => rules.Min(1).Max(64));

    public static ReptarApp Routes()
    {
        var app = ReptarApp.Create();
        app.RegisterSchema(Users, Sessions, OAuthAccounts, ApiKeys);

        app.Post("/signup", SignUpSchema, async (ctx, input) =>
        {
            var email = input.Email.Trim().ToLowerInvariant();

            if (await ctx.Db.ExistsAsync(Users, "email = @email", new { email }))
                return ctx.Conflict("Email already in use.");

            var user = new UserRow(
                Id: Ids.New("usr"),
                Email: email,
                Name: input.Name.Trim(),
                PasswordHash: await ctx.Crypto.HashPassword(input.Password),
                IsAnonymous: 0,
                Role: _options.BootstrapAdmins.Contains(email) ? "admin" : "user",
                CreatedAt: ctx.Clock.IsoNow());

            await ctx.Db.InsertAsync(Users, user);
            await StartSession(ctx, user.Id);

            return ctx.Json(new SessionUser(user.Id, user.Email, user.Name, user.Role, user.Image, user.EmailVerified), 201);
        })
            .Produces<SessionUser>(201)
            .Produces<ValidationError>(400)
            .Produces<ErrorBody>(409);

        app.Post("/signin", SignInSchema, async (ctx, input) =>
        {
            // Throttling keys on the socket peer address by default; proxy
            // headers are client-controlled and only consulted behind an
            // explicit TrustProxyHeaders opt-in.
            var throttleKey = _options.EnableRateLimiting
                ? LoginThrottle.ClientKey(ctx, _options)
                : null;
            var window = TimeSpan.FromMinutes(_options.RateLimitWindowMinutes);

            if (throttleKey is not null &&
                LoginThrottle.IsLimited(throttleKey, _options.RateLimitMaxAttempts, window))
            {
                return ctx.Json(new AuthErrorResponse("Too many failed login attempts. Please try again later."), 429);
            }

            var email = input.Email.Trim().ToLowerInvariant();

            var user = await ctx.Db.QuerySingleOrDefaultAsync<UserRow>(
                "SELECT * FROM users WHERE email = @email",
                new { email });

            if (user is null || user.PasswordHash is null || !await ctx.Crypto.VerifyPassword(input.Password, user.PasswordHash))
            {
                if (throttleKey is not null)
                    LoginThrottle.RecordFailure(throttleKey, _options.RateLimitMaxAttempts, window);
                return ctx.Unauthorized("Invalid email or password.");
            }

            if (throttleKey is not null)
                LoginThrottle.Reset(throttleKey);
            await StartSession(ctx, user.Id);

            return ctx.Json(new SessionUser(user.Id, user.Email, user.Name, user.Role, user.Image, user.EmailVerified));
        })
            .Produces<SessionUser>(200)
            .Produces<ValidationError>(400)
            .Produces<ErrorBody>(401);

        app.Post("/signout", async ctx =>
        {
            var token = ctx.Cookies.Get(_options.CookieName);

            if (token is not null)
            {
                await ctx.Db.DeleteWhereAsync(
                    Sessions,
                    "token_hash = @hash",
                    new { hash = await ctx.Crypto.Sha256(token) });
            }

            ctx.Cookies.Delete(_options.CookieName, _options.CookieDomain, _options.SecureCookies);
            return ctx.Status(204);
        })
            .Produces(204);

        app.Get("/session", async ctx =>
        {
            var user = await CurrentUser(ctx);
            return ctx.Json(new SessionResponse(user));
        })
            .Produces<SessionResponse>(200);

        app.Post("/anonymous", async ctx =>
        {
            if (!_options.AllowAnonymous)
                return ctx.NotFound("Anonymous sessions are disabled.");

            var id = Ids.New("usr");

            var user = new UserRow(
                Id: id,
                Email: $"guest_{id}@reptar.local",
                Name: "Guest",
                PasswordHash: "!",
                IsAnonymous: 1,
                Role: "user",
                CreatedAt: ctx.Clock.IsoNow());

            await ctx.Db.InsertAsync(Users, user);
            await StartSession(ctx, user.Id);

            return ctx.Json(new SessionUser(user.Id, user.Email, user.Name, user.Role, user.Image, user.EmailVerified), 201);
        })
            .Produces<SessionUser>(201)
            .Produces<ErrorBody>(404);

        // Social OAuth 2.0 PKCE Login
        app.Post<BuiltInSocialInput>("/social", async (ctx, input) =>
        {
            var provider = _options.OAuthProviders.Find(p =>
                p.ProviderName.Equals(input.Provider, StringComparison.OrdinalIgnoreCase));

            if (provider is null)
            {
                return ctx.BadRequest($"OAuth provider '{input.Provider}' is not configured.");
            }

            // Post-auth redirect targets must be site-relative paths: an
            // attacker-supplied absolute URL would turn the callback into an
            // open redirect (and its raw value into a header-injection probe).
            var callbackUrl = input.CallbackUrl;
            if (!IsSiteRelativePath(callbackUrl))
            {
                return ctx.BadRequest("Callback URL must be a site-relative path starting with '/'.");
            }

            var verifier = Pkce.GenerateCodeVerifier();
            var challenge = Pkce.GenerateCodeChallenge(verifier);
            var state = ctx.Crypto.RandomToken(16);

            var redirectUri = BuildRedirectUri(ctx, provider.ProviderName);

            var expires = ctx.Clock.IsoIn(TimeSpan.FromMinutes(10));
            // The state binds the authorization round-trip: the callback
            // compares it against this cookie before exchanging any code.
            ctx.Cookies.Set(
                $"reptar_oauth_pkce_{provider.ProviderName}",
                $"{state}|{verifier}|{callbackUrl}",
                expires,
                domain: null,
                secure: _options.SecureCookies);

            var authUrl = provider.BuildAuthorizationUrl(state, challenge, redirectUri);
            return ctx.Json(new AuthUrlResponse(authUrl));
        });

        app.Get("/callback/:provider", async ctx =>
        {
            var providerName = ctx.Req.Param("provider");
            var provider = _options.OAuthProviders.Find(p =>
                p.ProviderName.Equals(providerName, StringComparison.OrdinalIgnoreCase));

            if (provider is null)
                return ctx.BadRequest($"Provider '{providerName}' is not configured.");

            var code = ctx.Req.QueryValue("code");
            if (string.IsNullOrEmpty(code))
                return ctx.BadRequest("Authorization code missing.");

            var state = ctx.Req.QueryValue("state");
            var pkceCookie = ctx.Cookies.Get($"reptar_oauth_pkce_{provider.ProviderName}");
            if (string.IsNullOrEmpty(pkceCookie))
                return ctx.BadRequest("Missing or expired OAuth state.");

            // The state cookie binds this callback to the authorize request the
            // user actually made; without it, any code could be replayed into
            // this browser (login CSRF / account binding hijack).
            var parts = pkceCookie.Split('|', 3);
            if (parts.Length < 3 ||
                string.IsNullOrEmpty(state) ||
                !string.Equals(parts[0], state, StringComparison.Ordinal))
            {
                return ctx.BadRequest("Invalid OAuth state verification.");
            }

            var verifier = parts[1];
            var callbackUrl = string.IsNullOrEmpty(parts[2]) ? "/" : parts[2];

            var redirectUri = BuildRedirectUri(ctx, provider.ProviderName);

            OAuthUserInfo userInfo;
            try
            {
                userInfo = await provider.ExchangeCodeAsync(code, verifier, redirectUri);
            }
            catch (Exception ex)
            {
                return ctx.BadRequest($"OAuth token exchange failed: {ex.Message}");
            }

            var email = userInfo.Email.Trim().ToLowerInvariant();

            // An existing provider link wins: it was established at first
            // sign-in and is bound to the provider identity, not the email.
            var account = await ctx.Db.QuerySingleOrDefaultAsync<OAuthAccountRow>(
                "SELECT id, user_id as UserId, provider, provider_user_id as ProviderUserId, created_at as CreatedAt FROM oauth_accounts WHERE provider = @provider AND provider_user_id = @puid",
                new { provider = provider.ProviderName, puid = userInfo.ProviderUserId });

            string userId;

            if (account is not null)
            {
                userId = account.UserId;
            }
            else
            {
                var existing = await ctx.Db.QuerySingleOrDefaultAsync<UserRow>(
                    "SELECT * FROM users WHERE email = @email",
                    new { email });

                if (existing is not null && !userInfo.EmailVerified)
                {
                    // Never attach an OAuth identity to a password account on a
                    // provider email the IdP has not verified — that is the
                    // unverified-email account-takeover path.
                    return ctx.Conflict("Provider email is not verified. Sign in with your password, then link this provider from account settings.");
                }

                if (existing is null)
                {
                    var id = Ids.New("usr");
                    existing = new UserRow(
                        Id: id,
                        Email: email,
                        Name: string.IsNullOrEmpty(userInfo.Name) ? userInfo.Email : userInfo.Name,
                        PasswordHash: null,
                        IsAnonymous: 0,
                        Role: _options.BootstrapAdmins.Contains(email) ? "admin" : "user",
                        CreatedAt: ctx.Clock.IsoNow(),
                        Image: userInfo.Image,
                        EmailVerified: userInfo.EmailVerified ? 1 : 0);

                    await ctx.Db.InsertAsync(Users, existing);
                }

                userId = existing.Id;

                await ctx.Db.InsertAsync(OAuthAccounts, new OAuthAccountRow(
                    Id: Ids.New("acc"),
                    UserId: userId,
                    Provider: provider.ProviderName,
                    ProviderUserId: userInfo.ProviderUserId,
                    CreatedAt: ctx.Clock.IsoNow()));
            }

            var linkedUser = await FindUserById(ctx, userId);
            if (linkedUser is null)
                return ctx.Unauthorized("Linked account no longer exists.");

            ctx.Cookies.Delete(
                $"reptar_oauth_pkce_{provider.ProviderName}",
                domain: null,
                secure: _options.SecureCookies);
            await StartSession(ctx, userId);

            return ctx.Redirect(callbackUrl);
        });

        // Multi-Session Management
        app.Get("/sessions", async ctx =>
        {
            var user = await CurrentUser(ctx);
            if (user is null) return ctx.Unauthorized();

            var currentToken = ctx.Cookies.Get(_options.CookieName) ?? ctx.Cookies.Get("better-auth.session_token");
            var currentHash = currentToken is not null ? await ctx.Crypto.Sha256(currentToken) : null;

            var rows = await ctx.Db.QueryMappedAsync(
                "SELECT id, created_at, expires_at, token_hash FROM sessions WHERE user_id = @userId AND expires_at > @now ORDER BY created_at DESC",
                new Dictionary<string, object?> { ["userId"] = user.Id, ["now"] = ctx.Clock.IsoNow() },
                r => new SessionInfo(
                    (string)r["id"]!,
                    (string)r["created_at"]!,
                    (string)r["expires_at"]!,
                    currentHash is not null && (string)r["token_hash"]! == currentHash
                ));

            return ctx.Json(rows);
        });

        app.Post<RevokeSessionInput>("/sessions/revoke", async (ctx, input) =>
        {
            var user = await CurrentUser(ctx);
            if (user is null) return ctx.Unauthorized();

            await ctx.Db.DeleteWhereAsync(Sessions,
                "id = @id AND user_id = @userId",
                new { id = input.SessionId, userId = user.Id });

            return ctx.Json(new AuthSuccessResponse(true));
        });

        app.Post("/sessions/revoke-others", async ctx =>
        {
            var user = await CurrentUser(ctx);
            if (user is null) return ctx.Unauthorized();

            var currentToken = ctx.Cookies.Get(_options.CookieName) ?? ctx.Cookies.Get("better-auth.session_token");
            if (currentToken is not null)
            {
                var currentHash = await ctx.Crypto.Sha256(currentToken);
                await ctx.Db.DeleteWhereAsync(Sessions,
                    "user_id = @userId AND token_hash != @currentHash",
                    new { userId = user.Id, currentHash });
            }

            return ctx.Json(new AuthSuccessResponse(true));
        });

        // Change Password
        app.Post<ChangePasswordInput>("/change-password", ChangePasswordSchema, async (ctx, input) =>
        {
            var user = await CurrentUser(ctx);
            if (user is null) return ctx.Unauthorized();

            var userRow = await ctx.Db.QuerySingleOrDefaultAsync<UserRow>(
                "SELECT * FROM users WHERE id = @id",
                new { id = user.Id });

            if (userRow is null || userRow.PasswordHash is null ||
                !await ctx.Crypto.VerifyPassword(input.CurrentPassword, userRow.PasswordHash))
            {
                return ctx.Json(new AuthErrorResponse("Current password is incorrect."), 400);
            }

            var newHash = await ctx.Crypto.HashPassword(input.NewPassword);
            await ctx.Db.ExecuteAsync(
                "UPDATE users SET password_hash = @newHash WHERE id = @id",
                new Dictionary<string, object?> { ["newHash"] = newHash, ["id"] = user.Id });

            return ctx.Json(new AuthSuccessResponse(true));
        });

        // Personal Access Tokens / API Keys
        app.Post<CreateApiKeyInput>("/api-keys", CreateApiKeySchema, async (ctx, input) =>
        {
            var user = await CurrentUser(ctx);
            if (user is null) return ctx.Unauthorized();

            var rawToken = "rep_live_" + ctx.Crypto.RandomToken(24);
            var prefix = rawToken[..13];
            var keyHash = await ctx.Crypto.Sha256(rawToken);
            var id = Ids.New("key");
            var now = ctx.Clock.IsoNow();
            string? expiresAt = input.ExpiresInDays.HasValue
                ? ctx.Clock.IsoIn(TimeSpan.FromDays(input.ExpiresInDays.Value))
                : null;

            await ctx.Db.InsertAsync(ApiKeys, new ApiKeyRow(
                Id: id,
                UserId: user.Id,
                Name: input.Name.Trim(),
                KeyHash: keyHash,
                Prefix: prefix,
                ExpiresAt: expiresAt,
                CreatedAt: now));

            return ctx.Json(new ApiKeyResponse(id, input.Name.Trim(), rawToken, prefix, expiresAt, now), 201);
        });

        app.Get("/api-keys", async ctx =>
        {
            var user = await CurrentUser(ctx);
            if (user is null) return ctx.Unauthorized();

            var rows = await ctx.Db.QueryMappedAsync(
                "SELECT id, name, prefix, expires_at, created_at FROM api_keys WHERE user_id = @userId ORDER BY created_at DESC",
                new Dictionary<string, object?> { ["userId"] = user.Id },
                r => new ApiKeyInfo(
                    (string)r["id"]!,
                    (string)r["name"]!,
                    (string)r["prefix"]!,
                    r["expires_at"] as string,
                    (string)r["created_at"]!
                ));

            return ctx.Json(rows);
        });

        app.Delete("/api-keys/:id", async ctx =>
        {
            var user = await CurrentUser(ctx);
            if (user is null) return ctx.Unauthorized();

            var id = ctx.Req.Param("id");
            if (string.IsNullOrEmpty(id)) return ctx.BadRequest("API Key ID required.");

            await ctx.Db.DeleteWhereAsync(ApiKeys,
                "id = @id AND user_id = @userId",
                new { id, userId = user.Id });

            return ctx.Json(new AuthSuccessResponse(true));
        });

        return app;
    }

    public static ReptarApp BetterAuthRoutes(AuthOptions? options = null) =>
        BetterAuthProtocol.Routes(options ?? _options);

    public static readonly ReptarMiddleware RequireUser = async (ctx, next) =>
    {
        var user = await CurrentUser(ctx);

        if (user is null)
            return ctx.Unauthorized();

        ctx.User = user;
        return await next();
    };

    public static readonly ReptarMiddleware OptionalUser = async (ctx, next) =>
    {
        ctx.User = await CurrentUser(ctx);
        return await next();
    };

    public static ReptarMiddleware RequireRole(params string[] roles) => async (ctx, next) =>
    {
        var user = await CurrentUser(ctx);
        if (user is null)
            return ctx.Unauthorized();

        ctx.User = user;

        if (!roles.Contains(user.Role, StringComparer.OrdinalIgnoreCase))
            return ctx.Forbidden($"Forbidden: Requires role '{string.Join("' or '", roles)}'.");

        return await next();
    };

    public static readonly ReptarMiddleware RequireJwt = async (ctx, next) =>
    {
        var options = _options.Jwt;

        if (options is null)
            return ctx.Unauthorized("JWT authentication is not configured.");

        var token = Jwt.ExtractToken(ctx.Req);

        if (token is null)
            return ctx.Unauthorized();

        var payload = await Jwt.VerifyAsync(ctx.Crypto, options, token);

        if (payload is null)
            return ctx.Unauthorized();

        var user = await FindUserById(ctx, payload.Subject);

        if (user is null)
            return ctx.Unauthorized();

        ctx.User = user;
        ctx.Claims = payload;
        return await next();
    };

    public static async Task<SessionUser?> CurrentUser(Ctx ctx)
    {
        var token = ctx.Cookies.Get(_options.CookieName)
            ?? ctx.Cookies.Get("better-auth.session_token");

        if (token is null)
        {
            var authHeader = ctx.Req.Header("Authorization");
            if (authHeader != null && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                token = authHeader[7..].Trim();
            }
        }

        if (token is not null)
        {
            var hash = await ctx.Crypto.Sha256(token);

            var sessionUser = await ctx.Db.QuerySingleOrDefaultAsync<SessionUser>(
                """
                SELECT u.id, u.email, u.name, u.role, u.image, u.email_verified
                FROM sessions s
                JOIN users u ON u.id = s.user_id
                WHERE s.token_hash = @hash AND s.expires_at > @now
                """,
                new { hash, now = ctx.Clock.IsoNow() });

            if (sessionUser is not null)
                return sessionUser;

            // Also check api_keys table for personal access tokens
            var apiKeyUser = await ctx.Db.QuerySingleOrDefaultAsync<SessionUser>(
                """
                SELECT u.id, u.email, u.name, u.role, u.image, u.email_verified
                FROM api_keys k
                JOIN users u ON u.id = k.user_id
                WHERE k.key_hash = @hash AND (k.expires_at IS NULL OR k.expires_at > @now)
                """,
                new { hash, now = ctx.Clock.IsoNow() });

            if (apiKeyUser is not null)
                return apiKeyUser;
        }

        var jwt = _options.Jwt;
        var headerToken = Jwt.ExtractToken(ctx.Req);

        if (jwt is null || headerToken is null)
            return null;

        var payload = await Jwt.VerifyAsync(ctx.Crypto, jwt, headerToken);

        if (payload is null)
            return null;

        ctx.Claims = payload;
        return await FindUserById(ctx, payload.Subject);
    }

    public static async Task<string> IssueJwtAsync(
        Ctx ctx,
        SessionUser user,
        string? role = null)
    {
        var options = _options.Jwt
            ?? throw new InvalidOperationException(
                "JWT authentication is not configured.");

        var payload = Jwt.CreatePayload(
            user,
            role,
            TimeSpan.FromMinutes(options.LifetimeMinutes));

        return await Jwt.SignAsync(ctx.Crypto, options, payload);
    }

    /// <summary>
    /// OAuth callback targets must be site-relative paths ("/dashboard"), never
    /// absolute or protocol-relative URLs (open redirect / phishing), and never
    /// control characters (header injection).
    /// </summary>
    internal static bool IsSiteRelativePath(string? url) =>
        url is not null &&
        url.Length <= 512 &&
        url.StartsWith('/') &&
        !url.StartsWith("//", StringComparison.Ordinal) &&
        !url.StartsWith("/\\", StringComparison.Ordinal) &&
        url.AsSpan().IndexOfAny('\r', '\n', '\0') < 0;

    internal static string BuildRedirectUri(Ctx ctx, string providerName)
    {
        // A configured public origin wins: Origin/Host headers are client-
        // spoofable whenever the app is not behind a header-rewriting proxy.
        var publicOrigin = _options.PublicBaseUrl?.TrimEnd('/');
        if (!string.IsNullOrEmpty(publicOrigin))
        {
            if (!publicOrigin.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                publicOrigin = "https://" + publicOrigin;
            return $"{publicOrigin}/auth/callback/{providerName}";
        }

        var origin = ctx.Req.Header("Origin") ?? ctx.Req.Header("Host") ?? "http://localhost:8787";
        if (!origin.StartsWith("http", StringComparison.OrdinalIgnoreCase)) origin = "http://" + origin;
        return $"{origin}/auth/callback/{providerName}";
    }

    public static Task<SessionUser?> FindUserById(Ctx ctx, string id) =>
        ctx.Db.QuerySingleOrDefaultAsync<SessionUser>(
            "SELECT id, email, name, role, image, email_verified FROM users WHERE id = @id",
            new { id });

    private static async Task StartSession(Ctx ctx, string userId)
    {
        var token = ctx.Crypto.RandomToken(32);
        var expiresAt = ctx.Clock.IsoIn(TimeSpan.FromDays(_options.SessionDays));

        await ctx.Db.InsertAsync(Sessions, new SessionRow(
            Id: Ids.New("ses"),
            UserId: userId,
            TokenHash: await ctx.Crypto.Sha256(token),
            ExpiresAt: expiresAt,
            CreatedAt: ctx.Clock.IsoNow()));

        ctx.Cookies.Set(
            _options.CookieName,
            token,
            expiresAt,
            _options.CookieDomain,
            _options.SecureCookies);
    }
}
