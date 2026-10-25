---
name: reptar
description: Build, inspect, migrate, and generate typed RPC contracts for Reptar C# backend applications targeting Cloudflare Workers (NetWasm) and Native AOT. Use whenever working with Reptar routes, TableDef schemas, ColumnRef<T> queries, [ReptarEntity] models, migrations, or @reptar/client RPC codegen.
---

# Reptar Framework Skill

Use this skill whenever creating or modifying C# routes, database schemas (`TableDef`), queries (`ColumnRef<T>` / `TableQuery<T>`), or TypeScript RPC clients (`@reptar/client`).

## 1. Inspect Before Editing

Always query the compiler-backed project state before modifying routes or schemas:

```bash
bun reptar inspect --json
bun reptar db plan --json
bun reptar doctor --json
```

Or use the stdio MCP server (`bun reptar mcp`) tools:

- `reptar_inspect`
- `reptar_schema`
- `reptar_routes`
- `reptar_db_status`
- `reptar_db_plan`
- `reptar_check`
- `reptar_doctor`

## 2. Schema Definition & Roslyn-Generated `ColumnRef<T>` Tokens

Define tables in `schema/*.cs` inside a `partial class` using `DefineTable`:

```csharp
using Reptar;
using static Reptar.ReptarApi;

public static partial class Users
{
    public static readonly TableDef Table = DefineTable("users",
        Col.Text("id").PrimaryKey(),
        Col.Text("email").NotNull().Unique(),
        Col.Int("active").NotNull().Default(1),
        Col.Text("created_at").NotNull().DefaultSql("CURRENT_TIMESTAMP")
    );
}
```

- `TableDef` automatically registers into `SchemaRegistry.All` and exports via `app.ExportSchemaJson()` (`--schema`).
- The Roslyn `ReptarTableGenerator` automatically emits `Users.Id`, `Users.Email`, `Users.Active`, and `Users.CreatedAt` (`ColumnRef<T>`), or `<FieldName>Cols` (e.g. `Schema.PostsCols.Slug`).
- Decorate DTO records with `[ReptarEntity]` so Roslyn emits `IDataRecordMapper<T>`, `IReptarEntity.ToDbParameters()`, and `<Entity>.Cols.<Prop>`:

```csharp
[ReptarEntity("users")]
public partial record UserRow(string Id, string Email, int Active, string CreatedAt);
```

## 3. Querying & Mutating Data

```csharp
// Fluent type-safe query using Roslyn-generated ColumnRef<T> tokens
var activeUsers = await ctx.Db
    .From<UserRow>(Users.Table)
    .Where(Users.Email.Like("%@example.com") & Users.Active.Eq(1))
    .OrderBy(Users.CreatedAt.Desc())
    .Limit(20)
    .ToListAsync();

// Zero-reflection insert/update via [ReptarEntity]
await ctx.Db.InsertAsync(Users.Table, new UserRow("usr_1", "a@example.com", 1, ctx.Clock.IsoNow()));
```

## 4. Synchronizing Contracts & Migrations

After editing C# routes, validators, or `TableDef` schemas:

```bash
bun reptar codegen
bun reptar db plan --json
bun reptar db generate --name <migration_name>
bun reptar db migrate
```
