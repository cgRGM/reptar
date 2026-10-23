# Reptar — Design

**Reptar is the backend.**

A C# web framework that compiles to a small Wasm module and runs on Cloudflare Workers — with Hono-like routing, Drizzle-like schema, Zod-like validation, built-in auth, first-class Cloudflare bindings, and a generated typed client for any frontend.

This document is the source of truth for what Reptar is, how it works, and what gets built. Agents: read this before writing framework code, docs, or the README.

---

## 1. The one rule

Reptar is the backend. Full stop.

- Zero frontend opinions: no UI kit, no router, no styling, no frontend tooling in the framework.
- The only frontend-facing surface is **generated contracts**: a typed `fetch` client, types, Standard Schema validators, and OpenAPI.
- The TypeScript host shim is _backend infrastructure_ — generated, versioned, deployed with the C#. Like Kestrel, nobody writes it.

## 2. Non-goals

- No enterprise .NET ceremony: no `IServiceCollection`, no controllers, no EF, no repositories, no `.sln` bloat.
- No visible `.csproj`/MSBuild workflow for app authors. Apps are file-based C# (`app.cs` + `#:include`) driven by the `reptar` Bun CLI; the .NET SDK is a hidden build tool, never a deployment runtime.
- No hidden-SQL ORM magic. The query builder is Dapper-like: explicit SQL, parameterized, source-generated mappers.
- Not "desktop .NET squeezed onto Workers." Minimal, elegant, AOT/Wasm-shaped from day one.
- No runtime reflection. Everything hot is source-generated (JSON, mappers, validators, binding) so it works under NetWasm and Native AOT.

## 3. Architecture

```text
TanStack Start / Expo / Next / curl / anything
        │  createClient<AppType>  (generated)
        ▼
Cloudflare Worker — TS host shim (generated, ~thin)
   ├─ fetch, cookies, crypto (WebCrypto + node:crypto scrypt)
   ├─ D1, R2, Workers AI, RateLimit bindings
   ├─ telemetry (OTel/Axiom, logs)
   ├─ Better Auth mount (only when using the adapter)
   └─ calls reptar.wasm
        │
        ▼
C# Reptar core (compiled to Wasm via NetWasm)
   router → middleware → validation → handler → c.Db / c.AI / c.Storage
```

**Request lifecycle:** `fetch` → shim builds a request envelope (JSON) → `wasm.handle(...)` → router match → middleware → `Rule<T>` validation → handler → data calls go back to the shim as imports (`db.query`, `storage.put`, `ai.run`) → response envelope → `Response`.

**Dual-target:** the same C# feature files compile to `netwasm0.1` (Workers, primary) and `net10.0` Native AOT (container/Lambda fallback). Same code, different host.

## 4. What the user writes

```text
apps/api/
  app.cs              # composition: config, middleware, mount features
  schema/             # tables — the single source of truth
    users.cs
    posts.cs
    index.cs          # optional aggregate (can be generated)
  db/client.cs        # D1()/Sqlite() connection
  features/           # one file per slice: records + rules + routes
    auth.cs
    posts.cs
    ai.cs
  migrations/         # generated SQL (reptar db generate)

apps/worker/          # generated TS shim — do not edit
packages/rpc/          # generated client + types + schemas
packages/contract/     # reptar.spec.json + openapi.json
```

`apps/api/app.cs` is a **file-based C# app**. It references the framework with `#:project` and pulls schema/feature files in with `#:include`:

```csharp
#:project ../../framework/Reptar/Reptar.csproj
#:include schema/posts.cs
#:include features/posts.cs

global using Reptar;
global using static Reptar.ReptarApi;

var app = ReptarApp.Create(new ReptarConfig { Database = Db.Sqlite("blog.db") });
app.RegisterSchema(Schema.All);
app.Route("/posts", Posts.Routes());
await app.RunAsync(args);
```

There is no app `.csproj`, no `dotnet` command in the daily loop, and no `.sln`. `bun reptar dev` and `bun reptar codegen` locate (or install into `.reptar/`) the build SDK and invoke the file-based app. Production ships the Wasm module, not the SDK.

### 4.1 Schema (`schema/posts.cs`)

