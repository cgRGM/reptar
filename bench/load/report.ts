/**
 * Build a Markdown report from a benchmark result directory.
 *
 * Usage:
 *   bun bench/load/report.ts bench/results/vps-20261024T223413Z
 */

import { existsSync, readFileSync, writeFileSync } from "node:fs";
import path from "node:path";

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
  errors: number;
  cpuMsPerRequest: number | null;
  rssKb: number | null;
  breakdown?: BreakdownSnapshot;
}

interface ResultFile {
  label: string;
  baseUrl: string;
  generatedAt: string;
  config: Record<string, number>;
  scenarios: ScenarioResult[];
}

const directory = process.argv.at(2);

if (!directory) {
  console.error("Usage: bun bench/load/report.ts <results-dir>");
  process.exit(1);
}

const dir = path.resolve(directory);
const manifestPath = path.join(dir, "manifest.json");
const manifest = existsSync(manifestPath)
  ? (JSON.parse(readFileSync(manifestPath, "utf-8")) as Record<string, unknown>)
  : {};

const labels = ["reptar", "minimal-api", "hono-jit", "hono-compiled"];
const display: Record<string, string> = {
  "hono-compiled": "Hono (compiled)",
  "hono-jit": "Hono (JIT)",
  "minimal-api": "Minimal API",
  reptar: "Reptar",
};

const results = new Map<string, ResultFile>();

for (const label of labels) {
  const file = path.join(dir, `${label}.json`);
  if (existsSync(file)) {
    results.set(label, JSON.parse(readFileSync(file, "utf-8")) as ResultFile);
  }
}

const available = labels.filter((label) => results.has(label));

if (available.length === 0) {
  console.error(`No result files found in ${dir}`);
  process.exit(1);
}

const scenarioKeys: {
  name: string;
  tier: string;
  method: string;
  path: string;
}[] = [];
const seen = new Set<string>();

for (const label of available) {
  const result = results.get(label);

  if (!result) {
    continue;
  }

  for (const scenario of result.scenarios) {
    const key = `${scenario.tier}:${scenario.name}`;
    if (!seen.has(key)) {
      seen.add(key);
      scenarioKeys.push({
        method: scenario.method,
        name: scenario.name,
        path: scenario.path,
        tier: scenario.tier,
      });
    }
  }
}

const tierNumber = (tier: string): number =>
  Math.trunc(Number(tier.replaceAll(/\D/gu, ""))) || 0;

scenarioKeys.sort(
  (left, right) => tierNumber(left.tier) - tierNumber(right.tier)
);

const getScenario = (
  label: string,
  tier: string,
  name: string
): ScenarioResult | undefined =>
  results
    .get(label)
    ?.scenarios.find(
      (scenario) => scenario.tier === tier && scenario.name === name
    );

const format = (value: number | null | undefined, digits = 0): string =>
  value === null || value === undefined ? "n/a" : value.toFixed(digits);

const formatRss = (value: number | null | undefined): string =>
  value === null || value === undefined
    ? "n/a"
    : `${(value / 1024).toFixed(1)} MB`;

const delta = (value: number, baseline: number): string => {
  if (!(baseline > 0)) {
    return "n/a";
  }
  const percent = (value / baseline - 1) * 100;
  return `${percent >= 0 ? "+" : ""}${percent.toFixed(0)}%`;
};

const lines: string[] = [];

const add = (...values: string[]): void => {
  lines.push(...values);
};

add(`# Reptar Bench — ${path.basename(dir)}`, "");
add(`Generated: ${new Date().toISOString()}`, "");

if (Object.keys(manifest).length > 0) {
  add("## Environment", "");
  add("```json", JSON.stringify(manifest, null, 2));
  add("```", "");
}

const startup: Record<string, number> = {};
for (const label of available) {
  const file = path.join(dir, `${label}.startup.json`);
  if (existsSync(file)) {
    const data = JSON.parse(readFileSync(file, "utf-8")) as {
      startupMs?: number;
    };
    if (typeof data.startupMs === "number") {
      startup[label] = data.startupMs;
    }
  }
}

const binarySizes = (manifest.binarySizes ?? {}) as Record<string, number>;

