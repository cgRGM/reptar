---
title: Database Client (c.Db)
description: Reflection-free CRUD, TableDef schemas, and Dapper-like data access on Cloudflare D1.
---

## Philosophy

Reptar intentionally rejects heavyweight ORM abstractions in favor of a clean, reflection-free data access model tailored for ahead-of-time (AOT) WebAssembly:

- **Explicit Schemas:** Tables are defined with fluent `TableDef` builders.
- **Reflection-Free Execution:** Inserts and updates serialize through source-generated JSON and `JsonDocument` DOM traversal, avoiding runtime reflection entirely.
- **Transparent Snake Case Mapping:** Columns in SQLite (`created_at`, `image_key`, `user_id`) automatically map to C# record properties (`CreatedAt`, `ImageKey`, `UserId`) and TypeScript types (`createdAt`, `imageKey`, `userId`).
- **Zero ORM Overhead:** No hidden lazy-loading cascades or query generation magic.

---

## 1. Table Definitions (`TableDef`)

Define schemas using fluent column builders:

```csharp
using Reptar;
using static Reptar.ReptarApi;

public static readonly TableDef PostsTable = DefineTable("posts",
    Col.Text("id").PrimaryKey(),
    Col.Text("title").NotNull(),
    Col.Text("content").NotNull(),
    Col.Text("image_key"),
    Col.Text("created_at").NotNull()
);
```

Available column types include `Col.Text()`, `Col.Int()`, `Col.Real()`, and `Col.Blob()`, with modifiers `.PrimaryKey()`, `.NotNull()`, `.Unique()`, and `.Default(...)`.

---

## 2. Reflection-Free CRUD Operations

For standard operations, `c.Db` exposes strongly-typed CRUD helpers:

### Fetching Records

```csharp
// Get single record by primary key (returns null if not found)
var post = await c.Db.GetAsync<Post>(PostsTable, id);

// List records with optional WHERE / ORDER BY clauses
var posts = await c.Db.ListAsync<Post>(PostsTable, "where published = 1 order by created_at desc");

// Check if a record exists
bool exists = await c.Db.ExistsAsync(PostsTable, "slug = ?", slug);
```

### Inserting Records

```csharp
var post = new Post(
    Id: Ids.New("post"),
    Title: input.Title.Trim(),
    Content: input.Content,
    ImageKey: input.ImageKey,
    CreatedAt: c.Clock.IsoNow()
);

// Inserts all columns matching the TableDef schema
await c.Db.InsertAsync(PostsTable, post);
```

### Updating Records

```csharp
var updated = post with
{
    Title = input.Title.Trim(),
    Content = input.Content,
    ImageKey = input.ImageKey ?? post.ImageKey
};

// Updates all columns except primary key and created_at
await c.Db.UpdateAsync(PostsTable, updated, updated.Id);
```

### Deleting Records

```csharp
// Delete by primary key
await c.Db.DeleteAsync(PostsTable, id);

// Delete matching WHERE clause
await c.Db.DeleteWhereAsync(PostsTable, "user_id = ?", userId);
```

---

## 3. Direct SQL Queries

Direct parameterized SQL is always supported when you need joins, aggregations, or complex queries:

```csharp
// 1. Parameterized query
var activeUsers = await c.Db.QueryAsync<User>(
    "select * from users where is_active = ? and role = ?",
    "1", "admin"
);

// 2. Query single scalar / row
var user = await c.Db.QuerySingleOrDefaultAsync<User>(
    "select * from users where email = ?",
    email
);

// 3. Execute statement
int affectedRows = await c.Db.ExecuteAsync(
    "update posts set view_count = view_count + 1 where id = ?",
    postId
);
```

---

## 4. Supported Database Drivers

Reptar abstracts database access through the `IDbClient` interface, allowing you to run against local embedded SQLite during development and scale to distributed databases in production:

### Cloudflare D1 (Edge Relational SQLite)

The default relational database on Cloudflare Workers. Zero setup, replicated at the edge, and queried without external connection pools.

```csharp
var app = Reptar(new ReptarConfig
{
    Database = D1()
});
```

### Local SQLite

Embedded zero-dependency SQLite for local testing, CI pipelines, and standalone CLI utilities:

```csharp
var app = Reptar(new ReptarConfig
{
    Database = Db.Sqlite("blog.db")
});
```

### PostgreSQL & Cloudflare Hyperdrive

To connect your edge backend to central PostgreSQL databases (Supabase, Neon, AWS RDS, Tembo), Reptar supports **Cloudflare Hyperdrive**:

- **Edge Connection Pooling:** Reuses established TCP connections from Cloudflare's nearest point of presence to your database.
- **Query Acceleration:** Automatically caches read queries across the edge network.
- **Dialect Adaptation:** `SqlParamParser` automatically translates SQLite positional parameters (`?`) or named parameters (`@id`) to PostgreSQL positional parameters (`$1, $2`).

```csharp
var app = Reptar(new ReptarConfig
{
    // Connects via Cloudflare Hyperdrive connection pool
    Database = Db.Postgres(env.Get("HYPERDRIVE_URL"))
});
```

When running under **.NET Native AOT** on AWS Lambda or Docker, Reptar utilizes native `Npgsql` for direct high-performance connection pooling.
