# Plan: Scaffolding, Frontend Wiring, and C# Auth (OAuth + Better Auth interop)

Status: **proposal — no code changed yet** Scope: `create-reptar`, `reptar` CLI, generated project shape, frontend adapters, C# OAuth providers, Better Auth HTTP compatibility, docs.

> **Implementation gate:** the two benchmark agents are still working. Do **not** start structural changes (framework seams, auth refactor, CLI packages, templates) until they are done. Safe work in the meantime: this plan, provider data research, shadcn registry item drafts, and docs copy.

Related: `design.md` §6 (auth), §8 (codegen), §11 (usage modes), §14 (roadmap), §15 (layout), §16 (status).

> Note: the three pasted benchmark/VPS snippets did not come through in my context. This plan is based on the repo state. If those snippets contain decisions that change the scaffolder (e.g. a fixed project layout), send them again.

---

## 0. TL;DR

We should ship **two small CLIs** and **one auth story**:

1. `create-reptar` — a Better-T-Stack-shaped scaffolder that emits a Turborepo with:
   - `apps/api` — file-based C# app (`app.cs` + `schema/` + `features/`), no app `.csproj`
   - `apps/worker` — generated NetWasm/Workers host (only for the Cloudflare target)
   - `apps/web` — Astro or TanStack Start, wired to `@reptar/client` + `@reptar/rpc`
   - `packages/rpc` — generated `AppType`, DTOs, Standard Schema, OpenAPI
   - `packages/infra` — Alchemy (optional)
   - `reptar.jsonc` — project metadata for `reptar add`
2. `reptar` — the project-local CLI: `dev`, `codegen`, `db generate|push|apply`, `add`, `doctor`.
3. Auth stays **C#-native**, with two frontend paths over the same C# routes and DB:
   - **Built-in (default):** email/password, sessions, anonymous, JWT, roles. Frontend uses the typed `@reptar/client` helpers.
   - **Better Auth compat (first-class option):** mount `/api/auth/*` so `better-auth/client` works against the C# backend. Useful for its frontend hooks/UI and as a migration path. No Better Auth server code, no Better Auth DB schema, no TS mount in the shim.
   - New C# OAuth slice: provider registry, state + PKCE, token exchange, userinfo, account linking. Google first (the explicit ask), then GitHub/Microsoft and the rest later.
   - Better Auth **plugins** (organization, 2FA, passkey, magic link, API keys) are not free with the compat surface — they need C# endpoint ports. Those are future work, not v1.

The default frontend calls the API through a **same-origin proxy** (`/api` → API Worker or `localhost:8787`). This avoids `SameSite`/third-party-cookie pain and makes `better-auth/client` work without special cookie config.

The biggest external dependency is **NuGet packaging**: generated projects must reference `Reptar` / `Reptar.Wasm` as packages, not vendored source. A one-day spike should validate this before we build the templates.

---

## 1. What exists today (verified)

### 1.1 Backend

- `framework/Reptar` — router, `Ctx`, `Rule<T>`, `TableDef`, SQLite/Postgres clients, auth, Stripe, codegen.
- `framework/Reptar.Wasm` — compiles the same core sources for `netwasm0.1` (source includes, not a project reference).
- `framework/Reptar.Kestrel` — `app.RunAsync()` for `net10.0` (port `8787`).
- `framework/Reptar.Lambda` — Lambda runtime host.
- `examples/blog` — canonical reference app (`app.cs`, `schema/`, `features/`, generated `rpc/`).
- `examples/campfire` — realtime example.
- `examples/realworld` — spec implementation.

### 1.2 CLI

`scripts/reptar.ts` is **repo-internal only**. It supports `dev`, `codegen`, `test`, `build`, `build:worker`, `build:realworld`, `realworld:dev`. It installs the .NET SDK into `.reptar/` when missing and runs file-based apps.

There is **no** published `reptar` package, **no** `create-reptar` package, and **no** `db` command. The docs already promise all three.

### 1.3 Contracts

- `framework/Reptar/Codegen/RpcGenerator.cs` emits `app-type.d.ts`, `types.ts`, `schemas.ts`, `openapi.json`.
- `scripts/reptar.ts codegen` runs the blog app with `--codegen --out examples/blog/rpc`, then copies into `packages/rpc/src` and formats.
- `@reptar/client` is the ~100-line proxy runtime. `packages/rpc` is the generated contract package.
- Route metadata is `RouteMeta` (`Method`, `Path`, `InputType`, `Produces`, `Validator`). There is no way to mark a route as browser-only/non-client.

### 1.4 Auth

`framework/Reptar/Auth/Auth.cs`:

- Tables: `users` (`id, email, name, password_hash, is_anonymous, role, created_at`), `sessions` (`id, user_id, token_hash, expires_at, created_at`).
- **There is a second implementation** at `framework/Reptar.Wasm/Auth/Auth.cs` (positional SQL + explicit column aliases, because D1 returns snake_case JSON while the portable SQLite client normalizes rows). Any auth change currently has to be made twice. This is the single biggest refactor prerequisite for OAuth + Better Auth compat (see C13).
- Routes mounted at any prefix: `POST /signup`, `POST /signin`, `POST /signout`, `GET /session`, `POST /anonymous`.
- `Auth.RequireUser`, `Auth.OptionalUser`, `Auth.RequireJwt`.
- PBKDF2 (100k iterations) via `ICrypto`; docs still say scrypt.
- Cookie: hardcoded `HttpOnly; SameSite=Lax`, no `Secure` option.
- No OAuth, no accounts table, no account linking, no email verification.

`AuthOptions` (in `framework/Reptar/Core/ReptarApi.cs`): `SessionDays`, `CookieName`, `CookieDomain`, `AllowAnonymous`, `BootstrapAdmins`, `Jwt`.

### 1.5 Frontend

- `apps/web` — Astro + Tailwind 4, `/login` and `/blog`.
- The login page fetches the API **cross-origin** with `credentials: "include"`, while the cookie is `SameSite=Lax`. Cross-site cookies will not be sent in production (`*.workers.dev` subdomains are separate sites). This is likely broken today.
- The blog page uses a Cloudflare service binding for SSR but direct `fetch` for client-side mutations.
- `apps/web` defaults to `http://localhost:8788`; the Kestrel app defaults to `8787`. Local login only works if `PUBLIC_API_URL` is set.

### 1.6 Docs drift (fix as part of this work)

| Claim | Reality |
| --- | --- |
| `npm create reptar@latest` | No package exists |
| `reptar db generate/push/apply` | No `db` command; `EnsureCreatedAsync` only |
| `apps/worker/` generated TS shim | Only spikes have this; blog uses Kestrel |
| `packages/contract/reptar.spec.json` | Does not exist; OpenAPI lands in `packages/rpc/openapi.json` |
| `Auth = BetterAuth()` / `Clerk()` adapters | Not implemented |
| "scrypt" | PBKDF2 |
| Standard Schema "planned" (`client/codegen.mdx`) | `schemas.ts` is already generated (`core/validation.md` says so) |
| `VITE_API_URL` | Astro uses `PUBLIC_API_URL` |
| Kestrel `8787` | Login page defaults to `8788` |

---

## 2. Decisions (the "not hacky" contract)

| # | Decision | Why |
| --- | --- | --- |
| D1 | Two CLIs: `create-reptar` (scaffold once) and `reptar` (project-local dev). Shared private `@reptar/cli-core`. | Mirrors Better-T-Stack; keeps the generated project's dev dependency small. |
| D2 | Generated apps reference **NuGet** `Reptar`, `Reptar.Kestrel`, `Reptar.Wasm`; `--framework local` exists only for repo/CI testing. | No vendored framework source, no git submodules. |
| D3 | Generated backend is **composition + hosts**: `app.config.cs` returns `ReptarApp`; `app.cs` runs Kestrel; `apps/worker/csharp/Program.cs` runs the WASM export. | One app source, two hosts, codegen runs the portable host. |
| D4 | Frontends call **same-origin `/api`** via a generated proxy route. Direct cross-origin API URLs are opt-in. | Cookies work; Better Auth client works; no third-party-cookie cliff. |
| D5 | v1 frontends: **Astro** and **TanStack Start**. **No Next.js** (Cloudflare adapter + extra React framework is not worth it). SvelteKit/Nuxt/Solid/Expo are additive later. | Repo already uses Astro; TanStack Start is the BTS default; Next is explicitly out. |
| D6 | **Built-in auth remains the default**; Better Auth compat is a first-class `--auth better-auth` option. Both use the same C# routes/DB. | Built-in is the framework-native path and needs no extra frontend dependency. Better Auth is there when you want its client hooks/UI. The scaffold prompt explains the tradeoff. |
| D6b | The Better Auth compat surface covers **core email/password + social** only. Its plugins are **C# ports**, not automatic. | The client plugin calls extra endpoints; without C# handlers they 404. Be honest in the docs. |
| D7 | Better Auth support is an **HTTP compatibility surface**, not a DB adapter or TS mount. | `better-auth/client` only needs endpoints + cookies. No schema fork, no second auth implementation. |
| D8 | OAuth is implemented **in C#**, with a data-driven provider registry. **Google first**, then GitHub/Microsoft and the rest later. | The explicit ask; provider data is easy to extend; no Node in the request path. |
| D9 | Account linking is **email-verified + trusted-provider only**. Otherwise require an explicit link flow. | Prevents account takeover via unverified provider email. |
| D10 | UI: **plain Tailwind and shadcn** are both supported. shadcn auth pages are distributed as a **Reptar registry item**; we only ship sign-in/sign-up/social/user-menu, not app UI. | shadcn is the industry standard and already has the form primitives; the registry avoids duplicating UI in templates. |
| D13 | **Session token is never returned in JSON.** HttpOnly cookie only; signed cookies optional; `get-session` omits `token`. | Most secure default; `better-auth/client` core does not need it. |
| D14 | **No structural work until the benchmark agents finish.** | Avoids merge conflicts in `framework/`, `scripts/`, and `packages/host`. |
| D11 | Migrations v1 = `CREATE TABLE IF NOT EXISTS` + indexes, generated from `TableDef`; hand-written SQL for changes. | Matches `design.md` §9 v1 scope; makes `db generate/push` real. |
| D12 | Generated project writes `reptar.jsonc` with the reproducible config. `reptar add` reads it. | Better-T-Stack pattern; cheap and useful for docs/CI. |

