# Reptar on Cloudflare Workers

The reference Worker: C# compiled by NetWasm, running inside workerd, with D1 for posts and R2 for images. The API is built with `ReptarApp`, not hand-written exports.

## Result

- `GET /posts` → `ReptarApp` → `D1DbClient` → D1 → JSON.
- `POST /posts` → `ReptarApp` → `D1DbClient` → D1 insert → JSON.
- `POST /upload?key=…` → `ReptarApp` → `R2Client` → R2 put → `{ key, url }`.
- `GET /images/:key` → Worker → R2 → image bytes.
- `POST /auth/signup`, `POST /auth/signin`, `POST /auth/signout`, `GET /auth/session`, and `GET /me` → D1 users/sessions, PBKDF2 via host WebCrypto, HttpOnly cookie sessions.
- The Worker shim is `createReptarHost` from `@reptar/host`; the C# app exposes one generic `handle(string)` export and responds through `respond(string)`.
- Local response time: ~3–10 ms after warm-up.
- Verified with `wrangler dev` (workerd), not just a browser.

The Astro frontend is at `apps/web/src/pages/blog.astro` and renders the posts and images at `http://localhost:4321/blog`. It includes a publish form that writes to D1 and uploads images to R2 through the C# API.

## Run the demo

From the repo root:

```bash
bun run dev
```

This starts the web app (`:4321`), docs (`:4322`), and the C# Worker API (`:8788`). Or run the API alone:

```bash
cd spikes/netwasm-worker
bun run dev
```

Seed a post and image:

```bash
curl -X POST 'http://localhost:8788/upload?key=reptar-cover.svg' \
  -H 'content-type: image/svg+xml' \
  --data-binary @apps/web/public/favicon.svg

curl -X POST http://localhost:8788/posts \
  -H 'content-type: application/json' \
  -d '{"title":"Hello from C# on Workers","content":"D1 + R2 from C#.","imageKey":"reptar-cover.svg"}'

open http://localhost:4321/blog
```

Auth:

```bash
curl -c /tmp/reptar-cookies.txt -X POST http://localhost:8788/auth/signup \
  -H 'content-type: application/json' \
  -d '{"email":"demo@reptar.dev","password":"hunter2hunter2","name":"Demo"}'

curl -b /tmp/reptar-cookies.txt http://localhost:8788/auth/session
curl -b /tmp/reptar-cookies.txt http://localhost:8788/me
```

D1 tables:

```bash
bunx wrangler@latest d1 execute DB --local --file schema.sql
```

`schema.sql` creates `posts`, `users`, and `sessions`. `wrangler.jsonc` binds `DB` to a local D1 database and `BUCKET` to a local R2 bucket. The host sets `globalThis.__REPTAR_DB_QUERY`, `globalThis.__REPTAR_DB_EXECUTE`, `globalThis.__REPTAR_R2_PUT`, `globalThis.__REPTAR_RESPOND`, and the `__REPTAR_CRYPTO_*` globals; `app.host.mjs` forwards them to the C# `query`, `execute`, `r2put`, `respond`, `hashPassword`, `verifyPassword`, `sha256`, and `randomToken` imports.

## Build the C# module

The C# source lives in `csharp/` and targets `netwasm0.1`. The repo CLI installs the .NET SDK as a build tool if needed, publishes the app, copies the browser artifacts, and applies the Worker host rewrite:

```bash
cd spikes/netwasm-worker
bun run build
```

The generated `public/browser` artifacts are committed, so `bun run dev` works without rebuilding the C# module. The optimized module is currently ~10 MB because it carries the framework, auth, source-generated JSON, and LINQ surface; the original minimal spike was 348 KB. Trimming that surface is on the roadmap.

## Why the host is rewritten

The stock NetWasm host is a **browser** host. Four things break on Workers:

1. **`location` is not available at module evaluation.** The manifest URL must be supplied at request time (`globalThis.__REPTAR_MANIFEST_URL`).
2. **`WebAssembly.compile(bytes)` is disallowed.** Workers require a statically imported `WebAssembly.Module`; the spike imports the `.wasm` through Wrangler's `CompiledWasm` rule and passes it in (`globalThis.__REPTAR_WASM_MODULE`).
3. **`URL.createObjectURL` is not implemented.** The raw adapter JS module cannot be imported from a blob URL; it is imported statically and returned as a plain module object.
4. **The adapter verifier expects exactly `createAdapter` and `rawAdapterMetadata`.** A bundled module namespace has extra keys; the host returns a plain object with only those two.

`@reptar/host` owns all four rewrites. `bun reptar build:worker` applies them to the published browser host; there is no app-specific Python patch script.

## NetWasm async-export limitation

NetWasm rejects `[JSExport]` methods returning `Task<string>` (async exports only support `void` or primitive scalars). The framework therefore uses the host-driven pattern:

1. C# `[JSExport] handle(string)` starts async work and returns `void`.
2. C# awaits a `[JSImport]` (`query`, `execute`, `r2put`), which returns a JS Promise.
3. When the managed task completes, C# calls `[JSImport] respond(result)`.
4. The Worker awaits a JS Promise resolved by `respond` and writes the response.

This keeps the single-reactor model intact and lets the host own all I/O.

## Next

- Port the portable blog's `schema/` + `features/` (slugs, ownership, CRUD) to the Wasm target.
- Replace the `TableDef`/reflection stub with source-generated table metadata.
- Trim the Wasm payload (custom JSON codec / smaller LINQ surface).
- Deploy the Worker + site with Alchemy.
