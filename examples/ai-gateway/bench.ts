#!/usr/bin/env bun
/* eslint-disable eslint/no-await-in-loop -- bounded-concurrency batches and drain polling are sequential by design */
/**
 * Reptar AI Gateway — Load & Concurrency Benchmark
 *
 * Drives the gateway (`examples/ai-gateway/app.cs`) with 500 concurrent
 * streaming LLM requests and measures:
 *
 *   1. RPS throughput  — via `oha` streaming POSTs against /v1/chat/completions,
 *                        once with `?no_cache=1` (full proxy path) and once with
 *                        the prompt cache warm (SHA-256 replay path).
 *   2. TTFT p50 / p99  — Time To First Token, measured natively in Bun by
 *                        streaming responses and timing the first chunk.
 *   3. Prompt cache    — identical-prompt TTFT vs first-token TTFT (proves
 *                        sub-millisecond server-side cache hits).
 *   4. Process RSS     — resident memory of the gateway process.
 *
 * Usage:
 *   bun examples/ai-gateway/bench.ts [--port 8788] [--concurrency 500]
 *                                    [--requests 500] [--duration 10s]
 *                                    [--cache-requests 1000]
 *
 * The harness boots the gateway itself (PORT must be free) unless a server is
 * already listening on the port. Requires `oha` and `lsof` on PATH.
 *
 * macOS note: raise the file descriptor limit before large runs:
 *   ulimit -n 10000 && bun examples/ai-gateway/bench.ts
 */

import { spawn, spawnSync } from "node:child_process";
import { existsSync, mkdirSync, writeFileSync } from "node:fs";
import path from "node:path";

const repoRoot = path.resolve(import.meta.dir, "../..");

// ── CLI flags ────────────────────────────────────────────────────────────────

const flag = (name: string, fallback: string): string => {
  const index = process.argv.indexOf(`--${name}`);
  return index !== -1 && process.argv[index + 1]
    ? process.argv[index + 1]
    : fallback;
};

const PORT = Math.trunc(Number(flag("port", "8788")));
const CONCURRENCY = Math.trunc(Number(flag("concurrency", "500")));
const REQUESTS = Math.trunc(Number(flag("requests", "500")));
const DURATION = flag("duration", "10s");
const CACHE_REQUESTS = Math.trunc(Number(flag("cache-requests", "1000")));
const BASE_URL = `http://127.0.0.1:${PORT}`;

// ── Utilities ────────────────────────────────────────────────────────────────

const wait = (ms: number): Promise<void> => Bun.sleep(ms);

const pollHealth = async (timeoutMs: number): Promise<boolean> => {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      const response = await fetch(`${BASE_URL}/health`);
      if (response.ok) {
        return true;
      }
    } catch {
      // Server is not accepting connections yet.
    }
    await wait(250);
  }
  return false;
};

const percentile = (samples: number[], p: number): number => {
  if (samples.length === 0) {
    return 0;
  }
  const sorted = samples.toSorted((a, b) => a - b);
  const index = Math.min(
    sorted.length - 1,
    Math.ceil((p / 100) * sorted.length) - 1
  );
  return sorted[Math.max(0, index)] ?? 0;
};

const formatMs = (value: number): string => {
  if (value >= 100) {
    return `${Math.round(value)} ms`;
  }
  if (value >= 1) {
    return `${value.toFixed(2)} ms`;
  }
  return `${(value * 1000).toFixed(1)} µs`;
};

const chatBody = (prompt: string, stream = true): string =>
  JSON.stringify({
    messages: [{ content: prompt, role: "user" }],
    model: "mock-mini",
    stream,
  });

// ── Server lifecycle ─────────────────────────────────────────────────────────

let serverProcess: ReturnType<typeof spawn> | null = null;
let serverBootedHere = false;

const listeningPid = (): number | null => {
  const result = spawnSync("lsof", ["-ti", `tcp:${PORT}`, "-sTCP:LISTEN"], {
    encoding: "utf-8",
  });
  const pid = Math.trunc(Number(result.stdout.trim()));
  return Number.isFinite(pid) ? pid : null;
};

