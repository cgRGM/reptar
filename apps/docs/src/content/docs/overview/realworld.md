---
title: RealWorld API
description: A spec-compliant Conduit backend and the Native AOT benchmark target.
---

Reptar includes a complete RealWorld (Conduit) API under `examples/realworld`. It is the framework's API-completeness and performance benchmark target.

## Status

- **Official Hurl suite:** 13/13 files pass, 154/154 requests on SQLite, PostgreSQL, the local Wasm Worker, and the deployed edge Worker.
- **Targets:** portable SQLite/Kestrel, PostgreSQL, Linux Native AOT, and Cloudflare Workers + D1.
- **Wasm Worker:** the C# RealWorld Worker builds to ~9.5 MB and is deployed at `reptar-realworld-bench`.
- **Database:** SQLite by default; set `REALWORLD_DATABASE_URL` to run the same routes on PostgreSQL.

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
| `GET /api/tags`                   |         100 |   7,702 |  11 ms |  32 ms |
| `GET /api/articles/:slug`         |         100 |   7,164 |  12 ms |  29 ms |
| `GET /api/articles?limit=20`      |         100 |   6,468 |  13 ms |  34 ms |
| `GET /api/articles/feed?limit=20` |         100 |     721 | 134 ms | 244 ms |
| `GET /api/articles?tag=bench`     |         100 |   6,192 |  14 ms |  36 ms |
| `POST /api/articles`              |          10 |     455 |  19 ms |  47 ms |

The Kestrel app opts into `CacheControl.SWR(ttlSeconds: 15, swrSeconds: 30)`. Anonymous reads are served from the in-process cache, authenticated requests bypass it, and successful writes invalidate the affected path plus `/api/tags` when articles change. Cache-warm read p50 is 11–14 ms on this VPS. Feed requests remain uncached because they are authenticated; they are SQLite/CPU-bound at this concurrency.

List responses omit the article body and fetch tags, favorite counts, favorite state, and follow state in page-level batches. This removes the three correlated subqueries per row; `SqliteDbClient`'s WAL read pool runs those reads concurrently. Article creation batches the article, tag, and junction writes into one transaction. The image is 38.3 MB uncompressed / 11.6 MB compressed after removing the 22 MB `.dbg` symbols.

`synchronous=NORMAL` is a durability tradeoff; override it with `REPTAR_SQLITE_SYNCHRONOUS=FULL` if required.

## PostgreSQL

Set `REALWORLD_DATABASE_URL` (or `DATABASE_URL`) to a `postgres://` or
`postgresql://` connection string to run the same API on PostgreSQL:

```bash
REALWORLD_DATABASE_URL=postgresql://user@127.0.0.1:5432/reptar_realworld \
bun run realworld:dev
```

The official Hurl suite passes 154/154 on PostgreSQL 14. A local PostgreSQL 14
run with 300 seeded articles, 100 read clients, and the same SWR cache measured:

| Scenario | Req/sec | p50 |
| --- | --: | --: |
| `GET /api/tags` | 101,536 | <1 ms |
| `GET /api/articles?limit=20` | 94,112 | <1 ms |
| `GET /api/articles/:slug` | 102,816 | <1 ms |
| `GET /api/articles/feed` | 3,047 | 30 ms |
| `GET /api/articles?tag=bench` | 72,221 | 1 ms |
| `POST /api/articles` | 1,107 | 7 ms |

These are local-machine numbers and are not directly comparable to the VPS
SQLite table. `PostgresDbClient.BatchAsync` now uses a transactional
`NpgsqlBatch`, so article, tag, and junction writes execute in one round-trip.
The RealWorld slug check fetches matching slugs in one query instead of probing
one suffix at a time; a separate unique-title run measured 4,664 writes/sec.
Feed uses the composite follow and author/created indexes; the feed p50 dropped
from 104 ms to 30 ms.

## Edge comparison

A separate Cloudflare Workers + D1 deployment (`reptar-realworld-bench`) passes the same 154-request Hurl suite. From the Lightsail VPS, with 50 read clients and 100 seeded articles:

| Scenario                     | Cold p50 | Cache-warm req/sec | Cache-warm p50 |
| ---------------------------- | -------: | -----------------: | -------------: |
| `GET /api/tags`              |    68 ms |              3,280 |           9 ms |
| `GET /api/articles?limit=20` |   365 ms |              4,358 |           9 ms |
| `GET /api/articles/:slug`    |   145 ms |              4,940 |           8 ms |
| `GET /api/articles/feed`     |   635 ms |                 80 |         494 ms |
| `POST /api/articles`         |   559 ms |                 29 |         289 ms |

The Worker caches anonymous `GET /api/tags`, `GET /api/articles` (base and `?limit=20`), and `GET /api/articles/:slug` for 15 seconds. Authenticated requests bypass the cache, and writes purge the relevant keys. This takes cached list p50 from 365 ms to 9 ms, below the 150 ms edge target. Feed and writes are intentionally uncached. The D1 schema and `0004_realworld_feed_indexes.sql` carry the composite follow and article indexes.

Cloudflare logs show 1–2 ms client RTT from the VPS to the Columbus (CMH) colo, so the uncached edge gap is Worker CPU and D1 time rather than WAN latency. The logs show roughly 150–245 ms CPU time for a cold single-article request.

## AOT lessons

The Native AOT build exposed three framework issues that are now fixed:

1. `SqliteDbClient.DeleteAsync` and `GetAsync` no longer bind primary keys via anonymous types; they use dictionaries so trimming cannot remove the reflected properties.
2. `QueryMappedAsync` allows explicit row mapping without constructor reflection. The RealWorld models use it for AOT-safe reads.
3. `ReptarApp.ExecuteAsync` lets app startup create composite indexes after `EnsureCreatedAsync`; the D1 migration carries the same indexes.
4. Native AOT publishes a large `.dbg` file; the Dockerfile removes it after publish, taking the image from 67.9 MB to 38.3 MB.
5. SQLite WAL/NORMAL/busy_timeout are configurable through `REPTAR_SQLITE_JOURNAL_MODE` and `REPTAR_SQLITE_SYNCHRONOUS`.
6. D1 batch statements accept both JSON-string and array parameters in the host shim; `D1DbClient` now emits the string form. Article creation uses one transaction for the article, tags, and junction rows; PostgreSQL uses a transactional `NpgsqlBatch`.
7. Composite indexes cover `rw_follows(follower_id, followed_id)` and `rw_articles(author_id, created_at)`. PostgreSQL uses them for feed counts and selective ordering; D1 uses the follow index and the article index instead of a sequential scan.
8. RealWorld slug generation now fetches all matching base/prefix slugs in one query. Duplicate-title writes no longer issue one existence probe per suffix.
