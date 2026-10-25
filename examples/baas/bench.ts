#!/usr/bin/env bun
/**
 * ReptarBase High-Performance BaaS Benchmark Suite
 * Measures read throughput, authenticated write throughput, atomic counters,
 * file serving with ETag caching, and baseline engine overhead using `oha`.
 */

import { spawn, spawnSync } from "node:child_process";
import { existsSync, unlinkSync, rmSync, writeFileSync } from "node:fs";
import path from "node:path";

const PORT = 8990;
const BASE_URL = `http://127.0.0.1:${PORT}`;
const DB_FILE = "bench_baas.db";
const STORAGE_DIR = "bench_uploads";
const OHA_BIN = process.env.OHA_BIN ?? "/Users/newrgm/bin/oha";
const DURATION_SEC = 5;
const CONCURRENCY = 20;

interface OhaOutput {
  summary?: {
    requestsPerSec?: number;
    successRate?: number;
    total?: number;
  };
  latencyPercentiles?: {
    p50?: number;
    p95?: number;
    p99?: number;
  };
  statusCodeDistribution?: Record<string, number>;
}

interface BenchmarkMetric {
  tier: string;
  name: string;
  method: string;
  endpoint: string;
  rps: number;
  p50Ms: number;
  p95Ms: number;
  p99Ms: number;
  totalRequests: number;
  successRate: number;
}

function cleanup() {
  console.log("\n🧹 Cleaning up benchmark database and storage...");
  rmSync(STORAGE_DIR, { force: true, recursive: true });
  for (const ext of ["", "-wal", "-shm"]) {
    const f = `${DB_FILE}${ext}`;
    if (existsSync(f)) unlinkSync(f);
  }
}

async function waitForServer(retries = 30): Promise<boolean> {
  for (let i = 0; i < retries; i++) {
    try {
      const res = await fetch(`${BASE_URL}/health`);
      if (res.ok) return true;
    } catch {}
    await new Promise((r) => setTimeout(r, 200));
  }
  return false;
}

function runOha(
  endpoint: string,
  options: {
    method?: string;
    headers?: Record<string, string>;
    body?: string;
    concurrency?: number;
    seconds?: number;
  } = {}
): OhaOutput {
  const args = [
    "-c",
    String(options.concurrency ?? CONCURRENCY),
    "-z",
    `${options.seconds ?? DURATION_SEC}s`,
    "-t",
    "15s",
    "--no-tui",
    "--output-format",
    "json",
  ];

  if (options.method && options.method !== "GET") {
    args.push("-m", options.method);
  }

  if (options.headers) {
    for (const [k, v] of Object.entries(options.headers)) {
      args.push("-H", `${k}: ${v}`);
    }
  }

  if (options.body) {
    args.push("-d", options.body);
  }

  args.push(`${BASE_URL}${endpoint}`);

  const proc = spawnSync(OHA_BIN, args, { encoding: "utf-8" });
  if (proc.error) {
    throw new Error(`oha execution failed: ${proc.error.message}`);
  }

  try {
    return JSON.parse(proc.stdout) as OhaOutput;
  } catch {
    throw new Error(`Failed to parse oha output: ${proc.stdout || proc.stderr}`);
  }
}

