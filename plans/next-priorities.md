# Next Priorities

The provider matrix is the current trust gate. Everything below is deliberately queued behind it, in rough priority order.

## 1. One production-shaped reference app, end to end

Build a single app that exercises auth, Stripe checkout + webhooks, queues, realtime, object storage, migrations, OTLP, and metrics — then run the same suite against:

- Kestrel / Native AOT
- Cloudflare Workers / NetWasm / D1
- PostgreSQL and MySQL variants of the native host

This catches integration gaps that isolated unit tests cannot: webhook idempotency, transaction boundaries around payments, queue retry behavior, storage parity, and auth cookie/redirect behavior behind a proxy.

## 2. Failure UX

Every framework failure should answer three questions without a debugger:

- what failed (migration checksum, missing binding, unsupported provider capability, invalid route pattern);
- where it failed (file/route/table/migration id);
- how to fix it (the exact command or config key).

Priorities:

- provider-capability errors at startup, not first request;
- migration drift, lock contention, and pending-migration output;
- missing bindings (`DB`, `ROOMS`, `R2`, `KV`) reported at boot;
- request-id-correlated error bodies and logs.

## 3. API stability

- Freeze the public surface for 0.1.
- Add an upgrade guide and deprecation policy.
- Stop adding modules until the reference app is green on both hosts.

## 4. Performance gates after correctness

- Benchmark the new typed `Rule<T>` path against manual validation in CI.
- Keep the existing RPS/p95 gate.
- Add a provider-matrix latency smoke for transactions and batch writes.

## 5. Residual items

- OTLP **span** export (logs already export).
- Axiom-specific transport (use OTLP for now).
- `Rule<T>` invalid-path allocations (~656 B per failing validation).
- Migration lease renewal for migrations longer than 30 minutes.
- Redis/KV `ISwrCacheStore` adapters (file-backed and in-memory exist).
- Standalone NuGet-based scaffold path (`create-reptar` currently builds file-based against a clone).
- Repo-wide `bun run check` cleanup outside the CLI scripts: generated browser bundles, `packages/host`, `packages/rpc`, and a few examples still have pre-existing Ultracite findings.
- MySQL live provider test in local development (CI has a service container; local machines without Docker only run SQLite/Postgres/D1).
