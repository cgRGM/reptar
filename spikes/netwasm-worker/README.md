# NetWasm → Cloudflare Worker spike

Proves that C# compiled by NetWasm can run inside Cloudflare's workerd runtime, with D1 for data and R2 for images.

## Result

- `HelloReptar.wasm`: **26 KB** (raw core module + runtime; `du` of the full browser host directory is 676 KB).
- `GET /posts` → C# → D1 → JSON.
- `POST /posts` → C# → D1 insert → JSON.
- `POST /upload?key=…` → C# → R2 put → `{ key, url }`.
- `GET /images/:key` → Worker → R2 → image bytes.
- Local response time: ~3–10 ms after warm-up.
- Verified with `wrangler dev` (workerd), not just a browser.

The Astro frontend is at `apps/web/src/pages/blog.astro` and renders the posts and images at `http://localhost:4321/blog`. It includes a publish form that writes to D1 and uploads images to R2 through the C# API.

## Run the demo

```bash
# terminal 1 — C# Worker API (D1 + R2)
cd spikes/netwasm-worker
bunx wrangler@latest dev --port 8788 --local

# terminal 2 — Astro site
cd apps/web
bun run dev:bare --port 4321
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

D1 table:

```bash
bunx wrangler@latest d1 execute DB --local --command \
  "create table if not exists posts (
     id text primary key,
     title text not null,
     content text not null,
     image_key text,
     created_at text not null
   );"
```

`wrangler.jsonc` binds `DB` to a local D1 database and `BUCKET` to a local R2 bucket. The Worker exposes `globalThis.__REPTAR_DB_QUERY`, `globalThis.__REPTAR_R2_PUT`, and `globalThis.__REPTAR_RESOLVE`; `app.host.mjs` forwards them to the C# `query`/`r2put`/`resolve` imports.

## Reproduce the C# module

```bash
dotnet new install NetWasm.Templates@0.10.1
dotnet new netwasm-app -n HelloReptar --host page
# replace Program.cs and app.host.mjs with the versions in this spike
dotnet publish -c Release

cp -R bin/Release/netwasm0.1/publish/browser ./public/browser
python3 patch-host.py
bunx wrangler@latest dev --port 8788 --local
```

## Why the host is patched

The stock NetWasm host is a **browser** host. Four things break on Workers:

1. **`location` is not available at module evaluation.** The manifest URL must be supplied at request time (`globalThis.__REPTAR_MANIFEST_URL`).
2. **`WebAssembly.compile(bytes)` is disallowed.** Workers require a statically imported `WebAssembly.Module`; the spike imports the `.wasm` through Wrangler's `CompiledWasm` rule and passes it in (`globalThis.__REPTAR_WASM_MODULE`).
3. **`URL.createObjectURL` is not implemented.** The raw adapter JS module cannot be imported from a blob URL; it is imported statically and returned as a plain module object.
4. **The adapter verifier expects exactly `createAdapter` and `rawAdapterMetadata`.** A bundled module namespace has extra keys; the spike returns a plain object with only those two.

These are the four responsibilities of `@reptar/host`, the real Worker shim. The patched files here are a throwaway proof, not the product.

## NetWasm async-export limitation

NetWasm rejects `[JSExport]` methods returning `Task<string>` (async exports only support `void` or primitive scalars). The spike therefore uses the host-driven pattern the real framework will use:

1. C# `[JSExport]` starts async work and returns `void`.
2. C# awaits a `[JSImport]` (`query`, `r2put`), which returns a JS Promise.
3. When the managed task completes, C# calls `[JSImport] resolve(result)`.
4. The Worker awaits a JS Promise resolved by `resolve` and writes the response.

This keeps the single-reactor model intact and lets the host own all I/O.

## Next

- Productize `@reptar/host` so NetWasm output is wired automatically (no string patching).
- Move the blog's `schema/` + `features/` onto the Wasm target.
- Deploy the Worker + site with Alchemy.
