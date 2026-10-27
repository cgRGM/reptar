/* oxlint-disable eslint/no-await-in-loop */
/**
 * Run the Reptar Bench scenario suite against a running server.
 *
 * Usage:
 *   bun bench/load/run.ts --base-url http://127.0.0.1:9001 --label reptar
 *
 * Requires `oha` (https://github.com/hatoo/oha). Set OHA_BIN if it is not on PATH.
 */

import { spawnSync } from "node:child_process";
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import path from "node:path";

import fixturesJson from "../out/fixtures.json";
import { buildRequestHeaders } from "./request-headers";
import { buildScenarios } from "./scenarios";
import type { Fixtures, Scenario } from "./scenarios";

interface OhaResult {
  errorDistribution?: Record<string, number>;
  latencyPercentiles?: Record<string, number>;
  statusCodeDistribution?: Record<string, number>;
  summary?: {
    requestsPerSec?: number;
    successRate?: number;
    total?: number;
    totalData?: number;
    sizePerRequest?: number;
  };
}

interface RunSample {
  rps: number;
  p50Ms: number;
  p95Ms: number;
  p99Ms: number;
  successRate: number;
  totalRequests: number;
  errors: number;
  raw: OhaResult;
}

interface ScenarioResult {
  name: string;
  tier: string;
  method: string;
  path: string;
  rps: number;
  p50Ms: number;
  p95Ms: number;
  p99Ms: number;
  successRate: number;
  totalRequests: number;
  errors: number;
  cpuMsPerRequest: number | null;
  rssKb: number | null;
  breakdown?: unknown;
  runs: RunSample[];
}

interface ProcSnapshot {
  cpuTicks: number;
  rssKb: number;
}

interface StageStats {
  name: string;
  count: number;
  avgMs: number;
  p50Ms: number;
  p95Ms: number;
  p99Ms: number;
  maxMs: number;
}