const rssKb = (): number | null => {
  const pid = listeningPid();
  if (pid === null) {
    return null;
  }
  const result = spawnSync("ps", ["-o", "rss=", "-p", String(pid)], {
    encoding: "utf-8",
  });
  const rss = Math.trunc(Number(result.stdout.trim()));
  return Number.isFinite(rss) ? rss : null;
};

const stopServer = async (): Promise<void> => {
  // Never kill a server we did not start.
  if (!serverBootedHere) {
    return;
  }
  const pid = listeningPid();
  if (pid !== null) {
    process.kill(pid, "SIGTERM");
    await wait(500);
  }
  if (serverProcess?.pid) {
    try {
      serverProcess.kill("SIGTERM");
    } catch {
      // Process already exited.
    }
  }
};

const startServer = async (): Promise<boolean> => {
  try {
    const response = await fetch(`${BASE_URL}/health`);
    if (response.ok) {
      console.log(`▸ Reusing gateway already listening on :${PORT}`);
      return true;
    }
  } catch {
    // No server on the port — boot one below.
  }

  console.log(
    `▸ Booting gateway (bun scripts/reptar.ts gateway:dev) on :${PORT} …`
  );
  serverProcess = spawn(
    process.execPath,
    [path.join(repoRoot, "scripts/reptar.ts"), "gateway:dev"],
    {
      cwd: repoRoot,
      env: { ...process.env, GATEWAY_TOKEN_DELAY_MS: "0", PORT: String(PORT) },
      stdio: ["ignore", "inherit", "inherit"],
    }
  );

  const ready = await pollHealth(90_000);
  serverBootedHere = ready;
  if (!ready) {
    console.error("✗ Gateway did not become healthy within 90s.");
    await stopServer();
    process.exit(1);
  }
  console.log("▸ Gateway is healthy.\n");
  return true;
};

// ── Phase 1: oha RPS throughput ──────────────────────────────────────────────

interface OhaResult {
  latencyP50: number;
  latencyP99: number;
  rps: number;
  successRate: number;
}

const runOha = (label: string, bypassCache: boolean): OhaResult | null => {
  console.log(`▸ oha — ${label}: ${CONCURRENCY} concurrency for ${DURATION}`);

  // One-line JSON bodies; -Z cycles them across requests. With ?no_cache=1
  // every request exercises the full proxy path (parse → provider → frames);
  // with the cache enabled the warm prompt short-circuits upstream work.
  const bodiesFile = path.join(import.meta.dir, ".bench-bodies.jsonl");
  const bodies: string[] = [];
  for (let index = 0; index < REQUESTS; index += 1) {
    bodies.push(
      chatBody(`bench prompt ${index} ${Math.random().toString(36).slice(2)}`)
    );
  }
  writeFileSync(bodiesFile, `${bodies.join("\n")}\n`);

  const query = bypassCache ? "?no_cache=1" : "";

  const result = spawnSync(
    "oha",
    [
      "-m",
      "POST",
      "-H",
      "content-type: application/json",
      "-H",
      "accept: text/event-stream",
      "-Z",
      bodiesFile,
      "-c",
      String(CONCURRENCY),
      "-z",
      DURATION,
      "--no-tui",
      "--http-version",
      "1.1",
      "--output-format",
      "json",
      `${BASE_URL}/v1/chat/completions${query}`,
    ],
    { encoding: "utf-8", maxBuffer: 32 * 1024 * 1024, timeout: 120_000 }
  );

  if (result.status !== 0 || !result.stdout) {
    console.error(
      `✗ oha failed (exit ${result.status}): ${(result.stderr || result.stdout || "").slice(0, 400)}`
    );
    return null;
  }

  interface OhaJson {
    metrics: {
      success_rate: number;
      requests_per_sec: number;
      latency_ms: { p50?: number; p99?: number };
    };
    statusCodeDistribution: Record<string, number>;
  }

  const report = JSON.parse(result.stdout) as OhaJson;
  const rps = report.metrics?.requests_per_sec ?? 0;
  const p50 = report.metrics?.latency_ms?.p50 ?? 0;
  const p99 = report.metrics?.latency_ms?.p99 ?? 0;
  const successRate = report.metrics?.success_rate ?? 0;

  console.log(
    `  ${rps.toFixed(0)} req/s — success ${(successRate * 100).toFixed(1)}% — ` +
      `p50 ${formatMs(p50)} · p99 ${formatMs(p99)} — ` +
      `statuses ${JSON.stringify(report.statusCodeDistribution ?? {})}\n`
  );

  return { latencyP50: p50, latencyP99: p99, rps, successRate };
};

