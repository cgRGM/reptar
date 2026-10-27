#!/usr/bin/env bun
/**
 * reptar test:realtime — two-host protocol smoke for the realtime-rooms hub.
 *
 * Runs examples/realtime-rooms/smoke.ts against BOTH hosts:
 *   1. NetWasm/Workers (local workerd + RoomsHub Durable Object + D1)
 *   2. Native Kestrel (SQLite)
 *
 * A protocol regression that only appears on one host (thread safety, the
 * macOS accept bug, WS event-lifetime cancellation, D1 param marshaling)
 * gets caught here instead of on a deployed worker.
 */

import { spawnSync } from "node:child_process";
import { existsSync, mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";

import {
  assertPortAvailable,
  spawnOwnedProcess,
} from "./testing/owned-process";

const repoRoot = path.resolve(import.meta.dir, "..");
const roomsDir = path.join(repoRoot, "examples/realtime-rooms");
const workerDir = path.join(roomsDir, "worker");

const dotnet = (): string => {
  if (process.env.DOTNET_ROOT) {
    return path.join(process.env.DOTNET_ROOT, "dotnet");
  }
  const portable = path.join(repoRoot, ".reptar", "sdk", "dotnet");
  return existsSync(portable) ? portable : "dotnet";
};

const waitForHealth = async (
  url: string,
  timeoutMs: number
): Promise<boolean> => {
  const startedAt = Date.now();
  while (Date.now() - startedAt < timeoutMs) {
    try {
      // eslint-disable-next-line no-await-in-loop -- Poll until this host becomes ready.
      const response = await fetch(url, { signal: AbortSignal.timeout(1500) });
      if (response.ok) {
        return true;
      }
    } catch {
      // not up yet
    }
    // eslint-disable-next-line no-await-in-loop -- Back off between readiness attempts.
    await Bun.sleep(1500);
  }
  return false;
};

const runSmoke = (wsUrl: string, host: string): boolean => {
  const result = spawnSync(
    process.execPath,
    [path.join(roomsDir, "smoke.ts")],
    {
      cwd: roomsDir,
      env: {
        ...process.env,
        BENCH_HOST: host,
        BENCH_WS_URL: wsUrl,
      },
      stdio: "inherit",
      timeout: 90_000,
    }
  );
  return result.status === 0;
};

const runWorkersSmoke = async (
  port: number,
  statePath: string
): Promise<boolean> => {
  console.log("\n[workers host] building NetWasm worker...");
  const build = spawnSync(
    process.execPath,
    [path.join(repoRoot, "scripts/reptar.ts"), "build:worker:rooms"],
    { stdio: "inherit" }
  );
  if (build.status !== 0) {
    console.error("✗ workers build failed");
    return false;
  }

  console.log("[workers host] applying local D1 migration...");
  const migration = spawnSync(
    "bunx",
    [
      "wrangler",
      "d1",
      "execute",
      "reptar-rooms-local",
      "--local",
      "--persist-to",
      statePath,
      "--file",
      "migrations/0001_room_messages.sql",
    ],
    { cwd: workerDir, stdio: "ignore" }
  );

  if (migration.status !== 0) {
    throw new Error("Disposable local D1 migration failed.");
  }
  await assertPortAvailable(port);
  console.log(`[workers host] starting wrangler dev on :${port}...`);
  const wrangler = await spawnOwnedProcess(
    "bunx",
    [
      "wrangler",
      "dev",
      "--local",
      "--ip",
      "127.0.0.1",
      "--port",
      String(port),
      "--persist-to",
      statePath,
    ],
    {
      cwd: workerDir,
      stdio: "ignore",
    }
  );

  try {
    const healthy = await waitForHealth(
      `http://127.0.0.1:${port}/health`,
      90_000
    );
    if (!healthy) {
      console.error(`✗ worker did not become healthy on :${port}`);
      return false;
    }
    console.log(
      `[workers host] running smoke against ws://127.0.0.1:${port}...`
    );
    return runSmoke(`ws://127.0.0.1:${port}`, `http://127.0.0.1:${port}`);
  } finally {
    await wrangler.stop();
  }
};

const runKestrelSmoke = async (
  port: number,
  dbPath: string
): Promise<boolean> => {
  await assertPortAvailable(port);
  console.log(`\n[kestrel host] starting dotnet run app.cs on :${port}...`);
  const server = await spawnOwnedProcess(dotnet(), ["run", "app.cs"], {
    cwd: roomsDir,
    env: {
      ...process.env,
      // IPv4-only listen dodges the macOS accept() stall (see LEARNINGS §2.3)
      HOST: "127.0.0.1",
      PORT: String(port),
      REPTAR_ROOMS_DB: dbPath,
    },
    stdio: "ignore",
  });

  try {
    const healthy = await waitForHealth(
      `http://127.0.0.1:${port}/health`,
      180_000
    );
    if (!healthy) {
      console.error(`✗ Kestrel server did not become healthy on :${port}`);
      return false;
    }
    console.log(
      `[kestrel host] running smoke against ws://127.0.0.1:${port}...`
    );
    return runSmoke(`ws://127.0.0.1:${port}`, `http://127.0.0.1:${port}`);
  } finally {
    await server.stop();
  }
};

export const runRealtimeSmoke = async (options: {
  kestrelOnly?: boolean;
  workersOnly?: boolean;
}): Promise<boolean> => {
  if (options.kestrelOnly && options.workersOnly) {
    throw new Error("Choose at most one realtime smoke host filter.");
  }
  let workersPassed = true;
  let kestrelPassed = true;

  // Allow concurrent agents/CI jobs to avoid the default ports.
  const workersPort = Number(
    process.env.REPTAR_REALTIME_WORKERS_PORT ?? "8790"
  );
  const kestrelPort = Number(
    process.env.REPTAR_REALTIME_KESTREL_PORT ?? "8791"
  );

  if (!options.kestrelOnly) {
    await assertPortAvailable(workersPort);
  }
  if (!options.workersOnly) {
    await assertPortAvailable(kestrelPort);
  }
  if (
    !options.kestrelOnly &&
    !options.workersOnly &&
    workersPort === kestrelPort
  ) {
    throw new Error("Realtime smoke hosts must use distinct ports.");
  }
  const stateRoot = mkdtempSync(path.join(tmpdir(), "reptar-realtime-"));
  try {
    if (!options.kestrelOnly) {
      workersPassed = await runWorkersSmoke(
        workersPort,
        path.join(stateRoot, "worker")
      );
    }
    if (!options.workersOnly) {
      kestrelPassed = await runKestrelSmoke(
        kestrelPort,
        path.join(stateRoot, "rooms.db")
      );
    }
  } finally {
    rmSync(stateRoot, { force: true, recursive: true });
  }

  console.log("\n──────────────────────────────");
  if (!options.kestrelOnly) {
    console.log(
      `${workersPassed ? "✓" : "✗"} workers host (workerd + DO + D1): ${workersPassed ? "passed" : "FAILED"}`
    );
  }
  if (!options.workersOnly) {
    console.log(
      `${kestrelPassed ? "✓" : "✗"} kestrel host (SQLite): ${kestrelPassed ? "passed" : "FAILED"}`
    );
  }
  return workersPassed && kestrelPassed;
};

if (import.meta.main) {
  const args = new Set(process.argv.slice(2));
  const ok = await runRealtimeSmoke({
    kestrelOnly: args.has("--kestrel"),
    workersOnly: args.has("--workers"),
  });
  process.exit(ok ? 0 : 1);
}
