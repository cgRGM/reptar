---
title: Cloudflare Platform Bindings
description: Direct C# APIs for Cloudflare D1, R2, KV, Queues, Workers AI, Durable Objects, and WebCrypto.
---

## Overview

Reptar exposes Cloudflare platform capabilities as idiomatic C# interfaces on `c`. Rather than making external REST API roundtrips over the public internet, each call bridges in-memory via `[JSImport]` to native Cloudflare Worker `env` bindings inside the `workerd` isolate.

> [!NOTE] The reference Worker binds **D1, R2, KV, Queues, Workers AI, and a `Counter` Durable Object**. Alchemy provisions each resource and the Worker passes them through `createReptarHost`. `CORS_ORIGINS` is an explicit allowlist, and `c.Environment.Get("APP_NAME")` reads plain env values.

---

## 1. Cloudflare D1 (`c.Db`)

Cloudflare's serverless relational SQL database with reflection-free CRUD and parameterized SQL:

```csharp
// 1. Reflection-free TableDef CRUD
var post = await c.Db.GetAsync<Post>(PostsTable, "post_123");
var posts = await c.Db.ListAsync<Post>(PostsTable, "where published = 1 order by created_at desc");
await c.Db.InsertAsync(PostsTable, newPost);
await c.Db.UpdateAsync(PostsTable, updatedPost, updatedPost.Id);
await c.Db.DeleteAsync(PostsTable, "post_123");

// 2. Direct Parameterized SQL
var activeUsers = await c.Db.QueryAsync<User>(
    "select * from users where is_active = @active and role = @role",
    ("active", 1),
    ("role", "admin")
);
```

> [!NOTE] Column names in SQLite (`snake_case`, e.g. `created_at`, `image_key`) automatically map to C# record properties in `PascalCase` (`CreatedAt`, `ImageKey`) through source-generated deserialization.

---

## 2. Cloudflare R2 & Portable Object Storage (`c.Storage`)

`c.Storage` implements the unified `IStorage` interface across Cloudflare R2 (`R2Client`), local filesystem storage (`LocalStorage`), and S3-compatible object stores (`S3Storage` for AWS S3, Cloudflare R2 S3 API, MinIO, and Tigris with zero-dependency SigV4 signing and `PresignPutAsync`):

```csharp
// Upload an asset
await c.Storage.PutAsync("uploads/avatar.png", imageBytes, contentType: "image/png");

// Verify existence & read metadata (HeadAsync)
bool exists = await c.Storage.ExistsAsync("uploads/avatar.png");
StorageObjectMeta? meta = await c.Storage.HeadAsync("uploads/avatar.png");

// Download object or stream directly
byte[]? file = await c.Storage.GetAsync("uploads/avatar.png");

// Delete object
await c.Storage.DeleteAsync("uploads/avatar.png");
```

### Multipart File Uploads & Validation (`MultipartForm` & `Files.Check`)

Parse `multipart/form-data` payloads and validate file size, MIME type, and magic-byte signatures (`png`, `jpeg`, `webp`, `gif`, `pdf`) without external dependencies:

```csharp
app.Post("/upload", async c =>
{
    var form = MultipartForm.Parse(c.Req.BodyBytes, c.Req.Header("content-type"));
    var file = form.File("file");
    var check = Files.Check(file, maxBytes: 5 * 1024 * 1024, allowedSignatures: ["png", "jpeg", "webp"]);
    if (!check.IsValid) return c.BadRequest(check.Error!);

    await c.Storage.PutAsync($"uploads/{file!.FileName}", file.Data, file.ContentType);
    return c.Json(new { key = $"uploads/{file.FileName}" }, 201);
}).AcceptsFile("file", maxBytes: 5 * 1024 * 1024);
```

---

## 3. KV Storage (`c.Cache`)

Ultra-low latency global key-value store for caching and rate limiting:

```csharp
// Read cached value
string? cached = await c.Cache.GetAsync("user:profile:123");

// Write with optional Time-To-Live (TTL in seconds)
await c.Cache.PutAsync("user:profile:123", jsonString, ttlSeconds: 300);

// Invalidate cache
await c.Cache.DeleteAsync("user:profile:123");
```

---

## 4. Cloudflare Queues (`c.Queues`)

Guaranteed-delivery asynchronous message queues for background processing:

