/* oxlint-disable eslint/no-await-in-loop, eslint/no-unmodified-loop-condition */
/**
 * Mixed workload driver (T11).
 *
 * Runs a weighted mix of read/list/write requests against a running server.
 * This is intentionally a different tool from `oha` (which is per-endpoint):
 * the point is to measure a realistic blend, not the best case for one route.
 *
 * Usage:
 *   bun bench/load/mixed.ts --base-url http://127.0.0.1:9001 --label reptar
 */

import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import path from "node:path";

import { buildRequestHeaders } from "./request-headers";
import type { Fixtures } from "./scenarios";

interface WeightedScenario {
  name: string;
  weight: number;
  method: "GET" | "POST";
  path: string;
  body?: string;
  headers?: Record<string, string>;
}

const args = process.argv.slice(2);

const argValue = (name: string, fallback?: string): string | undefined => {
  const index = args.indexOf(name);
  if (index !== -1 && args[index + 1]) {
    return args[index + 1];
  }
  const inline = args.find((value) => value.startsWith(`${name}=`));
  return inline ? inline.slice(name.length + 1) : fallback;
};

const baseUrl = (argValue("--base-url") ?? "http://127.0.0.1:9001").replace(
  /\/$/u,
  ""
);
const label = argValue("--label") ?? "unknown";
const duration = Number(argValue("--duration") ?? "15");
const warmup = Number(argValue("--warmup") ?? "3");
const concurrency = Number(argValue("--concurrency") ?? "100");
const acceptEncoding =
  argValue("--accept-encoding") ??
  process.env.BENCH_ACCEPT_ENCODING ??
  "identity";

const fixturesPath =
  argValue("--fixtures") ??
  path.join(import.meta.dir, "..", "out", "fixtures.json");
const fixtures = JSON.parse(readFileSync(fixturesPath, "utf-8")) as Fixtures;
const { sample, login } = fixtures;
const bearer = { authorization: `Bearer ${login.jwt}` };
const jsonHeaders = { "content-type": "application/json" };

const weighted: WeightedScenario[] = [
  {
    method: "GET",
    name: "task-detail",
    path: `/api/v1/tasks/${sample.taskId}`,
    weight: 40,
  },
  {
    method: "GET",
    name: "task-list",
    path: `/api/v1/projects/${sample.projectId}/tasks?status=${sample.status}&limit=20`,
    weight: 20,
  },
  {
    method: "GET",
    name: "task-comments",
    path: `/api/v1/tasks/${sample.taskId}/comments?limit=20`,
    weight: 10,
  },
  {
    method: "GET",
    name: "task-detail",
    path: `/api/v1/tasks/${sample.secondTaskId}`,
    weight: 10,
  },
  {
    body: JSON.stringify({
      assigneeId: sample.assigneeId,
      description: "Created by the mixed workload.",
      priority: 2,
      status: "open",
      title: "Mixed workload task",
    }),
    headers: { ...jsonHeaders, ...bearer },
    method: "POST",
    name: "create-task",
    path: `/api/v1/projects/${sample.projectId}/tasks`,
    weight: 10,
  },
  {
    headers: bearer,
    method: "GET",
    name: "me",
    path: "/api/v1/me",
    weight: 10,
  },
];

// Construct once rather than adding header-allocation work to every client request.
for (const scenario of weighted) {
  scenario.headers = Object.fromEntries(
    buildRequestHeaders(scenario.headers, acceptEncoding)
  );
}

const totalWeight = weighted.reduce(
  (sum, scenario) => sum + scenario.weight,
  0
);

const pick = (): WeightedScenario => {
  let ticket = Math.random() * totalWeight;
  for (const scenario of weighted) {
    ticket -= scenario.weight;
    if (ticket <= 0) {
      return scenario;
    }
  }

  const [fallback] = weighted;

  if (!fallback) {
    throw new Error("No weighted scenarios configured.");
  }

  return fallback;
};

interface Sample {
  latencyMs: number;
  status: number;
  scenario: string;
}

const samples: Sample[] = [];
let running = false;

const worker = async (): Promise<void> => {
  while (running) {
    const scenario = pick();
    const started = performance.now();

    try {
      const response = await fetch(`${baseUrl}${scenario.path}`, {
        body: scenario.body,
        headers: scenario.headers,
        method: scenario.method,
      });
      await response.arrayBuffer();
      samples.push({
        latencyMs: performance.now() - started,
        scenario: scenario.name,
        status: response.status,
      });
    } catch {
      samples.push({
        latencyMs: performance.now() - started,
        scenario: scenario.name,
        status: 0,
      });
    }
  }
};

const runPhase = async (
  seconds: number
): Promise<{ elapsed: number; samples: Sample[] }> => {
  samples.length = 0;
  running = true;
  const started = performance.now();
  const workers = Array.from({ length: concurrency }, () => worker());

  await Bun.sleep(seconds * 1000);
  running = false;
  await Promise.all(workers);

  return {
    elapsed: (performance.now() - started) / 1000,
    samples: samples.splice(0),
  };
};

const percentile = (values: number[], p: number): number => {
  if (values.length === 0) {
    return 0;
  }
  const sorted = values.toSorted((left, right) => left - right);
  const index = Math.min(
    sorted.length - 1,
    Math.max(0, Math.ceil((p / 100) * sorted.length) - 1)
  );
  return sorted[index] ?? 0;
};

console.log(
  `\nMixed workload — ${label}\n` +
    `Target: ${baseUrl} | concurrency=${concurrency} | ` +
    `warmup=${warmup}s | duration=${duration}s\n`
);

await runPhase(warmup);

const measured = await runPhase(duration);
const { elapsed } = measured;
const measuredSamples = measured.samples;

const latencies = measuredSamples.map((entry) => entry.latencyMs);
const statuses: Record<string, number> = {};
const byScenario: Record<string, { count: number; p50: number; p95: number }> =
  {};

for (const entry of measuredSamples) {
  const key = String(entry.status);
  statuses[key] = (statuses[key] ?? 0) + 1;
}

for (const scenario of weighted) {
  const scenarioSamples = measuredSamples
    .filter((entry) => entry.scenario === scenario.name)
    .map((entry) => entry.latencyMs);
  byScenario[scenario.name] = {
    count: scenarioSamples.length,
    p50: percentile(scenarioSamples, 50),
    p95: percentile(scenarioSamples, 95),
  };
}

const payload = {
  baseUrl,
  byScenario,
  config: { acceptEncoding, concurrency, duration, warmup },
  elapsedSeconds: elapsed,
  generatedAt: new Date().toISOString(),
  label,
  latencyMs: {
    p50: percentile(latencies, 50),
    p95: percentile(latencies, 95),
    p99: percentile(latencies, 99),
  },
  requests: measuredSamples.length,
  rps: measuredSamples.length / elapsed,
  statuses,
};

const timestamp = new Date().toISOString().replaceAll(/[:.]/gu, "-");
const outDir = path.resolve(
  argValue("--out") ?? path.join(import.meta.dir, "..", "results", timestamp)
);
mkdirSync(outDir, { recursive: true });

const outPath = path.join(outDir, `${label}-mixed.json`);
writeFileSync(outPath, `${JSON.stringify(payload, null, 2)}\n`);

console.log(
  `${payload.rps.toFixed(0)} req/s  p50 ${payload.latencyMs.p50.toFixed(1)} ms  ` +
    `p95 ${payload.latencyMs.p95.toFixed(1)} ms  ` +
    `p99 ${payload.latencyMs.p99.toFixed(1)} ms  ` +
    `requests ${payload.requests}`
);
console.log(`Wrote ${outPath}`);