interface BreakdownSnapshot {
  enabled: boolean;
  stages: StageStats[];
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
const duration = Number(argValue("--duration") ?? "10");
const warmup = Number(argValue("--warmup") ?? "3");
const connections = Number(argValue("--connections") ?? "100");
const reps = Number(argValue("--reps") ?? "3");
const cooldown = Number(argValue("--cooldown") ?? "2");
const ohaBin = process.env.OHA_BIN ?? argValue("--oha") ?? "oha";
// Compare framework work, not a client's implicit compression preferences.
// Measure negotiated compression separately with --accept-encoding / BENCH_ACCEPT_ENCODING.
const acceptEncoding =
  argValue("--accept-encoding") ??
  process.env.BENCH_ACCEPT_ENCODING ??
  "identity";
const pid = Number(argValue("--pid") ?? "0");
const only = argValue("--only");
const skip = new Set((argValue("--skip") ?? "").split(",").filter(Boolean));

const fixtures = fixturesJson as Fixtures;
const scenarios = buildScenarios(fixtures).filter((scenario) => {
  if (skip.has(scenario.name) || skip.has(scenario.tier)) {
    return false;
  }
  if (!only) {
    return true;
  }
  return only
    .split(",")
    .some((value) => value === scenario.name || value === scenario.tier);
});

const median = (values: number[]): number => {
  if (values.length === 0) {
    return 0;
  }
  const sorted = values.toSorted((left, right) => left - right);
  const middle = Math.floor(sorted.length / 2);
  return sorted.length % 2 === 0
    ? ((sorted[middle - 1] ?? 0) + (sorted[middle] ?? 0)) / 2
    : (sorted[middle] ?? 0);
};

const readProc = (processId: number): ProcSnapshot | null => {
  if (!processId) {
    return null;
  }

  try {
    const stat = readFileSync(`/proc/${processId}/stat`, "utf-8")
      .trim()
      .split(/\s+/u);
    const cpuTicks = Number(stat[13]) + Number(stat[14]);
    const status = readFileSync(`/proc/${processId}/status`, "utf-8");
    const rss = /VmRSS:\s+(?<rss>\d+)/u.exec(status);
    return { cpuTicks, rssKb: Number(rss?.groups?.rss ?? 0) };
  } catch {
    return null;
  }
};

const clockTicks = (() => {
  const result = spawnSync("getconf", ["CLK_TCK"], { encoding: "utf-8" });
  const parsed = Number(result.stdout?.trim());
  return Number.isFinite(parsed) && parsed > 0 ? parsed : 100;
})();

const totalFromResult = (result: OhaResult): number => {
  const statuses = Object.values(result.statusCodeDistribution ?? {});
  const statusTotal = statuses.reduce((sum, count) => sum + count, 0);
  if (statusTotal > 0) {
    return statusTotal;
  }
  const errors = Object.values(result.errorDistribution ?? {});
  return errors.reduce((sum, count) => sum + count, 0);
};

const fetchJson = async (
  requestPath: string,
  method = "GET"
): Promise<unknown> => {
  const response = await fetch(`${baseUrl}${requestPath}`, { method });
  if (response.status === 404) {
    return undefined;
  }
  if (!response.ok) {
    throw new Error(`${method} ${requestPath} -> ${response.status}`);
  }
  return response.json();
};

const postPath = async (requestPath: string): Promise<void> => {
  const response = await fetch(`${baseUrl}${requestPath}`, { method: "POST" });
  if (response.status === 404) {
    return;
  }
  if (!response.ok) {
    throw new Error(`POST ${requestPath} -> ${response.status}`);
  }
  await response.arrayBuffer();
};

const runOha = (scenario: Scenario, seconds: number): OhaResult => {
  const ohaArgs = [
    "-c",
    String(scenario.connections ?? connections),
    "-z",
    `${seconds}s`,
    "-t",
    "20s",
    "--no-tui",
    "--output-format",
    "json",
  ];

  if (scenario.method !== "GET") {
    ohaArgs.push("-m", scenario.method);
  }

  const headers = buildRequestHeaders(scenario.headers, acceptEncoding);
  for (const [name, value] of headers) {
    ohaArgs.push("-H", `${name}: ${value}`);
  }

  if (scenario.body) {
    ohaArgs.push("-d", scenario.body);
  }

  ohaArgs.push(`${baseUrl}${scenario.path}`);

  const result = spawnSync(ohaBin, ohaArgs, {
    encoding: "utf-8",
    maxBuffer: 20 * 1024 * 1024,
  });

  if (result.error) {
    throw new Error(`oha failed: ${result.error.message}`);
  }

  if (result.status !== 0) {
    throw new Error(
      `oha exited ${result.status}: ${result.stderr || result.stdout}`
    );
  }

  return JSON.parse(result.stdout) as OhaResult;
};

const sample = (result: OhaResult): RunSample => {
  const totalRequests = totalFromResult(result);
  const non2xx = Object.entries(result.statusCodeDistribution ?? {})
    .filter(([status]) => !status.startsWith("2"))
    .reduce((sum, [, count]) => sum + count, 0);
  const errors =
    non2xx +
    Object.entries(result.errorDistribution ?? {})
      .filter(([name]) => name !== "aborted due to deadline")
      .reduce((sum, [, count]) => sum + count, 0);

  return {
    errors,
    p50Ms: (result.latencyPercentiles?.p50 ?? 0) * 1000,
    p95Ms: (result.latencyPercentiles?.p95 ?? 0) * 1000,
    p99Ms: (result.latencyPercentiles?.p99 ?? 0) * 1000,
    raw: result,
    rps: result.summary?.requestsPerSec ?? 0,
    successRate: result.summary?.successRate ?? 0,
    totalRequests,
  };
};

const results: ScenarioResult[] = [];

console.log(
  `\nReptar Bench — ${label}\n` +
    `Target: ${baseUrl}\n` +
    `Scenarios: ${scenarios.length} | connections=${connections} | ` +
    `warmup=${warmup}s | duration=${duration}s | reps=${reps}\n`
);

for (const scenario of scenarios) {
  process.stdout.write(
    `${scenario.tier.padEnd(4)} ${scenario.name.padEnd(18)} `
  );

  if (scenario.resetPath) {
    await postPath(scenario.resetPath);
  }

  if (scenario.warmup !== false) {
    runOha(scenario, warmup);
  }

  const runs: RunSample[] = [];
  const before = readProc(pid);

  for (let rep = 0; rep < reps; rep += 1) {
    runs.push(sample(runOha(scenario, duration)));
  }

  const breakdown = scenario.breakdownPath
    ? await fetchJson(scenario.breakdownPath)
    : undefined;

  const after = readProc(pid);
  const totalRequests = runs.reduce((sum, run) => sum + run.totalRequests, 0);
  const cpuMsPerRequest =
    before && after && totalRequests > 0
      ? ((after.cpuTicks - before.cpuTicks) * (1000 / clockTicks)) /
        totalRequests
      : null;

  const result: ScenarioResult = {
    breakdown,
    cpuMsPerRequest,
    errors: runs.reduce((sum, run) => sum + run.errors, 0),
    method: scenario.method,
    name: scenario.name,
    p50Ms: median(runs.map((run) => run.p50Ms)),
    p95Ms: median(runs.map((run) => run.p95Ms)),
    p99Ms: median(runs.map((run) => run.p99Ms)),
    path: scenario.path,
    rps: median(runs.map((run) => run.rps)),
    rssKb: after?.rssKb ?? null,
    runs,
    successRate: median(runs.map((run) => run.successRate)),
    tier: scenario.tier,
    totalRequests,
  };

  results.push(result);

  console.log(
    `${result.rps.toFixed(0).padStart(8)} req/s  ` +
      `p50 ${result.p50Ms.toFixed(1).padStart(6)} ms  ` +
      `p95 ${result.p95Ms.toFixed(1).padStart(6)} ms  ` +
      `p99 ${result.p99Ms.toFixed(1).padStart(6)} ms  ` +
      `errors ${result.errors}`
  );

  const snapshot = result.breakdown as BreakdownSnapshot | undefined;

  if (snapshot?.stages?.length) {
    console.log("         breakdown (ms):");

    for (const stage of snapshot.stages) {
      if (!stage.count) {
        continue;
      }

      console.log(
        `           ${stage.name.padEnd(10)} n=${stage.count} ` +
          `avg=${stage.avgMs.toFixed(3)} p50=${stage.p50Ms.toFixed(3)} ` +
          `p95=${stage.p95Ms.toFixed(3)} p99=${stage.p99Ms.toFixed(3)}`
      );
    }
  }

  await Bun.sleep(scenario.tier === "T9" ? 5000 : cooldown * 1000);
}

const timestamp = new Date().toISOString().replaceAll(/[:.]/gu, "-");
const outDir = path.resolve(
  argValue("--out") ?? path.join(import.meta.dir, "..", "results", timestamp)
);
mkdirSync(outDir, { recursive: true });

const payload = {
  baseUrl,
  config: {
    acceptEncoding,
    connections,
    cooldown,
    duration,
    reps,
    warmup,
  },
  generatedAt: new Date().toISOString(),
  label,
  pid: pid || null,
  scenarios: results,
};

const jsonPath = path.join(outDir, `${label}.json`);
writeFileSync(jsonPath, `${JSON.stringify(payload, null, 2)}\n`);

const markdown = [
  `## ${label}`,
  "",
  `Request Accept-Encoding: \`${acceptEncoding}\`.`,
  "",
  "| Tier | Scenario | Req/s | p50 | p95 | p99 | Errors | CPU ms/req | RSS |",
  "| :--- | :--- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |",
  ...results.map(
    (result) =>
      `| ${result.tier} | ${result.name} | ${result.rps.toFixed(0)} | ` +
      `${result.p50Ms.toFixed(1)} ms | ${result.p95Ms.toFixed(1)} ms | ` +
      `${result.p99Ms.toFixed(1)} ms | ${result.errors} | ` +
      `${result.cpuMsPerRequest?.toFixed(3) ?? "n/a"} | ` +
      `${result.rssKb ? `${(result.rssKb / 1024).toFixed(1)} MB` : "n/a"} |`
  ),
  "",
].join("\n");

writeFileSync(path.join(outDir, `${label}.md`), markdown);

console.log(`\nWrote ${jsonPath}`);
