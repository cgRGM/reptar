# Reptar

> **Reptar is the backend. Full stop.**

A C# web framework that compiles to a minimal WebAssembly module and runs on Cloudflare Workers — with Hono-like routing, Drizzle-like schema, Zod-like validation, built-in auth, first-class Cloudflare bindings, and a generated typed client for any frontend.

Read the canonical [design.md](./design.md) for architectural specifications, non-goals, and roadmap.

---

## The One Rule

- **Zero frontend opinions:** No UI kit, no frontend router, no styling in the framework.
- **Generated contracts only:** The only frontend-facing surface is generated contracts: a typed `fetch` client, TypeScript types, Standard Schema validators, and OpenAPI specifications.
- **Backend infrastructure:** The TypeScript host shim is backend infrastructure — generated, versioned, and deployed alongside the C#.

---

## Repository Layout

This repository contains Reptar's C# framework, reference backend, marketing site, documentation site, and deployment infrastructure:

```text
reptar/
├── apps/
│   ├── web/         # Landing page (Astro + Tailwind 4)
│   └── docs/        # Documentation site (Astro Starlight)
├── framework/
│   ├── Reptar/          # C# core: router, Ctx, validation, data, auth, codegen
│   └── Reptar.Kestrel/  # Optional local dev host (ASP.NET is not a runtime dependency)
├── examples/
│   └── blog/        # File-based C# reference app (no .csproj)
├── packages/
│   ├── rpc/         # @reptar/client runtime for generated contracts
│   ├── host/        # @reptar/host Worker shim (NetWasm host + D1 bridge)
│   ├── infra/       # Alchemy infrastructure (Cloudflare Worker website deployments)
│   └── config/      # Shared TypeScript configuration
├── spikes/
│   └── netwasm-worker/  # Phase 0 proof: C# Wasm + D1 inside workerd
├── scripts/
│   └── reptar.ts    # Bun CLI: dev, codegen, test, build
├── design.md        # Canonical design specification and roadmap
└── frontend.md      # Website and documentation brief
```

## Backend (Reptar)

The backend is a file-based C# app at `examples/blog/app.cs` with **no app `.csproj`**. The Bun CLI hides the build toolchain; on first run it installs the pinned .NET SDK into `.reptar/` (build tool only — never shipped).

```bash
bun api:dev       # run the blog API on http://localhost:8787
bun rpc:gen       # regenerate examples/blog/rpc contracts from the C# route tree
bun rpc:check     # typecheck the generated client
bun api:test      # run the C# framework tests
```

Framework code lives in `framework/Reptar` (core, data, auth, codegen) and `framework/Reptar.Kestrel` (optional dev host). Generated contracts are committed under `examples/blog/rpc`.

**Phase 0 status:** NetWasm compiles the C# module to a 26 KB `.wasm`; it runs inside `wrangler dev` (workerd) and reads real D1 rows. See `spikes/netwasm-worker/README.md`. `packages/host` is the start of the reusable Worker shim.

### Blog demo

The Astro site has a working `/blog` page powered by the C# Worker:

```bash
bun run dev
```

This starts the web app on `:4321`, the docs site on `:4322`, and the C# Worker API on `:8788` (D1 + R2). Open `http://localhost:4321/blog`. Posts are stored in D1, cover images in R2, and the page includes a publish form that writes through the C# API.

Prefer separate terminals? Use `bun run dev:web`, `bun run dev:docs`, and `bun run dev:api`.

---

## Local Development

Install dependencies:

```bash
bun install
```

Start development servers across apps:

```bash
bun run dev
```

- **Web Landing Page:** [http://localhost:4321](http://localhost:4321)
- **Documentation Site:** [http://localhost:4322](http://localhost:4322) (via `cd apps/docs && bun run dev`)

To check types across all workspaces:

```bash
bun run check-types
```

To run linting and code formatting:

```bash
bun run check
```

To auto-fix formatting issues:

```bash
bun run fix
```

---

## Deployment with Alchemy

Infrastructure is defined in `packages/infra/alchemy.run.ts` and deployed using Alchemy to Cloudflare:

```bash
# Deploy staging / dev stage
bun run deploy

# Deploy to production stage
cd packages/infra && bunx alchemy deploy --stage production
```

---

## License

MIT &copy; Rocktown Labs.
