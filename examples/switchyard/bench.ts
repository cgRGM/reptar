import { mkdir } from "node:fs/promises";
import { cpus, totalmem } from "node:os";

/* eslint-disable no-await-in-loop -- Each closed-loop worker must await its own response before sending another request. */
import { required, sessionCookie } from "./required";

const base = process.env.SWITCHYARD_URL ?? "http://127.0.0.1:8795";
const concurrency = Number(process.env.CONCURRENCY ?? 16);
const durationMs = Number(process.env.DURATION_MS ?? 5000);
if (
  !Number.isInteger(concurrency) ||
  concurrency < 1 ||
  concurrency > 256 ||
  !Number.isFinite(durationMs) ||
  durationMs < 100
) {
  throw new Error("Invalid benchmark settings");
}
const signup = await fetch(`${base}/api/auth/signup`, {
  body: JSON.stringify({
    email: `bench-${crypto.randomUUID()}@example.test`,
    name: "Benchmark",
    password: "Switchyard-benchmark-2026!",
  }),
  headers: { "content-type": "application/json" },
  method: "POST",
});
if (signup.status !== 201) {
  throw new Error(`Signup failed: ${signup.status} ${await signup.text()}`);
}
const cookie = sessionCookie(signup);
const post = async (
  path: string,
  body: unknown,
  method: "POST" | "PUT" = "POST"
) => {
  const init: RequestInit = {
    body: JSON.stringify(body),
    headers: { "content-type": "application/json", cookie },
    method,
  };
  const response = await fetch(new Request(base + path, init));
  if (!response.ok) {
    throw new Error(
      `Setup failed: ${response.status} ${await response.text()}`
    );
  }
  return await response.json();
};
const project = await post("/api/projects", { name: "Benchmark" });
const flags = `/api/projects/${project.id}/flags`;
await post(flags, { environment: "production", key: "checkout" });
await post(
  `${flags}/production/checkout/draft`,
  { enabled: true, expectedRevision: 0, rollout: 50, targets: [] },
  "PUT"
);
await post(`${flags}/production/checkout/publish`, {
  expectedRevision: 1,
  expectedVersion: 0,
});
const evaluatePath = `/api/evaluate/${project.id}/production/checkout?subject=benchmark`;
const baseline = await fetch(base + evaluatePath, { headers: { cookie } });
const etag = required(baseline.headers.get("etag"), "evaluation ETag");
await baseline.arrayBuffer();
const measure = async (
  label: string,
  path: string,
  headers: Record<string, string>,
  expected: number
) => {
  for (let warm = 0; warm < 100; warm += 1) {
    const response = await fetch(base + path, { headers });
    await response.arrayBuffer();
  }
  const latency: number[] = [];
  const statuses: Record<string, number> = {};
  let errors = 0;
  const start = performance.now();
  const deadline = start + durationMs;
  await Promise.all(
    Array.from({ length: concurrency }, async () => {
      while (performance.now() < deadline) {
        const before = performance.now();
        try {
          const response = await fetch(base + path, {
            headers,
            signal: AbortSignal.timeout(10_000),
          });
          await response.arrayBuffer();
          statuses[response.status] = (statuses[response.status] ?? 0) + 1;
          if (response.status !== expected) {
            errors += 1;
          }
        } catch {
          errors += 1;
        }
        latency.push(performance.now() - before);
      }
    })
  );
  const elapsedMs = performance.now() - start;
  latency.sort((a, b) => a - b);
  const percentile = (fraction: number) =>
    latency[
      Math.min(latency.length - 1, Math.floor(latency.length * fraction))
    ];
  return {
    concurrency,
    elapsedMs,
    errors,
    label,
    p50Ms: percentile(0.5),
    p95Ms: percentile(0.95),
    p99Ms: percentile(0.99),
    requests: latency.length,
    requestsPerSecond: latency.length / (elapsedMs / 1000),
    statuses,
  };
};
const results = [
  await measure("health / unauthenticated / no DB", "/health", {}, 200),
  await measure(
    "evaluation / authenticated / DB-backed / uncached",
    evaluatePath,
    { cookie },
    200
  ),
  await measure(
    "conditional evaluation / authenticated / DB-backed / 304",
    evaluatePath,
    { cookie, "if-none-match": etag },
    304
  ),
];
const report = {
  cpu: cpus()[0]?.model,
  loadGenerator: `Bun ${Bun.version}, same VPS, closed-loop fetch`,
  logicalCpus: cpus().length,
  memoryBytes: totalmem(),
  results,
  runtime: "Native AOT / SQLite / Kestrel",
  timestamp: new Date().toISOString(),
};
await mkdir("results", { recursive: true });
await Bun.write(
  "results/benchmark.json",
  `${JSON.stringify(report, null, 2)}\n`
);
process.stdout.write(`${JSON.stringify(report, null, 2)}\n`);
if (results.some((result) => result.errors > 0)) {
  process.exitCode = 1;
}
