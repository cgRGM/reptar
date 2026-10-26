#!/usr/bin/env bun
/**
 * bench-gate — compare a Reptar benchmark result to a baseline.
 *
 * Usage:
 *   bun scripts/bench-gate.ts \
 *     --results bench/results/vps-20261025T080730Z/reptar.json \
 *     --baseline bench/perf-baseline.json \
 *     --rps-drop 0.15 --p95-rise 0.20
 *
 * The result file may be a full `bench/load/run.ts` artifact or a compact
 * `{ scenarios: [{ name, rps, p95Ms }] }` file. The baseline uses the compact
 * shape. Exits non-zero when a scenario regresses beyond the thresholds.
 */

import { readFileSync } from "node:fs";
import path from "node:path";

interface Scenario {
  name: string;
  rps?: number;
  p50Ms?: number;
  p95Ms?: number;
  p99Ms?: number;
  errors?: number;
}

interface ResultFile {
  label?: string;
  scenarios?: Scenario[];
}

interface Baseline {
  label?: string;
  scenarios: Scenario[];
}

const argValue = (args: string[], name: string): string | undefined => {
  const index = args.indexOf(name);
  return index !== -1 && index + 1 < args.length ? args[index + 1] : undefined;
};

const args = process.argv.slice(2);
const resultsPath = argValue(args, "--results");
const baselinePath =
  argValue(args, "--baseline") ?? path.join("bench", "perf-baseline.json");
const rpsDrop = Number(argValue(args, "--rps-drop") ?? "0.15");
const p95Rise = Number(argValue(args, "--p95-rise") ?? "0.20");
const json = args.includes("--json");

if (!resultsPath) {
  console.error(
    "Usage: bun scripts/bench-gate.ts --results <result.json> [--baseline <baseline.json>] [--rps-drop 0.15] [--p95-rise 0.20]"
  );
  process.exit(2);
}

const readJson = <T>(file: string): T => {
  try {
    return JSON.parse(readFileSync(file, "utf-8")) as T;
  } catch (error) {
    console.error(`Failed to read ${file}: ${(error as Error).message}`);
    process.exit(2);
  }
};

const result = readJson<ResultFile>(resultsPath);
const baseline = readJson<Baseline>(baselinePath);

if (!result.scenarios?.length) {
  console.error(`No scenarios found in ${resultsPath}`);
  process.exit(2);
}

const baselineByName = new Map(
  baseline.scenarios.map((scenario) => [scenario.name, scenario])
);

interface Finding {
  scenario: string;
  metric: "rps" | "p95";
  baseline: number;
  actual: number;
  change: number;
  threshold: number;
  ok: boolean;
}

const findings: Finding[] = [];

for (const actual of result.scenarios) {
  const expected = baselineByName.get(actual.name);
  if (!expected) {
    continue;
  }

  if (typeof expected.rps === "number" && typeof actual.rps === "number") {
    const change = (actual.rps - expected.rps) / expected.rps;
    findings.push({
      actual: actual.rps,
      baseline: expected.rps,
      change,
      metric: "rps",
      ok: change >= -rpsDrop,
      scenario: actual.name,
      threshold: -rpsDrop,
    });
  }

  if (typeof expected.p95Ms === "number" && typeof actual.p95Ms === "number") {
    const change = (actual.p95Ms - expected.p95Ms) / expected.p95Ms;
    findings.push({
      actual: actual.p95Ms,
      baseline: expected.p95Ms,
      change,
      metric: "p95",
      ok: change <= p95Rise,
      scenario: actual.name,
      threshold: p95Rise,
    });
  }
}

const failures = findings.filter((finding) => !finding.ok);

if (json) {
  console.log(
    JSON.stringify(
      {
        baseline: baseline.label,
        failures,
        findings,
        ok: failures.length === 0,
        result: result.label,
        thresholds: { p95Rise, rpsDrop },
      },
      null,
      2
    )
  );
} else {
  console.log(
    `Benchmark gate: ${result.label ?? resultsPath} vs ${baseline.label ?? baselinePath}`
  );
  console.log(
    `Thresholds: RPS >= -${(rpsDrop * 100).toFixed(1)}%, p95 <= +${(p95Rise * 100).toFixed(1)}%\n`
  );

  for (const finding of findings) {
    const pct = `${(finding.change * 100).toFixed(1)}%`;
    const status = finding.ok ? "ok  " : "FAIL";
    const unit = finding.metric === "rps" ? "req/s" : "ms";
    console.log(
      `${status} ${finding.scenario.padEnd(18)} ${finding.metric.padEnd(4)} ` +
        `${finding.actual.toFixed(1)} ${unit} vs ${finding.baseline.toFixed(1)} (${pct})`
    );
  }

  if (findings.length === 0) {
    console.log("No matching scenarios; nothing to compare.");
  }
}

process.exit(failures.length === 0 ? 0 : 1);
