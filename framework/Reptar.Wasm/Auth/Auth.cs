using System.Text.Json;
using static Reptar.ReptarApi;

namespace Reptar;

// Wasm/D1 auth slice. Uses raw positional SQL against the D1 client and the
// host WebCrypto shim for password hashing, session tokens, and SHA-256.
public static class Auth
{
    private static AuthOptions _options = new();

    public sealed record UserRow(
        string Id,
        string Email,
        string Name,
        string PasswordHash,
        int IsAnonymous,
        string Role,
        string CreatedAt);

    public sealed record CountRow(int Count);

    public sealed record SignUpInput(string Email, string Password, string Name);

    public sealed record SignInInput(string Email, string Password);

    public static readonly Validator<SignUpInput> SignUpSchema = Rule<SignUpInput>()
        .Prop(x => x.Email, rules => rules.Email())
        .Prop(x => x.Password, rules => rules.Min(8).Max(128))
        .Prop(x => x.Name, rules => rules.Min(1).Max(80));

    public static readonly Validator<SignInInput> SignInSchema = Rule<SignInInput>()
        .Prop(x => x.Email, rules => rules.Email())
        .Prop(x => x.Password, rules => rules.Min(1).Max(128));

    public static void Configure(AuthOptions options) => _options = options;

    public static ReptarApp Routes()
    {
        var app = ReptarApp.Create();

        app.Post("/signup", SignUpSchema, async (ctx, input) =>
        {
            var email = input.Email.Trim().ToLowerInvariant();
            var count = await ctx.Db.QuerySingleOrDefaultAsync<CountRow>(
                "select count(*) as count from users where email = ?",
                Params(email));

            if (count is not null && count.Count > 0)
            {
                return ctx.Conflict("Email already in use.");
            }

            var user = new UserRow(
                Id: Ids.New("usr"),
                Email: email,
                Name: input.Name.Trim(),
                PasswordHash: await ctx.Crypto.HashPassword(input.Password),
                IsAnonymous: 0,
                Role: _options.BootstrapAdmins.Contains(email) ? "admin" : "user",
                CreatedAt: ctx.Clock.IsoNow());

            await ctx.Db.ExecuteAsync(
                """
                insert into users
                  (id, email, name, password_hash, is_anonymous, role, created_at)
                values (?, ?, ?, ?, ?, ?, ?)
                """,
                Params(
                    user.Id,
                    user.Email,
                    user.Name,
                    user.PasswordHash,
                    user.IsAnonymous.ToString(),
                    user.Role,
                    user.CreatedAt));

            await StartSession(ctx, user.Id);

            return ctx.Json(new SessionUser(user.Id, user.Email, user.Name), 201);
        });

        app.Post("/signin", SignInSchema, async (ctx, input) =>
        {
            var email = input.Email.Trim().ToLowerInvariant();
            var user = await ctx.Db.QuerySingleOrDefaultAsync<UserRow>(
                """
                select id, email, name, password_hash as passwordHash,
                       is_anonymous as isAnonymous, role, created_at as createdAt
                from users
                where email = ?
                """,
                Params(email));

            if (user is null ||
                !await ctx.Crypto.VerifyPassword(input.Password, user.PasswordHash))
            {
                return ctx.Unauthorized("Invalid email or password.");
            }

            await StartSession(ctx, user.Id);

            return ctx.Json(new SessionUser(user.Id, user.Email, user.Name));
        });

        app.Post("/signout", async ctx =>
        {
            var token = ctx.Cookies.Get(_options.CookieName);

            if (token is not null)
            {
                await ctx.Db.ExecuteAsync(
                    "delete from sessions where token_hash = ?",
                    Params(await ctx.Crypto.Sha256(token)));
            }

            ctx.Cookies.Delete(_options.CookieName);
            return ctx.Status(204);
        });

        app.Get("/session", async ctx =>
            ctx.Json(new SessionResponse(await CurrentUser(ctx))));

        return app;
    }

    public static readonly ReptarMiddleware RequireUser = async (ctx, next) =>
    {
        var user = await CurrentUser(ctx);

        if (user is null)
        {
            return ctx.Unauthorized();
        }

        ctx.User = user;
        return await next();
    };

    public static async Task<SessionUser?> CurrentUser(Ctx ctx)
    {
        var token = ctx.Cookies.Get(_options.CookieName);

        if (token is null)
        {
            return null;
        }

        return await ctx.Db.QuerySingleOrDefaultAsync<SessionUser>(
            """
            select u.id as id, u.email as email, u.name as name
            from sessions s
            join users u on u.id = s.user_id
            where s.token_hash = ? and s.expires_at > ?
            """,
            Params(await ctx.Crypto.Sha256(token), ctx.Clock.IsoNow()));
    }

    private static async Task StartSession(Ctx ctx, string userId)
    {
        var token = ctx.Crypto.RandomToken(32);
        var expiresAt = ctx.Clock.IsoIn(TimeSpan.FromDays(_options.SessionDays));

        await ctx.Db.ExecuteAsync(
            """
            insert into sessions (id, user_id, token_hash, expires_at, created_at)
            values (?, ?, ?, ?, ?)
            """,
            Params(
                Ids.New("ses"),
                userId,
                await ctx.Crypto.Sha256(token),
                expiresAt,
                ctx.Clock.IsoNow()));

        ctx.Cookies.Set(
            _options.CookieName,
            token,
            TimeSpan.FromDays(_options.SessionDays),
            _options.CookieDomain);
    }

    private static string Params(params string?[] values)
    {
        var parts = new string[values.Length];

        for (var index = 0; index < values.Length; index += 1)
        {
            parts[index] = values[index] is null
                ? "null"
                : JsonSerializer.Serialize(values[index], ReptarJson.Options);
        }

        return $"[{string.Join(",", parts)}]";
    }
}