const binaryKey = (label: string): string => {
  if (label === "minimal-api") {
    return "minimalApi";
  }

  if (label === "hono-jit" || label === "hono-compiled") {
    return "hono";
  }

  return label;
};

add("## Startup and binary size", "");
add(
  "| Implementation | Startup to first 200 | Binary size |",
  "| :--- | ---: | ---: |"
);
for (const label of available) {
  const key = binaryKey(label);
  const size = binarySizes[key];
  add(
    `| ${display[label] ?? label} | ${startup[label] ?? "n/a"} ms | ${
      size ? `${(size / 1024 / 1024).toFixed(1)} MB` : "n/a"
    } |`
  );
}
add("", "## Throughput (req/s)");
add(
  "",
  `| Tier | Scenario | ${available.map((label) => display[label] ?? label).join(" | ")} | Reptar vs Minimal API |`
);
add(`| :--- | :--- | ${available.map(() => "---:").join(" | ")} | ---: |`);

for (const scenario of scenarioKeys) {
  const values = available.map((label) =>
    getScenario(label, scenario.tier, scenario.name)
  );
  const reptar = getScenario("reptar", scenario.tier, scenario.name);
  const minimal = getScenario("minimal-api", scenario.tier, scenario.name);
  add(
    `| ${scenario.tier} | ${scenario.name} | ${values
      .map((value) => format(value?.rps))
      .join(" | ")} | ${
      reptar && minimal ? delta(reptar.rps, minimal.rps) : "n/a"
    } |`
  );
}

add("", "## Latency p50 (ms)");
add(
  "",
  `| Tier | Scenario | ${available.map((label) => display[label] ?? label).join(" | ")} |`
);
add(`| :--- | :--- | ${available.map(() => "---:").join(" | ")} |`);

for (const scenario of scenarioKeys) {
  add(
    `| ${scenario.tier} | ${scenario.name} | ${available
      .map((label) =>
        format(getScenario(label, scenario.tier, scenario.name)?.p50Ms, 1)
      )
      .join(" | ")} |`
  );
}

add("", "## Latency p99 (ms)");
add(
  "",
  `| Tier | Scenario | ${available.map((label) => display[label] ?? label).join(" | ")} |`
);
add(`| :--- | :--- | ${available.map(() => "---:").join(" | ")} |`);

for (const scenario of scenarioKeys) {
  add(
    `| ${scenario.tier} | ${scenario.name} | ${available
      .map((label) =>
        format(getScenario(label, scenario.tier, scenario.name)?.p99Ms, 1)
      )
      .join(" | ")} |`
  );
}

add("", "## CPU and memory");
add(
  "",
  `| Tier | Scenario | ${available
    .map((label) => `${display[label] ?? label} CPU ms/req`)
    .join(
      " | "
    )} | ${available.map((label) => `${display[label] ?? label} RSS`).join(" | ")} |`
);
add(
  `| :--- | :--- | ${available.map(() => "---:").join(" | ")} | ${available
    .map(() => "---:")
    .join(" | ")} |`
);

for (const scenario of scenarioKeys) {
  add(
    `| ${scenario.tier} | ${scenario.name} | ${available
      .map((label) =>
        format(
          getScenario(label, scenario.tier, scenario.name)?.cpuMsPerRequest,
          3
        )
      )
      .join(" | ")} | ${available
      .map((label) =>
        formatRss(getScenario(label, scenario.tier, scenario.name)?.rssKb)
      )
      .join(" | ")} |`
  );
}

add("", "## T7 write-path breakdown (ms)");

const t7 = available
  .map((label) => ({
    label,
    scenario: getScenario(label, "T7", "create-task"),
  }))
  .filter((entry): entry is { label: string; scenario: ScenarioResult } =>
    Boolean(entry.scenario?.breakdown?.stages?.length)
  );

