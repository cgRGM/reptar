---
title: RealWorld API
description: A spec-compliant Conduit backend and the Native AOT benchmark target.
---

Reptar includes a complete RealWorld (Conduit) API under `examples/realworld`. It is the framework's API-completeness and performance benchmark target.

## Status

- **Official Hurl suite:** 13/13 files pass, 154/154 requests.
- **Targets:** portable SQLite/Kestrel and Linux Native AOT.
- **Wasm Worker:** the C# RealWorld Worker builds to ~9.5 MB, but the edge deployment is still being hardened separately.
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

| Scenario                          | Connections | Req/sec |    p50 |  p97.5 |
| --------------------------------- | ----------: | ------: | -----: | -----: |
| `GET /api/tags`                   |         100 |   3,911 |  23 ms |  51 ms |
| `GET /api/articles/:slug`         |         100 |   2,174 |  40 ms |  83 ms |
| `GET /api/articles?limit=20`      |         100 |     327 | 260 ms | 619 ms |
| `GET /api/articles/feed?limit=20` |         100 |     325 | 299 ms | 346 ms |
| `GET /api/articles?tag=bench`     |         100 |     240 | 404 ms | 448 ms |
| `POST /api/articles`              |          10 |      60 | 164 ms | 203 ms |

Batching tag lookups raised feed throughput from ~200 to ~325 req/sec and tag-filter from ~200 to ~240 req/sec. The unfiltered list only moved from ~300 to ~327 req/sec: its main query still has three correlated subqueries per row, and `SqliteDbClient` serializes access through one connection.

SQLite now defaults to WAL with `synchronous=NORMAL` and a 5-second busy timeout. Write throughput rose from ~28 to ~60 req/sec; a new-code control with `DELETE`/`FULL` measured ~20 req/sec. `synchronous=NORMAL` is a durability tradeoff; override it with `REPTAR_SQLITE_SYNCHRONOUS=FULL` if required. The image is 38.3 MB uncompressed / 11.6 MB compressed after removing the 22 MB `.dbg` symbols.

## AOT lessons

The Native AOT build exposed three framework issues that are now fixed:

1. `SqliteDbClient.DeleteAsync` and `GetAsync` no longer bind primary keys via anonymous types; they use dictionaries so trimming cannot remove the reflected properties.
2. `QueryMappedAsync` allows explicit row mapping without constructor reflection. The RealWorld models use it for AOT-safe reads.
3. `ReptarApp.ExecuteAsync` lets app startup create composite indexes after `EnsureCreatedAsync`; the D1 migration carries the same indexes.
