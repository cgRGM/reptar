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
        Col.Text("password_hash").NotNull(),
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

    [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.All)]
    public sealed record UserRow(
        string Id,
        string Email,
        string Name,
        string PasswordHash,
        int IsAnonymous,
        string Role,
        string CreatedAt);

    [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.All)]
    public sealed record SessionRow(
        string Id,
        string UserId,
        string TokenHash,
        string ExpiresAt,
        string CreatedAt);

    public sealed record SignUpInput(string Email, string Password, string Name);

    public sealed record SignInInput(string Email, string Password);

    public static readonly Validator<SignUpInput> SignUpSchema = Rule<SignUpInput>()
        .Prop(x => x.Email, rules => rules.Email())
        .Prop(x => x.Password, rules => rules.Min(8).Max(128))
        .Prop(x => x.Name, rules => rules.Min(1).Max(80));

    public static readonly Validator<SignInInput> SignInSchema = Rule<SignInInput>()
        .Prop(x => x.Email, rules => rules.Email())
        .Prop(x => x.Password, rules => rules.Min(1).Max(128));

    public static ReptarApp Routes()
    {
        var app = ReptarApp.Create();
        app.RegisterSchema(Users, Sessions);

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

            return ctx.Json(new SessionUser(user.Id, user.Email, user.Name), 201);
        })
            .Produces<SessionUser>(201)
            .Produces<ValidationError>(400)
            .Produces<ErrorBody>(409);

        app.Post("/signin", SignInSchema, async (ctx, input) =>
        {
            var email = input.Email.Trim().ToLowerInvariant();

            var user = await ctx.Db.QuerySingleOrDefaultAsync<UserRow>(
                "SELECT * FROM users WHERE email = @email",
                new { email });

            if (user is null || !await ctx.Crypto.VerifyPassword(input.Password, user.PasswordHash))
                return ctx.Unauthorized("Invalid email or password.");

            await StartSession(ctx, user.Id);

            return ctx.Json(new SessionUser(user.Id, user.Email, user.Name));
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

            ctx.Cookies.Delete(_options.CookieName);
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

            return ctx.Json(new SessionUser(user.Id, user.Email, user.Name), 201);
        })
            .Produces<SessionUser>(201)
            .Produces<ErrorBody>(404);

        return app;
    }

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
        var token = ctx.Cookies.Get(_options.CookieName);

        if (token is not null)
        {
            var hash = await ctx.Crypto.Sha256(token);

            var sessionUser = await ctx.Db.QuerySingleOrDefaultAsync<SessionUser>(
                """
                SELECT u.id, u.email, u.name
                FROM sessions s
                JOIN users u ON u.id = s.user_id
                WHERE s.token_hash = @hash AND s.expires_at > @now
                """,
                new { hash, now = ctx.Clock.IsoNow() });

            if (sessionUser is not null)
                return sessionUser;
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

    public static Task<SessionUser?> FindUserById(Ctx ctx, string id) =>
        ctx.Db.QuerySingleOrDefaultAsync<SessionUser>(
            "SELECT id, email, name FROM users WHERE id = @id",
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

        ctx.Cookies.Set(_options.CookieName, token, expiresAt, _options.CookieDomain);
    }
}
