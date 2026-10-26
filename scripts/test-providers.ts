#!/usr/bin/env bun
/**
 * Provider matrix: the same data-layer contract against SQLite, Postgres,
 * MySQL, and D1.
 *
 * SQLite always runs. Postgres/MySQL run when their server is reachable; set
 * POSTGRES_TEST=1 / MYSQL_TEST=1 to make an unreachable server a hard failure
 * (CI does this). D1 runs through Miniflare, so it needs no Docker or account.
 *
 * Local databases:
 *   docker compose -f deploy/provider-matrix-compose.yml up -d
 *   POSTGRES_TEST=1 MYSQL_TEST=1 bun run test:providers --require-all
 */

import { spawnSync } from "node:child_process";
import { existsSync } from "node:fs";
import path from "node:path";

const repoRoot = path.resolve(import.meta.dir, "..");
const requireAll = process.argv.includes("--require-all");

const dotnet = (): string => {
  if (process.env.DOTNET_ROOT) {
    return path.join(process.env.DOTNET_ROOT, "dotnet");
  }
  const portable = path.join(repoRoot, ".reptar", "sdk", "dotnet");
  return existsSync(portable) ? portable : "dotnet";
};

const run = (label: string, command: string, args: string[], env = {}) => {
  console.log(`\n==> ${label}`);
  const result = spawnSync(command, args, {
    cwd: repoRoot,
    env: { ...process.env, ...env },
    stdio: "inherit",
  });

  if (result.status !== 0) {
    console.error(`✗ ${label} failed`);
    process.exit(result.status ?? 1);
  }

  console.log(`✓ ${label}`);
};

const hasPostgres = Boolean(process.env.POSTGRES_URL);
const hasMySql = Boolean(process.env.MYSQL_URL);

if (requireAll && (!hasPostgres || !hasMySql)) {
  console.error(
    "Provider matrix requires POSTGRES_URL and MYSQL_URL when --require-all is set."
  );
  process.exit(1);
}

if (!hasPostgres) {
  console.log("• Postgres skipped (set POSTGRES_URL to enable)");
}
if (!hasMySql) {
  console.log("• MySQL skipped (set MYSQL_URL to enable)");
}

run(
  "native provider contract (SQLite, Postgres, MySQL)",
  dotnet(),
  [
    "test",
    "framework/Reptar.Tests/Reptar.Tests.csproj",
    "--filter",
    "FullyQualifiedName~ProviderContract",
    "-v",
    "q",
  ],
  {
    MYSQL_TEST: hasMySql ? "1" : "0",
    POSTGRES_TEST: hasPostgres ? "1" : "0",
  }
);

run("D1 provider contract (Miniflare)", process.execPath, [
  "test",
  "packages/host/src/d1.contract.test.ts",
]);

console.log("\nProvider matrix passed.");
