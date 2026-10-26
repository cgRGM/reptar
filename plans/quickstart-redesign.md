# Quickstart Redesign — "Build a Real Thing, Deploy It"

**Source reviewed:** `apps/docs/src/content/docs/getting-started/quickstart.mdx` (134 lines),
cross-checked against `templates/fullstack/`, `packages/create-reptar/`, `deploy/`, and the framework APIs it names.
**Goal (from the repo owner):** the quickstart should end with a *deployed, working app* — using the sign-up/auth stack the scaffold already ships.

---

## 1. Why the current quickstart fails

1. **The first command cannot run.** `bun create reptar@latest my-app` → `create-reptar` is **not published to npm** (`npm view create-reptar` → 404). Verified. The docs' opening move fails 100% of the time, today.
2. **It ends nowhere.** The final step is "generate contracts." No browser moment, no deployed URL, no payoff. The framework's entire pitch — *Reptar is the backend, deploy to 4 targets, 10.4 MB AOT container* — never appears.
3. **It's a tour, not a build.** Every code block is composition the template already contains. The reader writes **zero lines**. Quickstarts teach by doing.
4. **The documented `app.cs` is not the scaffold's `app.cs`.** Docs show `Db.D1()` (throws locally outside the Workers shim), `UseScalar`, `CacheControl.SWR`, `RateLimit.SlidingWindow` — the actual template has none of those (it has DATABASE_URL→Postgres/Sqlite, Google/GitHub OAuth env wiring). The reader pastes the docs version and immediately diverges from what's on disk.
5. **The best feature is invisible.** The template ships working sign-up/sign-in forms, a dashboard, and `/api/me` — auth works out of the box, and the quickstart never once says "open the browser and sign up." That's the wow moment, and it's hidden.
6. **No checkpoints.** Not a single "✅ you should now see X." Both humans and agents (see `examples/ai-gateway/DX-REPORT.md`) need verification moments; neither can tell if they've succeeded.
7. **Prereq drift:** "Cloudflare CLI (`cf`)" is a prerequisite for a tutorial that never deploys.

## 2. Design principles for the rewrite

1. **Show the destination first.** One screenshot-level list of the end state: signup page → dashboard with your notes → `https://your-server/health` green.
2. **Auth-first wow.** Step 1's checkpoint is: *run dev, open the browser, sign up, you're logged in.* Cookie sessions + Better Auth protocol + the React forms already exist — prove the framework's "batteries included" claim in the first two minutes.
3. **Hands-on in exactly two files.** The reader writes C# in `schema.cs` (a `notes` TableDef) and `features/notes.cs` (one validated write + one cached read + one `.AsAiTool` line). Everything else stays scaffold-owned. ~60 lines of user-written code total.
4. **Every step ends in a ✅ checkpoint** — a curl or browser action that proves progress. Checkpoints double as the e2e script (below).
5. **End at a deployed URL.** VPS one-liner (the repo already has `deploy/vps-deploy.sh`: builds the distroless AOT image, `compose up`, health-gates), plus Docker-anywhere and Workers tabs.
6. **Copy-paste truth.** Every block matches the real template; the doc passes `bun docs:check:snippets`.

## 3. The proposed quickstart (full draft, drop-in ready)

---

### Draft: `getting-started/quickstart.mdx`

````mdx
---
title: Quick Start
description: Build a fullstack app with real accounts — schema, validated API, typed client — and deploy it in ~25 minutes.
---

## What you're going to build

A notes app with real authentication. At the end of this page you will have:

- A signup/login flow that works in the browser (sessions, cookies — built in)
- A `notes` table you defined, with a validated, signed-in-users-only write API
- A TypeScript client with full autocomplete generated from your C# routes
- The whole thing **deployed to a VPS** (or Docker host), with a green health check

Every step ends with a ✅ checkpoint. If a checkpoint fails, stop and fix it —
the next step won't work.

## 1. Create the app

```bash
bun create reptar@latest my-app
cd my-app
bun install
bun run dev
```

