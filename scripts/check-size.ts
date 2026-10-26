#!/usr/bin/env bun
/**
 * check-size — enforce AOT/Wasm size budgets.
 *
 * Usage:
 *   bun scripts/check-size.ts \
 *     --aot bench/out/reptar/reptar-bench \
 *     --wasm examples/realtime-rooms/worker/csharp/bin/Release/netwasm0.1/publish/browser/RoomsWorker.wasm \
 *     --budgets bench/size-budgets.json
 */

import { existsSync, readFileSync, statSync } from "node:fs";

interface Budgets {
  aotBytes?: number;
  wasmBytes?: number;
}

const argValue = (args: string[], name: string): string | undefined => {
  const index = args.indexOf(name);
  return index !== -1 && index + 1 < args.length ? args[index + 1] : undefined;
};

const args = process.argv.slice(2);
const budgetsPath = argValue(args, "--budgets") ?? "bench/size-budgets.json";
const budgets = JSON.parse(readFileSync(budgetsPath, "utf-8")) as Budgets;

const checks: {
  label: string;
  path?: string;
  bytes?: number;
  budget?: number;
}[] = [
  { budget: budgets.aotBytes, label: "AOT", path: argValue(args, "--aot") },
  { budget: budgets.wasmBytes, label: "Wasm", path: argValue(args, "--wasm") },
];

let failed = false;

for (const check of checks) {
  if (!check.path || !check.budget) {
    continue;
  }
  if (!existsSync(check.path)) {
    console.log(`${check.label}: missing ${check.path}`);
    failed = true;
    continue;
  }

  const bytes = statSync(check.path).size;
  const mb = (bytes / (1024 * 1024)).toFixed(2);
  const budgetMb = (check.budget / (1024 * 1024)).toFixed(2);
  const ok = bytes <= check.budget;
  console.log(
    `${ok ? "ok  " : "FAIL"} ${check.label} ${mb} MB (budget ${budgetMb} MB) ${check.path}`
  );
  if (!ok) {
    failed = true;
  }
}

process.exit(failed ? 1 : 0);
