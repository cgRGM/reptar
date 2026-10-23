---
title: The Ctx Surface
description: Complete reference for handler context, request inputs, and platform bindings.
---

## Overview

The `Ctx` object (`c`) is passed to every route handler and middleware. It exposes request data, platform bindings, cryptographic helpers, and response constructors.

```csharp
app.Get("/example", async c =>
{
    var id = c.Req.Param("id");
    var post = await c.Db.GetAsync<Post>(Schema.Posts, id);
    return c.Json(post);
});
```

---

## Member Reference

| Member | Backed by | Description |
| --- | --- | --- |
| `c.Req` | Request envelope | Query parameters, route params, headers, JSON body, multipart uploads |
| `c.Db` | Cloudflare D1 | Dapper-like query and execution client |
| `c.Storage` | Cloudflare R2 | Object operations: `PutAsync`, `GetAsync`, `ExistsAsync`, `DeleteAsync`, `Url` |
| `c.AI` | Workers AI | Neural inference: `GenerateAsync`, `TranscribeAsync`, `EmbedAsync` |
| `c.RateLimit` | Cloudflare RateLimiter | Edge rate limiting: `LimitAsync("chat", key)` |
| `c.Cookies` | Host Shim | Get, set, and delete cookies with HttpOnly and SameSite defaults |
| `c.Clock` | Host Shim | Deterministic time: `IsoNow()`, `IsoIn(duration)` |
| `c.Crypto` | Host WebCrypto | Native scrypt password hashing, SHA-256, and random token generation |
| `c.User` | Auth Slice | Authenticated user record populated by `RequireUser` middleware |
| `c.Json()` | Core Framework | Serializes typed JSON responses with status codes |
| `c.Status()` | Core Framework | Returns empty or text responses with explicit HTTP status codes |
| `c.Stream()` | Core Framework | Streams SSE or chunked responses across the Wasm boundary |

---

## Deterministic Testing with `c.Clock`

Avoid direct calls to `DateTime.UtcNow` in business logic. Using `c.Clock.IsoNow()` allows full determinism in unit tests and replay harnesses:

```csharp
var post = new Post(
    Id: Ids.New("post"),
    Title: input.Title,
    CreatedAt: c.Clock.IsoNow()
);
```