Every file is a partial `Schema` class. This is the Drizzle equivalent and the source of truth for tables, migrations, row mappers, and OpenAPI-ish constraints.

```csharp
public static partial class Schema
{
    public static readonly TableDef Posts = DefineTable("posts", new
    {
        Id        = Col.Text("id").PrimaryKey(),
        AuthorId  = Col.Text("author_id").NotNull().References(Users, "id", onDelete: "cascade"),
        Slug      = Col.Text("slug").NotNull().Unique(),
        Title     = Col.Text("title").NotNull(),
        Content   = Col.Text("content").NotNull(),
        Published = Col.Int("published").Default(0),
        CreatedAt = Col.Text("created_at").DefaultSql("CURRENT_TIMESTAMP")
    });
}
```

`schema/index.cs` (optional) aggregates: `public static readonly TableDef[] All = [Users, Posts, ...];` — or the build discovers every `TableDef` in the partial class.

### 4.2 DB (`db/client.cs`) — Dapper-like, not an ORM

```csharp
public static class Db
{
    public static DbClient D1() => DbClient.D1();
    public static DbClient Sqlite(string path = "local.db") => DbClient.Sqlite(path);
}
```

Handlers use `c.Db`:

```csharp
// raw SQL, always available
var posts = await c.Db.QueryAsync<Post>(
    "select * from posts where published = 1 order by created_at desc");
var post  = await c.Db.QuerySingleOrDefaultAsync<Post>(
    "select * from posts where id = @id", new { id });
await c.Db.ExecuteAsync(
    "insert into posts (id, title, content, created_at) values (@Id, @Title, @Content, @CreatedAt)",
    post);

// common CRUD helpers, generated from TableDef
var post  = await c.Db.GetAsync<Post>(Schema.Posts, id);
var posts = await c.Db.ListAsync<Post>(Schema.Posts, "where published = 1 order by created_at desc");
await c.Db.InsertAsync(Schema.Posts, post);
await c.Db.UpdateAsync(Schema.Posts, post);
await c.Db.DeleteAsync<Post>(Schema.Posts, id);
```

Mappers are source-generated (snake_case column → PascalCase property). No runtime reflection.

### 4.3 Features (`features/posts.cs`) — terse route style

One file per slice: records, validators, routes, helpers.

```csharp
public static class Posts
{
    public record CreatePost(string Title, string Content);
    public record Post(string Id, string Title, string Content, int Published, string CreatedAt);

    public static readonly Validator<CreatePost> CreateSchema = Rule<CreatePost>()
        .Prop(x => x.Title, r => r.Min(3).Max(120))
        .Prop(x => x.Content, r => r.Min(1));

    public static ReptarApp Routes() => ReptarApp.Create()
        .Get("/", async c => c.Json(
            await c.Db.ListAsync<Post>(Schema.Posts, "where published = 1 order by created_at desc")))
            .Produces<Post[]>(200)

        .Get("/:id", async c =>
        {
            var post = await c.Db.GetAsync<Post>(Schema.Posts, c.Req.Param("id"));
            return post is null
                ? c.Json(new { error = "Not found" }, 404)
                : c.Json(post);
        })
            .Produces<Post>(200)
            .Produces<ErrorBody>(404)

        .Post("/", CreateSchema, async (c, input) =>
        {
            var post = new Post(Ids.New("post"), input.Title, input.Content, 0, c.Clock.IsoNow());
            await c.Db.InsertAsync(Schema.Posts, post);
            return c.Json(post, 201);
        })
            .Produces<Post>(201)
            .Produces<ValidationError>(400);
}
```

`.Produces<T>(status)` is the only required metadata — it is the contract that makes the generated client and OpenAPI accurate.

### 4.4 Composition (`app.cs`)

```csharp
var app = ReptarApp.Create(new ReptarConfig
{
    Database = D1(),
    Storage = R2(),
    AI = WorkersAI(),
    Auth = BuiltIn(new AuthOptions
    {
        SessionDays = 30,
        AllowAnonymous = true
    }),
    Telemetry = Axiom()
});

app.Use(RequestId());
app.Use(Telemetry());
app.Use(Cors());

app.RegisterSchema(Schema.All);

app.Route("/auth", Reptar.Auth.Routes());
app.Route("/posts", Posts.Routes());
app.Route("/ai", Ai.Routes());

await app.RunAsync(args);
```

