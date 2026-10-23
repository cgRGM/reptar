---
title: Migrations Engine
description: Autonomous SQL DDL generation, local SQLite push, and Cloudflare D1 deployment.
---

## Overview

Because your `TableDef` declarations contain comprehensive schema information, Reptar generates pure SQL DDL migrations directly. There is no external ORM or Drizzle Kit dependency required in the standard workflow.

---

## Migration Workflow

### 1. Generate Migrations

When you create or update tables in `apps/api/schema/`, run:

```bash
bun reptar db generate
```

This diffs your current `TableDef` instances against `migrations/meta/_journal.json` and emits a sequential SQL file:

```sql
-- apps/api/migrations/0000_init.sql
CREATE TABLE IF NOT EXISTS posts (
  id TEXT PRIMARY KEY,
  author_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  slug TEXT NOT NULL UNIQUE,
  title TEXT NOT NULL,
  content TEXT NOT NULL,
  published INTEGER NOT NULL DEFAULT 0,
  created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);
```

### 2. Push to Local SQLite

During local development, apply pending schema changes to your local SQLite file without touching production:

```bash
bun reptar db push
```

### 3. Apply to Cloudflare D1

To apply pending migrations to your remote Cloudflare D1 database:

```bash
bun reptar db apply
```

In automated CI/CD pipelines, Alchemy reads the `apps/api/migrations/` directory directly and applies migrations atomically.

---

## Scope & Custom SQL

Reptar’s automated migration engine covers:

- `CREATE TABLE IF NOT EXISTS`
- `ADD COLUMN`
- `ADD INDEX`
- `ADD FOREIGN KEY`

For complex schema migrations, table renames, or data backfills, write custom SQL files in `apps/api/migrations/` to be applied in order.
