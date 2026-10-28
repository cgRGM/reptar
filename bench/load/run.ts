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
import {
  appendFileSync,
  mkdirSync,
  readFileSync,
  writeFileSync,
} from "node:fs";
import path from "node:path";

import { validateResponse } from "./correctness";
import { assertEquivalentResponse, validateManifest } from "./protocol";
import type { WorkManifest } from "./protocol";
import { buildRequestHeaders } from "./request-headers";
import { captureResponse, sha256 } from "./response-witness";
import type { ResponseWitness } from "./response-witness";
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
  connections: number;
  equivalenceFailure: string | null;
  before: ResponseWitness;
  after: ResponseWitness;
  requestBodySha256: string;
  requestAcceptEncoding: string;
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

const fixturesPath =
  argValue("--fixtures") ??
  path.join(import.meta.dir, "..", "out", "fixtures.json");
const fixtures = JSON.parse(readFileSync(fixturesPath, "utf-8")) as Fixtures;
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

for (const value of [connections, duration, reps]) {
  if (!Number.isFinite(value) || value <= 0) {
    throw new Error("Connections/duration/reps must be finite positive values");
  }
}
if (
  ![warmup, cooldown].every((value) => Number.isFinite(value) && value >= 0) ||
  scenarios.length === 0
) {
  throw new Error("Invalid warmup/cooldown or empty scenario selection");
}
const startedAt = new Date().toISOString();
const timestamp = startedAt.replaceAll(/[:.]/gu, "-");
const outDir = path.resolve(
  argValue("--out") ?? path.join(import.meta.dir, "..", "results", timestamp)
);
mkdirSync(outDir, { recursive: true });
if (!/^[a-z\d_-]+$/iu.test(label)) {
  throw new Error("Benchmark label must be a safe filename identifier");
}
const journalPath = path.join(outDir, `${label}-${timestamp}.jsonl`);
const record = (event: unknown): void => {
  appendFileSync(journalPath, `${JSON.stringify(event)}\n`);
};
const manifestPath = argValue("--work-manifest");
const workManifest = manifestPath
  ? (JSON.parse(readFileSync(manifestPath, "utf-8")) as WorkManifest)
  : null;
if (workManifest) {
  validateManifest(workManifest);
}
const executablePath = argValue("--server-executable");
const executableVerified = Boolean(workManifest && executablePath);
if (
  workManifest &&
  executablePath &&
  sha256(readFileSync(executablePath)) !== workManifest.executableSha256
) {
  throw new Error(
    "Declared server executable hash does not match the supplied artifact"
  );
}
const ohaPath = Bun.which(ohaBin);
if (!ohaPath) {
  throw new Error(`Load generator not found: ${ohaBin}`);
}
const loadToolIdentity = `${spawnSync(ohaPath, ["--version"], { encoding: "utf-8" }).stdout.trim()}:${sha256(readFileSync(ohaPath))}`;
const runnerIdentity = {
  bun: Bun.version,
  dirty:
    spawnSync("git", ["status", "--porcelain"], {
      cwd: import.meta.dir,
      encoding: "utf-8",
    }).stdout.trim().length > 0,
  fixturesSha256: sha256(JSON.stringify(fixtures)),
  platform: `${process.platform}-${process.arch}`,
  sourceCommit: spawnSync("git", ["rev-parse", "HEAD"], {
    cwd: import.meta.dir,
    encoding: "utf-8",
  }).stdout.trim(),
  sourceHashes: Object.fromEntries(
    [
      "run.ts",
      "correctness.ts",
      "response-witness.ts",
      "protocol.ts",
      "scenarios.ts",
      "request-headers.ts",
    ].map((file) => [
      file,
      sha256(readFileSync(path.join(import.meta.dir, file))),
    ])
  ),
};
record({ event: "start", loadToolIdentity, runnerIdentity, workManifest });
const witness = async (scenario: Scenario): Promise<ResponseWitness> => {
  const response = await captureResponse({
    body: scenario.body,
    headers: [...buildRequestHeaders(scenario.headers, acceptEncoding)],
    method: scenario.method,
    url: `${baseUrl}${scenario.path}`,
    validate: (status, decoded) =>
      validateResponse(scenario, fixtures, status, decoded),
  });
  record({ event: "response-witness", response, scenario: scenario.name });
  return response;
};

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

const redact = (text: string): string => {
  let safe = text;
  for (const secret of [fixtures.login.jwt, fixtures.login.password]) {
    if (secret) {
      safe = safe.replaceAll(secret, "[REDACTED]");
    }
  }
  return safe;
};

