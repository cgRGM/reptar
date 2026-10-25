# Reptar Framework Guidelines for AI Agents

When working on Reptar C# or TypeScript code, adhere to these architectural rules:

## Machine-Readable Inspection & MCP Tools

- Query project state using `bun reptar inspect --json` or the stdio MCP server (`bun reptar mcp`) instead of guessing.
- Use `bun reptar doctor --json` to verify SDK toolchain, database connectivity, and RPC sync.
- Run `bun reptar db plan --json` to inspect compiler-backed schema drift between `TableDef` (`app.ExportSchemaJson()`) and the active database.
- Use `bun reptar db generate` to create new migrations and `bun reptar db migrate` to apply them.
- Use `bun reptar codegen` to regenerate `@reptar/client` contracts and `schema.json` whenever C# routes or tables change.

## Data Layer & Compile-Time `ColumnRef<T>` Tokens

- Never use heavy ORM attribute annotations (`[PrimaryKey]`, `[Unique]`, `[DefaultSql]`) on DTO records. Storage schema belongs in `TableDef` (`schema/*.cs`), and projection shapes belong in clean C# records decorated with `[ReptarEntity]`.
- Every `DefineTable(...)` automatically registers into `SchemaRegistry.All` and generates compile-time `ColumnRef<T>` tokens via Roslyn (`Users.Email.Eq(...)`, `Schema.PostsCols.CreatedAt.Desc()`, or `OrderEntity.Cols.CustomerId.Eq(...)`).
- Use `ctx.Db.From<T>(table).Where(...).OrderBy(...).Limit(...).ToListAsync()` for fluent, readable single-table queries.
- Raw SQL (`ctx.Db.QueryAsync<T>(...)` with value-tuple parameters `("id", id)`) is always available and first-class for complex joins or CTEs.

## Reflection & Target Constraints

- Never rely on runtime reflection in request or database paths. Reptar targets Native AOT (`net10.0`) and NetWasm (`netwasm0.1` on Cloudflare Workers).
- Decorate database entities with `[ReptarEntity]` so Roslyn generates `IDataRecordMapper<T>` (positional reader) and `IReptarEntity.ToDbParameters()` (zero-reflection `InsertAsync`/`UpdateAsync` binder).
- Use `#if !NETWASM` when writing code that relies on full .NET cryptography or filesystem primitives not present in NetWasm.
