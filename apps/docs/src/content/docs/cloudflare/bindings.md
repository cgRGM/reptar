---
title: Cloudflare Platform Bindings
description: Direct C# APIs for Cloudflare D1, R2, Workers AI, and RateLimiting.
---

## Overview

Reptar exposes Cloudflare platform capabilities as idiomatic C# interfaces on `c`. The TypeScript host shim bridges each call directly to the Worker's `env` bindings.

---

## 1. Cloudflare D1 (`c.Db`)

Cloudflare's serverless relational SQL database:

```csharp
// Parameterized query execution
var activeUsers = await c.Db.QueryAsync<User>(
    "select * from users where is_active = 1"
);

// Transaction batching
await c.Db.BatchAsync([
    "insert into logs (event) values ('login')",
    "update users set last_login = datetime('now') where id = 1"
]);
```

---

## 2. Cloudflare R2 (`c.Storage`)

S3-compatible, zero-egress object storage:

```csharp
// Upload an asset
await c.Storage.PutAsync("uploads/avatar.png", imageBytes, contentType: "image/png");

// Verify existence
bool exists = await c.Storage.ExistsAsync("uploads/avatar.png");

// Download object
var file = await c.Storage.GetAsync("uploads/avatar.png");

// Delete object
await c.Storage.DeleteAsync("uploads/avatar.png");
```

---

## 3. Workers AI (`c.AI`)

Serverless GPU inference at the edge:

```csharp
// Generate text completions with Llama 3
var result = await c.AI.GenerateAsync(
    "@cf/meta/llama-3-8b-instruct",
    prompt: "Summarize this post into three key bullet points:"
);

// Generate vector embeddings
var vectors = await c.AI.EmbedAsync(
    "@cf/baai/bge-base-en-v1.5",
    text: input.Content
);
```

---

## 4. Rate Limiting (`c.RateLimit`)

Edge rate limiting to protect sensitive endpoints:

```csharp
app.Post("/chat", async (c, input) =>
{
    var allowed = await c.RateLimit.LimitAsync("chat_burst", key: c.Req.Ip());
    if (!allowed)
    {
        return c.Json(new { error = "Rate limit exceeded" }, 429);
    }

    return c.Json(new { status = "ok" });
});
```

---

## Platform Roadmap

| Surface             | C# Surface                | Availability   |
| ------------------- | ------------------------- | -------------- |
| D1 (SQL Database)   | `c.Db`                    | Available (v1) |
| R2 (Object Storage) | `c.Storage`               | Available (v1) |
| Workers AI          | `c.AI`                    | Available (v1) |
| Rate Limiting       | `c.RateLimit`             | Available (v1) |
| KV Storage          | `c.Cache`                 | Planned (v2)   |
| Cron Triggers       | `app.OnCron(...)`         | Planned (v2)   |
| Email Routing       | `c.Email.SendAsync(...)`  | Planned (v2)   |
| Queues              | `c.Queues.SendAsync(...)` | Planned (v2)   |