// ── Phase 2: native TTFT measurement (time to first streamed token) ──────────

const runTtft = async (
  label: string,
  prompts: string[]
): Promise<{ p50: number; p99: number; mean: number }> => {
  const ttfts: number[] = [];

  const measure = async (prompt: string): Promise<void> => {
    const started = performance.now();
    try {
      const response = await fetch(`${BASE_URL}/v1/chat/completions`, {
        body: chatBody(prompt),
        headers: {
          accept: "text/event-stream",
          "content-type": "application/json",
        },
        method: "POST",
      });
      if (!response.body) {
        return;
      }
      const reader = response.body.getReader();
      // First frame received = time to first token.
      await reader.read();
      ttfts.push(performance.now() - started);
      void reader.cancel();
    } catch {
      // Failed streams are excluded from the TTFT distribution.
    }
  };

  // Fire requests in bounded-concurrency batches.
  for (let offset = 0; offset < prompts.length; offset += CONCURRENCY) {
    await Promise.all(prompts.slice(offset, offset + CONCURRENCY).map(measure));
  }

  const p50 = percentile(ttfts, 50);
  const p99 = percentile(ttfts, 99);
  const mean =
    ttfts.reduce((sum, value) => sum + value, 0) / Math.max(1, ttfts.length);
  console.log(
    `  ${label}: ${ttfts.length} streams — TTFT p50 ${formatMs(p50)} · p99 ${formatMs(p99)} · mean ${formatMs(mean)}`
  );
  return { mean, p50, p99 };
};

// ── Phase 3: prompt cache benchmark (identical prompts → HIT path) ───────────

const runCacheBenchmark = async (): Promise<{
  p50: number;
  p99: number;
  mean: number;
}> => {
  const identical = Array.from(
    { length: CACHE_REQUESTS },
    () => "identical cache benchmark prompt"
  );
  const warmupPrompt = identical[0] ?? "warmup";

  // First request warms the cache (MISS), the rest replay it (HIT).
  const first = await fetch(`${BASE_URL}/v1/chat/completions`, {
    body: chatBody(warmupPrompt, false),
    headers: { "content-type": "application/json" },
    method: "POST",
  });
  const firstHeader = first.headers.get("x-reptar-cache") ?? "?";
  await first.text();

  console.log(
    `▸ Phase 3 — prompt cache: ${CACHE_REQUESTS} identical prompts (first was ${firstHeader})\n`
  );
  return runTtft("  cache HIT streams", identical);
};

// ── Main ──────────────────────────────────────────────────────────────────────