### 4.5 `Ctx` surface

| Member | Backed by | Notes |
| --- | --- | --- |
| `c.Req` | request | params, query, headers, body, files |
| `c.Db` | D1 (via shim) | Dapper-like API |
| `c.Storage` | R2 | `PutAsync`, `GetAsync`, `ExistsAsync`, `DeleteAsync`, `Url` |
| `c.AI` | Workers AI | `GenerateAsync`, `TranscribeAsync`, `EmbedAsync` |
| `c.RateLimit` | RateLimit binding | `LimitAsync("chat", key)` |
| `c.Cookies` | shim | get/set/delete, HttpOnly + SameSite defaults |
| `c.Clock` | shim | `IsoNow()`, `IsoIn(ts)` — deterministic/testable |
| `c.Crypto` | shim | `HashPassword`, `VerifyPassword` (scrypt), `Sha256`, `RandomToken` |
| `c.User` | auth | set by `RequireUser` middleware |
| `c.Json` / `c.Status` / `c.Stream` | framework | typed responses |

## 5. Validation — built in, no Zod on the backend

Three layers, all framework-owned:

1. **Records are the schema.** JSON binding is source-generated: missing required fields, wrong types, extra fields are errors. Non-nullable = required.
2. **`Rule<T>` for field rules** (min/max/regex/email/enum/length) plus `.Refine(x => ..., message, path)` for cross-field rules.
3. **Schema-derived base rules** where an input maps to a `TableDef` (NotNull, length, unique as constraints).

Error envelope (identical shape to what bigspella uses):

```json
{
  "code": "validation_error",
  "message": "Validation failed.",
  "requestId": "req_01J...",
  "issues": [{ "path": ["title"], "message": "Must be at least 3 characters" }]
}
```

## 6. Auth — built in C#, adapters for the TS world

**Default: maximum C#.** `Reptar.Auth` ships as a mountable feature slice:

- `users` + `sessions` tables in `schema/auth.cs`.
- Email/password with scrypt via `c.Crypto` (host native crypto — pure-JS scrypt blows the Workers CPU limit; this is a proven constraint).
- Session tokens: random 32 bytes, SHA-256 hash stored in D1, HttpOnly cookie, configurable `SessionDays` and cross-subdomain `CookieDomain`.
- Anonymous sessions (guest flows).
- Admin bootstrap emails + `RequireUser` / `RequireRole` middleware.
- Routes: `POST /signup`, `POST /signin`, `POST /signout`, `GET /session`, `POST /anonymous`.

It's a normal feature slice — readable, forkable, replaceable.

**Adapters** for apps that bring their own auth:

```csharp
Auth = BetterAuth(),          // same D1: read better-auth's session table by cookie token
Auth = Clerk(clerkOptions),   // verify Clerk JWTs via JWKS (host WebCrypto)
Auth = Custom(async c => ...)
```

The Better Auth adapter is small because Better Auth writes its session table to the same D1 — cookie token → session row → `c.User`.

## 7. Cloudflare bindings — written by us, exposed as C#

| Surface         | C# API                                  | Phase |
| --------------- | --------------------------------------- | ----- |
| D1              | `c.Db`                                  | v1    |
| R2              | `c.Storage`                             | v1    |
| Workers AI      | `c.AI`                                  | v1    |
| RateLimit       | `c.RateLimit`                           | v1    |
| KV              | `c.Cache`                               | v2    |
| Cron            | `app.OnCron("*/5 * * * *", ctx => ...)` | v2    |
| Email           | `c.Email.SendAsync(...)`                | v2    |
| Queues          | `c.Queues.SendAsync(...)`               | v2    |
| Durable Objects | `c.Durable("table", id)` bridge         | later |
| Vectorize       | `c.Vectors`                             | later |

The C# interface is the API; the generated TS shim implements each against `env`. Durable Objects stay TS (WebSocket live coordination); C# owns the records in D1.

## 8. Codegen — the contracts

`bun reptar codegen` (wraps dotnet; runs automatically in dev/build):