---

## 3. Target generated project

### 3.1 Directory shape

```text
my-app/
├── apps/
│   ├── api/
│   │   ├── app.cs                 # portable entry (Kestrel)
│   │   ├── app.config.cs          # public static class App.Build(AppPlatform)
│   │   ├── schema/
│   │   │   ├── posts.cs
│   │   │   └── index.cs
│   │   ├── features/
│   │   │   ├── posts.cs
│   │   │   └── profile.cs
│   │   └── migrations/            # generated SQL (reptar db generate)
│   ├── worker/                    # Cloudflare target only
│   │   ├── csharp/
│   │   │   ├── Worker.csproj      # netwasm0.1, includes ../api sources
│   │   │   └── Program.cs         # [JSExport] handle
│   │   ├── public/browser/        # published wasm artifacts
│   │   ├── src/index.ts           # createReptarHost wiring
│   │   ├── wrangler.jsonc
│   │   └── package.json
│   └── web/                       # Astro or TanStack Start
│       ├── src/lib/api.ts
│       ├── src/lib/auth.ts
│       ├── src/pages/api/[...path].ts   # same-origin proxy
│       └── ...
├── packages/
│   ├── rpc/                       # generated: AppType, types, schemas, openapi
│   ├── infra/                     # optional Alchemy stack
│   └── config/                    # shared tsconfig
├── package.json
├── turbo.json
├── tsconfig.json
├── reptar.jsonc
├── .env.example
├── .gitignore
└── README.md
```

### 3.2 Backend shape

`apps/api/app.config.cs` is the single composition file. Both hosts compile it.

```csharp
global using Reptar;
global using static Reptar.ReptarApi;

public sealed record AppPlatform
{
    public required IDbClient Database { get; init; }
    public string? PublicBaseUrl { get; init; }   // browser-facing origin (frontend/proxy)
    public string[] CorsOrigins { get; init; } = [];
    public IR2Client? Storage { get; init; }
    public IHttpClient? Http { get; init; }
    public IEnvironment? Environment { get; init; }
    public ICrypto? Crypto { get; init; }
}

public static class App
{
    public static ReptarApp Build(AppPlatform platform)
    {
        var app = ReptarApp.Create(new ReptarConfig
        {
            Auth = BuiltIn(new AuthOptions
            {
                SessionDays = 30,
                OAuth = new OAuthOptions
                {
                    PublicBaseUrl = platform.PublicBaseUrl ?? "",
                    PublicCallbackPath = "/api/auth/oauth",
                    StateSecret = new SecretRef(null, "OAUTH_STATE_SECRET"),
                    Providers =
                    [
                        OAuthProviders.Google(
                            new SecretRef(null, "GOOGLE_CLIENT_ID"),
                            new SecretRef(null, "GOOGLE_CLIENT_SECRET")),
                        OAuthProviders.GitHub(
                            new SecretRef(null, "GITHUB_CLIENT_ID"),
                            new SecretRef(null, "GITHUB_CLIENT_SECRET")),
                    ],
                },
            }),
            Crypto = platform.Crypto,
            Database = platform.Database,
            Environment = platform.Environment,
            Http = platform.Http,
            Storage = platform.Storage,
        });

        app.Use(RequestId());
        app.Use(Telemetry());
        app.Use(Cors(platform.CorsOrigins));   // empty = same-origin only

        app.RegisterSchema(Schema.All);
        app.Route("/auth", Auth.Routes());
        app.Route("/posts", Posts.Routes());
        app.Route("/profile", Profile.Routes());

        return app;
    }
}
```

`apps/api/app.cs`:

```csharp
#:project ../../framework/Reptar/Reptar.csproj        // published: #:package Reptar@0.1.0
#:project ../../framework/Reptar.Kestrel/Reptar.Kestrel.csproj
#:include app.config.cs
#:include schema/**/*.cs
#:include features/**/*.cs

global using Reptar.Hosting;

var app = App.Build(new AppPlatform
{
    Database = Db.Sqlite("app.db"),
    PublicBaseUrl = "http://localhost:4321",   // the web app / proxy origin
    CorsOrigins = ["http://localhost:4321"],   // only needed for direct cross-origin calls
    Storage = new LocalR2Client("uploads"),    // generated local helper; omit for no storage
});

await app.RunAsync(args);
```

`apps/worker/csharp/Program.cs`:

```csharp
var app = App.Build(new AppPlatform
{
    Database = new D1DbClient(json),
    Storage = new R2Client(),
    Http = new WasmHttpClient(),
    Environment = new WasmEnvironment(),
});
```

`Worker.csproj` includes `../../api/app.config.cs`, `../../api/schema/**/*.cs`, `../../api/features/**/*.cs` — not `app.cs`.

