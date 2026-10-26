---
title: Database Client (c.Db)
description: Zero-reflection [ReptarEntity] row mapping, stack-allocated tuple parameters, pipelined batching, and D1 / SQLite / PostgreSQL / MySQL drivers.
---

## Philosophy

Reptar replaces heavyweight reflection-based ORMs with a compile-time data access layer engineered for WebAssembly (`netwasm0.1`) and Native AOT (`net10.0`):

- **Compile-Time `[ReptarEntity]` Mappers:** A Roslyn incremental source generator implements `IDataRecordMapper<T>` for your C# records, emitting both a zero-allocation positional fast-path (`MapPositional`) and an ordinal-lookup fallback.
- **Zero-Allocation Tuple Parameters:** Pass SQL parameters as C# value tuples `("id", id)` instead of allocating anonymous objects or dictionaries on the heap.
- **Pipelined Transactional Batching:** `c.Db.BatchAsync(...)` executes multi-statement writes in a single network round-trip via `NpgsqlBatch` on PostgreSQL, atomic batching on Cloudflare D1, and single-transaction execution on SQLite and MySQL.
- **Transparent `snake_case` Mapping:** Columns in SQL (`created_at`, `image_key`, `author_id`) automatically map to `PascalCase` C# record properties (`CreatedAt`, `ImageKey`, `AuthorId`).

## 1. Zero-Reflection `[ReptarEntity]` Mapping

Decorate any C# record or class with `[ReptarEntity]`:

```csharp
using Reptar;

[ReptarEntity]
public sealed record TaskSummary(
    string Id,
    string Title,
    string Status,
    int Priority,
    string? AssigneeName,
    string CreatedAt);
```

At compile time, `Reptar.SourceGenerators` generates:

1. **`PositionalDelegate` (`MapPositional`)**: When your `SELECT` column order matches the record constructor (`SELECT id, title, status, priority, assignee_name, created_at FROM ...`), Reptar reads columns using constant indices (`0, 1, 2...`) with zero per-query string comparisons or closure allocations.
2. **Ordinal Resolution Fallback**: If columns are selected in a different order, the generated mapper resolves column ordinals once per query and maps every row without reflection.

## 2. Direct SQL with Value-Tuple Parameters

`c.Db` provides dedicated overloads for 1, 2, 3, 4, and `params (string Name, object? Value)[]` value tuples—completely avoiding anonymous-type reflection and dictionary allocations:

```csharp
// 1. Single-row query (direct reader without intermediate List<T> allocation)
var task = await c.Db.QuerySingleOrDefaultAsync<TaskSummary>(
    "SELECT id, title, status, priority, assignee_name, created_at FROM tasks WHERE id = @id",
    ("id", taskId));

// 2. Multi-row query with 2-tuple parameters
var tasks = await c.Db.QueryAsync<TaskSummary>(
    """
    SELECT id, title, status, priority, assignee_name, created_at
    FROM tasks
    WHERE org_id = @orgId AND status = @status
    ORDER BY created_at DESC
    LIMIT @limit
    """,
    ("orgId", orgId),
    ("status", "open"),
    ("limit", 20));

// 3. Non-query command execution
int affected = await c.Db.ExecuteAsync(
    "UPDATE tasks SET status = @status WHERE id = @id",
    ("status", "done"),
    ("id", taskId));
```

## 3. Transactional Pipelining (`BatchAsync` & `TransactionAsync`)

When inserting related records (such as an article, its tags, and junction rows), use `c.Db.BatchAsync(...)` to execute all statements atomically in a single round-trip:

```csharp
await c.Db.BatchAsync(
    ("INSERT INTO articles (id, slug, title, author_id) VALUES (@id, @slug, @title, @author)",
        new object?[] { ("id", articleId), ("slug", slug), ("title", title), ("author", userId) }),
    ("INSERT INTO article_tags (article_id, tag) VALUES (@id, @tag)",
        new object?[] { ("id", articleId), ("tag", "csharp") }),
    ("INSERT INTO article_tags (article_id, tag) VALUES (@id, @tag)",
        new object?[] { ("id", articleId), ("tag", "cloudflare") })
);
```