```text
packages/rpc/src/
  app-type.d.ts           # route tree: AppType
  types.ts                # DTO types
  schemas.generated.ts    # Standard Schema validators (zero-dep)
  client.ts               # stable ~100-line createClient runtime

packages/contract/
  reptar.spec.json        # language-neutral contract (future: Kotlin/Swift/Dart clients)
  openapi.json            # REST consumers, docs, generators

apps/api/migrations/      # from reptar db generate
```

- `--check` fails CI on stale generated files.
- **No Drizzle schema by default** — C# owns the whole API layer, so nothing in the default path consumes it. `--drizzle` is an opt-in adapter flag for incremental adoption; `--zod` opt-in for real Zod objects.

### Standard Schema validators (zero-dependency)

Generated from `Rule<T>`, implementing `~standard` (version 1, vendor `"reptar"`) so TanStack Form or anything that speaks Standard Schema consumes them with **no validation library installed**. One `Rule<T>` in C# = server validation + client validation + TS types + OpenAPI constraints.

### Typed client usage

```ts
const api = createClient<AppType>(import.meta.env.VITE_API_URL);

const posts = await api.posts.$get();
const post = await api.posts[":id"].$get({ param: { id } });
const created = await api.posts.$post({
  json: { title: "Hello", content: "World" },
});
if (created.ok) {
  const row = await created.json();
} // PostRow
```

## 9. Migrations — our own engine

`TableDef` contains everything needed to emit DDL, so Reptar writes SQL itself:

- `reptar db generate` → `migrations/0000_init.sql` + `meta/_journal.json`
- `reptar db push` → local SQLite for dev
- `reptar db apply` → D1 (`wrangler d1 migrations apply`; Alchemy `D1Database({ migrationsDir })` points at the folder)

v1 scope: `CREATE TABLE IF NOT EXISTS`, add-column, add-index, add-FK. Renames/drops/backfills → hand-written SQL files in `migrations/`, applied in order. Drizzle Kit remains available via the `--drizzle` escape hatch but is never in the default path.

## 10. Telemetry — observable by default

Per request, one structured JSON log line: `request_id`, `trace_id`, `route`, `status`, `duration_ms`, `db_ms`, `db_queries`, `user_id`, `cf_ray`, `colo`. Plus `Server-Timing: reptar;dur=…, db;dur=…`, a dev-only `/_reptar` dashboard (last 500 requests, p50/p95/p99, slow routes, validation failures), and OTLP export with Axiom as the default destination (Sentry shim-side if preferred).

## 11. Usage modes

