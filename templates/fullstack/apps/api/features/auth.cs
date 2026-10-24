namespace ReptarApp.Features;

using Reptar;
using Reptar.Auth;

public static class AuthFeature
{
    public static void Configure(ReptarApp app, AuthOptions authOptions)
    {
        // Mount Better Auth HTTP Wire Protocol routes under /api/auth
        var authGroup = app.Group("/api/auth");
        BetterAuthProtocol.Routes(authGroup, authOptions);

        // Current authenticated user profile
        app.Get("/api/me", async ctx =>
        {
            var user = await ctx.UserAsync();
            if (user is null)
            {
                return ctx.Json(new { authenticated = false, user = (object?)null });
            }

            return ctx.Json(new
            {
                authenticated = true,
                user = new
                {
                    id = user.Id,
                    name = user.Name ?? user.Email,
                    email = user.Email,
                    image = user.Image,
                    role = user.Role,
                }
            });
        });
    }
}