For interactive multi-step transactions with conditional logic, use `c.Db.TransactionAsync`:

```csharp
await c.Db.TransactionAsync(async tx =>
{
    var balance = await tx.QuerySingleOrDefaultAsync<int>(
        "SELECT credits FROM accounts WHERE id = @id",
        ("id", accountId));

    if (balance < 10)
    {
        throw new InvalidOperationException("Insufficient credits.");
    }

    await tx.ExecuteAsync(
        "UPDATE accounts SET credits = credits - 10 WHERE id = @id",
        ("id", accountId));
});
```

### Provider transaction matrix

`TransactionAsync` is a real read/write transaction on SQLite, PostgreSQL, and MySQL: reads inside the callback see uncommitted writes, and the transaction commits or rolls back as one unit. Cloudflare D1 has no interactive transaction API, so D1 uses the buffered write-only path: writes are collected and committed atomically through `batch`, but reads and table-shaped writes inside the callback fail explicitly.

| Capability | SQLite | PostgreSQL | MySQL | D1 |
| :-- | :-- | :-- | :-- | :-- |
| CRUD + fluent queries | ✅ | ✅ | ✅ | ✅ |
| Atomic `BatchAsync` | ✅ | ✅ | ✅ | ✅ |
| Read/write `TransactionAsync` | ✅ | ✅ | ✅ | ❌ buffered writes only |
| Migration checksums + lease lock | ✅ | ✅ | ✅ | ✅ |

The provider contract suite runs the same CRUD, commit, rollback, nested-transaction, batch-atomicity, concurrency, and migration checks against every provider:

```bash
# SQLite always; Postgres/MySQL when reachable
POSTGRES_URL='Host=localhost;Database=reptar_test;Username=postgres;Password=postgres;' \
MYSQL_URL='Server=localhost;Port=3306;Database=reptar_test;User ID=root;Password=root;' \
bun run test:providers

# CI: missing Postgres/MySQL is a failure
bun run test:providers --require-all
```

`deploy/provider-matrix-compose.yml` starts local Postgres and MySQL for the matrix. D1 runs through Miniflare against the same host bindings the C# client uses.

## 4. Fluent `TableQuery<T>` & Typed `ColumnRef<T>` Filters

For single-table queries, combine `c.Db.From<T>(table)` with Roslyn-generated `ColumnRef<T>` tokens (`Schema.PostsCols.Published`, `Users.Email`, or `Post.Cols.CreatedAt`) for compile-time column safety and zero heap overhead:

```csharp
// Fluent query builder with compile-time ColumnRef<T> tokens
var publishedPosts = await c.Db.From<Post>(Schema.Posts)
    .Where(Schema.PostsCols.Published.Eq(1) & Schema.PostsCols.AuthorId.Eq(authorId))
    .OrderBy(Schema.PostsCols.CreatedAt.Desc())
    .Limit(20)
    .Offset(0)
    .ToListAsync();

// Single-row lookup, Count, Exists, and Delete
var post = await c.Db.From<Post>(Schema.Posts)
    .Where(Schema.PostsCols.Slug.Eq(slug))
    .FirstOrDefaultAsync();

long total = await c.Db.From<Post>(Schema.Posts)
    .Where(Schema.PostsCols.Published.Eq(1))
    .CountAsync();
```

Supported `ColumnRef<T>` operators include `.Eq(val)`, `.Neq(val)`, `.Gt(val)`, `.Gte(val)`, `.Lt(val)`, `.Lte(val)`, `.Like(pattern)`, `.In(values)`, `.IsNull()`, `.IsNotNull()`, `.Asc()`, and `.Desc()`. Filters compose with `&` (`AND`) and `|` (`OR`).

