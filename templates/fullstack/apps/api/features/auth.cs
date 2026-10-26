namespace {{PROJECT_PASCAL}}.Features;

using Reptar;

public static class AuthFeature
{
    public static void Configure(ReptarApp app, AuthOptions authOptions)
    {
        // Mount the Better Auth HTTP wire protocol under /api/auth.
        // The auth tables (users, sessions, oauth) register on the sub-app and
        // merge into this app via Route(), so EnsureCreatedAsync covers them.
        app.Route("/api/auth", BetterAuthProtocol.Routes(authOptions));

        // Current authenticated user profile.
        app.Get("/api/me", async ctx =>
        {
            var user = await ctx.UserAsync();
            if (user is null)
            {
                return ctx.Json(new AuthMeResponse(false, null));
            }

            return ctx.Json(new AuthMeResponse(
                true,
                new UserProfile(
                    user.Id,
                    user.Name ?? user.Email,
                    user.Email,
                    user.Image,
                    user.Role)));
        })
        .Produces<AuthMeResponse>(200);
    }
}
