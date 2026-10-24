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
    var post = await c.Db.GetAsync<Post>(PostsTable, id);
    return c.Json(post);
});
```

---

## Member Reference

| Member | Backed by | Description |
| --- | --- | --- |
| `c.Req` | Request envelope | Query parameters, route params, headers, JSON body, raw byte stream |
| `c.Db` | Cloudflare D1 / SQLite | Reflection-free CRUD (`GetAsync`, `ListAsync`, `InsertAsync`, `UpdateAsync`, `DeleteAsync`) & parameterized SQL |
| `c.Storage` | Cloudflare R2 | Object operations: `PutAsync`, `GetAsync`, `ExistsAsync`, `DeleteAsync` |
| `c.Cache` | Cloudflare KV | Low-latency key-value caching: `GetAsync`, `PutAsync`, `DeleteAsync` |
| `c.Queues` | Cloudflare Queues | Message queue producer: `SendAsync(queueName, message)` |
| `c.AI` | Workers AI | Serverless GPU neural inference: `RunAsync(model, prompt)` |
| `c.Durable` | Durable Objects | In-process bridge to stateful actor stubs: `FetchAsync(...)` |
| `c.Cookies` | Host Cookie Jar | Get, set, and delete cookies with `HttpOnly`, `SameSite`, and `Max-Age` |
| `c.Clock` | Host Clock | Deterministic time: `IsoNow()`, `IsoIn(duration)` |
| `c.Crypto` | Host WebCrypto | PBKDF2 password hashing, SHA-256 tokens, and HMAC-SHA256 signatures |
| `c.Environment` | Worker env | Reads string env vars/secrets: `Get("STRIPE_KEY")` |
| `c.User` | Auth Slice | Authenticated user record populated by `Auth.RequireUser` |
| `c.Json()` | Core Framework | Serializes typed JSON responses with HTTP status codes |
| `c.Status()` | Core Framework | Returns empty or status responses (e.g. `c.Status(204)`) |
| `c.NotFound()` | Core Framework | Standardized 404 response envelope |
| `c.BadRequest()` | Core Framework | Standardized 400 validation error envelope |
| `c.Unauthorized()` | Core Framework | Standardized 401 unauthorized envelope |

---

## Deterministic Testing with `c.Clock`

Avoid direct calls to `DateTime.UtcNow` in business logic. Using `c.Clock.IsoNow()` allows full determinism in unit tests and replay harnesses:

```csharp
var post = new Post(
    Id: Ids.New("post"),
    Title: input.Title,
    Content: input.Content,
    ImageKey: input.ImageKey,
    CreatedAt: c.Clock.IsoNow()
);
```
