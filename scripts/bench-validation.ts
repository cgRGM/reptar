#!/usr/bin/env bun
// Runs the Rule<T> vs manual-validation microbenchmark with the repo's
// portable .NET SDK when available.
//
//   bun scripts/bench-validation.ts [results.json]

import { spawnSync } from "node:child_process";
import { existsSync } from "node:fs";
import path from "node:path";

const root = path.resolve(import.meta.dir, "..");
const localDotnet = path.join(root, ".reptar", "sdk", "dotnet");
const dotnet = existsSync(localDotnet) ? localDotnet : "dotnet";
const env = {
  ...process.env,
  DOTNET_CLI_TELEMETRY_OPTOUT: "1",
  DOTNET_NOLOGO: "1",
  DOTNET_SKIP_FIRST_TIME_EXPERIENCE: "1",
};

if (existsSync(localDotnet)) {
  env.DOTNET_ROOT = path.join(root, ".reptar", "sdk");
}

const result = spawnSync(
  dotnet,
  [
    "run",
    "-c",
    "Release",
    "--project",
    path.join("bench", "validation"),
    "--",
    ...process.argv.slice(2),
  ],
  {
    cwd: root,
    env,
    stdio: "inherit",
  }
);

process.exit(result.status ?? 1);
