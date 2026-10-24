---
title: Cloudflare Platform Bindings
description: Direct C# APIs for Cloudflare D1, R2, KV, Queues, Workers AI, Durable Objects, and WebCrypto.
---

## Overview

Reptar exposes Cloudflare platform capabilities as idiomatic C# interfaces on `c`. Rather than making external REST API roundtrips over the public internet, each call bridges in-memory via `[JSImport]` to native Cloudflare Worker `env` bindings inside the `workerd` isolate.

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
    "select * from users where is_active = ? and role = ?",
    "1", "admin"
);
```

> [!NOTE] Column names in SQLite (`snake_case`, e.g. `created_at`, `image_key`) automatically map to C# record properties in `PascalCase` (`CreatedAt`, `ImageKey`) through source-generated deserialization.

---

## 2. Cloudflare R2 (`c.Storage`)

S3-compatible, zero-egress object storage with direct binary streaming:

```csharp
// Upload an asset
await c.Storage.PutAsync("uploads/avatar.png", imageBytes, contentType: "image/png");

// Verify existence
bool exists = await c.Storage.ExistsAsync("uploads/avatar.png");

// Download object
byte[]? file = await c.Storage.GetAsync("uploads/avatar.png");

// Delete object
await c.Storage.DeleteAsync("uploads/avatar.png");
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
await c.Queues.SendAsync("order-fulfillment", new
{
    orderId = "ord_01",
    userId = "usr_01",
    timestamp = c.Clock.IsoNow()
});
```

---

## 5. Workers AI (`c.AI`)

Serverless GPU inference at the edge:

```csharp
// Run inference on serverless open-source models
string summaryJson = await c.AI.RunAsync(
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
    namespaceBinding: "ROOMS",
    instanceId: "room_north_america",
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

## 8. Outbound HTTP Fetch (`WasmHttpClient`)

Perform outbound HTTP requests to external APIs (e.g. Stripe, OpenAI, GitHub):

```csharp
using Reptar;

var client = new WasmHttpClient();
var response = await client.PostJsonAsync("https://api.external.com/v1/event", payload);
```

---

## Platform Availability Matrix

| Surface | C# Accessor | Implementation | Availability |
| :-- | :-- | :-- | :-- |
| **D1 (SQL Database)** | `c.Db` | `D1DbClient` via `[JSImport]` | ✅ Production Ready |
| **R2 (Object Storage)** | `c.Storage` | `R2Client` + Binary Stream | ✅ Production Ready |
| **KV Storage** | `c.Cache` | `KvClient` via `[JSImport]` | ✅ Supported |
| **Cloudflare Queues** | `c.Queues` | `QueueClient` via `[JSImport]` | ✅ Supported |
| **Workers AI** | `c.AI` | `AiClient` via `[JSImport]` | ✅ Supported |
| **Durable Objects** | `c.Durable` | `DurableBridge` via `[JSImport]` | ✅ Supported |
| **WebCrypto** | `c.Crypto` | PBKDF2 / SHA-256 / HMAC | ✅ Production Ready |
| **Outbound HTTP** | `WasmHttpClient` | Edge `fetch` Bridge | ✅ Production Ready |
