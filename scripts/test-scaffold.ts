#!/usr/bin/env bun
/** External-workspace scaffold and published Native AOT client contract gate. */
/* oxlint-disable no-await-in-loop -- bounded server readiness polling */
import { spawnSync } from "node:child_process";
import {
  copyFileSync,
  existsSync,
  mkdtempSync,
  readdirSync,
  readFileSync,
  rmSync,
  statSync,
} from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";

import {
  assertPortAvailable,
  spawnOwnedProcess,
} from "./testing/owned-process";

const repoRoot = path.resolve(import.meta.dir, "..");
const workRoot = mkdtempSync(path.join(tmpdir(), "reptar-scaffold-"));
const appName = "scaffold-smoke";
const targetDir = path.join(workRoot, appName);
let keep = process.argv.includes("--keep");
// An inherited DATABASE_URL must never turn a disposable smoke into a write
// against the operator's Postgres database. OAuth credentials are unnecessary.
const testEnv = {
  ...process.env,
  DATABASE_URL: "",
  GITHUB_CLIENT_ID: "",
  GITHUB_CLIENT_SECRET: "",
  GOOGLE_CLIENT_ID: "",
  GOOGLE_CLIENT_SECRET: "",
};
const textExtensions = new Set([
  ".cs",
  ".json",
  ".jsonc",
  ".md",
  ".mjs",
  ".ts",
  ".tsx",
  ".css",
  ".html",
  ".toml",
  ".yml",
  ".yaml",
  ".sh",
  ".example",
]);

const run = (
  label: string,
  command: string,
  args: string[],
  cwd: string,
  env: NodeJS.ProcessEnv = testEnv
): void => {
  const started = performance.now();
  const result = spawnSync(command, args, {
    cwd,
    env,
    stdio: "inherit",
    timeout: 10 * 60_000,
  });
  if (result.error) {
    throw new Error(`${label}: ${result.error.message}`);
  }
  if (result.status !== 0) {
    throw new Error(
      `${label} failed (exit ${result.status ?? "null"}, signal ${result.signal ?? "none"})`
    );
  }
  console.log(
    `  ✓ ${label} (${((performance.now() - started) / 1000).toFixed(1)}s)`
  );
};

const freePort = (): number => {
  const probe = Bun.serve({
    fetch: () => new Response(""),
    hostname: "127.0.0.1",
    port: 0,
  });
  const { port } = probe;
  probe.stop(true);
  return port;
};

const findUnreplacedPlaceholders = (
  dir: string,
  found: string[] = []
): string[] => {
  for (const entry of readdirSync(dir)) {
    if (
      [
        "node_modules",
        ".git",
        "bin",
        "obj",
        "dist",
        ".turbo",
        ".reptar",
      ].includes(entry)
    ) {
      continue;
    }
    const full = path.join(dir, entry);
    if (statSync(full).isDirectory()) {
      findUnreplacedPlaceholders(full, found);
      continue;
    }
    if (!textExtensions.has(path.extname(entry)) && !entry.startsWith(".")) {
      continue;
    }
    const matches = readFileSync(full, "utf-8").match(
      /\{\{\s*[A-Z][A-Z0-9_]*\s*\}\}/gu
    );
    if (matches) {
      found.push(
        `${path.relative(targetDir, full)}: ${[...new Set(matches)].join(", ")}`
      );
    }
  }
  return found;
};

const runtimeId = (): string => {
  if (
    !["darwin", "linux"].includes(process.platform) ||
    !["arm64", "x64"].includes(process.arch)
  ) {
    throw new Error("Published scaffold gate requires Linux/macOS x64/arm64.");
  }
  return `${process.platform === "darwin" ? "osx" : "linux"}-${process.arch}`;
};