```csharp
// Send a typed message payload to a queue
await c.Queues.SendAsync("order-fulfillment", new { orderId = "o1" });

// Delayed delivery (up to 24h) maps to Cloudflare's delaySeconds
await c.Queues.SendAsync("order-fulfillment", new { orderId = "o2" },
    new QueueSendOptions(DelaySeconds: 3600));

// Native batch send maps to sendBatch
await c.Queues.SendBatchAsync("order-fulfillment", [body1, body2]);
```

Handlers registered with `app.OnQueue` run inside the Worker's `queue()`
event: return normally to ack, throw to retry, or `throw new RetryIn(seconds)`
to retry after a Cloudflare-native delay (`message.retry({ delaySeconds })`).
Consumer batching, `max_retries`, `dead_letter_queue`, and `max_concurrency`
are configured in `wrangler.jsonc`. See [Queues & Background Jobs](/features/queues)
for the full adapter matrix — the same `SendAsync`/`OnQueue` code also runs
against SQS, Azure Storage Queues, Pub/Sub, or your own SQL database on
native targets.

---

## 5. Workers AI (`c.AI`)

Serverless GPU inference at the edge:

```csharp
// Run inference on serverless open-source models
AiResult summary = await c.AI.RunAsync(
    "@cf/meta/llama-3-8b-instruct",
    prompt: "Summarize this article in two concise sentences:"
);
```

---

## 6. Durable Objects Bridge (`c.Durable`)

Stateful coordination, real-time collaboration, and WebSocket routing:

```csharp
// Route an edge request to a specific Durable Object instance
string response = await c.Durable.FetchAsync(
    namespaceName: "ROOMS",
    id: "room_north_america",
    path: "/broadcast",
    method: "POST",
    body: "{\"msg\":\"User joined\"}"
);
```

---

## 7. Native WebCrypto (`c.Crypto`)

Hardware-accelerated cryptography using the Worker host's native V8 WebCrypto engine:

```csharp
// 1. Password Hashing (PBKDF2, workerd 100k iteration cap)
string hash = await c.Crypto.HashPassword("UserPassword123!");
bool isValid = await c.Crypto.VerifyPassword("UserPassword123!", hash);

// 2. Cryptographic Session Tokens
string token = c.Crypto.RandomToken(bytes: 32);
string sha256Hash = await c.Crypto.Sha256(token);

// 3. HMAC-SHA256 Signatures (Webhook validation)
string signature = await c.Crypto.HmacSha256(secretKey, payload);
```

---

## 8. Outbound HTTP Fetch (`c.Http`)

Perform outbound HTTP requests to external APIs (e.g. Stripe, OpenAI, GitHub):

```csharp
var response = await c.Http.FetchAsync(
    "https://api.external.com/v1/event",
    "POST",
    new Dictionary<string, string>
    {
        ["content-type"] = "application/json",
    },
    payload);
```

The host rejects non-HTTPS URLs and private/link-local hosts by default. Pass `httpAllowedHosts` to `createReptarHost` to restrict outbound calls to an explicit allowlist.

---

## Platform Availability Matrix

See the [Per-Target Feature Support Matrix](/overview/deployment-targets/#per-target-feature-support-matrix) for a full comparison of features executing in C# (`netwasm0.1` vs `net10.0`) versus features bridged by the `@reptar/host` TypeScript Worker shim.

| Surface | C# Accessor | Implementation | Availability |
| :-- | :-- | :-- | :-- |
| **D1 (SQL Database)** | `c.Db` | `D1DbClient` via `[JSImport]` | ✅ Production Ready |
| **R2 (Object Storage)** | `c.Storage` | `R2Client` + Binary Stream | ✅ Production Ready |
| **KV Storage** | `c.Cache` | `KvClient` via `[JSImport]` | ✅ Wired in reference |
| **Cloudflare Queues** | `c.Queues` | `QueueClient` via `[JSImport]` | ✅ Wired in reference |
| **Workers AI** | `c.AI` | `AiClient` via `[JSImport]` | ✅ Wired in reference |
| **Durable Objects** | `c.Durable` | `DurableBridge` via `[JSImport]` | ✅ Wired in reference |
| **WebCrypto** | `c.Crypto` | PBKDF2 / SHA-256 / HMAC | ✅ Production Ready |
| **Outbound HTTP** | `WasmHttpClient` | Edge `fetch` Bridge | ✅ Production Ready |
