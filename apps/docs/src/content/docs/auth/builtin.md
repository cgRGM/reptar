---
title: Built-in C# Authentication
description: Host-native PBKDF2 hashing, multi-session management, API keys, OAuth PKCE, and role-based access.
---

## Overview

Reptar ships with a complete, mountable authentication feature slice (`Reptar.Auth`) that requires zero external services or ASP.NET Core packages:

- **Tables:** `users`, `sessions`, `oauth_accounts`, and `api_keys` automatically managed across Cloudflare D1, SQLite, PostgreSQL, and MySQL.
- **Password Security:** Host WebCrypto PBKDF2 hashing with automated brute-force rate-limiting on sign-in attempts.
- **Multi-Device Sessions:** List active devices and revoke specific sessions or revoke all other devices remotely.
- **Personal Access Tokens (API Keys):** Built-in support for generating secure `rep_live_...` developer tokens with instant `Authorization: Bearer` support.
- **Social OAuth 2.0 with PKCE:** Direct Google and GitHub login with SHA-256 code challenge verification.
- **Role-Based Authorization:** `Auth.RequireRole("admin")` middleware with zero reflection overhead.

---

## Configuration

Enable built-in auth in `Program.cs` / `app.cs`:

```csharp
var authOptions = new AuthOptions
{
    SessionDays = 30,
    AllowAnonymous = true,
    CookieName = "reptar_session",
    SecureCookies = true, // default; set false only for plain-HTTP local dev
    RateLimitMaxAttempts = 5,
    RateLimitWindowMinutes = 15,
};

// Wire optional OAuth providers
var googleClientId = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_ID");
var googleClientSecret = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_SECRET");
if (!string.IsNullOrEmpty(googleClientId))
{
    authOptions.WithGoogle(googleClientId, googleClientSecret!);
}

var app = ReptarApp.Create(new ReptarConfig
{
    Auth = authOptions,
    Database = Db.D1(), // or Db.Sqlite() / Db.Postgres() / Db.MySql()
});

// Mount the standard auth routes under /auth
app.Route("/auth", Reptar.Auth.Routes());
```

Session cookies are `HttpOnly`, `SameSite=Lax`, and `Secure` by default. Set
`SecureCookies = false` only when serving plain HTTP locally (for example the
scaffolded app does this under `#if DEBUG`). Login throttling uses
`RateLimitMaxAttempts` / `RateLimitWindowMinutes` on both Kestrel and the Wasm
auth slice (per-isolate best effort on Workers).

---

## Mounted Endpoints

Mounting `Reptar.Auth.Routes()` exposes the following endpoints:

| Endpoint | Method | Status | Description |
| --- | --- | --- | --- |
| `/auth/signup` | POST | 201 | Registers a new user with email, name, and password |
| `/auth/signin` | POST | 200 | Authenticates credentials with rate-limit protection |
| `/auth/signout` | POST | 204 | Revokes the current session and clears cookies |
| `/auth/session` | GET | 200 | Returns the currently authenticated user profile |
| `/auth/anonymous` | POST | 201 | Creates a temporary guest user and starts a session |
| `/auth/social` | POST | 200 | Generates a PKCE challenge and returns OAuth redirect URL |
| `/auth/callback/:provider` | GET | 302 | Exchanges OAuth code for tokens and starts session |
| `/auth/sessions` | GET | 200 | Lists all active sessions for current user |
| `/auth/sessions/revoke` | POST | 200 | Revokes a specific session by ID |
| `/auth/sessions/revoke-others` | POST | 200 | Revokes all active sessions except the current request |
| `/auth/change-password` | POST | 200 | Validates current password and sets new password |
| `/auth/api-keys` | POST | 201 | Generates a `rep_live_...` personal access token |
| `/auth/api-keys` | GET | 200 | Lists active API keys (with prefix and creation date) |
| `/auth/api-keys/:id` | DELETE | 200 | Revokes an API key |

---

## Route Protection & Role Authorization

Protect your application routes using `Auth.RequireUser` or `Auth.RequireRole`:

```csharp
// Require any authenticated user (cookie, session token, or API key)
app.Get("/me", Auth.RequireUser, async ctx =>
{
    return ctx.Json(new SessionResponse(ctx.User));
});

// Require specific role (e.g., admin)
app.Get("/admin/metrics", Auth.RequireRole("admin"), async ctx =>
{
    return ctx.Json(new { ok = true, admin = ctx.User!.Name });
});
```

---

## Authenticating with Personal Access Tokens

Any protected route in Reptar accepts both cookie-based sessions and API Keys generated via `POST /auth/api-keys`:

```bash
curl -H "Authorization: Bearer rep_live_a1b2c3d4..." http://localhost:8787/me
```

Reptar automatically hashes the bearer token, checks the `api_keys` table, verifies expiration, and populates `ctx.User` with zero manual code!