const runOha = (scenario: Scenario, seconds: number): OhaResult => {
  const ohaArgs = [
    "-c",
    String(scenario.connections ?? connections),
    "-z",
    `${seconds}s`,
    "--wait-ongoing-requests-after-deadline",
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

  record({
    event: "load-result",
    exitCode: result.status,
    scenario: scenario.name,
    seconds,
    stderr: redact(result.stderr ?? ""),
    stdout: redact(result.stdout ?? ""),
  });
  if (result.error) {
    throw new Error(`oha failed: ${result.error.message}`);
  }

  if (result.status !== 0) {
    throw new Error(
      `oha exited ${result.status}: ${redact(result.stderr || result.stdout)}`
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
    Object.values(result.errorDistribution ?? {}).reduce(
      (sum, count) => sum + count,
      0
    );

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

  const beforeResponse = await witness(scenario);
  if (scenario.warmup !== false && warmup > 0) {
    const warmupResult = sample(runOha(scenario, warmup));
    if (warmupResult.errors > 0) {
      throw new Error(
        `Warmup failed for ${scenario.name}; raw result retained in ${journalPath}`
      );
    }
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

  const afterResponse = await witness(scenario);
  let equivalenceFailure: string | null = null;
  try {
    assertEquivalentResponse(beforeResponse, afterResponse);
  } catch (error) {
    equivalenceFailure =
      error instanceof Error ? error.message : "Invalid response equivalence";
    record({
      event: "equivalence-failure",
      reason: equivalenceFailure,
      scenario: scenario.name,
    });
  }
  const result: ScenarioResult = {
    after: afterResponse,
    before: beforeResponse,
    breakdown,
    connections: scenario.connections ?? connections,
    cpuMsPerRequest,
    equivalenceFailure,
    errors: runs.reduce((sum, run) => sum + run.errors, 0),
    method: scenario.method,
    name: scenario.name,
    p50Ms: median(runs.map((run) => run.p50Ms)),
    p95Ms: median(runs.map((run) => run.p95Ms)),
    p99Ms: median(runs.map((run) => run.p99Ms)),
    path: scenario.path,
    requestAcceptEncoding:
      buildRequestHeaders(scenario.headers, acceptEncoding).get(
        "Accept-Encoding"
      ) ?? "",
    requestBodySha256: sha256(scenario.body ?? ""),
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

const payload = {
  baseUrl,
  comparisonEligible:
    executableVerified &&
    workManifest?.mode === "equal-work" &&
    results.every(
      (result) =>
        !result.equivalenceFailure &&
        result.errors === 0 &&
        result.totalRequests > 0
    ),
  config: {
    acceptEncoding,
    completionPolicy: "stop-issuing-wait-inflight-20s",
    connections,
    cooldown,
    duration,
    loadToolIdentity,
    reps,
    warmup,
  },
  generatedAt: new Date().toISOString(),
  label,
  pid: pid || null,
  runnerIdentity,
  scenarios: results,
  schemaVersion: 2,
  startedAt,
  verificationScope: "sampled-response-semantics-not-every-load-response",
  workManifest,
};

const jsonPath = path.join(outDir, `${label}.json`);
writeFileSync(jsonPath, `${JSON.stringify(payload, null, 2)}\n`);

const markdown = [
  `## ${label}`,
  "",
  `Request Accept-Encoding: \`${acceptEncoding}\`.`,
  `Comparison eligible: **${payload.comparisonEligible}**. ${payload.verificationScope}.`,
  "Actual response encodings and encoded/decoded byte counts are retained per scenario in JSON; a request header alone is not compression evidence.",
  "",
  "| Tier | Scenario | Req/s | p50 | p95 | p99 | Errors | CPU ms/req | RSS | Actual encoding before/after | Encoded/decoded bytes before → after | Equivalence |",
  "| :--- | :--- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | :--- | :--- | :--- |",
  ...results.map(
    (result) =>
      `| ${result.tier} | ${result.name} | ${result.rps.toFixed(0)} | ` +
      `${result.p50Ms.toFixed(1)} ms | ${result.p95Ms.toFixed(1)} ms | ` +
      `${result.p99Ms.toFixed(1)} ms | ${result.errors} | ` +
      `${result.cpuMsPerRequest?.toFixed(3) ?? "n/a"} | ` +
      `${result.rssKb ? `${(result.rssKb / 1024).toFixed(1)} MB` : "n/a"} | ` +
      `${result.before.encoding}/${result.after.encoding} | ` +
      `${result.before.encodedBytes}/${result.before.decodedBytes} → ${result.after.encodedBytes}/${result.after.decodedBytes} | ` +
      `${result.equivalenceFailure ?? "sampled response matched"} |`
  ),
  "",
].join("\n");

writeFileSync(path.join(outDir, `${label}.md`), markdown);

console.log(`\nWrote ${jsonPath}`);
if (results.some((result) => result.errors > 0)) {
  process.exitCode = 1;
}