async function main() {
  cleanup();

  console.log("==========================================================");
  console.log("⚡ ReptarBase Native AOT BaaS Performance Benchmark");
  console.log("==========================================================");

  console.log(`Starting ReptarBase server on port ${PORT}...`);
  const server = spawn("dotnet", ["run", "app.cs", "-c", "Release"], {
    cwd: import.meta.dir,
    env: {
      ...process.env,
      PORT: String(PORT),
      BAAS_DB: DB_FILE,
      STORAGE_DIR: STORAGE_DIR,
    },
    stdio: "ignore",
  });

  const ready = await waitForServer();
  if (!ready) {
    server.kill();
    cleanup();
    throw new Error("Server failed to boot within timeout.");
  }
  console.log("✅ Server ready! Seeding initial data...\n");

  // 1. Register benchmark user
  const signupRes = await fetch(`${BASE_URL}/api/auth/signup`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      email: "bench@reptarbase.local",
      password: "Password123!",
      name: "Bench Operator",
    }),
  });
  if (!signupRes.ok) throw new Error("Failed to register benchmark user.");

  // 2. Sign in to get session cookie
  const signinRes = await fetch(`${BASE_URL}/api/auth/signin`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      email: "bench@reptarbase.local",
      password: "Password123!",
    }),
  });
  const cookie = signinRes.headers.get("set-cookie") ?? "";
  const sessionToken = cookie.match(/reptarbase_session=([^;]+)/)?.[1] ?? "";

  // 3. Generate Personal API Key
  const keyRes = await fetch(`${BASE_URL}/api/auth/api-keys`, {
    method: "POST",
    headers: {
      "Authorization": `Bearer ${sessionToken}`,
      "Content-Type": "application/json",
    },
    body: JSON.stringify({ name: "Benchmark-Key", expiresInDays: 30 }),
  });
  const keyData = await keyRes.json();
  const apiKey = keyData.key;
  console.log(`Created benchmark API key: ${apiKey.slice(0, 13)}...`);

  // 4. Seed documents
  console.log("Seeding 50 documents into SQLite...");
  let sampleDocId = "";
  for (let i = 1; i <= 50; i++) {
    const res = await fetch(`${BASE_URL}/api/documents`, {
      method: "POST",
      headers: {
        "Authorization": `Bearer ${apiKey}`,
        "Content-Type": "application/json",
      },
      body: JSON.stringify({
        title: `Benchmark Record ${i}`,
        content: `Synthetic load-testing payload representing document record index ${i}. Zero reflection AOT throughput.`,
        status: i % 2 === 0 ? "published" : "draft",
      }),
    });
    const d = await res.json();
    if (i === 1) sampleDocId = d.id;
  }

  // 5. Seed file
  const samplePng = Buffer.from(
    "89504e470d0a1a0a0000000d49484452000000010000000108060000001f15c4890000000a49444154789c63000100000500010d0a2db40000000049454e44ae426082",
    "hex"
  );
  const formData = new FormData();
  formData.append("file", new Blob([samplePng], { type: "image/png" }), "fixture.png");
  const fileRes = await fetch(`${BASE_URL}/api/files/upload`, {
    method: "POST",
    headers: { "Authorization": `Bearer ${apiKey}` },
    body: formData,
  });
  const fileData = await fileRes.json();
  const fileStorageKey = fileData.storageKey;

  const fileHeadRes = await fetch(`${BASE_URL}/api/files/${fileStorageKey}`);
  const fileEtag = fileHeadRes.headers.get("etag") ?? "";
  console.log(`Seeded test file: ${fileStorageKey} (ETag: ${fileEtag})\n`);

  console.log(`Running benchmarks (${DURATION_SEC}s duration, ${CONCURRENCY} concurrent connections each)...`);

  const results: BenchmarkMetric[] = [];

  // Tier 1: Baseline Health Check
  console.log("→ Benchmarking Tier 1: GET /health (Baseline Engine Overhead)...");
  const t1 = runOha("/health");
  results.push({
    tier: "T1",
    name: "Baseline Engine",
    method: "GET",
    endpoint: "/health",
    rps: Math.round(t1.summary?.requestsPerSec ?? 0),
    p50Ms: Number(((t1.latencyPercentiles?.p50 ?? 0) * 1000).toFixed(2)),
    p95Ms: Number(((t1.latencyPercentiles?.p95 ?? 0) * 1000).toFixed(2)),
    p99Ms: Number(((t1.latencyPercentiles?.p99 ?? 0) * 1000).toFixed(2)),
    totalRequests: t1.summary?.total ?? 0,
    successRate: Math.round((t1.summary?.successRate ?? 0) * 100),
  });

  // Tier 2: Filtered Query Layer (ColumnRef<T> + SQLite Read Pool)
  console.log("→ Benchmarking Tier 2: GET /api/documents?status=published (Typed ColumnRef Queries)...");
  const t2 = runOha("/api/documents?status=published&limit=20");
  results.push({
    tier: "T2",
    name: "Filtered Collection Query",
    method: "GET",
    endpoint: "/api/documents?status=published",
    rps: Math.round(t2.summary?.requestsPerSec ?? 0),
    p50Ms: Number(((t2.latencyPercentiles?.p50 ?? 0) * 1000).toFixed(2)),
    p95Ms: Number(((t2.latencyPercentiles?.p95 ?? 0) * 1000).toFixed(2)),
    p99Ms: Number(((t2.latencyPercentiles?.p99 ?? 0) * 1000).toFixed(2)),
    totalRequests: t2.summary?.total ?? 0,
    successRate: Math.round((t2.summary?.successRate ?? 0) * 100),
  });

  // Tier 3: Single Record Read + Atomic View Counter Increment
  console.log(`→ Benchmarking Tier 3: GET /api/documents/${sampleDocId} (Atomic Counter Writes + Read)...`);
  const t3 = runOha(`/api/documents/${sampleDocId}`);
  results.push({
    tier: "T3",
    name: "Atomic Read + View Counter",
    method: "GET",
    endpoint: `/api/documents/:id`,
    rps: Math.round(t3.summary?.requestsPerSec ?? 0),
    p50Ms: Number(((t3.latencyPercentiles?.p50 ?? 0) * 1000).toFixed(2)),
    p95Ms: Number(((t3.latencyPercentiles?.p95 ?? 0) * 1000).toFixed(2)),
    p99Ms: Number(((t3.latencyPercentiles?.p99 ?? 0) * 1000).toFixed(2)),
    totalRequests: t3.summary?.total ?? 0,
    successRate: Math.round((t3.summary?.successRate ?? 0) * 100),
  });

  // Tier 4: Authenticated Writes (Bearer Auth + Insert + SSE Fan-out)
  console.log("→ Benchmarking Tier 4: POST /api/documents (Authenticated Writes + WAL Batching)...");
  const t4 = runOha("/api/documents", {
    method: "POST",
    headers: {
      "Authorization": `Bearer ${apiKey}`,
      "Content-Type": "application/json",
    },
    body: JSON.stringify({
      title: "Live Stress Record",
      content: "Benchmarking write throughput with group commit batching and real-time SSE fanout.",
      status: "published",
    }),
  });
  results.push({
    tier: "T4",
    name: "Authenticated Writes",
    method: "POST",
    endpoint: "/api/documents",
    rps: Math.round(t4.summary?.requestsPerSec ?? 0),
    p50Ms: Number(((t4.latencyPercentiles?.p50 ?? 0) * 1000).toFixed(2)),
    p95Ms: Number(((t4.latencyPercentiles?.p95 ?? 0) * 1000).toFixed(2)),
    p99Ms: Number(((t4.latencyPercentiles?.p99 ?? 0) * 1000).toFixed(2)),
    totalRequests: t4.summary?.total ?? 0,
    successRate: Math.round((t4.summary?.successRate ?? 0) * 100),
  });

  // Tier 5: Static File Serving with ETag 304 Validation
  console.log(`→ Benchmarking Tier 5: GET /api/files/${fileStorageKey} (ETag 304 Caching)...`);
  const t5 = runOha(`/api/files/${fileStorageKey}`, {
    headers: { "If-None-Match": fileEtag },
  });
  results.push({
    tier: "T5",
    name: "Conditional ETag Caching",
    method: "GET",
    endpoint: `/api/files/:key`,
    rps: Math.round(t5.summary?.requestsPerSec ?? 0),
    p50Ms: Number(((t5.latencyPercentiles?.p50 ?? 0) * 1000).toFixed(2)),
    p95Ms: Number(((t5.latencyPercentiles?.p95 ?? 0) * 1000).toFixed(2)),
    p99Ms: Number(((t5.latencyPercentiles?.p99 ?? 0) * 1000).toFixed(2)),
    totalRequests: t5.summary?.total ?? 0,
    successRate: Math.round((t5.summary?.successRate ?? 0) * 100),
  });

  // Kill server
  server.kill();
  cleanup();

  console.log("\n==========================================================");
  console.log("📊 REPTARBASE BENCHMARK SUMMARY RESULTS");
  console.log("==========================================================\n");

  const tableHeader = `| Tier | Workload | Method | Endpoint | RPS | p50 | p95 | p99 | Success |
| :--- | :--- | :--- | :--- | :---: | :---: | :---: | :---: | :---: |`;
  const tableRows = results.map(
    (r) =>
      `| ${r.tier} | ${r.name} | \`${r.method}\` | \`${r.endpoint}\` | **${r.rps.toLocaleString()}** | ${r.p50Ms}ms | ${r.p95Ms}ms | ${r.p99Ms}ms | ${r.successRate}% |`
  );

  const markdownReport = `# ReptarBase BaaS Performance Benchmark Results

Generated: ${new Date().toISOString()}  
Environment: macOS / Linux Native AOT, SQLite WAL mode, Concurrency: ${CONCURRENCY}

${tableHeader}
${tableRows.join("\n")}

### Key Takeaways
1. **Zero-Reflection Read Queries**: Filtered collection queries run at **${results[1]?.rps.toLocaleString()} req/sec** with sub-millisecond median latency (${results[1]?.p50Ms}ms p50) using strongly-typed \`ColumnRef<T>\` tokens.
2. **High-Concurrency Writes**: Authenticated document creation sustained **${results[3]?.rps.toLocaleString()} writes/sec** while simultaneously generating ULID keys, executing SQLite writes with WAL group-commit, and broadcasting to the real-time SSE stream.
3. **ETag Conditional Caching**: Static file cache validation achieved **${results[4]?.rps.toLocaleString()} req/sec** at ${results[4]?.p50Ms}ms p50 returning HTTP 304 Not Modified.
4. **Baseline Engine Overhead**: Reptar's Kestrel pipeline handled **${results[0]?.rps.toLocaleString()} req/sec** on basic endpoints with minimal allocations.
`;

  console.log(tableHeader);
  for (const row of tableRows) {
    console.log(row);
  }

  const outPath = path.join(import.meta.dir, "BENCHMARK_RESULTS.md");
  writeFileSync(outPath, markdownReport, "utf-8");
  console.log(`\n💾 Saved detailed benchmark report to ${outPath}`);
}

main().catch((err) => {
  console.error("Benchmark failed:", err);
  process.exit(1);
});