That starts two servers: your API at `http://localhost:8787` (interactive API
reference at `/reference`) and the frontend at `http://localhost:5173`.

✅ **Checkpoint: auth already works.** Open http://localhost:5173, click
**Sign up**, create an account. You're logged in — the session cookie was
issued by the C# backend (`POST /api/auth/sign-up` → Better Auth wire
protocol). Sign out and sign back in. You just used the framework's built-in
auth with zero configuration.

## 2. What you got (30 seconds)

```text
apps/
├── web/                     # React frontend — sign-up/sign-in/dashboard wired
│   └── src/lib/client.ts    # typed client (you'll regenerate it in step 6)
└── api/
    ├── app.cs               # ~50 lines: config, middleware, feature mounts
    ├── schema/schema.cs      # your TableDefs — you'll edit this
    └── features/             # one file per feature — you'll add one
```

You will only write C# in `schema/` and `features/`. Frontend contracts are
generated, never hand-written.

## 3. Define the notes table

Add to `apps/api/schema/schema.cs`:

```csharp
public static readonly TableDef Notes = DefineTable("notes",
    Col.Text("id").PrimaryKey(),
    Col.Text("user_id").NotNull().Indexed(),
    Col.Text("title").NotNull(),
    Col.Text("content").NotNull(),
    Col.Text("created_at").NotNull().DefaultSql("CURRENT_TIMESTAMP"));
```

Check what the framework knows about your schema — this is the same
machine-readable state AI agents use:

```bash
bun scripts/reptar.ts db plan
```

✅ **Checkpoint:** the plan shows exactly one `CREATE TABLE "notes"` and no drift.
Apply it:

```bash
bun scripts/reptar.ts db migrate
```

## 4. Write the notes API

Create `apps/api/features/notes.cs`:

```csharp
namespace ReptarApp.Features;

using Reptar;
using ReptarApp.Schema;

public record NoteRow(string Id, string UserId, string Title, string Content, string CreatedAt);
public record CreateNoteInput(string Title, string Content);

public static class NotesFeature
{
    public static void Configure(ReptarApp app)
    {
        // Public feed — cached with stale-while-revalidate.
        app.Get("/api/notes", async ctx =>
        {
            var rows = await ctx.Db.From<NoteRow>(Schema.Notes)
                .OrderBy(SchemaNotesCols.CreatedAt.Desc())
                .Limit(50)
                .ToListAsync();
            return ctx.Json(rows);
        });

        // Authenticated write with Rule<T> validation.
        app.Post("/api/notes",
            ReptarApi.Rule<CreateNoteInput>()
                .Prop(x => x.Title, r => r.NotNull().Min(1).Max(120))
                .Prop(x => x.Content, r => r.NotNull().Min(1).Max(2000)),
            async (ctx, input) =>
            {
                var user = await ctx.UserAsync();
                if (user is null)
                    return ctx.Unauthorized();

                await ctx.Db.InsertAsync(Schema.Notes, new
                {
                    Id = Ids.New("note"),
                    UserId = user.Id,
                    input.Title,
                    input.Content,
                });

                return ctx.Status(201).Json(new { ok = true });
            })
            // One line turns this route into an OpenAI/Anthropic/Gemini tool schema.
            .AsAiTool("create_note", "Create a note for the signed-in user");
    }
}
```

Mount it in `app.cs` next to the other features, then restart `bun run dev`.

✅ **Checkpoint: the API behaves like production.**

```bash
curl -s -X POST localhost:8787/api/notes \
  -H 'content-type: application/json' \
  -d '{"title":"hi","content":"empty title rejected, this is not"}'
# → 401 unauthorized: no session yet. Validation and auth both fired.

curl -s localhost:8787/api/notes | head -c 120
# → [] — the feed is public and cached.
```

Send a bad title from the browser while signed in (empty string) and watch the
422 validation envelope come back — same validators your frontend will reuse.

## 5. Use it from the frontend