const main = async (): Promise<void> => {
  console.log("Reptar AI Gateway — Streaming Load & Concurrency Benchmark");
  console.log(
    "══════════════════════════════════════════════════════════════════\n"
  );

  const ohaCheck = spawnSync("oha", ["--version"], { encoding: "utf-8" });
  if (ohaCheck.status !== 0) {
    console.error("✗ `oha` is required. Install with: brew install oha");
    process.exit(1);
  }

  await startServer();
  await fetch(`${BASE_URL}/gateway/reset`, { method: "POST" });

  console.log("▸ Phase 1 — throughput (streaming POST)\n");
  const ohaUncached = runOha("uncached path (no_cache=1)", true);
  const ohaCached = runOha("prompt-cache hits", false);

  console.log(`▸ Phase 2 — TTFT: ${REQUESTS} unique concurrent streams\n`);
  const ttftResult = await runTtft(
    "  miss streams",
    Array.from(
      { length: REQUESTS },
      (_, index) => `ttft probe ${index} ${Math.random().toString(36).slice(2)}`
    )
  );

  const cacheResult = await runCacheBenchmark();

  // The gateway logs fire-and-forget through the SQLite group-commit batcher.
  // Wait until the committed row count stabilizes so the final stats read
  // reflects the queue actually draining, not a mid-flight snapshot.
  console.log("\n▸ Waiting for telemetry group-commit queue to drain …");
  // Poll committed rows until the count stabilizes (60s cap). The gateway
  // writes telemetry fire-and-forget, so a large burst can outpace the
  // SQLite group-commit writer; this reports the committed-row rate honestly.
  const maxDrainPolls = 60;
  let previous = -1;
  let first = -1;
  let stabilized = false;
  for (let attempt = 0; attempt < maxDrainPolls; attempt += 1) {
    const statsResponse = await fetch(`${BASE_URL}/gateway/stats`);
    const current =
      ((await statsResponse.json()) as Record<string, number>)[
        "total_requests"
      ] ?? 0;
    if (first === -1) {
      first = current;
    }
    if (current === previous && current > 0) {
      stabilized = true;
      break;
    }
    previous = current;
    await wait(1000);
  }
  const committedPerSec = Math.max(0, previous - first) / 60;
  console.log(
    stabilized
      ? `  Queue drained (${previous} rows committed).\n`
      : `  ${previous} rows committed so far (~${committedPerSec.toFixed(0)} rows/s — queue still draining).\n`
  );

  const rss = rssKb();
  const statsResponse = await fetch(`${BASE_URL}/gateway/stats`);
  const stats = (await statsResponse.json()) as Record<string, number>;

  console.log(
    "\n══════════════════════════════════════════════════════════════════"
  );
  console.log("Summary");
  console.log("──────────────────────────────────────────────────────────────");
  if (ohaUncached) {
    console.log(
      `  Throughput uncached (${CONCURRENCY} conn):  ${ohaUncached.rps.toFixed(0)} req/s (p50 ${formatMs(ohaUncached.latencyP50)} · p99 ${formatMs(ohaUncached.latencyP99)})`
    );
  }
  if (ohaCached) {
    console.log(
      `  Throughput prompt-cache hits:         ${ohaCached.rps.toFixed(0)} req/s (p50 ${formatMs(ohaCached.latencyP50)} · p99 ${formatMs(ohaCached.latencyP99)})`
    );
  }
  console.log(
    `  Streaming TTFT (cache miss):          p50 ${formatMs(ttftResult.p50)} · p99 ${formatMs(ttftResult.p99)}`
  );
  console.log(
    `  Streaming TTFT (prompt cache HIT):    p50 ${formatMs(cacheResult.p50)} · p99 ${formatMs(cacheResult.p99)}`
  );
  if (rss !== null) {
    console.log(
      `  Gateway RSS:                          ${(rss / 1024).toFixed(1)} MB (pid ${listeningPid()})`
    );
  }
  if (stats) {
    console.log(
      `  Telemetry rows committed:             ${stats["total_requests"] ?? 0}`
    );
    console.log(
      `  Cache hit rate (server view):         ${stats["cache_hit_rate"] ?? 0}%`
    );
  }
  console.log("──────────────────────────────────────────────────────────────");

  const resultsDir = path.join(repoRoot, "examples/ai-gateway/results");
  if (!existsSync(resultsDir)) {
    mkdirSync(resultsDir, { recursive: true });
  }
  const resultPath = path.join(
    resultsDir,
    `bench-${new Date().toISOString().replaceAll(/[:.]/gu, "-")}.json`
  );
  writeFileSync(
    resultPath,
    JSON.stringify(
      {
        concurrency: CONCURRENCY,
        ohaCached,
        ohaUncached,
        requests: REQUESTS,
        rssKb: rss,
        serverStats: stats,
        target: BASE_URL,
        timestamp: new Date().toISOString(),
        ttftCacheHit: cacheResult,
        ttftMiss: ttftResult,
      },
      null,
      2
    )
  );
  console.log(`\n▸ Results written to ${path.relative(repoRoot, resultPath)}`);

  await stopServer();
};

process.on("SIGINT", () => {
  if (serverBootedHere) {
    const pid = listeningPid();
    if (pid !== null) {
      process.kill(pid, "SIGTERM");
    }
    if (serverProcess?.pid) {
      serverProcess.kill("SIGTERM");
    }
  }
  process.exit(130);
});

await main();
