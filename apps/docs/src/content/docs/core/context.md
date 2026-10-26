---
title: The Ctx Surface
description: Complete reference for handler context, request inputs, responses, background tasks, authorization, and platform bindings.
---

import { Code } from "@astrojs/starlight/components";

The `Ctx` object (`c` or `ctx`) is passed to every route handler and middleware. It exposes request inputs, response builders, platform bindings (D1, R2, KV, Queues, AI), cryptographic helpers, and serverless-safe background tasks without requiring constructor dependency injection.

```csharp
app.Get("/posts/:id", async c =>
{
    var id = c.Param("id");
    var post = await c.Db.QuerySingleOrDefaultAsync<Post>(
        "SELECT id, title, content, created_at FROM posts WHERE id = @id",
        ("id", id));

    return post is null ? c.NotFound() : c.Json(post);
});
```

---

## 1. Request Inputs

`Ctx` provides instant access to route parameters, query strings, headers, multipart forms, and JSON payloads:

| Member | Return Type | Description |
| :-- | :-- | :-- |
| `c.Param(name)` | `string?` | Retrieves a matched route parameter (e.g. `c.Param("id")` for `/posts/:id`). |
| `c.Req.Param(name)` | `string?` | Direct accessor for route parameters via the request envelope. |
| `c.Req.QueryValue(name, default)` | `string` | Retrieves a query string parameter with an optional default fallback value. |
| `c.Req.Header(name)` | `string?` | Retrieves an HTTP header (case-insensitive lookup). |
| `c.Req.Headers` | `Dictionary<string, string>` | Read-only dictionary of all incoming HTTP request headers. |
| `c.Req.Method` | `string` | The HTTP method (`GET`, `POST`, `PUT`, `DELETE`, etc.). |
| `c.Req.Path` | `string` | The request URL path without query strings (e.g. `/api/posts`). |
| `c.Req.Body` | `byte[]?` | Raw incoming request body bytes. |
| `c.Req.Form` | `MultipartForm?` | Parsed multipart form fields and file uploads. |
| `c.BindJsonAsync<T>()` | `Task<T?>` | Deserializes the request body to `T` using the zero-reflection JSON codec. |

```csharp
app.Post("/api/users", async c =>
{
    var user = await c.BindJsonAsync<CreateUserDto>();
    var userAgent = c.Req.Header("User-Agent");
    var includeProfile = c.Req.QueryValue("include_profile", defaultValue: "false");

    return c.Json(user, 201);
});
```

---

## 2. Response Builders

Construct typed, standard responses directly from `Ctx`:

| Method | Status | Content-Type | Description |
| :-- | :-- | :-- | :-- |
| `c.Json<T>(data, status = 200)` | Custom | `application/json` | Serializes `data` to UTF-8 JSON bytes with compile-time type safety. |
| `c.JsonRaw(json, status = 200)` | Custom | `application/json` | Emits pre-serialized JSON string directly. |
| `c.Text(text, status = 200)` | Custom | `text/plain` | Returns a plaintext string response. |
| `c.Html(html, status = 200)` | Custom | `text/html` | Returns an HTML string response. |
| `c.Status(code)` | Custom | None | Returns an empty response with the specified HTTP status code (e.g. `204`). |
| `c.Redirect(url, status = 302)` | `301`/`302` | None | Emits an HTTP redirect with the `Location` header. |
| `c.Stream(body, contentType)` | Custom | Custom | Streams a raw byte array or stream delegate directly to the client. |
| `c.File(key, options?)` | Custom | Stored type | Serves a stored object with `ETag`/`304`, `Range`/`206`, `HEAD`, and cache headers. |
| `c.Attachment(key, fileName?)` | Custom | Stored type | Serves a stored object as a download with `Content-Disposition`. |
| `c.Sse(stream => ...)` | `200` | `text/event-stream` | Streams Server-Sent Events with automatic `FlushAsync` and zero allocations. |

---

## 3. Standardized Error Envelopes

`Ctx` includes standardized JSON error responses that conform to RFC 7807 problem details:

```csharp
c.NotFound("Post does not exist.");        // 404
c.BadRequest("Invalid email format.");       // 400
c.Unauthorized("Authentication required.");  // 401
c.Forbidden("Insufficient permissions.");    // 403
c.Conflict("Slug already taken.");           // 409
c.Invalid(validationIssues);                 // 400 with structured issue array
```

Example JSON output:

```json
{
  "code": "not_found",
  "message": "Post does not exist.",
  "requestId": "req_01J8F91M"
}
```

---

## 4. Serverless-Safe Background Tasks (`WaitUntil`)

