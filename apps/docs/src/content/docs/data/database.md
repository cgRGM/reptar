---
title: Database Client (c.Db)
description: Explicit, parameterized, Dapper-like data access without ORM overhead.
---

## Philosophy

Reptar intentionally rejects heavyweight ORM abstractions in favor of a clean, Dapper-like query interface:

- **Explicit SQL:** Queries are written in plain, readable SQL.
- **Source-Generated Mappers:** Column names in `snake_case` map directly to C# record properties in `PascalCase` without runtime reflection.
- **Zero Runtime Magic:** No hidden subqueries, lazy loading pitfalls, or unexpected query cascades.

---

## Raw SQL Queries

Direct SQL execution is always available on `c.Db`:

```csharp
// 1. Query multiple records
var posts = await c.Db.QueryAsync<Post>(
    "select * from posts where published = 1 order by created_at desc"
);

// 2. Query single record with parameters
var post = await c.Db.QuerySingleOrDefaultAsync<Post>(
    "select * from posts where id = @id",
    new { id }
);

// 3. Execute statement
await c.Db.ExecuteAsync(
    "insert into posts (id, title, content, created_at) values (@Id, @Title, @Content, @CreatedAt)",
    post
);
```

---

## Generated CRUD Helpers

For routine operations, `c.Db` exposes type-safe helpers derived from your `TableDef`:

```csharp
// Get by primary key
var post = await c.Db.GetAsync<Post>(Schema.Posts, id);

// Filter with WHERE clause
var posts = await c.Db.ListAsync<Post>(Schema.Posts, "where published = 1 order by created_at desc");

// Insert row
await c.Db.InsertAsync(Schema.Posts, post);

// Update row
await c.Db.UpdateAsync(Schema.Posts, post);

// Delete row
await c.Db.DeleteAsync<Post>(Schema.Posts, id);
```
