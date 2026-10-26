---
title: Authentication Adapters
description: Integrating Better Auth, pure C# OAuth 2.0 PKCE, Clerk, and custom identity providers.
---

:::caution[Roadmap]
The `Clerk(...)` and `Custom(...)` adapters shown on this page are **not
implemented yet**. The built-in C# auth slice and the Better Auth wire
protocol are available today. The examples below document the intended shape
for the upcoming adapter release.
:::

## Better Auth Protocol & C# Server

Reptar includes a first-class C# server implementation of the **Better Auth HTTP specification**. Your React, Vue, Svelte, or Astro frontend can use official `@better-auth/client` and `better-auth/react` hooks to sign in, register, and manage sessions directly against Reptar—with zero Node.js server dependencies.

### How It Works

1. **Wire Protocol Compatibility:** Reptar exposes standard Better Auth REST endpoints (`/api/auth/sign-in/email`, `/api/auth/sign-up/email`, `/api/auth/get-session`, `/api/auth/sign-out`, `/api/auth/sign-in/social`, and `/api/auth/callback/:provider`).
2. **Dual-Token Support:** Authenticates requests using both the standard `better-auth.session_token` `HttpOnly` cookie and `Authorization: Bearer <token>` headers.
3. **Pure C# OAuth 2.0 PKCE:** Direct Google and GitHub OAuth 2.0 with cryptographic SHA-256 code challenge verification.

### C# Server Configuration

```csharp
var authOptions = new AuthOptions
{
    CookieName = "better-auth.session_token",
};

// Wire Google OAuth with PKCE
var googleClientId = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_ID");
var googleClientSecret = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_SECRET");
if (!string.IsNullOrEmpty(googleClientId) && !string.IsNullOrEmpty(googleClientSecret))
{
    authOptions.WithGoogle(googleClientId, googleClientSecret);
}

// Wire GitHub OAuth with PKCE
var githubClientId = Environment.GetEnvironmentVariable("GITHUB_CLIENT_ID");
var githubClientSecret = Environment.GetEnvironmentVariable("GITHUB_CLIENT_SECRET");
if (!string.IsNullOrEmpty(githubClientId) && !string.IsNullOrEmpty(githubClientSecret))
{
    authOptions.WithGitHub(githubClientId, githubClientSecret);
}

var app = ReptarApp.Create(new ReptarConfig
{
    Auth = authOptions,
    Database = Db.Sqlite("app.db"), // or Db.D1() / Db.Postgres() / Db.MySql()
});

// Mount Better Auth endpoints under /api/auth
app.Route("/api/auth", BetterAuthProtocol.Routes(authOptions));

// Use in protected endpoints
app.Get("/api/dashboard", async ctx =>
{
    var user = await ctx.UserAsync();
    if (user is null)
    {
        return ctx.Unauthorized();
    }

    return ctx.Json(new { message = $"Hello, {user.Name}!" });
});
```

### Frontend React Client Setup

In your frontend application (`apps/web/src/lib/auth-client.ts`):

```typescript
import { createAuthClient } from "better-auth/react";

export const authClient = createAuthClient({
  baseURL: import.meta.env.VITE_API_URL ?? "http://localhost:8787",
});

export const { useSession, signIn, signOut, signUp } = authClient;
```

## Clerk Adapter

For teams using [Clerk](https://clerk.com), the Clerk adapter verifies incoming JWTs against Clerk's JWKS endpoint using host WebCrypto:

```csharp
var app = ReptarApp.Create(new ReptarConfig
{
    Auth = Clerk(new ClerkOptions
    {
        PublishableKey = "pk_test_...",
        SecretKey = "sk_test_...",
    }),
});
```

## Custom Authentication Providers

You can define custom authentication logic with an asynchronous delegate and zero-allocation tuple parameters:

```csharp
var app = ReptarApp.Create(new ReptarConfig
{
    Auth = Custom(async c =>
    {
        var token = c.Req.Header("Authorization")?.Replace("Bearer ", "");
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        var user = await c.Db.QuerySingleOrDefaultAsync<User>(
            "SELECT id, email, role FROM api_keys WHERE token = @token",
            ("token", token));

        return user is not null ? new AuthUser(user.Id, user.Email, user.Role) : null;
    }),
});
```