console.log(
  `Reptar external scaffold / Native AOT E2E\nWorkspace: ${workRoot}`
);
try {
  run(
    "scaffold",
    process.execPath,
    [
      path.join(repoRoot, "scripts/reptar.ts"),
      "new",
      appName,
      "-y",
      "--db",
      "sqlite",
      "--auth",
      "none",
    ],
    workRoot
  );
  if (!existsSync(path.join(targetDir, "apps/api/app.cs"))) {
    throw new Error("Scaffold did not create apps/api/app.cs");
  }
  const placeholders = findUnreplacedPlaceholders(targetDir);
  if (placeholders.length > 0) {
    throw new Error(
      `Unreplaced template placeholders:\n${placeholders.join("\n")}`
    );
  }
  run("bun install", process.execPath, ["install"], targetDir);
  run(
    "make:model dry run",
    process.execPath,
    ["run", "make:model", "Widget", "--dry-run"],
    targetDir
  );
  run("api build", process.execPath, ["run", "api:build"], targetDir);
  run("api codegen", process.execPath, ["run", "api:codegen"], targetDir);
  copyFileSync(
    path.join(repoRoot, "scripts/testing/scaffold-client.fixture.ts"),
    path.join(targetDir, "packages/client/src/runtime-smoke.ts")
  );
  run(
    "typecheck web",
    process.execPath,
    ["x", "tsc", "--noEmit", "-p", "apps/web/tsconfig.json"],
    targetDir
  );
  run(
    "typecheck generated client and runtime assertions",
    process.execPath,
    ["x", "tsc", "--noEmit", "-p", "packages/client/tsconfig.json"],
    targetDir
  );

  const publishDir = path.join(workRoot, "published-api");
  const dotnet = process.env.DOTNET_ROOT
    ? path.join(process.env.DOTNET_ROOT, "dotnet")
    : "dotnet";
  run(
    "publish Native AOT",
    dotnet,
    [
      "publish",
      "app.cs",
      "-c",
      "Release",
      "-r",
      runtimeId(),
      "--self-contained",
      "-p:PublishAot=true",
      "-p:DebugType=None",
      "-p:DebugSymbols=false",
      "-o",
      publishDir,
    ],
    path.join(targetDir, "apps/api")
  );
  const port = freePort();
  await assertPortAvailable(port);
  let logs = "";
  const owned = await spawnOwnedProcess(path.join(publishDir, "app"), [], {
    cwd: path.join(targetDir, "apps/api"),
    env: { ...testEnv, HOST: "127.0.0.1", PORT: String(port) },
    stdio: ["ignore", "pipe", "pipe"],
  });
  owned.child.stdout?.on("data", (chunk) => {
    logs += chunk.toString();
  });
  owned.child.stderr?.on("data", (chunk) => {
    logs += chunk.toString();
  });
  const cleanup = async (): Promise<void> => {
    try {
      await owned.stop();
    } catch (error) {
      keep = true;
      throw new Error(
        `Published server cleanup failed; preserving state:\n${logs}`,
        { cause: error }
      );
    }
  };
  try {
    const deadline = Date.now() + 60_000;
    let ready = false;
    while (Date.now() < deadline) {
      if (owned.child.exitCode !== null || owned.child.signalCode !== null) {
        throw new Error(`Published API exited during startup:\n${logs}`);
      }
      try {
        const response = await fetch(`http://127.0.0.1:${port}/health`, {
          signal: AbortSignal.timeout(1000),
        });
        if (response.ok) {
          ready = true;
          break;
        }
      } catch {
        // Wait for this owned server to finish startup.
      }
      await Bun.sleep(100);
    }
    if (!ready) {
      throw new Error(`Published API readiness timed out:\n${logs}`);
    }
    run(
      "generated client against published server",
      process.execPath,
      [
        "-e",
        'import { runScaffoldContractSmoke } from "./packages/client/src/runtime-smoke.ts"; await runScaffoldContractSmoke(process.env.REPTAR_SMOKE_URL);',
      ],
      targetDir,
      { ...testEnv, REPTAR_SMOKE_URL: `http://127.0.0.1:${port}` }
    );
  } finally {
    await cleanup();
  }
  console.log("\n✓ external scaffold / published AOT E2E passed");
} finally {
  if (keep) {
    console.log(`Kept generated app and artifacts at ${workRoot}`);
  } else {
    rmSync(workRoot, { force: true, recursive: true });
  }
}
