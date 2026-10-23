---
title: Quick Start
description: Get up and running with a Reptar project in minutes.
---

## Prerequisites

Before starting, ensure you have the following installed:

- **.NET SDK 10.0+**
- **Bun 1.3+** (or Node.js 22+)
- **Cloudflare CLI (`cf`)** or Wrangler

---

## 1. Scaffold a New Project

Initialize a standalone API or a full-stack monorepo:

```bash
bun create reptar@latest my-reptar-app
cd my-reptar-app
```

---

## 2. Project Composition (`apps/api/app.cs`)

Your entire application entry point is configured in `app.cs`:

```csharp
var app = Reptar(new ReptarConfig
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

await app.RunAsync(args);
```

---

## 3. Local Development

Run the unified development environment:

```bash
bun run dev
```

This starts:

- Local SQLite database emulation for `c.Db`
- The TypeScript host shim watching for C# recompilation
- The local frontend at `http://localhost:4321`

---

## 4. Run Migrations

Generate and push your database schema:

```bash
# Generate SQL migration files from your TableDef declarations
bun reptar db generate

# Apply to local SQLite during development
bun reptar db push
```

---

## 5. Consume from the Frontend

Import the generated client in your frontend:

```ts
import { createClient } from "@reptar/client";
import type { AppType } from "@reptar/rpc";

const api = createClient<AppType>(import.meta.env.VITE_API_URL);

// End-to-end typed request
const posts = await api.posts.$get();
console.log("Fetched posts:", posts);
```
