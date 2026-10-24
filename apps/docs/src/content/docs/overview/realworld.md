---
title: RealWorld API
description: A spec-compliant Conduit backend and the Native AOT benchmark target.
---

Reptar includes a complete RealWorld (Conduit) API under `examples/realworld`. It is the framework's API-completeness and performance benchmark target.

## Status

- **Official Hurl suite:** 13/13 files pass, 154/154 requests.
- **Targets:** portable SQLite/Kestrel and Linux Native AOT.
- **Wasm Worker:** the C# RealWorld Worker builds to ~8.8 MB, but the edge deployment is still being hardened separately.
- **Database:** SQLite by default; the same routes can use `Db.Postgres(...)`.

## Run locally

```bash
bun run realworld:dev
```

The API listens on `http://localhost:8787` with routes under `/api`.

To run the official suite, point it at the API:

```bash
export REALWORLD_SPEC_DIR=/path/to/realworld-apps/realworld
HOST=http://localhost:8787 bun run realworld:hurl
```

## Seed and benchmark

```bash
REALWORLD_URL=http://127.0.0.1:8787 \
REALWORLD_SEED_COUNT=300 \
REALWORLD_SEED_OUT=/tmp/realworld-bench.json \
bun run scripts/seed-realworld.ts

REALWORLD_URL=http://127.0.0.1:8787 \
REALWORLD_SEED=/tmp/realworld-bench.json \
REALWORLD_BENCH_CONNECTIONS=100 \
bun run realworld:bench
```

## VPS results

Measured on a small Lightsail VPS with the Native AOT container and SQLite, 300 seeded articles, 5-second autocannon runs:

| Scenario | Connections | Req/sec | p50 | p95 |
| --- | --: | --: | --: | --: |
| `GET /api/tags` | 100 | 3,810 | 25 ms | 47 ms |
| `GET /api/articles/:slug` | 100 | 2,261 | 44 ms | 61 ms |
| `GET /api/articles?limit=20` | 100 | 320 | 301 ms | 372 ms |
| `GET /api/articles/feed?limit=20` | 100 | ~300 | ~322 ms | ~377 ms |
| `GET /api/articles?tag=bench` | 100 | ~240 | ~386 ms | ~451 ms |
| `POST /api/articles` | 10 | ~28 | ~304 ms | ~730 ms |

The list endpoints spend most of their time in the per-article tag lookup and SQLite serialization. The single-article and tags endpoints show the raw Native AOT/Kestrel ceiling.

## AOT lessons

The Native AOT build exposed three framework issues that are now fixed:

1. `SqliteDbClient.DeleteAsync` and `GetAsync` no longer bind primary keys via anonymous types; they use dictionaries so trimming cannot remove the reflected properties.
2. `QueryMappedAsync` allows explicit row mapping without constructor reflection. The RealWorld models use it for AOT-safe reads.
3. `ReptarApp.ExecuteAsync` lets app startup create composite indexes after `EnsureCreatedAsync`; the D1 migration carries the same indexes.
