# AdonisJS → Reptar Adaptation Audit

**Status:** research/audit — no framework code changed  
**Date:** 2026-10-26  
**Scope:** the three proposals in the pasted transcript (Ace-style CLI, Lucid-style preload, service lifecycles), plus its AOT/JSON, middleware, and RPS warnings.  
**Related:** `design.md` §2/§13, `llms.txt` §2/§5b/§6/§7, `plans/master-audit-and-roadmap.md`, `plans/scaffolding-and-auth.md`, `plans/agent-engine.md`, `plans/core-focus-roadmap.md`.

> **Priority note:** this document audits the three AdonisJS-derived proposals. For the broader question — what makes Reptar fastest, most reliable, and most observable without adding bloat — see [`plans/core-focus-roadmap.md`](./core-focus-roadmap.md). That roadmap puts performance gates, observability, and reliability hardening *ahead* of the CLI/preload/lifecycle features discussed here.

---

## 0. TL;DR

The transcript points at three real gaps, but none of its three implementation sketches is usable as written. Two are misdiagnosed, one is over-engineered, and one of its final offers is for something that already exists.

| Proposal | Verdict | Short reason |
| :-- | :-- | :-- |
| **Ace-style CLI generators** | **Worth doing — redesign the location and mechanism** | Reptar has `create-reptar` and templates but no `make:*` resource generators. The win is developer ergonomics, not AOT. `make:model` belongs in the project-local CLI, not the repo-internal `scripts/reptar.ts`, and the real pattern is stubs + codemods + command composition, not one hardcoded generator. |
| **Lucid-style preloading** | **Worth doing — but not as described** | The N+1 problem is real and especially expensive on D1. But `ColumnRef<T>` does not encode relationships, `QueryMappedAsync` maps one row to one object, and Reptar DTOs are immutable records. This needs explicit relation metadata, a batching/hydration layer, and source-generated accessors. Lucid itself uses separate `WHERE ... IN (...)` queries, not joins. |
| **Unified service lifecycles** | **Worth a small version — reject the source-generator/IoC framing** | Startup/shutdown hooks are useful for Kestrel/Lambda (warmup, pool cleanup, workers). But `register`/`boot` are container concepts Reptar deliberately does not have, and a source generator cannot inject an initialization sequence into the top-level `app.cs` entry point. Use explicit `IReptarModule`/`OnStart`/`OnShutdown`; no container, no generator. |
| **AOT/JSON warning** | **Partly right, overbroad** | Anonymous/dictionary JSON is a real NetWasm/AOT trap and is already documented. But registered records with `SourceGenJsonCodec` are safe, the scanner only warns for two patterns, and the right fix is generated `JsonSerializerContext`s + an analyzer — not a universal `JsonObject`/`JsonRaw` wrapper. |
| **Middleware/RPS warning** | **Non-issue** | Reptar middleware is already static delegates; AdonisJS middleware is lazy-import functions, not arrays of instances. `132,907 req/s` is a local `GET /health` benchmark, not a design target, and none of these proposals touch the hot path. |
| **"Rule 5"** | **Does not exist** | The repo has `AGENTS.md` principles and `llms.txt` §7 "Strict Agent Conventions"; there is no numbered "Rule 5" about JSON. |
| **Offered `Schema.PostsCols` generator** | **Already implemented** | `ReptarTableGenerator` emits `<Table>Cols` (and `Users.Email`-style tokens when the field is named `Table`). `ReptarEntityGenerator` emits `[ReptarEntity]` `Cols` and row mappers. |

**Bottom line:** steal the AdonisJS *ergonomics*, not its runtime architecture. The highest-value new capability is preload; the easiest is CLI generators; the least valuable as proposed is the source-generated lifecycle. None of them should be built on top of the current template/AOT issues — fix those first.

### What the transcript got right

- AdonisJS's `make:*` workflow is genuinely ergonomic, and Reptar has no equivalent resource generators.
- Lucid's `.preload()` is a good model for avoiding N+1, and Reptar has no relationship API today.
- The AOT/JSON trap is real; anonymous/dictionary payloads are a recurring source of NetWasm/AOT failures.
- Middleware and route dispatch must stay static and reflection-free; that instinct is correct even if the premise about AdonisJS middleware is not.
- The framework's constraints (Native AOT, NetWasm, no runtime reflection) are real and should shape any adaptation.

---

## 1. What AdonisJS actually has, and where it lives

The transcript says "AdonisJS" as if it were one repo. It is an ecosystem:

