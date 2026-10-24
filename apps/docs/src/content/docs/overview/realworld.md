---
title: RealWorld API
description: A spec-compliant Conduit backend and the Native AOT benchmark target.
---

Reptar includes a complete RealWorld (Conduit) API under `examples/realworld`. It is the framework's API-completeness and performance benchmark target.

## Status

- **Official Hurl suite:** 13/13 files pass, 154/154 requests.
- **Targets:** portable SQLite/Kestrel and Linux Native AOT.
- **Wasm Worker:** the C# RealWorld Worker builds to ~9.5 MB and passes the 154-request Hurl suite locally under `wrangler dev`; edge deployment and benchmarking are next.
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
| `GET /api/tags`                   |         100 |   4,220 |  22 ms |  47 ms |
| `GET /api/articles/:slug`         |         100 |   2,627 |  36 ms |  59 ms |
| `GET /api/articles?limit=20`      |         100 |     901 | 110 ms | 172 ms |
| `GET /api/articles/feed?limit=20` |         100 |     755 | 129 ms | 200 ms |
| `GET /api/articles?tag=bench`     |         100 |     592 | 164 ms | 200 ms |
| `POST /api/articles`              |          10 |     210 |  42 ms | 101 ms |

List responses omit the article body and fetch tags, favorite counts, favorite state, and follow state in page-level batches. This removes the three correlated subqueries per row; `SqliteDbClient`'s WAL read pool runs those reads concurrently. Article creation batches the article, tag, and junction writes into one transaction. The image is 38.3 MB uncompressed / 11.6 MB compressed after removing the 22 MB `.dbg` symbols.

`synchronous=NORMAL` is a durability tradeoff; override it with `REPTAR_SQLITE_SYNCHRONOUS=FULL` if required.

## AOT lessons

The Native AOT build exposed three framework issues that are now fixed:

1. `SqliteDbClient.DeleteAsync` and `GetAsync` no longer bind primary keys via anonymous types; they use dictionaries so trimming cannot remove the reflected properties.
2. `QueryMappedAsync` allows explicit row mapping without constructor reflection. The RealWorld models use it for AOT-safe reads.
3. `ReptarApp.ExecuteAsync` lets app startup create composite indexes after `EnsureCreatedAsync`; the D1 migration carries the same indexes.
4. Native AOT publishes a large `.dbg` file; the Dockerfile removes it after publish, taking the image from 67.9 MB to 38.3 MB.
5. SQLite WAL/NORMAL/busy_timeout are configurable through `REPTAR_SQLITE_JOURNAL_MODE` and `REPTAR_SQLITE_SYNCHRONOUS`.
6. D1 batch statements accept both JSON-string and array parameters in the host shim; `D1DbClient` now emits the string form. Article creation uses one transaction for the article, tags, and junction rows.
