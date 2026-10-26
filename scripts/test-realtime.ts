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

import { spawn, spawnSync } from "node:child_process";
import { existsSync, rmSync } from "node:fs";
import path from "node:path";

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
      const response = await fetch(url);
      if (response.ok) {
        return true;
      }
    } catch {
      // not up yet
    }
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
    }
  );
  return result.status === 0;
};

/** Kill a spawned tree: the muxer, its children, and anything on the port. */
const killTree = (pid: number | undefined, port: number): void => {
  if (pid) {
    try {
      process.kill(pid, "SIGKILL");
    } catch {}
    try {
      spawnSync("pkill", ["-P", String(pid)]);
    } catch {}
  }
  // Kill the wrangler parent too; otherwise it respawns workerd on the port
  // after the listener is killed.
  try {
    spawnSync("pkill", ["-f", `wrangler.*--port ${port}`], {
      stdio: "ignore",
    });
  } catch {
    // pkill unavailable
  }

  // Belt and braces: kill whatever still listens on the port (the orphaned
  // runfile app process survives its muxer).
  try {
    const listeners = spawnSync(
      "lsof",
      ["-nP", `-iTCP:${port}`, "-sTCP:LISTEN", "-t"],
      { encoding: "utf-8" }
    );
    for (const line of listeners.stdout.trim().split("\n")) {
      const listenerPid = Number.parseInt(line, 10);
      if (Number.isFinite(listenerPid) && listenerPid !== process.pid) {
        try {
          process.kill(listenerPid, "SIGKILL");
        } catch {}
      }
    }
  } catch {}
};

const runWorkersSmoke = async (port: number): Promise<boolean> => {
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
  spawnSync(
    "bunx",
    [
      "wrangler",
      "d1",
      "execute",
      "reptar-rooms-local",
      "--local",
      "--file",
      "migrations/0001_room_messages.sql",
    ],
    { cwd: workerDir, stdio: "ignore" }
  );

  // A previous smoke run can leave wrangler/workerd holding the port; clear
  // it before starting so the health check measures this run, not a stale one.
  killTree(undefined, port);
  console.log(`[workers host] starting wrangler dev on :${port}...`);
  const wrangler = spawn("bunx", ["wrangler", "dev", "--port", String(port)], {
    cwd: workerDir,
    stdio: "ignore",
  });

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
    killTree(wrangler.pid, port);
  }
};

const runKestrelSmoke = async (port: number): Promise<boolean> => {
  const dbPath = path.join(roomsDir, "rooms.db");
  for (const suffix of ["", "-shm", "-wal"]) {
    rmSync(`${dbPath}${suffix}`, { force: true });
  }

  killTree(undefined, port);
  console.log(`\n[kestrel host] starting dotnet run app.cs on :${port}...`);
  const server = spawn(dotnet(), ["run", "app.cs"], {
    cwd: roomsDir,
    env: {
      ...process.env,
      // IPv4-only listen dodges the macOS accept() stall (see LEARNINGS §2.3)
      HOST: "0.0.0.0",
      PORT: String(port),
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
    killTree(server.pid, port);
  }
};

export const runRealtimeSmoke = async (options: {
  kestrelOnly?: boolean;
  workersOnly?: boolean;
}): Promise<boolean> => {
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
    workersPassed = await runWorkersSmoke(workersPort);
  }
  if (!options.workersOnly) {
    kestrelPassed = await runKestrelSmoke(kestrelPort);
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
