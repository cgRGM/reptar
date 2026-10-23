# NetWasm → Cloudflare Worker spike

Proves that C# compiled by NetWasm can run inside Cloudflare's workerd runtime.

## Result

- `HelloReptar.wasm`: **26 KB** (raw core module + runtime; `du` of the full browser host directory is 676 KB).
- `GET /greet?name=Cloudflare` → `Hello, Cloudflare, from C#.`
- Local response time: ~3–5 ms after warm-up.
- Verified with `wrangler dev` (workerd), not just a browser.
- **D1 bridge proven:** `GET /db?sql=select * from posts` → `[{"id":"1","title":"Hello D1"}]`. C# calls `[JSImport("query", "app.host")]`, the Worker runs `env.DB.prepare(sql).all()`, and C# resolves the response through a callback.

### D1 setup

```bash
bunx wrangler@latest d1 execute DB --local --command \
  "create table if not exists posts (id text primary key, title text);
   insert into posts values ('1','Hello D1');"
```

`wrangler.jsonc` binds `DB` to a local D1 database. The Worker exposes `globalThis.__REPTAR_DB_QUERY` and `globalThis.__REPTAR_DB_RESOLVE`; `app.host.mjs` forwards both to the C# `query`/`resolve` imports.

## Reproduce

```bash
dotnet new install NetWasm.Templates@0.10.1
dotnet new netwasm-app -n HelloReptar --host page
# edit app.host.mjs:
#   export function show(message) { console.log(message); }
dotnet publish -c Release

cp -R bin/Release/netwasm0.1/publish/browser ./public/browser
python3 patch-host.py
bunx wrangler@latest dev --port 8788 --local

curl 'http://localhost:8788/greet?name=Cloudflare'
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

1. C# `[JSExport] Handle(...)` starts async work and returns `void`.
2. C# awaits a `[JSImport]` (`query`), which returns a JS Promise.
3. When the managed task completes, C# calls `[JSImport] resolve(result)`.
4. The Worker awaits a JS Promise resolved by `resolve` and writes the response.

This keeps the single-reactor model intact and lets the host own all I/O.

## Next

- Productize `@reptar/host` so NetWasm output is wired automatically (no string patching).
- Move the blog's `schema/` + `features/` onto the Wasm target.
- Deploy the Worker with Alchemy.
