# frontend.md — Reptar website brief

This repo contains **Reptar's own website**, not a sample app: a marketing landing page and the docs site for a C# web framework that runs on Cloudflare Workers.

**Before writing any copy, the README, or landing page content: read `design.md` at the repo root.** It is the source of truth for what Reptar is, does, and promises. Every claim on the site must come from it — never invent benchmarks, features, or comparisons.

## What's here (scaffolded with better-t-stack)

```text
apps/web/     # Astro + Tailwind 4 — the landing page (this is "the page")
  src/pages/index.astro       # the landing page (only page needed)
  src/components/Header.astro
  src/layouts/Layout.astro
  src/styles/global.css
apps/docs/    # Starlight (Astro) — the docs site
  src/content/docs/
    index.mdx                 # docs home
    guides/example.md         # placeholder structure
    reference/example.md
packages/infra/ # Alchemy — deploys apps/web via Cloudflare.Website.Astro
```

Dev: `bun run dev` → http://localhost:4321. Deploy: `bun run deploy` (Alchemy).

## Style direction — dev-tool landing page for a web framework

Dark-first, technical, code-forward. The aesthetic lane is Hono / Cloudflare Workers / Vercel developer-tool landing pages — not a generic SaaS template.

- **Dark by default**, high contrast, near-black background (`zinc-950`/`neutral-950` territory).
- **Reptar green accent** (Reptar is a green dinosaur — own it, keep it tasteful; use for CTAs, code highlights, logo). Fun name, serious tool.
- **Monospace for code** — code samples are the hero content, not decoration.
- Terminal-adjacent details are welcome (prompt cursors, `$` prefixes, keycaps) but don't overdo it.
- Tailwind 4 is already wired (`@tailwindcss/vite`); extend `global.css` with a `@theme` for the brand colors and fonts.

## Landing page scope — one page, `apps/web/src/pages/index.astro`

1. **Hero:** name, tagline from `design.md` (e.g. "The backend, in C#. On Cloudflare Workers."), CTAs → **Get started** (docs) and **GitHub**.
2. **"What you write / what you get"** — the signature section: side-by-side code blocks showing one C# feature file (`Reptar().Get(...).Post(...)` with `Rule<T>`) and the frontend calling it (`api.posts.$post({ json: ... })`). Pull exact code from `design.md` §4.3 and §8.
3. **Architecture strip:** the small diagram from `design.md` §3 (frontend → TS shim → `reptar.wasm` → Cloudflare).
4. **Feature grid:** schema-as-source-of-truth, built-in validation, built-in auth, D1/R2/Workers AI bindings, generated typed client (Standard Schema), migrations, telemetry, no .NET runtime in production.
5. **Footer:** docs link, GitHub, "built with C# + Wasm on Cloudflare Workers."

## Copy rules

- Tone: confident, plain, a little playful ("Reptar is the backend."), zero enterprise-speak.
- Only promise what `design.md` states (e.g. Node/Hono parity is the _target_, not a measured claim yet — see §12).
- Docs live at `apps/docs` (Starlight) — link there, don't duplicate long-form content on the landing page.

## README rule

`README.md` is currently the generic better-t-stack scaffold output. **Rewrite it by reading `design.md`**: what Reptar is, the one rule ("the backend"), quick-start intent, repo layout (apps/web, apps/docs, packages/infra), and pointers to design.md and the docs site.

## Note on shadcn/ui

Reptar itself has zero frontend opinions. The shadcn/ui connection is only this: `create-reptar` (the future scaffolder) will optionally generate a Turborepo with a shadcn/ui frontend — login pages already wired to Reptar's built-in auth. If the landing page mentions it, it's a "starter" bullet under usage modes — never a framework dependency.