if (t7.length === 0) {
  add("_No T7 breakdown in this directory (set BENCH_BREAKDOWN=1)._");
} else {
  for (const entry of t7) {
    const snapshot = entry.scenario.breakdown;

    if (!snapshot) {
      continue;
    }

    add(`### ${display[entry.label] ?? entry.label}`, "");
    add(
      "| Stage | Count | Avg | p50 | p95 | p99 | Max |",
      "| :--- | ---: | ---: | ---: | ---: | ---: | ---: |"
    );

    for (const stage of snapshot.stages) {
      if (!stage.count) {
        continue;
      }

      add(
        `| ${stage.name} | ${stage.count} | ${format(stage.avgMs, 3)} | ` +
          `${format(stage.p50Ms, 3)} | ${format(stage.p95Ms, 3)} | ` +
          `${format(stage.p99Ms, 3)} | ${format(stage.maxMs, 3)} |`
      );
    }

    add("");
  }
}

add("", "## Headline");

const headline = (tier: string, name: string) => {
  const reptar = getScenario("reptar", tier, name);
  const minimal = getScenario("minimal-api", tier, name);
  const hono = getScenario("hono-jit", tier, name);

  if (!reptar) {
    return;
  }

  add(
    `- **${tier} ${name}**: Reptar ${format(reptar.rps)} req/s${
      minimal ? `, ${delta(reptar.rps, minimal.rps)} vs Minimal API` : ""
    }${
      hono ? `, ${delta(reptar.rps, hono.rps)} vs Hono (JIT)` : ""
    }. p50 ${format(reptar.p50Ms, 1)} ms, p99 ${format(reptar.p99Ms, 1)} ms.`
  );
};

headline("T0", "plaintext");
headline("T1", "json");
headline("T2", "router-params");
headline("T3", "validate");
headline("T4", "task-detail");
headline("T5", "task-list");
headline("T6", "task-comments");
headline("T7", "create-task");
headline("T8", "me");
headline("T9", "login");
headline("T10", "update-task");

add("");
add("## Mixed workload (T11)");
add("");

const mixed: Record<
  string,
  {
    rps: number;
    latencyMs: { p50: number; p95: number; p99: number };
    requests: number;
    statuses: Record<string, number>;
  }
> = {};

for (const label of available) {
  const file = path.join(dir, `${label}-mixed.json`);
  if (existsSync(file)) {
    mixed[label] = JSON.parse(
      readFileSync(file, "utf-8")
    ) as (typeof mixed)[string];
  }
}

if (Object.keys(mixed).length === 0) {
  add("_No mixed-workload results in this directory._");
} else {
  add(
    "| Implementation | Req/s | p50 | p95 | p99 | Requests | Statuses |",
    "| :--- | ---: | ---: | ---: | ---: | ---: | :--- |"
  );

  for (const label of available) {
    const value = mixed[label];
    if (!value) {
      continue;
    }
    add(
      `| ${display[label] ?? label} | ${format(value.rps)} | ` +
        `${format(value.latencyMs.p50, 1)} ms | ${format(value.latencyMs.p95, 1)} ms | ` +
        `${format(value.latencyMs.p99, 1)} ms | ${value.requests} | ` +
        `${Object.entries(value.statuses)
          .map(([status, count]) => `${status}:${count}`)
          .join(", ")} |`
    );
  }
}

add("");
add("## Notes");
add(
  "",
  "- Server and load generator run on the same Lightsail box (2 vCPU = 2 SMT threads on one physical core). The server is pinned to CPU 0 and `oha` to CPU 1, so these are **relative** comparisons, not absolute capacity numbers."
);
add(
  "- Logging and Reptar telemetry are off for the main run. The existing RealWorld measurements showed default logging roughly halves in-memory throughput, so leaving it on would measure stdout, not the framework.",
  "- Reptar uses a source-generated `JsonSerializerContext` (`BenchJsonCodec`) because the default reflection codec trims nested DTO properties under Native AOT. The .NET Minimal API control also uses source-generated JSON."
);
add(
  "- `Rule<T>` and `ctx.Json<T>` with the default codec are not AOT-safe today; the benchmark app uses manual validation and a source-gen codec. That is a finding, not a hidden advantage.",
  "- Hono runs both the JIT path (`bun run src/index.ts`) and the compiled bundle (`bun build --compile`). The compiled bundle is ~105 MB because it embeds the Bun runtime; it is not AOT."
);
add("");

const reportPath = path.join(dir, "report.md");
writeFileSync(reportPath, lines.join("\n"));

console.log(`Wrote ${reportPath}`);