In serverless edge runtimes like Cloudflare Workers, the isolate freezes immediately once the HTTP response completes. Any fire-and-forget tasks spawned with `_ = Task.Run(...)` get terminated.

`Ctx.WaitUntil` captures async tasks and extends the isolate's execution lifetime:

```csharp
app.Post("/api/orders", async c =>
{
    var order = await c.BindJsonAsync<CreateOrderDto>();
    await c.Db.InsertAsync(Schema.Orders, order);

    // Flushes response to client immediately; keeps isolate alive in background
    c.WaitUntil(async () =>
    {
        await SendConfirmationEmailAsync(order);
        await FlushTelemetryAsync(order);
    });

    return c.Created(order);
});
```

- **Cloudflare Workers (`netwasm0.1`)**: Passes the promise to `executionCtx.waitUntil(promise)`.
- **Kestrel (`net10.0`)**: Dispatches to the .NET thread pool with top-level unhandled exception capture.

---

## 5. Declarative Policies & Authorization (`Can` & `Authorize`)

Separate permission logic from handler code using zero-reflection policies:

```csharp
// Define once at startup
app.DefinePolicy<PostRecord>("post:edit", (user, post) =>
    user is not null && (user.Role == "admin" || post.AuthorId == user.Id));

// Check in handlers:
app.Put("/api/posts/:id", async c =>
{
    var post = await c.Db.QuerySingleOrDefaultAsync<PostRecord>(
        "SELECT * FROM posts WHERE id = @id", ("id", c.Param("id")));

    if (post is null)
        return c.NotFound();

    // Check authorization:
    if (!c.Authorize("post:edit", post, out var forbidden))
        return forbidden; // Automatically returns 403 Forbidden with requestId

    return c.Json(post);
});
```

---

## 6. Observability & DevTools (`ServerTiming`)

Emit standard W3C `Server-Timing` headers visible in browser DevTools network waterfalls:

```csharp
c.ServerTiming("db", 14.2, "SQLite query");
c.ServerTiming("auth", 1.8, "JWT verification");
```

Response header:

```http
Server-Timing: db;dur=14.2;desc="SQLite query", auth;dur=1.8;desc="JWT verification"
```

---

## 7. Platform Bindings & Services Reference

| Member | Backed by | Description |
| :-- | :-- | :-- |
| `c.Db` | D1 / SQLite / Postgres / MySQL | Zero-reflection `[ReptarEntity]` queries, tuple parameters, CRUD helpers, and `BatchAsync`. |
| `c.Storage` | R2 / S3 / local disk | Object operations: `PutAsync` (bytes or stream), `GetAsync`, `ExistsAsync`, `DeleteAsync`, `DeleteManyAsync`, `CopyAsync`, `MoveAsync`, `HeadAsync`, `GetRangeAsync`, `ListAsync`, `PresignPutAsync`, `PresignGetAsync`, `PublicUrl`. |
| `c.Cache` | Cloudflare KV | Low-latency key-value caching: `GetAsync`, `PutAsync`, `DeleteAsync`. |
| `c.Queues` | Cloudflare Queues | Message queue producer: `SendAsync(queueName, message)`. |
| `c.AI` | Workers AI | Serverless GPU inference: `RunAsync(model, prompt)`. |
| `c.Durable` | Durable Objects | In-process bridge to stateful actor stubs: `FetchAsync(...)`. |
| `c.Cookies` | Host Cookie Jar | Get, set, delete, and HMAC-SHA256 sign/verify cookies (`SetSigned`, `GetSigned`). |
| `c.Clock` | Host Clock | Deterministic time: `IsoNow()`, `IsoIn(duration)`. |
| `c.Crypto` | Host WebCrypto | PBKDF2 password hashing, SHA-256 tokens, and HMAC-SHA256 signatures. |
| `c.Environment` | Worker / OS env | Reads string env vars and secrets: `Get("STRIPE_KEY")`. |
| `c.Log` | Structured logger | `Info`, `Warn`, and `Error` with correlation request IDs. |
| `c.Claims` | JWT middleware | Zero-allocation verified JWT claims populated by `Auth.RequireJwt`. |
| `c.User` / `c.UserAsync()` | Auth Slice / Better Auth | Authenticated user record populated by session cookie, bearer token, or API key. |
| `c.WebSocket` | Host WebSocket transport | Sends and closes live WebSocket sessions. |
| `c.SetHeader(name, val)` | Pending Headers | Adds or overrides an outgoing HTTP response header. |
| `c.RequestId` | Request Correlation | The unique request identifier (e.g. `req_01J8F91M`). |
