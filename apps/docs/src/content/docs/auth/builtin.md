---
title: Built-in C# Authentication
description: Host-native scrypt hashing, D1 session tokens, and middleware.
---

## Overview

Reptar ships with a complete, mountable authentication feature slice (`Reptar.Auth`):

- **Tables:** `users` and `sessions` managed in D1.
- **Password Security:** Scrypt password hashing executed via `c.Crypto` (delegating to native host crypto to stay comfortably within Worker CPU limits).
- **Session Tokens:** Cryptographically random 32-byte tokens. SHA-256 hashes stored in D1; plain tokens stored in HttpOnly cookies.
- **Anonymous Sessions:** First-class guest sessions for frictionless user onboarding.
- **Middleware:** `RequireUser()` and `RequireRole("admin")`.

---

## Configuration

Enable built-in auth in `app.cs`:

```csharp
var app = Reptar(new ReptarConfig
{
    Auth = BuiltIn(new AuthOptions
    {
        SessionDays = 30,
        AllowAnonymous = true,
        CookieDomain = ".example.com" // Optional cross-subdomain sharing
    })
});

// Mount the standard auth routes
app.Route("/auth", Reptar.Auth.Routes());
```

---

## Mounted Endpoints

Mounting `Reptar.Auth.Routes()` exposes the following endpoints:

| Endpoint | Method | Description |
| --- | --- | --- |
| `/auth/signup` | POST | Registers a new user with email and password |
| `/auth/signin` | POST | Authenticates and returns an HttpOnly session cookie |
| `/auth/signout` | POST | Revokes the current session and clears cookies |
| `/auth/session` | GET | Returns the currently authenticated user profile |
| `/auth/anonymous` | POST | Creates a temporary guest session |

---

## Route Protection Middleware

Protect your application routes using `RequireUser`:

```csharp
app.Get("/profile", RequireUser(), async c =>
{
    // c.User is guaranteed non-null
    return c.Json(new
    {
        userId = c.User.Id,
        email = c.User.Email
    });
});
```