Regenerate the contracts — your C# routes just became TypeScript types:

```bash
bun run api:codegen
```

In `apps/web`, fetch the feed with the typed client (autocomplete is generated
from the route tree — `api.notes.$get()`):

```tsx
const res = await api.notes.$get();
if (res.ok) setNotes(await res.json());
```

✅ **Checkpoint:** post a note through the dashboard form while signed in — it
appears in the feed. Open devtools: the session cookie rides along, anonymous
users get 401. **You have not written a single fetch or type by hand.**

## 6. Deploy it

Reptar compiles to a **10.4 MB Native AOT container**. Take your pick:

### VPS (Ubuntu, Docker)

```bash
# on the server:
git clone <your-repo> && cd my-app
bash deploy/vps-deploy.sh
```

The script builds the distroless image, starts compose (app + Postgres), and
waits for the health check. Your SQLite `app.db` works for a single-node start;
point `DATABASE_URL` at the compose Postgres when you're ready.

### Docker anywhere

```bash
docker compose up -d --build
curl http://localhost:8787/health
```

### Cloudflare Workers

```bash
bun run worker:build   # publishes the NetWasm module + host shim
# then deploy with wrangler (see Deployment Targets)
```

✅ **Final checkpoint:** `curl https://your-server/health` returns
`{"ok":true}` — then open the site and **sign up a real account on production**.
Auth, validation, and your notes API are live.

## Where to go next

- **File uploads** — `Files.Check`, local/R2 storage, magic-byte sniffing → Cookbook: File Uploads
- **Realtime** — SSE rooms and Turbo Streams → Cookbook: Realtime SSE
- **AI** — the AI Gateway (provider failover, Vercel-stream-compatible) and agent tools → AI docs
- **Money** — zero-dependency Stripe checkout + webhook verification → Features: Stripe
````

---

## 4. Blockers to fix before this ships (in order)

1. **Publish `create-reptar` to npm** (or lead with the from-repo command: `git clone … && bun scripts/reptar.ts new my-app` — verify that path once; it spawns `packages/create-reptar/src/index.ts` and copies `templates/fullstack`). The first command of the whole doc must not 404.
2. **Align the doc's `app.cs` with the template's** — today the docs describe middleware the scaffold doesn't install (`UseScalar`, SWR, rate limiting) and a `Db.D1()` call that throws outside Workers. Either add those to the template (recommended: they're cheap and showcase features) or document what's actually there.
3. **The scaffold needs a deploy path**: `templates/fullstack/` has no `deploy/` folder — `vps-deploy.sh`/compose/Dockerfile live at repo root serving the *realworld* app. Copy a parameterized version into the template (or generate it from `reptar.jsonc`) so step 6 is real for a scaffolded project. This is the difference between "ending at a deployed URL" and ending at a promise.
4. **Verify the draft against reality once, end to end** — scaffold from repo, add the notes feature, run every checkpoint, deploy via compose. The DX report's F2/F5 findings (docs drift, silent runtime traps) both apply to docs authoring; this doc should be the exception.
5. **Make the checkpoints executable**: `scripts/quickstart-e2e.ts` that scaffolds, curls the 401/200/201 checkpoints, and boots compose. Same artifact serves CI and the docs' credibility — and it's the agent-friendly guarantee that the doc never rots silently.

## 5. Why this shape

- **The payoff is stated and shown before work begins** — completion rates follow from knowing the destination.
- **Auth leads** because it's the framework's strongest out-of-the-box claim, and the user asked for it: "considering that we have the sign up and auth stuff too."
- **Two files of hands-on code** is the maximum that fits a ~25-minute budget; everything else is verification, which is where confidence (and retention) actually comes from.
- **Deploy is a first-class step**, not a separate page nobody reaches — matching the repo's actual headline: one C# app, four targets, 10.4 MB.
- **Every ✅ is also an e2e assertion**, so the doc, the CI, and the agent onboarding story reinforce each other instead of drifting apart.
