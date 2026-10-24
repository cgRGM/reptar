---
title: Built-in C# Authentication
description: Host-native PBKDF2 hashing, D1 session tokens, and middleware.
---

## Overview

Reptar ships with a complete, mountable authentication feature slice (`Reptar.Auth`):

- **Tables:** `users` and `sessions` managed in D1.
- **Password Security:** PBKDF2 password hashing (100,000 iterations capped to comply with Cloudflare `workerd` limits) executed via `c.Crypto` over native WebCrypto.
- **Session Tokens:** Cryptographically random 32-byte tokens. SHA-256 hashes stored in D1; plain tokens stored in secure `HttpOnly` cookies.
- **Anonymous Sessions:** First-class guest sessions for frictionless user onboarding via `POST /auth/anonymous`.
- **Middleware:** `Auth.RequireUser` and role-based access checks.

---

## Configuration

Enable built-in auth in `Program.cs` / `app.cs`:

```csharp
var app = ReptarApp.Create(new ReptarConfig
{
    Auth = BuiltIn(new AuthOptions
    {
        SessionDays = 30,
        AllowAnonymous = true,
        CookieName = "reptar_session"
    })
});

// Mount the standard auth routes
app.Route("/auth", Reptar.Auth.Routes());
```

---

## Mounted Endpoints

Mounting `Reptar.Auth.Routes()` exposes the following endpoints:

| Endpoint | Method | Status | Description |
| --- | --- | --- | --- |
| `/auth/signup` | POST | 201 | Registers a new user with email, name, and password |
| `/auth/signin` | POST | 200 | Authenticates and returns an HttpOnly session cookie |
| `/auth/signout` | POST | 204 | Revokes the current session and clears cookies |
| `/auth/session` | GET | 200 | Returns the currently authenticated user profile |
| `/auth/anonymous` | POST | 201 | Creates a temporary guest user and starts a session |

---

## Route Protection Middleware

Protect your application routes using `Auth.RequireUser`:

```csharp
app.Get("/me", Auth.RequireUser, async ctx =>
{
    // ctx.User is guaranteed non-null
    return ctx.Json(new SessionResponse(ctx.User));
});
```

If an unauthenticated request hits a protected route, Reptar automatically returns a standardized 401 response envelope:

```json
{
  "code": "unauthorized",
  "message": "Unauthorized.",
  "requestId": "req_01..."
}
```