## 5. TableDef CRUD Helpers & Zero-Reflection `IReptarEntity` Writes

For standard table operations, `c.Db` exposes schema-driven CRUD helpers backed by `TableDef`. Every `[ReptarEntity]` record automatically implements `IReptarEntity.ToDbParameters()` at compile time, so `InsertAsync` and `UpdateAsync` bind columns with zero runtime reflection:

```csharp
// Get single record by primary key
var post = await c.Db.GetAsync<Post>(Schema.Posts, id);

// List records with optional WHERE / ORDER BY clauses
var posts = await c.Db.ListAsync<Post>(Schema.Posts, "where published = 1 order by created_at desc");

// Zero-reflection Insert, Update, and Delete via IReptarEntity.ToDbParameters()
await c.Db.InsertAsync(Schema.Posts, post);
await c.Db.UpdateAsync(Schema.Posts, updatedPost, updatedPost.Id);
await c.Db.DeleteAsync(Schema.Posts, id);
```

## 6. Supported Database Drivers

Reptar supports four relational backends through the unified `IDbClient` interface:

### Cloudflare D1 (Edge SQLite)

```csharp
var app = ReptarApp.Create(new ReptarConfig
{
    Database = Db.D1(),
});
```

### Local & VPS SQLite (Group-Commit Batcher & WAL Read Pool)

`SqliteDbClient` is engineered for high-concurrency multi-tenant workloads on a single node:

- **Lock-Free Group-Commit Write Batcher:** Uncontended writes execute immediately without allocation. When concurrent writes arrive while the writer gate is held, `SqliteDbClient` automatically coalesces up to 64 queued statements into a single `BEGIN ... COMMIT` transaction—with automatic rollback and per-statement isolated replay if any single statement violates a constraint.
- **Synchronous WAL Read Fast-Path:** Local SQLite WAL reads complete synchronously on the calling thread via `TryRentReadConnectionSync` when an idle connection is available in the pool, avoiding `async` state-machine allocations on the hot read path.
- **Per-SQL Mapper Caching:** `EntityMapper<T>.ResolveForSql` caches the resolved positional or ordinal mapper per SQL string so subsequent queries execute zero `GetName(i)` string lookups.
- **Clamped WAL Read Pool:** Opens a low-contention pool of `Mode=ReadOnly` WAL connections (`Math.Clamp(ProcessorCount * 2, 4, 16)`, configurable via `REPTAR_SQLITE_READ_POOL_SIZE`) to eliminate SQLite WAL shared-memory (`-shm`) lock contention under high concurrency.
- **Automatic Transient Retry:** Transparently retries `SQLITE_BUSY` (`5`) and `SQLITE_LOCKED` (`6`) with jittered exponential backoff.

```csharp
var app = ReptarApp.Create(new ReptarConfig
{
    Database = Db.Sqlite("app.db"),
});
```

> [!TIP] When publishing a SQLite-only Native AOT binary, pass `-p:ReptarSqliteOnly=true` to `dotnet publish` to trim Npgsql and MySqlConnector, reducing the standalone executable from 18.4 MB to **10.4 MB** (124 ms cold boot).

### PostgreSQL & Cloudflare Hyperdrive

Uses `NpgsqlDataSource` connection pooling, transactional `NpgsqlBatch` pipelining, and automatic jittered backoff retries on transient PostgreSQL serialization/deadlock/connection errors (`40001`, `40P01`, `53300`, `08006`):

```csharp
var app = ReptarApp.Create(new ReptarConfig
{
    Database = Db.Postgres(Environment.GetEnvironmentVariable("DATABASE_URL")!),
});
```

### MySQL / MariaDB

Uses `MySqlConnector` with automatic parameter translation and connection pooling:

```csharp
var app = ReptarApp.Create(new ReptarConfig
{
    Database = Db.MySql(Environment.GetEnvironmentVariable("MYSQL_URL")!),
});
```