- [`adonisjs/core`](https://github.com/adonisjs/core) — HTTP server, router, middleware, config, env, logger, hashing, encryption, health, REPL integration, and the core `make:*` commands. Its `package.json` depends on `@adonisjs/ace`, `@adonisjs/fold` (the IoC container), and `@adonisjs/http-server`.
- [`adonisjs/ace`](https://github.com/adonisjs/ace) — the CLI framework (`node ace`, commands, args/flags, prompts, stubs).
- [`adonisjs/lucid`](https://github.com/adonisjs/lucid) — the SQL ORM: models, relationships, preloading, migrations, factories, seeders, and `make:model`/`make:migration`.

So the transcript's "Ace-inspired CLI" and "Lucid-style preload" are accurate at the ecosystem level, but `make:model` is a **Lucid** command, not a core command, and Ace is a **separate package**, not the core repo itself. The core repo's own `make:*` directory contains `command`, `controller`, `event`, `exception`, `listener`, `middleware`, `preload`, `provider`, `service`, `test`, `transformer`, `validator`, and `view`. Official packages add `make:model`, `make:migration`, `make:factory`, `make:seeder` (Lucid), `make:mail` (Mail), `make:policy` (Bouncer), and `make:page` (Inertia).

### Relevant AdonisJS facts (verified)

- **Ace commands use stubs and codemods.** `make:model` composes `make:migration`, `make:controller`, `make:transformer`, and `make:factory`; commands support `--force`, `--contents-from`, and command-specific flags. `make:controller` has `--resource`, `--api`, `--singular`, and custom action names. The real pattern is a reusable stub system, not a single static file writer.
- **Lucid preload is batched side-loading.** The docs say `.preload('user')` "executes two queries total: one for posts, one for all users." The source confirms it: for an array of parents, `has_many` eager loading uses `whereIn(foreignKey, unique(parentValues))`. Nested preloads, constraint callbacks, and many-to-many are supported.
- **Service providers have five hooks.** `register` (sync, container bindings), `boot` (async, after all providers registered), `start` (async, warmup before preload files), `ready` (async, after the server is ready), `shutdown` (async, graceful termination). Hooks run in phases across all providers, in registration order. `register`/`boot`/`start` also run during tooling warmup; `ready`/`shutdown` only in `run` mode.
- **The IoC container is runtime and decorator-based.** `@inject()` uses TypeScript's `experimentalDecorators`/`emitDecoratorMetadata` to detect constructor dependencies at runtime. Class constructors are the usual tokens; some packages also use string tokens (e.g. `container.make('lucid.db')`). This is exactly the kind of runtime metadata Reptar cannot use under Native AOT/NetWasm.
- **Middleware has three stacks.** Server middleware runs for every request before routing; router middleware runs after a route matches; named middleware is applied per route/group. The router stores global middleware as converted lazy-import functions, and named middleware as a collection of lazy imports. It is dynamic module resolution, not "arrays of class instances."

---

## 2. Claim-by-claim audit

| # | Transcript claim | Accuracy | Evidence / correction |
| :-- | :-- | :-- | :-- |
| 1 | Ace is ergonomic for scaffolding (`make:model`, etc.) | **Correct** | Ace + stubs + codemods are real. `make:model` is in Lucid, not core. |
| 2 | "The AOT win" of a CLI generator is avoiding runtime reflection | **Wrong** | The CLI runs at build time. AOT is irrelevant to scaffolding. The real win is DX and consistency. |
| 3 | Implement the generator in `scripts/reptar.ts` | **Wrong location for a shipped feature** | `plans/scaffolding-and-auth.md` calls `scripts/reptar.ts` "repo-internal only." User-facing generators belong in `@reptar/cli-core` / the scaffold runner. |
| 4 | Lucid `.preload('posts')` is a clean way to avoid N+1 | **Correct** | Docs and source confirm batched `WHERE IN` queries. |
| 5 | Parse "explicit `ColumnRef<T>` relationships" at compile time | **Wrong** | `ColumnRef<T>` stores only `Table` and `Name` (`framework/Reptar/Data/ColumnRef.cs:6-36`). It has no relation semantics. `TableDef.Columns[].ReferencesTable` exists, but it is optional and is not used in `examples/blog/schema/posts.cs` or `examples/realworld/schema/realworld.cs`. |
| 6 | Map preloaded data "straight into `QueryMappedAsync` pipelines" | **Incomplete** | `QueryMappedAsync` maps a single `IDataRecord` to a single `T` (`framework/Reptar/Data/Services.cs:91-96`). It does not hydrate nested relations. Preload needs a batching + assignment layer above it. |
| 7 | Preload "bypasses reflection entirely" | **True but not the hard part** | The hydration can be reflection-free with generated delegates. The hard parts are relation declarations, DTO shape, batching, and codegen. |
| 8 | AdonisJS uses a "heavy, runtime string-mapped IoC container" | **Partly true** | It is a runtime container with decorator metadata and class tokens; some string tokens exist. "Heavy, string-mapped" is a caricature, but the AOT incompatibility is real. |
| 9 | A source generator can emit a flat init sequence "straight into your entry point" | **Wrong** | C# source generators are additive. They can add partial members, new files, or module initializers; they cannot rewrite top-level statements or the synthesized `Main`. You still need a call site such as `app.AddModule(...)` or `app.UseModules()`. |
| 10 | Lifecycle hooks eliminate dynamic dependency resolution and shrink binaries | **Overstated** | Reptar already has no dynamic dependency resolution. Hooks improve operability; they do not materially change binary size or route throughput. |
| 11 | `ctx.Json(new { ... })` crashes under Native AOT and NetWasm | **Mostly right, too absolute** | NetWasm: anonymous types/dictionaries throw `NotSupportedException` because the source-gen context has no metadata (`scripts/check-netwasm.ts` warns). Native AOT: `plans/agent-engine.md:57` records a real failure with `SmallObjectWithParameterizedConstructorConverter`; `llms.txt` documents it. But registered records with `SourceGenJsonCodec` are safe, and Kestrel JIT works. It is target/type/configuration-dependent, not a universal law. |
| 12 | All payload utilities must use `JsonObject`/`JsonArray` + `ctx.JsonRaw` | **Too broad** | Typed source-generated JSON is the primary path (`SourceGenJsonCodec` + `[JsonSerializable]`). `JsonRaw`/`JsonObject` is the escape hatch for dynamic payloads, not the default. |
| 13 | AdonisJS uses "arrays of dynamic class instances for middleware" | **Inaccurate** | The router stores converted lazy-import functions; named middleware is a lazy-import collection. It is dynamic resolution, not an array of instances. |
| 14 | The 132k+ T1 RPS figure is a target | **Misleading** | It is a measured local benchmark: `GET /health`, `oha -c 20 -z 5s`, SQLite WAL (`examples/baas/BENCHMARK_RESULTS.md:8`). The results file labels it Native AOT, but `examples/baas/bench.ts` uses `dotnet run app.cs -c Release` (JIT), so the label is unverified. The calibrated VPS T1 JSON result is ~42k req/s (`bench/results/vps-20261025T080730Z/report.md`). Neither is a framework-wide target. |
| 15 | "Critical Rule 5" | **Does not exist** | No "Rule 5" in `AGENTS.md`, `design.md`, or `llms.txt`. The closest is `llms.txt` §7, whose fifth convention is "Keep Raw SQL Available," not JSON. |
| 16 | Offer to write a generator for `Schema.PostsCols` | **Redundant** | `ReptarTableGenerator` already emits `<Field>Cols` and, for a field named `Table`, direct `ColumnRef` members (`framework/Reptar.SourceGenerators/ReptarTableGenerator.cs:195-217`). `ReptarEntityGenerator` already emits `[ReptarEntity]` `Cols` and mappers (`framework/Reptar.SourceGenerators/ReptarEntityGenerator.cs:164-197`). |

---

## 3. Deep dive: Ace-style CLI generators

### What AdonisJS does

- `node ace make:model Post -m` creates a model and a migration; `-c` also creates a controller, `-t` a transformer, `-f` a factory.
- Commands are built from stubs; `eject` copies a stub into the app for customization.
- `--force`, `--contents-from`, `--resource`, `--api`, `--singular`, and custom action names are all supported.
- Codemods update the app's registration files (e.g. `adonisrc.ts`) so generated code is actually wired in.

### What Reptar has today

- `create-reptar` scaffolds a full project from `templates/fullstack`, with `--db`, `--auth`, `--template`, `-y`, and `test:scaffold` (new → install → build → codegen → tsc).
- `scripts/reptar.ts` has `new/create/init`, `dev`, `agent`, `run`, `inspect`, `doctor`, `mcp`, `db` (`status`, `migrate`, `new`, `plan`, `generate`), `codegen`, `test`, `test:scaffold`, `test:realtime`, `check:netwasm`, `dev:status`, `self-check`, `build`, `build:worker`, `build:worker:rooms`.
- The scaffolded project's `templates/fullstack/scripts/reptar.ts` only supports `dev`, `codegen`, and `build`.
- There is **no** `make:*` command, no stub system, no codemods, no `add`/`configure`, no `generate:key`, and no `repl`.

### What would work

Add a **stub-based generator** to the project-local CLI (or the planned `@reptar/cli-core`), not to the repo-internal script.

Proposed commands:

```bash
bun reptar make:model Post -m          # TableDef + DTO + migration
bun reptar make:model Post -m -f       # + feature slice (records, Rule<T>, CRUD routes)
bun reptar make:feature Comments       # route slice only
bun reptar make:middleware AuthGuard   # ReptarMiddleware delegate
bun reptar make:validator CreatePost   # Rule<T> definition
bun reptar make:test posts             # xUnit test
```

`make:model` should emit:

1. A `TableDef` in `schema/posts.cs` using the existing `DefineTable`/`Col.*` syntax.
2. A `[ReptarEntity("posts")] public partial record PostDto(...)` projection.
3. Optional migration via `bun reptar db generate` (not a hand-written stub, because `TableDef` is the source of truth).
4. Optional feature slice with `Rule<T>` validators, `.Produces<T>()`, and typed `ColumnRef` filters.
5. A `[JsonSerializable(typeof(PostDto))]` entry in the app's JSON context, or a generated context (see §6).
6. Wiring: update `app.cs` `#:include` lines, or maintain `schema/index.cs` / `features/index.cs` aggregators. The plan already suggests `#:include schema/**/*.cs` / `features/**/*.cs`; globs avoid brittle codemods but have a build-caching cost (`plans/scaffolding-and-auth.md:1092`).
7. Run `bun reptar codegen` or print the next command.

Important design details:

- **Two modes.** File-based apps need `#:include` or aggregator updates; csproj-based apps get SDK-style globbing but still need a `Configure(app)` call unless features use an index. Support both.
- **Idempotency.** Skip existing files by default; `--force` overwrites. Print `CREATE`/`SKIP`/`UPDATE`.
- **Stubs.** Keep them in `templates/stubs/` or `packages/cli-core/templates/`, and run `check:netwasm` on generated output. The stub must not emit `ctx.Json(new { ... })` or unregistered records.
- **Pluralization/naming.** A small singular/plural helper is enough; don't add a heavy dependency. Use the same Pascal/camel/snake rules as `ReptarTableGenerator`.
- **Tests.** Extend `scripts/test-scaffold.ts` to run `make:model` + `make:feature`, then build, `codegen`, and `tsc --noEmit`.

### What we would gain

- Lower onboarding friction for file-based C#.
- Consistent generated code that follows the framework's own patterns (`TableDef`, `ColumnRef`, `Rule<T>`, `.Produces<T>`, source-gen JSON).
- Fewer agent/human errors from hand-written boilerplate.
- Parity with AdonisJS/Laravel's "make" ergonomics without adding runtime dependencies.

### Costs and risks

- **Wiring is the hard part.** Writing a file is easy; making it part of the build and the route tree is not. Codemods must be safe and reversible.
- **Template drift.** Generated output must be tested in CI, or it will rot.
- **Two project shapes.** File-based and csproj-based apps behave differently.
- **No AOT/performance impact.** This is a build-time feature; don't sell it as a runtime win.

**Effort:** ~3–5 days for `make:model`/`make:feature`/`make:test` + stubs + wiring + tests.

**Verdict:** do this first. It is the lowest-risk, highest-DX return.

---

## 4. Deep dive: Lucid-style preloading

### What AdonisJS does

Lucid models declare relationships (`@hasMany`, `@belongsTo`, `@manyToMany`) and queries eager-load them:

```ts
const posts = await Post.query()
  .preload('user', (query) => query.preload('profile'))
  .preload('comments', (query) => query.preload('author'))
```

For a list of parents, Lucid runs one query per relation with `WHERE foreign_key IN (...)`; for a single parent, it uses `WHERE foreign_key = ?`. It then assigns the related models to each parent. This is side-loading, not a SQL join.

### What Reptar has today

- `TableDef` with columns and optional `References(table, column, onDelete)` metadata (`framework/Reptar/Data/TableDef.cs:25-35`).
- `ColumnRef<T>` with `Eq`, `NotEq`, `Gt`, `Gte`, `Lt`, `Lte`, `Like`, `In`, `NotIn`, `IsNull`, `IsNotNull`, `Asc`, `Desc` (`framework/Reptar/Data/ColumnRef.cs:6-36`).
- `TableQuery<T>` with `Where`, `OrderBy`, `Limit`, `Offset`, `ToListAsync`, `FirstOrDefaultAsync`, `CountAsync`, `ExistsAsync`, `DeleteAsync` (`framework/Reptar/Data/ColumnRef.cs:144-235`).
- `QueryMappedAsync<T>(sql, param, mapper)` for explicit, reflection-free row mapping (`framework/Reptar/Data/Services.cs:91-96`).
- No `Join`, no `Preload`, no relationship metadata on DTOs.
- `References` is optional and is not used in `examples/blog/schema/posts.cs` or `examples/realworld/schema/realworld.cs`; the template uses it once (`templates/fullstack/apps/api/schema/schema.cs`).

### Why the transcript's sketch doesn't work as written

1. **`ColumnRef<T>` does not encode relationships.** It only knows a table name and a column name. There is no target table, cardinality, or FK direction. You cannot "parse `ColumnRef<T>` relationships" without adding a new declaration.
2. **`QueryMappedAsync` is single-row.** It maps one `IDataRecord` to one `T`; it does not know how to attach a `UserDto` to a `PostDto`.
3. **Reptar DTOs are immutable records.** Lucid mutates model instances (`post.user = user`). Reptar would need `init` relation properties plus `with`, generated mutable projections, or a wrapper type. `with` allocates a new record per row; fine for pages, potentially costly for large lists.
4. **The hard part is not reflection.** Source-generated accessors can make hydration reflection-free. The hard parts are relation declarations, type inference, batching, chunking, and JSON/codegen.

### What would work

A v1 built around **explicit relation descriptors + batched side-loading**:

```csharp
[ReptarEntity("posts")]
public partial record PostDto(
    string Id,
    string AuthorId,
    string Title,
    string Content,
    string CreatedAt)
{
    [ReptarBelongsTo(typeof(UserDto), nameof(AuthorId))]
    public UserDto? Author { get; init; }
}

public static class PostRelations
{
    public static readonly BelongsTo<PostDto, UserDto> Author =
        Relation.BelongsTo<PostDto, UserDto>(
            parent: Schema.Posts,
            foreignKey: "author_id",
            related: Schema.Users,
            relatedKey: "id");
}
```

Usage:

```csharp
var posts = await ctx.Db.From<PostDto>(Schema.Posts)
    .Where(Schema.PostsCols.Published.Eq(1))
    .OrderBy(Schema.PostsCols.CreatedAt.Desc())
    .Limit(20)
    .Preload(PostRelations.Author)
    .ToListAsync();
```

Or, if you prefer property expressions:

```csharp
var posts = await ctx.Db.From<PostDto>(Schema.Posts)
    .Preload(x => x.Author)
    .ToListAsync();
```

Implementation shape:

1. The source generator reads `[ReptarBelongsTo]` / `[ReptarHasMany]` attributes (or fluent relation declarations) and emits a static `IRelation<TParent, TRelated>` descriptor plus a direct setter delegate:
   ```csharp
   public static readonly IRelation<PostDto, UserDto> Author =
       new Relation<PostDto, UserDto>(
           parentTable: "posts",
           parentKey: "author_id",
           relatedTable: "users",
           relatedKey: "id",
           attach: (post, user) => post with { Author = user });
   ```
2. `TableQuery<T>.Preload(relation)` fetches the parent page, collects distinct FK values, chunks them to the dialect's parameter limit (SQLite 999; D1 has its own limit), runs one `WHERE id IN (...)` query per chunk, builds a dictionary, and applies the generated setter.
3. `HasMany` works in reverse: collect parent PKs, query the child table `WHERE parent_id IN (...)`, group by FK, assign `List<TRelated>`.
4. Nested preloads: apply the same process to the child list, with a depth limit. Defer to v2 if scope is tight.
5. Many-to-many: requires a pivot `TableDef`; defer to v2.
6. Constraints: allow `Preload(relation, q => q.Where(...).OrderBy(...).Limit(...))`. Per-parent limits need window functions; defer.
7. JSON: the generator must also emit `[JsonSerializable(typeof(PostDto))]` and the nested types, or `ctx.Json(posts)` will fail under AOT/Wasm. This is a hard dependency on §6.

### What we would gain

- Eliminates the N+1 footgun without adopting an ORM.
- Fewer D1 round trips on list endpoints; D1 is network-bound, so this matters more than on SQLite.
- Simplifies the hand-batched code in `examples/realworld` (articles + authors + tags + favorites + follows) and the blog.
- Keeps SQL explicit: the generated SQL is `SELECT ... WHERE id IN (...)`, not hidden join magic.
- Generated TypeScript/OpenAPI contracts can include nested relation types.

### Costs and risks

- **New public API + source generator.** Medium-to-large surface; needs careful naming and docs.
- **Explicit declarations are required.** `TableDef.References` alone is insufficient (no cardinality, no DTO link), and existing apps don't use it consistently.
- **DTO shape.** `init` relation properties are the cleanest fit for records; benchmark `with` on large lists. A generated mutable projection is an alternative.
- **Transactions.** Multiple queries are not a single snapshot; document that preload is read-optimized and eventually consistent unless wrapped in a transaction. D1 batch semantics need care.
- **Pagination + preload.** Preload only the page's parents; don't preload the whole table.
- **Chunking.** `SqlFilter.In` does not chunk; add dialect-aware chunking.
- **Testing.** SQLite, PostgreSQL, MySQL, and D1 all need coverage; D1's parameter limits and JSON row shapes are the riskiest.
- **Scope creep.** Resist polymorphic relations, composite keys, soft deletes, and through-relations in v1.

**Effort:** ~2–3 weeks for `BelongsTo` + `HasMany`, generated metadata/accessors, chunked batching, JSON context updates, and cross-database tests.

**Verdict:** do this second. It is the biggest new capability, but it is not a one-afternoon `.Preload()` add.

---

## 5. Deep dive: service lifecycles

### What AdonisJS does

Five hooks, in order:

| Hook | Type | When | Typical use |
| :-- | :-- | :-- | :-- |
| `register` | sync | on provider import | container bindings |
| `boot` | async | after all providers registered | extend framework classes, configure services |
| `start` | async | warmup, before preload files | register routes, named middleware |
| `ready` | async | after server ready / before a command | attach to server, start workers |
| `shutdown` | async | graceful termination | close connections, release resources |

The first three also run during tooling warmup; `ready`/`shutdown` only in `run` mode.

### What Reptar has today

- `ReptarApp.Create(config)` builds a composition root; services are a fixed `ServiceSet` constructed from `ReptarConfig` (`framework/Reptar/Core/ReptarApp.cs:1158-1180`, `framework/Reptar/Core/Ctx.cs:233-249`).
- Startup is explicit in `app.cs`: `await app.EnsureCreatedAsync(); await RealWorld.EnsureIndexesAsync(app); await app.RunAsync(args);` (`examples/realworld/app.cs:47-49`).
- `KestrelHost.RunAsync` builds a `WebApplication` and runs it; there are no start/stop hooks (`framework/Reptar.Kestrel/KestrelHost.cs:12-147`).
- There is no `IServiceCollection`, no container, and no `register`/`boot` phases. This is a deliberate non-goal (`design.md:21`, `apps/docs/src/content/docs/overview/the-one-rule.md:20-24`).
- Workers freeze the isolate between events; there is no reliable graceful shutdown. Lambda has an init phase but no guaranteed shutdown.

### What would work

A small, explicit module lifecycle — no container, no reflection, no generator:

```csharp
public interface IReptarModule
{
    void Configure(ReptarApp app);
    Task StartAsync(ReptarApp app, CancellationToken ct) => Task.CompletedTask;
    Task StopAsync(ReptarApp app, CancellationToken ct) => Task.CompletedTask;
}

public sealed class BlogModule : IReptarModule
{
    public void Configure(ReptarApp app)
    {
        app.RegisterSchema(Schema.All);
        app.Route("/posts", Posts.Routes());
    }

    public async Task StartAsync(ReptarApp app, CancellationToken ct)
    {
        await app.EnsureCreatedAsync();
        await Blog.EnsureIndexesAsync(app);
    }

    public Task StopAsync(ReptarApp app, CancellationToken ct)
    {
        // flush telemetry, close pools, stop workers
        return Task.CompletedTask;
    }
}

var app = ReptarApp.Create(config);
app.AddModule(new BlogModule());
await app.RunAsync(args);
```

`RunAsync` (Kestrel/Lambda) calls `Configure` for all modules, then `StartAsync` in registration order before serving, then `StopAsync` in reverse order on shutdown. On Workers, only `Configure` and `StartAsync` run; `StopAsync` is a no-op because the runtime cannot guarantee it.

If you want simpler ergonomics, delegate hooks are enough:

```csharp
app.OnStart(async (app, ct) => await app.EnsureCreatedAsync());
app.OnShutdown(async (app, ct) => await telemetry.FlushAsync(ct));
```

### Why the transcript's source-generator/entry-point idea doesn't work

- C# source generators **add** source. They cannot rewrite top-level statements or the compiler-synthesized `Main`.
- A generator can emit a partial method, a partial class member, or a `[ModuleInitializer]`. A `[ModuleInitializer]` runs before `ReptarApp.Create` and has no app instance; a partial method still needs a call site.
- It can generate a `ModuleRegistry.RegisterAll(app)` method, but `app.cs` must call it. That is a normal explicit call, not "straight into your entry point."
- Async ordering, failure handling, and shutdown are runtime concerns; a compile-time generator cannot sequence them.
- `register`/`boot` are container phases. Without a container, they collapse into "configure" and "start."

### What we would gain

- Graceful shutdown of DB pools, background workers, telemetry, and caches on Kestrel/Lambda.
- A single place for startup warmup (schema creation, indexes, cache priming).
- Better organization for larger apps; modules bundle routes + schema + startup.
- Testability: start/stop modules in integration tests.

### Costs and risks

- **Scope creep into DI.** The moment `IReptarModule` grows a service registry, you have an IoC container by another name. Keep it to lifecycle only.
- **Workers semantics.** Do not promise `shutdown` on Workers; document that `StopAsync` is best-effort/no-op there. Do not put long-running background loops in `StartAsync` on Workers.
- **Ordering.** Registration order is simple but implicit; document it and log it in debug mode.
- **Hidden initialization.** Prefer explicit `AddModule` calls over magic discovery until there is a proven need.
- **No performance win.** This is operability, not RPS.

**Effort:** ~3–5 days for the interface, `ReptarApp`/Kestrel wiring, tests, and docs. Source-generated discovery adds a week and buys little.

**Verdict:** do this after CLI + preload. Use explicit modules; skip the generator.

---

## 6. The AOT/JSON warning: what is real, and what is already solved

### What is real

- Under NetWasm, `ctx.Json(new { ... })` and `ctx.Json(new Dictionary<string, object?>)` throw `NotSupportedException` because the app's `JsonSerializerContext` has no metadata for them. `scripts/check-netwasm.ts` warns for exactly these two patterns (I ran it against a temp file and got both warnings).
- Under Native AOT, `plans/agent-engine.md:57` records a real runtime failure for `ctx.Json(new { ... })` and `ctx.Json(record)` with `SmallObjectWithParameterizedConstructorConverter`; `llms.txt` §5b documents the same class of failure. The exact failure depends on the type, the app's JSON configuration, and whether reflection-based serialization is enabled.
- `bench/reptar` proves the safe path: `PublishAot=true`, `JsonSerializerIsReflectionEnabledByDefault=false`, and a source-generated `BenchJsonContext` with every DTO registered (`bench/reptar/ReptarBench.csproj:10-14`, `bench/reptar/Program.cs:760-786`).

### What is already solved

- `SourceGenJsonCodec` resolves types from one or more `JsonSerializerContext`s (`framework/Reptar/Core/JsonCodec.cs:34-80`).
- `ctx.JsonRaw(string)` and `System.Text.Json.Nodes.JsonObject`/`JsonArray` are available for dynamic payloads (`framework/Reptar/Core/Ctx.cs:141-145`).
- Templates already use `SourceGenJsonCodec(AppJsonContext.Default)` and register their DTOs (`templates/fullstack/apps/api/app.cs:50`, `templates/fullstack/apps/api/json-context.cs`).
- `check:netwasm` catches anonymous/dictionary JSON before publish.

### What is still missing

- **Manual context maintenance.** Every new DTO must be added to `AppJsonContext` by hand. This is easy to forget and exactly what the transcript's "AOT-safe wrapper" should really address.
- **The scanner does not catch unregistered records.** It warns on `ctx.Json(new { ... })` and `Dictionary<string, object?>`, but not on `ctx.Json(new MyRecord())` where `MyRecord` is absent from the context. That is the more common failure in real apps.
- **The default codec masks the problem in dev.** `ReptarJson.Options` installs `DefaultJsonTypeInfoResolver` on non-NetWasm (`framework/Reptar/Core/ReptarJson.cs:8-19`), so Kestrel JIT works while AOT publish fails. The feedback loop is too late.
- **Templates/examples still contain anonymous JSON in places.** `plans/master-audit-and-roadmap.md` Item 7 tracks this; the fix is typed records + context registration, not a universal `JsonRaw` rule.
- **The evidence is inconsistent and should be settled with a test.** `examples/baas/app.cs` uses `ctx.Json(new { ... })` on `/health`, and `examples/baas/BENCHMARK_RESULTS.md` labels the run Native AOT, while `plans/agent-engine.md` says anonymous JSON fails in AOT. `examples/baas/bench.ts` actually spawns `dotnet run app.cs -c Release` (JIT), so the "Native AOT" label in that results file is itself unverified. Add an AOT smoke test that hits an anonymous-JSON route and a registered-record route; until then, treat both claims as hypotheses.

### What would actually work

1. **Generate the JSON context from route metadata.** A Roslyn source generator (or the codegen host) can emit `[JsonSerializable(typeof(T))]` for every `Produces<T>` and every validated input type. Since `RouteMeta` already carries `InputType` and `Produces`, this is a natural extension of `RpcGenerator`. The app keeps a `partial class AppJsonContext : JsonSerializerContext;` and the generator adds the attributes.
2. **Add a compile-time analyzer** for `ctx.Json(new { ... })`, `ctx.Json(new Dictionary<string, object?>)`, and `ctx.Json<T>` where `T` has no `[JsonSerializable]` in any context. Make the first two warnings in dev, errors in `--aot`/CI.
3. **Fail fast in `doctor`/`build`.** If the app uses `DefaultJsonCodec` and `PublishAot`/`netwasm0.1`, print a clear warning with the fix. If `bun reptar build` publishes AOT, assert the output is a native executable (the master audit already flags the Dockerfile masking a failed publish).
4. **Keep `JsonRaw`/`JsonObject` as the escape hatch.** Do not make it the default; typed source-gen is smaller, faster, and safer.

**Effort:** ~1–2 weeks for the generator + analyzer + tests.

**Verdict:** higher value than the transcript's vague wrapper. Do it alongside the template AOT fixes.

---

## 7. The middleware/RPS warnings: non-issues

- **Reptar middleware is already static.** `ReptarMiddleware` is a delegate: `Task<ReptarResponse> (Ctx ctx, Func<Task<ReptarResponse>> next)` (`framework/Reptar/Core/Middleware.cs:5-7`). `ReptarApp.Use` stores a `List<ReptarMiddleware>`; routes store a delegate handler. There are no dynamic class instances or reflection in the request path.
- **AdonisJS middleware is not an array of instances either.** The router stores converted lazy-import functions; named middleware is a lazy-import collection (`adonisjs/http-server` `src/router/main.ts:74`, `:139-156`). It is dynamic module resolution, but the transcript's description is inaccurate.
- **The 132k figure is a benchmark, not a target.** It is `GET /health` on a local Kestrel + SQLite WAL setup with `oha -c 20 -z 5s` (`examples/baas/BENCHMARK_RESULTS.md:8`). The results file labels it Native AOT, but `examples/baas/bench.ts` spawns `dotnet run app.cs -c Release` (JIT), so even that label is unverified. The calibrated VPS T1 JSON result is ~42k req/s; the README's 7,217 req/s figure is a different setup. Treat all three as different measurements, not one target.
- **None of the three proposals touches the hot path.** CLI generators are build-time; preload replaces N+1 with a bounded number of queries; lifecycle hooks run at startup/shutdown. The "132k target" warning is a non-sequitur.

---

## 8. What else from AdonisJS is worth stealing

| AdonisJS feature | Reptar status | Priority | Notes |
| :-- | :-- | :-- | :-- |
| `make:*` stubs + `eject` | Missing | **High** | The CLI generator system in §3. |
| `make:model -m/-c/-f` composition | Missing | **High** | Generate TableDef + DTO + feature + migration; no Active Record. |
| `list:routes` | `inspect`/MCP covers it | Low | A pretty `bun reptar routes` is nice but not necessary. |
| `generate:key` | Missing | Medium | Easy win for auth/JWT secrets; write a generated secret to `.env` (`BETTER_AUTH_SECRET`/JWT secret). |
| Factories/seeders (`make:factory`, `db:seed`) | Seed scripts only | Medium | Useful for tests/dev data; C# factories are straightforward. |
| `add`/`configure` package integration | Missing | Low | No package ecosystem yet; revisit with NuGet packaging. |
| REPL | Missing | Low | Hard with file-based apps; use `bun reptar run` + tests instead. |
| Health checks | `MapHealth` exists | — | Already covered. |
| Bouncer policies | `DefinePolicy`/`Can` exists | — | Already covered. |
| Named middleware with parameters | Delegates + per-route middleware exist | Low | A registry could improve ergonomics; not urgent. |
| HMR/dev server | `bun reptar dev` exists | Low | C# file-based compile is the constraint, not the CLI. |
| Vine validators | `Rule<T>` exists | — | Already covered; generated Standard Schema is the frontend bridge. |
| Lucid migrations | `db generate`/`db migrate` exists | — | Already covered. |
| Lucid relationships/preload | Missing | **High** | The §4 feature. |
| Service providers/lifecycle | Partial | Medium | The §5 feature, explicit version only. |

---

## 9. Recommended sequence

The repo's own `plans/master-audit-and-roadmap.md` already has Phase 1 items for scaffolding correctness and the AOT JSON trap. Build on those; don't add new features on top of known template failures.

| Order | Work | Why now | Effort | Risk |
| :-- | :-- | :-- | :-- | :-- |
| **0** | Fix template/example AOT JSON + scaffold correctness (master audit Items 1 & 7) | Prevents new apps from shipping the exact bug the transcript warns about | 1–2 days | Low |
| **1** | `make:model` / `make:feature` / `make:test` + stubs + wiring + scaffold smoke test | Highest DX return; lowest runtime risk | 3–5 days | Medium (wiring) |
| **2** | Preload v1: `BelongsTo` + `HasMany`, explicit relation metadata, chunked batching, JSON context updates, cross-DB tests | Biggest new capability; fixes N+1 on D1 | 2–3 weeks | Medium-high |
| **3** | Generated `JsonSerializerContext` + analyzer + fail-fast doctor/build | Makes AOT reliable; removes manual context maintenance | 1–2 weeks | Medium |
| **4** | Explicit `IReptarModule` lifecycle + Kestrel start/stop + docs | Production readiness; no container | 3–5 days | Low-medium |
| **5** | Smaller DX: `generate:key`, factories/seeders, route list | Nice-to-have after the core | 1–2 days each | Low |

**Success criteria:**

- **CLI:** `bun reptar new` → `make:model Post -m -f` → `db generate` → `codegen` → build passes with no manual edits; generated files pass `check:netwasm`.
- **Preload:** RealWorld article list uses `.Preload(...)`; query count drops from N+1 to 2–3; D1 round trips drop; no reflection in the hot path; generated TS types include nested relations.
- **JSON:** zero anonymous `ctx.Json` in templates/examples; AOT publish + route smoke passes; `check:netwasm` has no anonymous/dictionary warnings.
- **Lifecycle:** a Kestrel module starts and stops cleanly; DB pool closes; Workers build is unaffected; no measurable Wasm size increase.

---

## 10. Bottom line

The transcript is a useful pointer, not a specification.

- **Right:** AdonisJS's `make:*` ergonomics and Lucid's preload are worth adapting; N+1 and AOT JSON are real problems.
- **Wrong:** The AOT rationale for the CLI, the "ColumnRef relationships" mechanism, the source-generator entry-point injection, the universal `JsonObject` rule, the middleware-instance claim, and the 132k "target."
- **Already done:** `Schema.PostsCols`, `[ReptarEntity]` mappers, `ctx.JsonRaw`, `SourceGenJsonCodec`, `inspect`/MCP, health checks, policies.
- **Best next move:** fix the template AOT JSON/scaffold issues, then ship `make:model`/`make:feature`, then preload v1. Treat lifecycle modules and JSON context generation as the next layer, not the first.

**What it would bring to the framework:** if the CLI generators, preload, and lifecycle modules land, Reptar can credibly claim **AdonisJS/Laravel ergonomics with Native AOT/NetWasm performance**: scaffold a project, generate a model/feature, load relations without N+1, manage startup/shutdown, and still ship a small reflection-free binary with generated TypeScript contracts. That is a stronger story than any single feature. The differentiator remains the runtime — zero reflection, small binaries, D1/edge support, generated contracts — so the adaptations must not dilute it with an ORM, a container, or dynamic middleware.

The framework's identity should stay: **AdonisJS/Laravel ergonomics, Dapper-like explicit SQL, zero runtime reflection, Native AOT/NetWasm performance.** Borrow the developer experience; leave the runtime container, Active Record models, and dynamic middleware behind.

---

## Verification notes

What I actually ran/checked while writing this:

- Read `design.md`, `llms.txt`, the source generators, the data layer, the CLI, the scaffold code, and the relevant plans.
- Ran `bun scripts/reptar.ts check:netwasm` against a temp C# file containing `ctx.Json(new { ... })`, `ctx.Json(new Dictionary<string, object?>)`, `ctx.Json(record)`, and `ctx.JsonRaw(...)`. Result: 2 warnings (anonymous JSON, dictionary JSON), 0 errors; the record and `JsonRaw` forms were not flagged.
- Ran `bun scripts/reptar.ts inspect --json` and `bun scripts/reptar.ts doctor --json`; both worked against the current checkout (24 routes, SQLite connected, 4 migrations applied).
- Checked AdonisJS docs/source for commands, service-provider lifecycle, DI, Lucid preloading, and middleware.
- Did **not** run a fresh Native AOT publish to reproduce the JSON failure. The AOT finding is based on the repo's own `plans/agent-engine.md` record and the `bench/reptar` safe-path configuration; §6 flags the inconsistency that should be settled with an AOT smoke test.

---

## Sources and evidence

**Reptar (local):**

- `design.md` §2 (non-goals), §13 (honest constraints), §16 (status)
- `llms.txt` §2 (`Schema.PostsCols`), §5b (AOT JSON rule), §6 (NetWasm matrix), §7 (agent conventions)
- `framework/Reptar.SourceGenerators/ReptarTableGenerator.cs` (ColumnRef emission)
- `framework/Reptar.SourceGenerators/ReptarEntityGenerator.cs` (`[ReptarEntity]` mappers/Cols)
- `framework/Reptar/Data/ColumnRef.cs` (`ColumnRef<T>`, `TableQuery<T>`)
- `framework/Reptar/Data/TableDef.cs` (`References`, `ColumnDef`)
- `framework/Reptar/Data/Services.cs` (`IDbClient`, `QueryMappedAsync`)
- `framework/Reptar/Core/Ctx.cs` (`Json`, `JsonRaw`, `ServiceSet`)
- `framework/Reptar/Core/JsonCodec.cs` (`SourceGenJsonCodec`, `DefaultJsonCodec`)
- `framework/Reptar/Core/ReptarJson.cs` (reflection resolver on non-NetWasm)
- `framework/Reptar/Core/Middleware.cs` (`ReptarMiddleware` delegate)
- `framework/Reptar/Core/ReptarApp.cs` (composition, `ServiceSet`)
- `framework/Reptar.Kestrel/KestrelHost.cs` (`RunAsync`)
- `scripts/check-netwasm.ts` (JSON/middleware/reflection rules)
- `scripts/reptar.ts` (CLI commands)
- `packages/create-reptar/src/scaffold.ts` (scaffold/wiring)
- `templates/fullstack/scripts/reptar.ts` (scaffold CLI)
- `templates/fullstack/apps/api/json-context.cs` (manual JSON context)
- `plans/scaffolding-and-auth.md` (repo-internal CLI, glob includes)
- `plans/agent-engine.md` (AOT JSON finding)
- `plans/master-audit-and-roadmap.md` (Phase 1 scaffolding/AOT items)
- `examples/baas/BENCHMARK_RESULTS.md` (132k T1 `/health`)
- `bench/reptar/ReptarBench.csproj` and `bench/reptar/Program.cs` (AOT + source-gen JSON reference)
- `examples/blog/schema/posts.cs`, `examples/realworld/schema/realworld.cs` (no `.References` usage)
- `examples/realworld/app.cs` (explicit startup calls)

**AdonisJS:**

- [`adonisjs/core`](https://github.com/adonisjs/core)
- [`adonisjs/ace`](https://github.com/adonisjs/ace)
- [`adonisjs/lucid`](https://github.com/adonisjs/lucid)
- [Commands reference](https://docs.adonisjs.com/reference/commands)
- [Service providers / lifecycle](https://docs.adonisjs.com/guides/concepts/service_providers)
- [Dependency injection / IoC](https://docs.adonisjs.com/guides/concepts/dependency_injection)
- [Lucid relationships / preloading](https://docs.adonisjs.com/guides/database/lucid)
- [Middleware](https://docs.adonisjs.com/guides/basics/middleware)
- Lucid `has_many` eager query: `src/orm/relations/has_many/query_builder.ts` (`whereIn` for arrays)
- Lucid `Preloader`: `src/orm/preloader/index.ts` (`processRelationForMany`)