1. **Standalone API:** `reptar init my-api` → an API-only repo. Deploy once, consume from any number of apps.
2. **Inside a monorepo:** `apps/api` (C#) + generated `packages/rpc`, picked up by TanStack Start, Expo, or whatever lives there. The better-t-stack shape with the backend swapped for C#.
3. **Optional frontend wiring at scaffold time:** `create-reptar` can generate a Turborepo monorepo with a frontend wired up — **shadcn/ui** starter pages including login pages already wired to Reptar's built-in auth. This is scaffold _output_, not framework surface: the generated frontend is ordinary TanStack Start/Expo code the user owns, and Reptar itself still has zero frontend dependencies.

## 12. Performance targets

- **Parity with a Node/Hono backend for CRUD is the bar; faster is a bonus.** I/O-bound routes are dominated by D1/network, not language.
- Design choices that get there: Wasm instance cached per isolate (no per-request instantiation); raw JSON strings across the boundary (no double serialization); prepared statements cached; source-generated JSON; ~100-line client.
- The spike benchmarks `GET /posts` (D1 query) and `POST /signup` (scrypt) against a Hono baseline and records cold start, p50/p95, and Wasm size.

## 13. Honest constraints

- **NetWasm is pre-1.0** (single maintainer, Community License — free under 250 employees / $10M revenue). It's the best C#→Wasm toolchain available, but the spike must prove stability, and the Native AOT fallback means a failed spike doesn't kill the project.
- **Workers CPU limits** are comfortable on the paid plan; heavy crypto goes through host native crypto.
- **No sockets/threads in Wasm** — the shim owns all I/O; C# is single-reactor async.
- **No runtime reflection** — source generators do the work; keeps Wasm small and AOT-safe.

## 14. Roadmap

- **Phase 0 — Spike (prove the chain):** C# route `GET /posts` + `POST /signup` → Wasm → shim on a Worker → D1 → generated client → `tsc --noEmit` → Alchemy deploy. Record size, cold start, p50/p95. _Go/no-go gate._
- **Phase 1 — Core:** router, `Ctx`, `Rule<T>`, error envelope, source-gen JSON binding.
- **Phase 2 — Data:** `TableDef`, Dapper-like `c.Db`, migrations engine, SQLite local + D1.
- **Phase 3 — Auth:** built-in slice (email/password, anonymous, sessions, admin) + BetterAuth/Clerk adapters.
- **Phase 4 — Bindings:** R2, Workers AI, RateLimit (shim side).
- **Phase 5 — Codegen + CLI:** `reptar codegen` (client, types, Standard Schema, OpenAPI, spec), `db generate/push/apply`, `dev`, `deploy`.
- **Phase 6 — Telemetry:** log line, Server-Timing, `/_reptar`, Axiom.
- **Phase 7 — Reference app:** the blog (posts + auth + tags + AI summary) as the canonical example and template source.
- **Later:** `create-reptar` scaffolder (with optional shadcn/ui frontend wiring), KV/Cron/Email/Queues, DO bridge, Vectorize, native clients from `reptar.spec.json`.

## 15. Framework repo layout (what we ship)

```text
Reptar.Core         router, Ctx, middleware, error envelope, Rule<T>
Reptar.Data         TableDef/Col, DbClient, source-gen mappers, migrations
Reptar.Auth         built-in slice + adapters
Reptar.Cloudflare   D1/R2/AI/RateLimit surfaces (+ KV/Cron/Email/Queues later)
Reptar.Telemetry    OTel/Axiom, Server-Timing, /_reptar
Reptar.Codegen      TS client, types, Standard Schema, OpenAPI, spec, migrations
reptar CLI          codegen, db, dev, deploy (Bun-first; no app .csproj)
@reptar/host        the generated TS shim package
@reptar/client      createClient<AppType> runtime
create-reptar       the scaffolder (incl. optional shadcn/ui frontend starters)
```

## 16. Implementation status (current)

- **Portable core:** router, `Ctx`, `Rule<T>`, `TableDef`, Dapper-like SQLite client, built-in auth, codegen, and the file-based blog app are implemented. `bun api:test` passes (8 tests); `bun rpc:gen` + `bun rpc:check` pass.
- **Phase 0 spike (local):** NetWasm compiles the C# module to a **26 KB** `.wasm`; it runs inside `wrangler dev` (workerd). `GET /greet` returns `Hello, Cloudflare, from C#.` in ~3–5 ms warm. See `spikes/netwasm-worker/`.
- **Host contract:** the stock NetWasm host is browser-only. The Worker shim must (1) set the manifest URL at request time, (2) use a statically imported `WebAssembly.Module`, (3) avoid `URL.createObjectURL` by statically importing the raw adapter, and (4) return exactly `createAdapter` + `rawAdapterMetadata` from the adapter module. These are the responsibilities of `@reptar/host`.
- **Blog demo:** Astro `/blog` renders posts from the C# Worker. Posts are in D1, images are in R2, and the page includes a publish form that writes through the C# API. See `apps/web/src/pages/blog.astro` and `spikes/netwasm-worker/`.
- **Next:** productize `@reptar/host`, move the blog's `schema/` + `features/` onto the Wasm target, then deploy the Worker + site with Alchemy.

### Phase 0 update: D1 bridge proven

- C# `[JSImport]` → Worker `env.DB.prepare(sql).all()` works inside workerd.
- `GET /db?sql=select * from posts` returned `[{"id":"1","title":"Hello D1"}]` from a local D1 database.
- NetWasm does not support async `[JSExport]` returning `Task<string>`. The host-driven pattern is: C# starts async work from a `void` export, awaits a JS import, then calls a `resolve` import; the Worker awaits a JS Promise resolved by that callback. This is the contract for `@reptar/host`.
