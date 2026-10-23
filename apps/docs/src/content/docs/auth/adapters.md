---
title: Authentication Adapters
description: Integrating Better Auth, Clerk, and custom identity providers.
---

## Better Auth Adapter

If your application already uses [Better Auth](https://better-auth.com) in TypeScript, Reptar provides a native adapter that reads the session table directly from the shared D1 database:

```csharp
var app = Reptar(new ReptarConfig
{
    // Reads better-auth's session table by cookie token in the same D1 instance
    Auth = BetterAuth()
});
```

Because Better Auth writes to the same D1 database, the adapter inspects the incoming session cookie, resolves the session row, and populates `c.User` with zero network overhead.

---

## Clerk Adapter

For teams leveraging [Clerk](https://clerk.com), the Clerk adapter verifies incoming JWTs against Clerk’s JWKS endpoint using host WebCrypto:

```csharp
var app = Reptar(new ReptarConfig
{
    Auth = Clerk(new ClerkOptions
    {
        PublishableKey = "pk_test_...",
        SecretKey = "sk_test_..."
    })
});
```

---

## Custom Authentication Providers

You can define custom authentication logic with an asynchronous delegate:

```csharp
var app = Reptar(new ReptarConfig
{
    Auth = Custom(async c =>
    {
        var token = c.Req.Header("Authorization")?.Replace("Bearer ", "");
        if (string.IsNullOrEmpty(token)) return null;

        var user = await c.Db.QuerySingleOrDefaultAsync<User>(
            "select * from api_keys where token = @token",
            new { token }
        );

        return user is not null ? new AuthUser(user.Id, user.Email, user.Role) : null;
    })
});
```
