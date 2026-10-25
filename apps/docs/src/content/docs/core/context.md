---
title: The Ctx Surface
description: Complete reference for handler context, request inputs, streaming, and platform bindings.
---

## Overview

The `Ctx` object (`c` or `ctx`) is passed to every route handler and middleware. It exposes request data, database and platform bindings, cryptographic helpers, and response builders without requiring constructor dependency injection.

```csharp
app.Get("/posts/:id", async c =>
{
    var id = c.Req.Param("id");
    var post = await c.Db.QuerySingleOrDefaultAsync<Post>(
        "SELECT id, title, content, created_at FROM posts WHERE id = @id",
        ("id", id));
    return post is null ? c.NotFound() : c.Json(post);
});
```

## Member Reference

| Member | Backed by | Description |
| --- | --- | --- |
| `c.Req` | Request envelope / PipeReader | Route params, query strings, headers, JSON body, and zero-copy byte spans |
| `c.Db` | D1 / SQLite / Postgres / MySQL | Zero-reflection `[ReptarEntity]` queries, tuple parameters, CRUD helpers, and `BatchAsync` |
| `c.Storage` | Cloudflare R2 | Object operations: `PutAsync`, `GetAsync`, `ExistsAsync`, `DeleteAsync` |
| `c.Cache` | Cloudflare KV | Low-latency key-value caching: `GetAsync`, `PutAsync`, `DeleteAsync` |
| `c.Queues` | Cloudflare Queues | Message queue producer: `SendAsync(queueName, message)` |
| `c.AI` | Workers AI | Serverless GPU inference: `RunAsync(model, prompt)` |
| `c.Durable` | Durable Objects | In-process bridge to stateful actor stubs: `FetchAsync(...)` |
| `c.Cookies` | Host Cookie Jar | Get, set, delete, and HMAC-SHA256 sign/verify cookies (`SetSigned`, `GetSigned`) |
| `c.Clock` | Host Clock | Deterministic time: `IsoNow()`, `IsoIn(duration)` |
| `c.Crypto` | Host WebCrypto | PBKDF2 password hashing, SHA-256 tokens, and HMAC-SHA256 signatures |
| `c.Environment` | Worker / OS env | Reads string env vars and secrets: `Get("STRIPE_KEY")` |
| `c.Log` | Structured logger | `Info`, `Warn`, and `Error` with correlation request IDs |
| `c.Claims` | JWT middleware | Zero-allocation verified JWT claims populated by `Auth.RequireJwt` |
| `c.User` / `c.UserAsync()` | Auth Slice / Better Auth | Authenticated user record populated by session cookie, bearer token, or API key |
| `c.WebSocket` | Host WebSocket transport | Sends and closes live WebSocket sessions |
| `c.Sse(...)` | `Channel<byte[]>` / PipeWriter | Streams Server-Sent Events (`text/event-stream`) with zero intermediate string allocations |
| `c.StreamLlmAsync(...)` | PipeWriter / TransformStream | Zero-copy LLM token streaming with automatic `ctx.CancellationToken` abort propagation |
| `c.Json()` | `SourceGenJsonCodec` | Serializes typed JSON responses directly to UTF-8 bytes |
| `c.Status()` | Core Framework | Returns empty status responses (e.g. `c.Status(204)`) |
| `c.NotFound()` | Core Framework | Standardized 404 response envelope |
| `c.BadRequest()` | Core Framework | Standardized 400 validation error envelope |
| `c.Unauthorized()` | Core Framework | Standardized 401 unauthorized envelope |

## Deterministic Testing with `c.Clock`

Avoid direct calls to `DateTime.UtcNow` in domain handlers. Using `c.Clock.IsoNow()` guarantees deterministic timestamps in unit tests and replay harnesses:

```csharp
var post = new Post(
    Id: Ids.New("post"),
    Title: input.Title,
    Content: input.Content,
    ImageKey: input.ImageKey,
    CreatedAt: c.Clock.IsoNow()
);
```