Why this split: `dotnet run app.cs` is the portable/codegen host; NetWasm needs a `.csproj`; both compile the same composition and feature files. The file-based app must live **outside** the `.csproj` directory tree (Microsoft's guidance), so `apps/api` and `apps/worker/csharp` are siblings.

### 3.3 Frontend shape

`src/lib/api.ts`:

```ts
import { createClient } from "@reptar/client";
import type { AppType } from "@reptar/rpc";

export const api = createClient<AppType>(
  import.meta.env.PUBLIC_API_URL ?? "/api",
  { credentials: "include" }
);
```

`src/lib/auth.ts` — **Better Auth variant (`--auth better-auth`)**:

```ts
import { createAuthClient } from "better-auth/client";

export const authClient = createAuthClient({
  baseURL: "/api/auth",
});
```

`src/lib/auth.ts` — **built-in default (`--auth builtin`)**:

```ts
import { api } from "./api";

export const auth = {
  session: () => api.auth.session.$get(),
  signIn: (input: { email: string; password: string }) =>
    api.auth.signin.$post({ json: input }),
  signUp: (input: { email: string; password: string; name: string }) =>
    api.auth.signup.$post({ json: input }),
  signOut: () => api.auth.signout.$post(),
  social: (provider: "google" | "github") => {
    window.location.assign(`/api/auth/oauth/${provider}?redirect=/`);
  },
};
```

Both variants use the same C# routes and cookie; only the frontend helper changes.

Same-origin proxy (Astro):

```ts
// src/pages/api/[...path].ts
import type { APIRoute } from "astro";

const API_URL = import.meta.env.API_URL ?? "http://localhost:8787";

export const ALL: APIRoute = async ({ request, params }) => {
  const incoming = new URL(request.url);
  const target = new URL(`/${params.path ?? ""}${incoming.search}`, API_URL);
  const headers = new Headers(request.headers);
  headers.delete("host");

  const response = await fetch(target, {
    body:
      request.method === "GET" || request.method === "HEAD"
        ? undefined
        : request.body,
    headers,
    method: request.method,
    redirect: "manual",
  });

  return new Response(response.body, {
    headers: response.headers,
    status: response.status,
  });
};
```

TanStack Start gets the same thing as a server route (`src/routes/api/$.ts`).

Proxy gotchas to bake into the template:

- On Node/Undici, `Set-Cookie` must be copied with `response.headers.getSetCookie()` and appended one by one; passing a combined `Headers` object can collapse multiple cookies. On Workers, passing `response.headers` is fine.
- Streaming request bodies may need `duplex: "half"` in Node.
- Never forward the incoming `host` header; let `fetch` set it for the API origin.
- Use `redirect: "manual"` so OAuth 302s and API redirects reach the browser instead of being followed server-side.
- The proxy must forward `Cookie` and `Authorization` headers and return the API's `Set-Cookie` headers untouched.

**OAuth callback and cookies (important).** The provider must redirect back through the **frontend origin**, not directly to the API origin. Otherwise the API sets the session cookie on the API origin and the frontend proxy never sees it.

- `OAuthOptions.PublicBaseUrl` = browser-facing origin (`http://localhost:4321` in dev, `https://app.example.com` in prod).
- `OAuthOptions.PublicCallbackPath` = `/api/auth/oauth` (the proxied browser path).
- The provider redirect URI is `{PublicBaseUrl}{PublicCallbackPath}/{provider}/callback`, e.g. `https://app.example.com/api/auth/oauth/google/callback`.
- The proxy forwards that request to the API as `/auth/oauth/google/callback`; the API's `Set-Cookie` flows back through the proxy and is stored on the frontend origin.
- For a direct-API deployment (no proxy), set `PublicBaseUrl` to the API origin and `PublicCallbackPath` to `/auth/oauth`.

Register the **frontend** callback URL in the Google/GitHub console, not the API Worker URL.

### 3.4 Generated `package.json` scripts

```json
{
  "scripts": {
    "dev": "turbo run dev",
    "dev:api": "reptar dev",
    "dev:web": "turbo run dev -F web",
    "build": "turbo run build",
    "codegen": "reptar codegen",
    "db:generate": "reptar db generate",
    "db:push": "reptar db push",
    "db:apply": "reptar db apply",
    "check-types": "turbo run check-types",
    "check": "ultracite check",
    "fix": "ultracite fix",
    "deploy": "cd packages/infra && bun run deploy"
  }
}
```

### 3.5 `reptar.jsonc`

```jsonc
{
  "$schema": "https://reptar.dev/schema.json",
  "version": "0.1.0",
  "createdAt": "2026-10-24T00:00:00.000Z",
  "reproducibleCommand": "bun create reptar@latest . --target cloudflare --db d1 --frontend astro --auth builtin --providers google,github --addons turborepo ultracite lefthook --pm bun",
  "target": "cloudflare",
  "database": "d1",
  "frontend": ["astro"],
  "auth": {
    "builtin": true,
    "providers": ["google", "github"],
    "compat": false,
  },
  "addons": ["turborepo", "ultracite", "lefthook"],
  "packageManager": "bun",
  "framework": "published",
}
```

---

## 4. Workstream A — `create-reptar` + `reptar` CLI

### 4.1 Package layout

```text
packages/
├── cli-core/                  # private, bundled into both CLIs
│   ├── dotnet.ts              # SDK detect/install, dotnet run/build/publish
│   ├── config.ts              # ProjectConfig, validation, reptar.jsonc
│   ├── files.ts               # FileOp[], safe writer, template render
│   ├── prompts.ts             # @clack/prompts helpers
│   └── project.ts             # detect project root / config
├── create-reptar/
│   ├── src/cli.ts             # bin: create-reptar
│   ├── src/plan.ts            # config -> FileOp[]
│   ├── src/fragments/         # backend, worker, frontend, auth, deploy, addons
│   └── templates/             # static assets (frontend pages, Dockerfile)
└── reptar-cli/
    ├── src/cli.ts             # bin: reptar
    ├── src/commands/dev.ts
    ├── src/commands/codegen.ts
    ├── src/commands/db.ts
    ├── src/commands/add.ts
    └── src/commands/doctor.ts
```

Both are built with `bun build --target=node` (or `tsdown` like BTS) and publish compiled `dist`. `bun create reptar@latest` resolves `create-reptar`; `bunx reptar` resolves `reptar`.

### 4.2 `create-reptar` flags

```text
bun create reptar@latest [dir]

  --target cloudflare|vps|lambda        (default: cloudflare)
  --db d1|sqlite|postgres               (default: d1 for cloudflare, sqlite for vps)
  --frontend astro|tanstack-start|none  (default: astro)
  --auth none|builtin|better-auth        (default: builtin; better-auth for the client hooks/UI)
  --providers google,github,microsoft   (default: google when auth is enabled; empty for none)
  --ui tailwind|shadcn                  (default: shadcn for TanStack Start, tailwind for Astro)
  --addons turborepo,ultracite,lefthook,docs
  --pm bun|pnpm|npm|yarn
  --install / --no-install
  --git / --no-git
  --framework published|local
  --yes                                 (non-interactive)
```

Interactive prompts when flags are omitted. `--yes` uses defaults for CI.

### 4.3 Template matrix (v1)

| Target | Backend | Database | Worker | Frontend |
| --- | --- | --- | --- | --- |
| `cloudflare` | `apps/api` + `apps/worker` | D1 (+ SQLite local) | NetWasm + `@reptar/host` | Astro/TanStack Start |
| `vps` | `apps/api` only | Postgres or SQLite | — | Astro/TanStack Start |
| `lambda` | `apps/api` + Lambda host | Postgres/SQLite | — | none in v1 |

Auth matrix:

| `--auth` | Backend | Frontend |
| --- | --- | --- |
| `none` | no `Auth` mount | no auth pages |
| `builtin` **(default)** | `BuiltIn()` + `/auth` routes + optional OAuth providers | typed `@reptar/client` helpers; shadcn/Tailwind auth pages |
| `better-auth` | builtin + `Auth.BetterAuthRoutes()` at `/auth` (browser: `/api/auth`) + optional OAuth providers | `better-auth/client` hooks; shadcn/Tailwind auth pages |

`--providers` is independent of `--auth`: either mode can expose Google/GitHub/etc. through the same C# OAuth slice.

### 4.4 `reptar` CLI commands

```text
reptar dev                 # run apps/api/app.cs with Kestrel on :8787
reptar codegen             # run app.cs --codegen --out packages/rpc/src
reptar codegen --check     # fail if generated files are stale
reptar db generate         # emit migrations/0000_init.sql from TableDefs
reptar db push             # apply DDL to local SQLite
reptar db apply            # wrangler d1 migrations apply <db> --remote
reptar build               # publish Native AOT binary
reptar build:worker        # publish netwasm0.1 + patch host + copy artifacts
reptar add <addon>         # patch project from reptar.jsonc
reptar doctor              # check SDK, node, wrangler, config, generated files
```

Implementation notes:

- Port `scripts/reptar.ts`'s SDK detection/install into `cli-core/dotnet.ts`; keep `REPTAR_SKIP_SDK_CHECK` and `.reptar/` behavior.
- `codegen` runs the **portable** app (`apps/api/app.cs`) with `--codegen --out packages/rpc/src`, then formats with `oxfmt`. No copy step.
- `codegen --check` compares generated files to disk (hash or byte compare) and exits non-zero.
- `db generate` adds `--migrations --out apps/api/migrations` to the portable app. The C# side writes `0000_init.sql` and `meta/_journal.json`.
- `db push` adds `--db-push` to the portable app; it calls `EnsureCreatedAsync()` against the configured SQLite file.
- `db apply` shells to `wrangler d1 migrations apply <name> --remote` (requires `wrangler`; `doctor` checks it).

### 4.5 `reptar add`

v1 addons: `docs` (Starlight), `infra` (Alchemy), `lefthook`, `ultracite`. Each addon is a list of `FileOp`s + dependency edits. `add` reads `reptar.jsonc`, refuses to run on an unknown version, and is idempotent.

### 4.6 shadcn registry (auth UI)

We only ship **auth UI** — sign-in, sign-up, social buttons, user menu/session guard. No dashboard or CRUD screens. shadcn is the distribution mechanism, so the same files work in generated apps and in existing projects.

```text
registry/
├── registry.json
└── default/
    ├── auth-builtin/
    │   ├── auth-signin.tsx
    │   ├── auth-signup.tsx
    │   ├── auth-social.tsx
    │   └── lib/auth.ts          # @reptar/client helpers
    └── auth-better/
        ├── auth-signin.tsx
        ├── auth-signup.tsx
        ├── auth-social.tsx
        └── lib/auth.ts          # better-auth/client
```

`registry.json` items:

- `auth-builtin` — shadcn block wired to `@reptar/client` + `@reptar/rpc`.
- `auth-better` — shadcn block wired to `better-auth/client` (`baseURL: "/api/auth"`).
- Both depend on `button`, `input`, `label`, `card`, `field`, `separator`; `auth-better` also depends on `better-auth`.
- Optional `auth-guard` (client-side session check) and `user-menu` items.

Distribution:

- `bunx shadcn@latest build` emits `apps/web/public/r/*.json`; deploy with the web app so the registry lives at `https://reptar.dev/r/registry.json`.
- Users add it with `bunx shadcn@latest add https://reptar.dev/r/auth-better.json` (or a namespace in `components.json`).
- For `create-reptar --ui shadcn`, the CLI writes the same files from its embedded templates (no network dependency during scaffold). The registry is for existing apps and manual installs.
- Astro + shadcn needs `@astrojs/react` and React islands; TanStack Start is React-native and simpler. `--ui tailwind` avoids React entirely.

### 4.7 Packaging / release

Two registries are required, not one:

**NuGet (backend)**

- Add pack metadata to `framework/Reptar/Reptar.csproj`, `Reptar.Kestrel`, `Reptar.Lambda`, `Reptar.Wasm`.
- `Reptar.Wasm` must pack for `netwasm0.1`; confirm `dotnet pack` works with `NetWasm.Sdk` and that `NetWasm.System.Linq` / `NetWasm.System.Text.Json` flow as transitive dependencies.
- Publish to NuGet (or GitHub Packages during alpha) from a tag workflow.

**npm (frontend + worker)**

- Publish `@reptar/client` (the typed fetch runtime) and `@reptar/host` (the Worker shim + `patchNetWasmHost`) — both are currently `private: true`.
- `@reptar/rpc` is **generated per project** and should stay a workspace package in the generated monorepo, not a published package.
- `@reptar/adapter-lambda` if the Lambda target ships a JS wrapper.
- `create-reptar` pins `REPTAR_VERSION` (NuGet) and the matching npm versions; `--framework local` emits `#:project`/`ProjectReference` paths and workspace deps for repo/CI tests.
- Keep the C# and npm versions in lockstep (one release tag → both registries).

---

## 5. Workstream B — Auth in C#

### 5.1 Schema changes

Extend the built-in auth tables (all additive):

```csharp
// users
Col.Text("image"),
Col.Int("email_verified").NotNull().Default(0),
Col.Text("updated_at").DefaultSql("CURRENT_TIMESTAMP"),

// sessions
Col.Text("updated_at").DefaultSql("CURRENT_TIMESTAMP"),
Col.Text("ip_address"),
Col.Text("user_agent"),

// new: accounts (one row per provider identity)
public static readonly TableDef Accounts = DefineTable("accounts",
    Col.Text("id").PrimaryKey(),
    Col.Text("user_id").NotNull().References("users", "id", onDelete: "cascade").Indexed(),
    Col.Text("provider_id").NotNull().Indexed(),
    Col.Text("account_id").NotNull().Indexed(),
    Col.Text("email"),
    Col.Text("name"),
    Col.Text("image"),
    Col.Text("access_token"),
    Col.Text("refresh_token"),
    Col.Text("id_token"),
    Col.Text("access_token_expires_at"),
    Col.Text("refresh_token_expires_at"),
    Col.Text("scope"),
    Col.Text("created_at").DefaultSql("CURRENT_TIMESTAMP"),
    Col.Text("updated_at").DefaultSql("CURRENT_TIMESTAMP")
);
```

Add composite unique index support to `TableDef` (small seam):

```csharp
public TableDef UniqueIndex(params string[] columns);
// -> CREATE UNIQUE INDEX IF NOT EXISTS "accounts_provider_account_uq"
//    ON "accounts"("provider_id", "account_id");
```

`Auth.Users` / `Auth.Sessions` / `Auth.Accounts` remain public so apps can query them.

### 5.2 Auth file layout

```text
framework/Reptar/Auth/
├── Auth.cs                    # public API: options, tables, native routes, middleware
├── AuthOptions.cs             # moved out of ReptarApi.cs
├── AuthStore.cs               # internal: create user, find by email, start session, link
├── AuthCookies.cs             # cookie policy + signed compat cookies
├── OAuth/
│   ├── OAuthOptions.cs
│   ├── OAuthProvider.cs       # record + profile model
│   ├── OAuthProviders.cs      # Google, GitHub, Microsoft, Discord, LinkedIn, Slack, GitLab
│   ├── OAuthFlow.cs           # state, PKCE, token exchange, userinfo, callback
│   └── OAuthRoutes.cs         # GET /oauth/:provider, GET /oauth/:provider/callback, link/unlink
└── BetterAuth/
    ├── BetterAuthRoutes.cs    # /auth/* compat paths; browser sees /api/auth/*
    └── BetterAuthErrors.cs
```

`Auth.cs` keeps its public shape (`Auth.Routes()`, `Auth.RequireUser`, `Auth.CurrentUser`) so existing apps don't break. Internals delegate to `AuthStore`.

### 5.3 OAuth options

```csharp
public sealed class OAuthOptions
{
    // Browser-facing origin + path used to build the provider redirect_uri.
    // Must go through the frontend proxy so the session cookie lands on the
    // frontend origin. Direct-API deployments use the API origin + /auth/oauth.
    public string PublicBaseUrl { get; set; } = "";       // e.g. https://app.example.com
    public string PublicCallbackPath { get; set; } = "/api/auth/oauth";
    public string StateCookieName { get; set; } = "reptar_oauth";
    public int StateTtlSeconds { get; set; } = 600;
    public SecretRef StateSecret { get; set; }
    public List<OAuthProviderConfig> Providers { get; set; } = [];
    public string[] AllowedRedirectOrigins { get; set; } = [];
    public bool AllowAccountLinking { get; set; } = true;
    public string[] TrustedEmailProviders { get; set; } =
        ["google", "github", "microsoft", "linkedin", "slack", "gitlab"];
    public bool AllowUnlinkingLastAccount { get; set; }
}
```

`SecretRef` resolves at request time from `ctx.Environment`:

```csharp
public readonly record struct SecretRef(string? Value, string? EnvVar)
{
    public string? Resolve(IEnvironment env) =>
        Value ?? (EnvVar is null ? null : env.Get(EnvVar));
}
```

Why: on Workers, secrets are bindings, not `Environment.GetEnvironmentVariable`. Resolving per request keeps `Auth` static config free of build-time env reads.

### 5.4 Provider registry

`OAuthProvider` is data + two functions:

```csharp
public sealed record OAuthProviderConfig(
    OAuthProvider Provider,
    SecretRef ClientId,
    SecretRef ClientSecret,
    string[]? Scopes = null,
    Dictionary<string, string>? AuthorizationParams = null);

public sealed record OAuthTokens(
    string AccessToken,
    string? RefreshToken,
    string? IdToken,
    int? ExpiresIn,
    string? Scope);

public sealed record OAuthProfile(
    string AccountId,
    string? Email,
    bool EmailVerified,
    string? Name,
    string? Image,
    JsonElement Raw);

public sealed record OAuthProvider
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string AuthorizationEndpoint { get; init; }
    public required string TokenEndpoint { get; init; }
    public required string UserInfoEndpoint { get; init; }
    public string[] DefaultScopes { get; init; } = ["openid", "email", "profile"];
    public string ScopeSeparator { get; init; } = " ";
    public bool UsePkce { get; init; } = true;
    public bool UseBasicAuthForToken { get; init; }
    public Dictionary<string, string> AuthorizationParams { get; init; } = [];
    public Dictionary<string, string> UserInfoHeaders { get; init; } = [];
    public Func<JsonElement, OAuthProfile> MapProfile { get; init; }
    // Escape hatch for providers that need extra calls (GitHub /user/emails)
    // or return only an id_token (Apple). Default: GET UserInfoEndpoint.
    public Func<Ctx, OAuthProviderConfig, OAuthTokens, Task<OAuthProfile?>>? GetProfileAsync { get; init; }
}
```

Provider config:

```csharp
OAuthProviders.Google(
    clientId: new SecretRef(null, "GOOGLE_CLIENT_ID"),
    clientSecret: new SecretRef(null, "GOOGLE_CLIENT_SECRET"));
```

First-class v1:

| Provider | Scopes | Notes |
| --- | --- | --- |
| `google` | `openid email profile` | `access_type=offline`, optional `prompt`, `hd`; userinfo endpoint |
| `github` | `read:user user:email` | `Accept: application/json`; fetch `/user/emails` for private email; PKCE off |
| `microsoft` | `openid email profile` | tenant `common` by default; use `oid` as account id |
| `discord` | `identify email` | email may be absent for phone-only accounts |
| `linkedin` | `openid profile email` | OIDC userinfo |
| `slack` | `openid profile email` | OIDC userinfo |
| `gitlab` | `read_user` | `https://gitlab.com/api/v4/user` |

Deferred: Apple (ES256 client secret + ID-token-only profile), X/Twitter (PKCE + basic auth quirks), Facebook (no email verification signal), and the long tail. The registry makes them additive.

### 5.5 OAuth flow

**Start** — `GET /auth/oauth/:provider?redirect=/dashboard`

1. Resolve provider config + secrets from `ctx.Environment`.
2. Generate `state` (32 random bytes, base64url) and `code_verifier` (32 bytes, base64url).
3. Build the authorization URL:
   - `client_id`, `redirect_uri = {PublicBaseUrl}{PublicCallbackPath}/{provider}/callback`
   - `response_type=code`, `scope`, `state`
   - `code_challenge`, `code_challenge_method=S256` when `UsePkce`
   - provider extras (`access_type`, `prompt`, `hd`, `include_granted_scopes`, tenant)
4. Store a signed state cookie:
   ```json
   {"state":"...","verifier":"...","redirect":"/dashboard","provider":"google","intent":"signin","exp":...}
   ```
   `base64url(json) + "." + base64(HMAC-SHA256(...))`, `HttpOnly`, `SameSite=Lax`, `Secure` in prod, `Max-Age=600`.
5. `302 Location: <authorization URL>`.

**Callback** — `GET /auth/oauth/:provider/callback?code&state`

1. Read + verify the state cookie (signature, expiry, provider, constant-time state compare). Delete the cookie.
2. Exchange the code:
   - `POST` form-encoded to `TokenEndpoint`
   - `grant_type=authorization_code`, `code`, `redirect_uri`, `client_id`, `client_secret`, `code_verifier`
   - `client_secret_basic` when the provider requires it
3. Fetch the profile from `UserInfoEndpoint` with `Authorization: Bearer <access_token>` + provider headers. (No ID-token signature verification needed for v1; TLS + token exchange is the trust boundary.)
4. Map to `OAuthProfile(AccountId, Email, EmailVerified, Name, Image, Raw)`.
5. Resolve the user (see 5.6).
6. Start a Reptar session, set the session cookie.
7. `302` to the validated `redirect` (default `/`), or JSON `{ user }` when `Accept: application/json`.

**Link** — `POST /auth/oauth/:provider/link` (authenticated) uses the same flow with `intent=link`; the callback attaches the account to `ctx.User` instead of resolving by email.

**Unlink** — `POST /auth/oauth/:provider/unlink` (authenticated) deletes the account row. Refuse if the user has no password and no other account (`AllowUnlinkingLastAccount` overrides).

**Redirect safety** — `redirect` must be a relative path starting with `/` and not `//`, or an absolute URL whose origin is in `AllowedRedirectOrigins`. Otherwise use `/`.

### 5.6 Account linking rules

On callback, with profile `P`:

1. **Account exists** `(provider_id, account_id)` → sign in that user. Optionally refresh stored profile/tokens.
2. **No account, email matches an existing user**:
   - If `P.EmailVerified == true` and provider is in `TrustedEmailProviders` and `AllowAccountLinking` → create the account row and sign in.
   - Else → `409` with a clear message: sign in with the existing method, then link. Do **not** auto-link unverified emails.
3. **No account, no user**:
   - Create a user (`password_hash = "!"`, `email_verified = P.EmailVerified ? 1 : 0`), create the account row, sign in.
4. **Current session is anonymous** (`is_anonymous = 1`) and no existing user with `P.Email`:
   - Link the OAuth account to the anonymous user, set `is_anonymous = 0`, update `email/name/image`, sign in. This upgrades guest sessions cleanly.
5. **Current session is anonymous but email exists**:
   - Do not merge. Sign in to the existing user (or return `409` if `intent=link`). Leave the anonymous user untouched.

Token storage: access/refresh tokens are stored on the account row. v1 exposes `Auth.GetOAuthAccessTokenAsync(ctx, providerId)` with refresh-if-expired for providers that support it; otherwise return the stored token. Document that tokens are sensitive and should not be returned to the client.

### 5.7 What Better Auth gives you (and what it doesn't)

The built-in C# auth is not going away, and Better Auth is not a drop-in replacement for it. They share the same C# routes and DB; the difference is the frontend surface and the plugin surface.

| Capability | Built-in C# (`--auth builtin`) | Better Auth client + compat (`--auth better-auth`) | Notes |
| --- | --- | --- | --- |
| Email/password, sessions, anonymous, JWT, roles | ✅ | ✅ | Same C# implementation |
| Typed end-to-end contracts (`AppType`) | ✅ | ⚠️ Better Auth client types, not `AppType` | Built-in wins on type safety |
| Frontend hooks (`useSession`, `signIn`, `signUp`, `signOut`) | ❌ (small helper needed) | ✅ | The main reason to pick better-auth |
| Prebuilt auth UI (shadcn registry) | ✅ | ✅ | We ship both variants |
| Social OAuth (Google, GitHub, …) | ✅ | ✅ | Same C# OAuth slice |
| Organization / multi-tenant | ❌ | ⚠️ client plugin calls endpoints we don't have | Needs a C# port |
| 2FA / TOTP | ❌ | ⚠️ same | Needs a C# port |
| Passkey / WebAuthn | ❌ | ⚠️ same | Needs a C# port |
| Magic link / email OTP | ❌ | ⚠️ same | Needs a C# port + email sender |
| API keys | ❌ | ⚠️ same | Needs a C# port |
| Admin / user management | ❌ | ⚠️ same | Needs a C# port |
| Extra frontend dependency | none | `better-auth` | Built-in is lighter |

Short version: **if the reason to use Better Auth is its client hooks/UI, the compat surface is enough. If the reason is its plugins, those are C# ports — the compat surface doesn't provide them for free.** That's why built-in stays the default and Better Auth is an option, not the only path.

### 5.8 Better Auth HTTP compatibility (optional, first-class)

Mount explicitly on the **API** at `/auth` when `--auth better-auth` is selected:

```csharp
app.Route("/auth", Auth.BetterAuthRoutes());
```

The browser reaches it at `/api/auth/*` through the same-origin proxy (which strips `/api`). This keeps the native routes (`/auth/signin`, `/auth/session`, `/auth/oauth/*`) and the compat routes (`/auth/sign-in/email`, `/auth/get-session`, `/auth/callback/*`) under one prefix with no collisions.

Supported surface (enough for `better-auth/client` core):

| Better Auth client call | Browser path (proxy) | API path | Response |
| --- | --- | --- | --- |
| `signUp.email` | `POST /api/auth/sign-up/email` | `POST /auth/sign-up/email` | `{ user }` (token omitted; cookie set) |
| `signIn.email` | `POST /api/auth/sign-in/email` | `POST /auth/sign-in/email` | `{ redirect: false, user }` (token omitted) |
| `getSession` | `GET /api/auth/get-session` | `GET /auth/get-session` | `{ session, user }` or `null` |
| `signOut` | `POST /api/auth/sign-out` | `POST /auth/sign-out` | `{ success: true }` |
| `signIn.social` | `POST /api/auth/sign-in/social` | `POST /auth/sign-in/social` | `{ url, redirect: true }` |
| OAuth callback | `GET /api/auth/callback/:provider` | `GET /auth/callback/:provider` | `302` + session cookie (alias of `/auth/oauth/:provider/callback`) |
| health | `GET /api/auth/ok` | `GET /auth/ok` | `{ ok: true }` |

`createAuthClient({ baseURL: "/api/auth" })` (or the default origin-relative `/api/auth`) works without further configuration.

Better Auth's TypeScript types include `token` on sign-up/sign-in; we omit it for security (D13). The client works because it relies on the HttpOnly cookie. `CompatExposeSessionToken = true` restores the field for a strict client, at the cost of exposing the session token to JS.

Rules:

- Use the **same** `users`/`sessions` tables and the same `reptar_session` cookie. No Better Auth DB schema, no signed `better-auth.session_token` by default.
- `get-session` returns `{ session, user }` with `user: { id, email, name, emailVerified, image, createdAt, updatedAt }` and `session: { id, userId, expiresAt, createdAt, updatedAt, ipAddress, userAgent }`. `session.token` is omitted by default (HttpOnly should mean something); `CompatExposeSessionToken = true` restores it for strict clients.
- Error envelope: `{ message, code }` with Better Auth-style codes (`INVALID_EMAIL_OR_PASSWORD`, `USER_ALREADY_EXISTS`, `INVALID_EMAIL`, `PASSWORD_TOO_SHORT`, `PASSWORD_TOO_LONG`, `SESSION_EXPIRED`, `PROVIDER_NOT_FOUND`, `ACCOUNT_NOT_FOUND`, `EMAIL_NOT_VERIFIED`).
- Accept both JSON and `application/x-www-form-urlencoded` bodies on sign-up/sign-in (Better Auth does).
- Add an origin check for state-changing compat routes using `AuthOptions.TrustedOrigins` (Better Auth's `formCsrfMiddleware` equivalent). Same-origin proxy makes this easy; cross-origin requires an explicit allowlist.
- Pin and test a Better Auth client version (e.g. `better-auth@1.x`). Document that plugins and server-side `betterAuth()` DB interop are **not** supported.

Why this is not hacky: the compat surface is a thin adapter over `AuthStore`, it doesn't fork the auth model, and it's opt-in. The native typed routes remain the default.

### 5.9 Auth feature ports (later, explicitly out of v1)

Design the tables/options so these can land incrementally. The order below is the proposed priority for SaaS/AI/CRUD/social apps, not a promise for v1.

- **Username** — `users.username` (case-insensitive unique, reserved names), login by email or username.
- **Email verification + password reset** — `verification` table; opaque token + hash; `IEmailSender`.
- **Magic link / email OTP** — same `verification` table + `IEmailSender`; short TTL, single-use.
- **Two-factor (TOTP)** — `two_factor` table; RFC 6238 using `ICrypto.HmacSha256`; recovery codes; QR via `otpauth://` URI.
- **RBAC / permissions** — `roles`, `role_permissions`, `RequireRole`, `RequirePermission`; optional resource ownership checks.
- **API keys** — `api_keys` table (prefix + hash + scopes + last_used_at) + `Auth.RequireApiKey`.
- **Phone number + SMS OTP** — `users.phone_number`, `phone_number_verified`; `ISmsSender` adapter (Twilio, etc.).
- **Organizations / teams / invites** — `organizations`, `members`, `invitations`; `RequireOrgRole`.
- **Passkey / WebAuthn** — `passkeys` table; CBOR/COSE + host WebCrypto; hardest item, defer to v2.
- **SSO (OIDC first, SAML later)** — enterprise; OIDC client is close to the OAuth slice; SAML is a separate project.
- **Admin** — user list/ban/impersonate; audit log.
- **Audit logs** — `auth_events` table (sign-in, failed sign-in, password change, 2FA change, API key create/revoke).
- **Multi-session / device management** — list/revoke sessions by device; useful for SaaS.
- **JWT / JWKS** — already partially present (`Jwt`, `Auth.RequireJwt`); add RS256/ES256 verification via host WebCrypto + a JWKS endpoint.
- **OIDC provider** — only if we want Reptar to be an auth server; optional native-only package later.

Utility slices that make the above usable in real apps:

- **Session management** — list/revoke sessions by device; “sign out everywhere”.
- **CSRF tokens** — double-submit token for cookie-auth form posts; origin checks for JSON.
- **Bot protection** — Cloudflare Turnstile verification middleware for sign-up/sign-in.
- **Password policy** — min length, common-password list, optional HIBP k-anonymity check via `IHttpClient`.
- **Email normalization** — trim/lowercase, plus-addressing rules, disposable-domain list (opt-in).
- **Account lifecycle** — email change, account deletion/export (GDPR), soft-delete + purge job.
- **Auth events** — audit log + optional webhook on sign-up/sign-in/failure/2FA change/API key revoke.
- **Rate limiting** — apply the existing `RateLimit` middleware to `/auth/*` in the scaffold.

Schema roadmap (additive, one table/column set per feature):

| Feature | Storage |
| --- | --- |
| Username | `users.username` (unique, indexed) |
| Email verification / password reset / magic link / email OTP | `verification` (`identifier`, `value_hash`, `purpose`, `expires_at`, `consumed_at`) |
| 2FA TOTP | `two_factor` (`user_id`, `secret`, `recovery_codes_hash`, `enabled_at`) |
| RBAC | `roles`, `role_permissions`, `user_roles` (simple apps can stay on `users.role`) |
| API keys | `api_keys` (`user_id`, `name`, `prefix`, `key_hash`, `scopes`, `last_used_at`, `expires_at`) |
| Organizations | `organizations`, `members`, `invitations` |
| Phone + SMS OTP | `users.phone_number`, `users.phone_number_verified`; reuse `verification` |
| Passkeys | `passkeys` (`user_id`, `credential_id`, `public_key`, `sign_count`, `transports`, `aaguid`) |
| Audit | `auth_events` (`user_id`, `type`, `ip`, `user_agent`, `metadata`, `created_at`) |
| SSO | `sso_connections` (`org_id`, `provider`, `client_id`, `secret`, `metadata`) |

### 5.10 Security checklist

- [ ] State + PKCE; state cookie signed, single-use, 10 min TTL, constant-time compare.
- [ ] `redirect` allowlist; reject protocol-relative and absolute unknown origins.
- [ ] Auto-link only verified emails from trusted providers.
- [ ] CSRF/origin check on compat POST routes.
- [ ] CORS allowlist (see C3); never reflect arbitrary origins with credentials.
- [ ] Cookie policy: `HttpOnly`, `SameSite=Lax` default, `Secure` in prod, `SameSite=None` requires `Secure`.
- [ ] Rate-limit auth endpoints (`RateLimit(10, 60, "auth")` in the scaffold).
- [ ] Never return access/refresh tokens to the browser.
- [ ] Password `password_hash = "!"` sentinel cannot verify (already true in `DevCrypto` because it isn't a valid hash).
- [ ] Session fixation: always issue a new token on OAuth sign-in; delete the anonymous session if upgrading.
- [ ] Token-at-rest: document that OAuth tokens are stored plaintext in D1; encryption is a future hardening item.

### 5.11 Tests

- Unit: provider URL building, PKCE challenge, state cookie sign/verify/expiry, profile mapping, redirect validation, linking rules.
- Integration: fake OAuth provider via a stub `IHttpClient` (authorize → callback → session), including unverified-email conflict and anonymous upgrade.
- Compat: `bun test` that boots the portable app, calls `createAuthClient` from `better-auth/client`, and exercises sign-up/sign-in/get-session/sign-out.
- E2E: generated scaffold → `reptar codegen` → `tsc --noEmit` → `reptar dev` → health + auth.
- **Known-answer tests**: TOTP against RFC 6238 vectors, JWT against RFC 7519/7515 vectors, PKCE against RFC 7636, base32 against RFC 4648. Use battle-tested libraries as **test-only oracles** (see §5.12).
- **Property/fuzz tests**: state-cookie parser, JWT parser, base32, redirect validation, provider profile mapping.

### 5.12 Auth libraries: build vs borrow

The other agent's list is sensible for a normal ASP.NET Core app. Reptar is not that: the primary target is `netwasm0.1`, where `System.Security.Cryptography`, `System.Formats.Cbor`, `System.IO`, and expression compilation are largely unavailable. NetWasm ships a deliberately small package set (`System.Linq`, `System.Text.Json`, `System.Memory`, `System.Text.RegularExpressions`, `System.IO.Pipelines`, `System.Text.Encodings.Web`). That decides most of these.

| Library | Verdict | Why |
| --- | --- | --- |
| `System.IdentityModel.Tokens.Jwt` / `Microsoft.IdentityModel.JsonWebTokens` | ❌ core; ✅ **test oracle** | Heavy dependency graph; RSA/ECDSA/X509 via `System.Security.Cryptography`; trimming/AOT friction; not viable on NetWasm. Use it in `Reptar.Tests` to cross-check our JWT output. |
| `SimpleAuthentication` | ❌ | ASP.NET Core `IServiceCollection`/`AuthenticationHandler`; directly contradicts Reptar's non-goals; not Wasm. Our `RequireUser`/`RequireJwt` is the equivalent. |
| `Casbin.NET` | ❌ core; optional native adapter | Expression compilation/reflection/file I/O; heavy for 95% of SaaS apps. Build a small RBAC model; offer `Reptar.Auth.Casbin` for `net10.0` only if demand appears. |
| `Gatekeeper` | ❌ | SQLite-specific persistence (no D1), reflection; not portable. |
| `OpenIddict Core` | ❌ core; optional native-only later | DI/options/EF Core/crypto; not Wasm. If we need an OIDC provider, build a small slice on our JWT + JWKS. |
| `Microsoft.AspNetCore.Identity` | ❌ | ASP.NET Core + EF Core; not Wasm, not reflection-free. |
| `Fido2NetLib` / `WebAuthn.Net` | ⚠️ later | CBOR/COSE + crypto; `System.Formats.Cbor` is not in the NetWasm package set; ASP.NET Core integration. Passkey is the hardest item — defer, or write a minimal verifier with a small CBOR reader + host WebCrypto. |
| `Otp.NET` | ❌ core; ✅ **test oracle** | Uses .NET HMAC; our HMAC is host WebCrypto. RFC 6238 is ~50 lines; validate against `Otp.NET` in tests. |
| `QRCoder` | ❌ | Not needed. Emit an `otpauth://` URI and let the frontend render the QR. |

**Build vs borrow principle:**

- **Borrow primitives, not frameworks.** All crypto goes through `ICrypto` (host WebCrypto on Workers, .NET crypto on native). We never implement AES/RSA/ECDSA math ourselves.
- **Build flows and policy.** Sessions, OAuth, account linking, TOTP, magic link, RBAC, API keys, orgs, audit logs. These are small, explicit, testable, and Wasm-safe.
- **Use libraries as test oracles.** `Reptar.Tests` (net10.0) can reference `System.IdentityModel.Tokens.Jwt`, `Otp.NET`, and `Fido2NetLib` to validate our implementations against battle-tested code. Zero runtime baggage, real confidence.
- **Optional native adapters.** Casbin, OpenIddict, or Fido2 can live in separate `net10.0`-only packages (`Reptar.Auth.Casbin`, `Reptar.Auth.OpenIddict`) if a customer needs them. They never enter the Wasm core.
- **Threat-model every feature.** The bug risk is in policy and flow, not in SHA-256. Review CSRF, open redirects, account takeover, session fixation, timing, token replay, and enumeration before shipping each slice.

### 5.13 Auth feature roadmap by app type

| Feature | CRUD | SaaS | AI | Social | Target |
| --- | --- | --- | --- | --- | --- |
| Email/password + sessions | ✅ | ✅ | ✅ | ✅ | v1 (exists) |
| OAuth (Google first) + account linking | ✅ | ✅ | ✅ | ✅ | v1 |
| Username |  | ✅ |  | ✅ | v1.1 |
| Email verification + password reset |  | ✅ | ✅ | ✅ | v1.1 |
| RBAC / permissions | ✅ | ✅ | ✅ | ✅ | v1.1 |
| API keys |  | ✅ | ✅ |  | v1.1 |
| Magic link / email OTP |  | ✅ | ✅ | ✅ | v1.1 |
| 2FA TOTP + recovery codes |  | ✅ | ✅ | ✅ | v1.1 |
| Organizations / teams / invites |  | ✅ | ✅ |  | v1.2 |
| Phone number + SMS OTP |  | ✅ |  | ✅ | v2 |
| Passkey / WebAuthn |  | ✅ | ✅ | ✅ | v2 |
| SSO (OIDC, then SAML) |  | ✅ |  |  | v2 |
| Audit logs |  | ✅ |  |  | v2 |
| Multi-session / device management |  | ✅ |  | ✅ | v2 |
| Admin / impersonation |  | ✅ |  |  | v2 |
| OIDC provider |  | ✅ |  |  | v2+ (optional) |

---

## 6. Workstream C — Framework seams

These are the small, non-hacky changes the scaffolder and auth need.

| # | Change | Files | Notes |
| --- | --- | --- | --- |
| C1 | Split composition from host (documented pattern; no framework code) | `examples/blog/app.cs` → `app.config.cs` + `app.cs` | Do this in the reference app first so templates copy a proven shape. |
| C2 | `RouteBuilder.NoClient()` + `RouteMeta.ClientExcluded` | `Core/ReptarApp.cs`, `Codegen/RpcGenerator.cs` | Skip browser-only OAuth routes in `AppType`; keep them in OpenAPI or mark 302. Copy the flag in `CloneWithPath`. |
| C3 | CORS allowlist | `Core/Middleware.cs` | `Cors(params string[] origins)` and/or `CORS_ORIGINS` from `ctx.Environment`; `Vary: Origin`; reject disallowed preflights. **Breaking**: update examples/scaffold. |
| C4 | Cookie policy options | `Core/Cookies.cs`, `Auth.cs` | `CookieOptions { HttpOnly, SameSite, Secure, Domain, Path }`; keep old overloads. Add `AuthOptions.CookieSameSite`, `CookieSecure`. |
| C5 | Form-encoded HTTP | `Data/Services.cs`, `Reptar.Wasm/Data/WasmHttpClient.cs`, `packages/host/src/host.ts` | `PostFormAsync(url, Dictionary<string,string>)`; fix `DefaultHttpClient` to honor an explicit `content-type` instead of always JSON. |
| C6 | `SecretRef` + env resolution | `Auth/OAuth/*`, `Data/Services.cs` | Resolve at request time from `IEnvironment` (Workers bindings). |
| C7 | Composite unique index | `Data/TableDef.cs` | `UniqueIndex(params string[])`; emit in `SchemaDdl.CreateIndexes`. Needed for `(provider_id, account_id)`. |
| C8 | Migration generator | `Data/TableDef.cs`, new `Data/Migrations.cs`, `examples/blog/app.cs` | Public `Migrations.Generate(tables)`; `--migrations --out` and `--db-push` modes. v1: `CREATE TABLE IF NOT EXISTS` + indexes. |
| C9 | `AuthStore` refactor | `Auth/Auth.cs` | Extract user/session helpers so native, OAuth, and compat routes share one implementation. Keep public API stable. |
| C10 | NuGet pack metadata | all `framework/**/*.csproj` | `IsPackable`, `PackageId`, `Version`, `Description`, `PackageReadmeFile`. Spike `Reptar.Wasm` on `netwasm0.1`. |
| C11 | `IEmailSender` seam (optional) | `Data/Services.cs` | Only if we port magic-link/verification in v1. Otherwise defer. |
| C12 | `AuthOptions` relocation | `Core/ReptarApi.cs` → `Auth/AuthOptions.cs` | Keep type name/namespace so `BuiltIn(new AuthOptions {...})` still compiles. |
| C13 | **Unify the two auth implementations** | `framework/Reptar/Auth/Auth.cs`, delete `framework/Reptar.Wasm/Auth/Auth.cs`, `Reptar.Wasm/Reptar.Wasm.csproj` | One `AuthStore` + routes shared by both targets. Use named params (`@name`; D1 rewrites them) and **explicit column aliases** (`password_hash as passwordHash`) so SQLite/Postgres/D1 all map rows correctly. Change the Wasm csproj to `<Compile Include="../Reptar/Auth/**/*.cs" />`. Without this, OAuth and compat would be duplicated or work on only one target. |

---

## 7. Workstream D — Docs & examples

### 7.1 New docs pages

- `getting-started/scaffolding.mdx` — flags, matrix, generated layout, `reptar.jsonc`.
- `getting-started/frontend.mdx` — same-origin proxy, `@reptar/client`, auth helper, Better Auth client compat, deployment.
- `getting-started/auth-ui.mdx` — shadcn registry items, plain Tailwind variant, wiring sign-in/sign-up.
- `auth/oauth.mdx` — provider setup, callback URLs, account linking, tokens, security.
- `auth/better-auth.mdx` — supported endpoints, cookie behavior, limitations, migration notes, and the built-in-vs-compat table (§5.7).
- `cli/index.mdx` — `dev`, `codegen`, `db`, `add`, `doctor`.

### 7.2 Updates

- `getting-started/quickstart.mdx` — real commands; fix the broken tab code fences; `bun reptar dev`.
- `getting-started/project-structure.md` — match the generated layout; remove `packages/contract` unless we add it.
- `client/codegen.mdx` — `packages/rpc/src` paths, `NoClient`, `--check`; remove "Standard Schema (planned)".
- `data/migrations.mdx` — actual v1 behavior and hand-written SQL.
- `auth/builtin.md` — PBKDF2, new columns, cookie options.
- `auth/adapters.md` — replace the fictional `BetterAuth()`/`Clerk()` adapters with the real compat surface; mark Clerk as future.
- `getting-started/quickstart.mdx` — mention both `--auth builtin` and `--auth better-auth` and when to pick each.
- `overview/architecture.md` — remove "Better Auth mount" from the shim diagram; clarify the compat surface.
- `README.md` — quickstart, CLI commands, project layout, auth.
- `design.md` — §6 (OAuth + compat), §8 (contract paths), §11 (scaffold modes), §14 (roadmap status), §16 (status).

### 7.3 Examples

- Refactor `examples/blog` to the `app.config.cs` + host split (C1). It becomes the canonical template source.
- Add `examples/minimal/` — `posts` + `profile` + auth, no uploads/Stripe/realtime. Used as the scaffold smoke fixture.
- Keep `examples/campfire` and `examples/realworld` as-is.
- Add `scripts/scaffold-smoke.ts`:
  1. `create-reptar` into a temp dir with `--framework local --no-install`.
  2. `bun install`, `reptar codegen`, `tsc --noEmit`.
  3. Start `reptar dev`, hit `/health`, sign up, `GET /auth/session`, sign out.
  4. Kill, clean up.
- Add `scripts/better-auth-compat.test.ts` (boots the app + `better-auth/client`).
- Add the `registry/` shadcn items (`auth-builtin`, `auth-better`, `user-menu`) and build them in CI.

### 7.4 CI

- `bun run check` (Ultracite).
- `bun run api:test` (C# unit/integration).
- `bun run scaffold:test` (generated project smoke).
- `bun run auth:compat` (Better Auth client).
- `bun run registry:build` (`shadcn build`; validate `registry.json`).
- `bun run docs:build` (Starlight).

---

## 8. Phases, deliverables, exit criteria

### Phase 0 — De-risk packaging (0.5–1 day)

- Spike: pack `Reptar` and `Reptar.Wasm`; consume them from a throwaway file-based app + `netwasm0.1` csproj.
- Confirm `#:package Reptar@0.1.0`, transitive NetWasm packages, and `dotnet publish` for the worker.
- **Exit:** a hello-world project outside the repo builds and runs both targets from packages. If this fails, stop and redesign (fallback: `dotnet new` template + local framework path).

### Phase 1 — Framework seams (2–3 days)

- C2, C3, C4, C5, C7, C8, C9, C12, **C13** (unify auth; this is the largest item).
- Refactor `examples/blog` to the composition/host split (C1).
- **Exit:** `bun api:test`, `bun e2e`, `bun rpc:gen`, `bun rpc:check` pass; CORS/cookie tests added.

### Phase 2 — OAuth in C# (2–3 days)

- `OAuthProvider`, registry (Google/GitHub/Microsoft), flow, state/PKCE, accounts table, linking, unlink.
- Add Discord/LinkedIn/Slack/GitLab.
- **Exit:** fake-provider integration tests pass; Google/GitHub happy path documented; account-linking conflict test passes.

### Phase 3 — Better Auth compat (1–2 days)

- `Auth.BetterAuthRoutes()`, error mapping, cookie reuse, origin check.
- `better-auth/client` test.
- **Exit:** sign-up/sign-in/get-session/sign-out/social-start work through the real client; docs list limitations.

### Phase 4 — `create-reptar` + `reptar` (3–5 days)

- `cli-core`, `create-reptar` (Astro first), `reptar` (`dev`, `codegen`, `db`, `doctor`).
- Cloudflare target first, then VPS.
- **Exit:** generated Astro project installs, codegens, type-checks, runs, and passes the smoke test. `--framework local` passes in CI.

### Phase 5 — Second frontend + auth UI (2–3 days)

- TanStack Start adapter, shadcn `auth-builtin` / `auth-better` registry items, `reptar add docs/infra/lefthook/ultracite`.
- **Exit:** both frontends pass the smoke test; the shadcn registry builds; addons are idempotent.

### Phase 6 — Docs, examples, release (2–3 days)

- All doc updates, `examples/minimal` polish, `README`, `design.md`.
- NuGet + npm publish workflows, version pinning.
- **Exit:** docs build; every command in the quickstart works from a clean machine; `create reptar@latest` works from the published package.

### Phase 7 — Auth depth (post-v1, not in the initial estimate)

- **v1.1:** username, email verification, password reset, RBAC/permissions, API keys, magic link/email OTP, 2FA TOTP + recovery codes.
- **v1.2:** organizations/teams/invites.
- **v2:** phone/SMS OTP, passkey/WebAuthn, SSO (OIDC then SAML), audit logs, multi-session, admin/impersonation, optional OIDC provider.
- **Exit:** each feature has RFC/known-answer tests, a threat-model review, and a docs page. Nothing enters the Wasm core unless it compiles and passes the Wasm auth test.

Total estimate for Phases 0–6: **~10–16 focused days**. Phase 7 is a separate roadmap. Phases 0–3 are independent of the benchmark scripts, but they still wait for the implementation gate (benchmark agents done) before any structural change.

---

## 9. Risks & mitigations

| Risk | Impact | Mitigation |
| --- | --- | --- |
| NuGet `netwasm0.1` packaging fails | Blocks clean scaffold | Phase 0 spike; fallback to a pinned GitHub release tarball or `#:project` for alpha |
| `@reptar/client` / `@reptar/host` are still private | Generated projects can't install them | Publish both to npm in the same release as the NuGet packages; `--framework local` for CI |
| NetWasm optimizer limits (known) | Worker template may not support SSE/WS/cron | Keep the generated worker lean (HTTP + D1/R2/KV); document the limitation |
| Cross-site cookies break auth | Users see "logged out" in prod | Same-origin proxy by default; document custom-domain and `SameSite=None` alternatives |
| OAuth provider quirks (GitHub email, Microsoft tenant, Apple ES256) | Broken sign-in per provider | Start with 3 providers; per-provider integration tests; Apple deferred |
| Better Auth endpoint drift | Compat breaks on upgrade | Pin a tested client version; keep the surface small; test in CI |
| Scope creep into 50+ plugins | Delays scaffolding | v1 = OAuth + account linking only; plugin ports listed as future |
| Static `Auth` options | Test order / multi-app issues | Refactor to an internal `AuthRuntime`; add a test reset hook |
| Two auth slices drift | OAuth/compat works on one target only | C13: unify into `Reptar/Auth/**`; add a Wasm-target test that runs the auth flow |
| `#:include` globs disable build caching | Slower dev builds | Accept for small apps; `doctor` can suggest explicit includes |
| Generated project drifts from templates | Docs/examples lie | `scaffold-smoke` in CI; templates generated from `examples/minimal` where possible |
| Two CLIs duplicate code | Maintenance | Shared private `@reptar/cli-core`, bundled at publish |
| Third-party auth libraries don't fit `netwasm0.1` | Tempting shortcuts that break the primary target | §5.12 build-vs-borrow: primitives via `ICrypto`, flows in C#, libraries as **test oracles**, optional native adapters |
| Policy/flow bugs in custom auth | Security incidents | Small RBAC model, RFC/known-answer tests, fuzz parsers, threat-model checklist (§5.10), test-only oracles |
| Passkey/WebAuthn is expensive | Delays v1 | Defer to v2; don't ship a half-verified implementation; evaluate a minimal CBOR reader + host WebCrypto |
| Plugin scope creep | Delays the scaffold | v1 = core + OAuth + Better Auth compat; v1.1/v2 features are explicitly listed in §5.13 |

---

## 10. Open questions

1. **Registries** (deferred): do we have a NuGet org/feed and npm org, or should alpha use GitHub Packages + a private npm scope? This is the one hard prerequisite (both C# and npm).
2. ~~**Frontends**~~ — resolved: Astro + TanStack Start; no Next.js.
3. ~~**Target default**~~ — resolved: Cloudflare-first, with VPS as a first-class target.
4. ~~**"Tigether"**~~ — resolved: "together with better-auth", not a separate integration.
5. ~~**Better Auth compat default**~~ — resolved: built-in is the scaffold default; `--auth better-auth` is a first-class option. The compat surface is core-only; plugins are future C# ports.
6. ~~**Session token exposure**~~ — resolved: omit `session.token`; HttpOnly cookie only (D13).
7. ~~**shadcn/ui**~~ — resolved: both plain Tailwind and shadcn; shadcn auth pages via the Reptar registry (D10 / §4.6).

Still deferred until the benchmark agents finish: registry credentials, release versioning, and the exact OAuth provider order after Google.

---

## 11. Suggested first PR

A single, reviewable PR that proves the shape without the full CLI:

1. Refactor `examples/blog` to `app.config.cs` + `app.cs` (C1).
2. Add `RouteBuilder.NoClient()` (C2).
3. Add CORS allowlist (C3) and cookie options (C4).
4. Add `Migrations.Generate` + `--migrations --out` (C8).
5. Add `examples/minimal/` and a `scripts/scaffold-smoke.ts` that copies it into a temp dir, rewrites the `#:project` paths, and runs `dotnet run --codegen` + `tsc --noEmit`.

That gives us the composition pattern, the contract path, and a smoke harness before we commit to templates. Then Phase 0 packaging spike runs in parallel.

---

## Appendix A — Current auth vs Better Auth shapes

| Field | Reptar today | Better Auth | Plan |
| --- | --- | --- | --- |
| User table | `users` | `user` | Keep `users` |
| User id | `usr_...` | generated string | Keep |
| `emailVerified` | — | boolean | Add |
| `image` | — | string? | Add |
| `updatedAt` | — | date | Add |
| Session table | `sessions` | `session` | Keep `sessions` |
| Session token | SHA-256 hash only | plain `token` + signed cookie | Keep hash; compat omits token by default |
| Session metadata | — | `ipAddress`, `userAgent` | Add nullable columns |
| Account table | — | `account` | Add `accounts` |
| Password | `users.password_hash` | `account.password` | Keep on `users`; `!` sentinel for OAuth-only |
| Cookie | `reptar_session` | `better-auth.session_token` (signed) | Keep `reptar_session`; optional signed compat cookie |
| API prefix | any | `/api/auth` | Compat mounts at `/auth` on the API; the proxy exposes `/api/auth` |

## Appendix B — Generated `.env.example`

```dotenv
# Backend (never exposed to the browser)
DATABASE_URL=
AUTH_SECRET=
OAUTH_STATE_SECRET=
# Browser-facing origin for OAuth redirect URIs (frontend/proxy origin)
OAUTH_PUBLIC_BASE_URL=http://localhost:4321
GOOGLE_CLIENT_ID=
GOOGLE_CLIENT_SECRET=
GITHUB_CLIENT_ID=
GITHUB_CLIENT_SECRET=
CORS_ORIGINS=http://localhost:4321

# Frontend
API_URL=http://localhost:8787
PUBLIC_API_URL=/api
```

## Appendix C — File-by-file change map (first PR)

```text
examples/blog/app.cs                    # split into app.cs + app.config.cs
examples/blog/app.config.cs             # new: App.Build(AppPlatform)
framework/Reptar/Core/ReptarApp.cs      # RouteMeta.ClientExcluded, RouteBuilder.NoClient
framework/Reptar/Core/Middleware.cs     # CORS allowlist
framework/Reptar/Core/Cookies.cs        # CookieOptions
framework/Reptar/Core/ReptarApi.cs      # move AuthOptions; add OAuth/Cookie/TrustedOrigins
framework/Reptar/Data/TableDef.cs       # UniqueIndex
framework/Reptar/Data/Migrations.cs     # new: public DDL generator
framework/Reptar/Codegen/RpcGenerator.cs# skip NoClient routes
framework/Reptar/Auth/Auth.cs           # AuthStore extraction + shared routes
framework/Reptar/Auth/OAuth/*.cs        # new
framework/Reptar.Wasm/Auth/Auth.cs      # delete; compile ../Reptar/Auth/** instead
framework/Reptar.Wasm/Reptar.Wasm.csproj# include shared Auth sources
framework/Reptar/Auth/BetterAuth/*.cs   # new (phase 3)
framework/Reptar/Reptar.csproj          # pack metadata
framework/Reptar.Wasm/Reptar.Wasm.csproj# pack metadata + new auth files
scripts/scaffold-smoke.ts               # new
scripts/reptar.ts                       # delegate to packages/reptar-cli (later)
apps/docs/src/content/docs/**           # docs updates
```
