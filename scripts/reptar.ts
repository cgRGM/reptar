#!/usr/bin/env bun

import { Database } from "bun:sqlite";
import { createHash } from "node:crypto";
import { spawnSync } from "node:child_process";
import {
  copyFileSync,
  cpSync,
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  readdirSync,
  renameSync,
  rmSync,
  statSync,
  writeFileSync,
} from "node:fs";
import path from "node:path";
import { tmpdir } from "node:os";

import { upsertReptarAgentsContext } from "../packages/create-reptar/src/scaffold";
import { patchNetWasmHost } from "../packages/host/src/patch";
import { runCheck } from "./check-netwasm";
import { listDevServers } from "./dev-status";
import { runMake } from "./make";
import { ensureJsonContextInclude, generateJsonContext } from "./json-context";
import { planMigrations, generateMigrationFile } from "./diff";
import {
  inspectProject,
  doctorProject,
  resolveDatabaseTarget,
  resolveMigrationsDir,
} from "./inspect";
import { runMcpServer } from "./mcp";
import { runRealtimeSmoke } from "./test-realtime";

const repoRoot = path.resolve(import.meta.dir, "..");
const requiredSdk = "10.0.401";
const toolchainDir = path.join(repoRoot, ".reptar");
const sdkDir = path.join(toolchainDir, "sdk");
const cliHome = path.join(toolchainDir, "cli-home");

mkdirSync(cliHome, { recursive: true });

const versionAtLeast = (actual: string, required: string): boolean => {
  const left = actual.split(".").map(Number);
  const right = required.split(".").map(Number);
  const length = Math.max(left.length, right.length);

  for (let index = 0; index < length; index += 1) {
    const a = left[index] ?? 0;
    const b = right[index] ?? 0;
    if (a !== b) {
      return a > b;
    }
  }

  return true;
};

const dotnetPath = (): string => {
  if (process.env.DOTNET_ROOT) {
    return path.join(process.env.DOTNET_ROOT, "dotnet");
  }
  const localDotnet = path.join(sdkDir, "dotnet");
  if (existsSync(localDotnet)) {
    return localDotnet;
  }
  return "dotnet";
};

const dotnetEnv = (): NodeJS.ProcessEnv => ({
  ...process.env,
  DOTNET_CLI_HOME: cliHome,
  DOTNET_NOLOGO: "1",
  DOTNET_SKIP_FIRST_TIME_EXPERIENCE: "1",
  DOTNET_CLI_TELEMETRY_OPTOUT: "1",
  NUGET_HTTP_CACHE_PATH: path.join(toolchainDir, "nuget-cache"),
});

/**
 * Reads `--flag value` or `--flag=value` from an argument list.
 */
const argValue = (args: string[], ...names: string[]): string | undefined => {
  for (const name of names) {
    const equals = args.find((arg) => arg.startsWith(`${name}=`));
    if (equals) {
      return equals.slice(name.length + 1);
    }
    const index = args.indexOf(name);
    if (index !== -1 && index + 1 < args.length) {
      return args[index + 1];
    }
  }
  return undefined;
};

/**
 * Removes `--flag value` / `--flag=value` pairs before forwarding args to an app.
 */
const withoutArgs = (args: string[], ...names: string[]): string[] => {
  const result: string[] = [];
  for (let index = 0; index < args.length; index += 1) {
    const arg = args[index];
    if (names.some((name) => arg === name)) {
      index += 1;
      continue;
    }
    if (names.some((name) => arg.startsWith(`${name}=`))) {
      continue;
    }
    result.push(arg);
  }
  return result;
};

/**
 * Resolves the app directory for repo-level commands. Explicit `--app` wins,
 * then a file-based app in the current directory, then the reference blog.
 */
const resolveAppDir = (args: string[]): string => {
  const explicit = argValue(args, "--app", "-a");
  if (explicit) {
    return path.resolve(process.cwd(), explicit);
  }
  if (existsSync(path.join(process.cwd(), "app.cs"))) {
    return process.cwd();
  }
  return path.join(repoRoot, "examples/blog");
};

/**
 * Finds processes holding the framework's build outputs. On macOS/Linux this
 * is the exact cause of MSB4018 ("file is being used by another process") when
 * an orphaned `dotnet run` or another agent is still running.
 */
const findBuildLockHolders = (): { command: string; pid: number }[] => {
  if (process.platform === "win32") {
    return [];
  }

  const files = [
    path.join(repoRoot, "framework/Reptar/bin/Debug/net10.0/Reptar.deps.json"),
    path.join(repoRoot, "framework/Reptar/bin/Debug/net10.0/Reptar.dll"),
    path.join(repoRoot, "framework/Reptar/bin/Release/net10.0/Reptar.deps.json"),
    path.join(repoRoot, "framework/Reptar/bin/Release/net10.0/Reptar.dll"),
    path.join(
      repoRoot,
      "framework/Reptar.Tests/bin/Debug/net10.0/Reptar.Tests.deps.json"
    ),
    path.join(
      repoRoot,
      "framework/Reptar.Tests/bin/Debug/net10.0/Reptar.Tests.runtimeconfig.json"
    ),
    path.join(
      repoRoot,
      "framework/Reptar.Tests/bin/Debug/net10.0/Reptar.Tests.dll"
    ),
  ].filter((file) => existsSync(file));

  const holders = new Map<number, string>();
  for (const file of files) {
    const result = spawnSync("lsof", ["-t", file], { encoding: "utf-8" });
    if (result.status !== 0 || !result.stdout) {
      continue;
    }
    for (const line of result.stdout.split("\n")) {
      const pid = Number.parseInt(line.trim(), 10);
      if (!Number.isFinite(pid) || holders.has(pid)) {
        continue;
      }
      const ps = spawnSync("ps", ["-p", String(pid), "-o", "command="], {
        encoding: "utf-8",
      });
      holders.set(pid, ps.stdout?.trim() || "(unknown process)");
    }
  }

  return [...holders.entries()].map(([pid, command]) => ({ command, pid }));
};

const preflightBuildLocks = (args: string[]): void => {
  const holders = findBuildLockHolders();
  if (holders.length === 0) {
    return;
  }

  if (args.includes("--kill-orphans")) {
    for (const holder of holders) {
      try {
        process.kill(holder.pid, "SIGTERM");
      } catch {
        // Already gone or not ours.
      }
    }
    const remaining = findBuildLockHolders();
    if (remaining.length === 0) {
      console.log(
        `  ✓ released build lock(s): ${holders.map((h) => h.pid).join(", ")}`
      );
      return;
    }
    console.error("Could not release every build lock:");
    for (const holder of remaining) {
      console.error(`  pid ${holder.pid}: ${holder.command}`);
    }
    process.exit(1);
  }

  console.error(
    "Build output is locked by another process. A dev server or another agent is still running:"
  );
  for (const holder of holders) {
    console.error(`  pid ${holder.pid}: ${holder.command}`);
  }
  console.error(
    "Stop it with `kill <pid>`, or re-run with `--kill-orphans` to release stale locks automatically."
  );
  process.exit(1);
};

const readSdkVersion = (): string | null => {
  const result = spawnSync(dotnetPath(), ["--version"], {
    encoding: "utf-8",
    env: dotnetEnv(),
  });

  return result.status === 0 ? result.stdout.trim() : null;
};

const installSdk = (): void => {
  console.log(
    `Reptar needs the .NET ${requiredSdk} SDK as a build tool (never shipped). Installing into .reptar/sdk...`
  );

  mkdirSync(sdkDir, { recursive: true });
  const installer = path.join(toolchainDir, "dotnet-install.sh");

  const download = spawnSync(
    "curl",
    ["-sSL", "https://dot.net/v1/dotnet-install.sh", "-o", installer],
    { stdio: "inherit" }
  );

  if (download.status !== 0) {
    process.exit(download.status ?? 1);
  }

  const install = spawnSync(
    "bash",
    [installer, "--version", requiredSdk, "--install-dir", sdkDir, "--no-path"],
    { stdio: "inherit" }
  );

  if (install.status !== 0) {
    process.exit(install.status ?? 1);
  }

  process.env.DOTNET_ROOT = sdkDir;
  process.env.PATH = `${sdkDir}:${process.env.PATH ?? ""}`;
};

const ensureSdk = (): void => {
  const current = readSdkVersion();

  if (current && versionAtLeast(current, requiredSdk)) {
    return;
  }
  if (process.env.REPTAR_SKIP_SDK_CHECK === "1") {
    return;
  }

  installSdk();
};

const run = (args: string[], cwd = repoRoot): void => {
  const result = spawnSync(dotnetPath(), args, {
    cwd,
    env: dotnetEnv(),
    stdio: "inherit",
  });

  if (result.status !== 0) {
    process.exit(result.status ?? 1);
  }
};

const formatPaths = (paths: string[]): void => {
  const result = spawnSync(
    process.execPath,
    ["x", "oxfmt", "--write", ...paths],
    {
      cwd: repoRoot,
      env: dotnetEnv(),
      stdio: "inherit",
    }
  );

  if (result.status !== 0) {
    process.exit(result.status ?? 1);
  }
};

const formatGenerated = (): void => {
  formatPaths(["examples/blog/rpc", "packages/rpc"]);
};

const syncRpcContracts = (): void => {
  const generated = path.join(repoRoot, "examples/blog/rpc");
  const rpcSource = path.join(repoRoot, "packages/rpc/src");

  const appType = readFileSync(
    path.join(generated, "app-type.d.ts"),
    "utf-8"
  ).replaceAll('from "@reptar/client"', 'from "./client"');

  writeFileSync(path.join(rpcSource, "app-type.ts"), appType);
  copyFileSync(
    path.join(generated, "types.ts"),
    path.join(rpcSource, "types.ts")
  );
  copyFileSync(
    path.join(generated, "schemas.ts"),
    path.join(rpcSource, "schemas.ts")
  );
  copyFileSync(
    path.join(generated, "openapi.json"),
    path.join(repoRoot, "packages/rpc/openapi.json")
  );
  if (existsSync(path.join(generated, "schema.json"))) {
    copyFileSync(
      path.join(generated, "schema.json"),
      path.join(repoRoot, "packages/rpc/schema.json")
    );
  }
  if (existsSync(path.join(repoRoot, "llms.txt"))) {
    copyFileSync(
      path.join(repoRoot, "llms.txt"),
      path.join(repoRoot, "packages/rpc/llms.txt")
    );
  }
};

/**
 * Post-pass for the Worker module. NetWasm 0.10.1's host-tools wasm-opt
 * crashes on the current module (SIGBUS / "unknown error"), but the Binaryen
 * binary shipped in netwasm.toolchain handles it correctly. Optimizing after
 * publish brings the blog Worker from ~27 MB to ~15 MB, under Cloudflare's
 * 25 MiB per-asset limit.
 */
const optimizeWasmModule = (wasmPath: string): number | null => {
  if (process.env.REPTAR_SKIP_WASM_OPT === "1") {
    return null;
  }
  if (!existsSync(wasmPath)) {
    return null;
  }

  const packagesDir = path.join(
    toolchainDir,
    "cli-home",
    ".nuget",
    "packages",
    "netwasm.toolchain"
  );
  if (!existsSync(packagesDir)) {
    return null;
  }

  const version = readdirSync(packagesDir).sort().at(-1);
  if (!version) {
    return null;
  }

  const binary = path.join(
    packagesDir,
    version,
    "tools",
    "binaryen",
    "bin",
    process.platform === "win32" ? "wasm-opt.exe" : "wasm-opt"
  );
  if (!existsSync(binary)) {
    return null;
  }

  const optimized = `${wasmPath}.optimized`;
  const result = spawnSync(
    binary,
    [
      "--enable-bulk-memory",
      "--enable-exception-handling",
      "--enable-nontrapping-float-to-int",
      "--enable-reference-types",
      wasmPath,
      "-Oz",
      "-o",
      optimized,
    ],
    { stdio: "inherit" }
  );

  if (result.status !== 0 || !existsSync(optimized)) {
    rmSync(optimized, { force: true });
    console.warn(
      `wasm-opt post-pass skipped (${result.status ?? "no output"}) — keeping the unoptimized module.`
    );
    return null;
  }

  const before = statSync(wasmPath).size;
  const after = statSync(optimized).size;
  renameSync(optimized, wasmPath);
  console.log(
    `Optimized ${path.basename(wasmPath)}: ${(before / 1024 / 1024).toFixed(1)} MB -> ${(after / 1024 / 1024).toFixed(1)} MB`
  );
  return after;
};

const sha256File = (filePath: string): string =>
  createHash("sha256").update(readFileSync(filePath)).digest("hex");

/**
 * The optimized wasm no longer matches the hash recorded by NetWasm's
 * deployment manifest. Refresh the artifact hash and the generated loader's
 * manifestSha256 so the host's integrity check passes.
 */
const refreshWasmManifest = (publishRoot: string, wasmFile: string): void => {
  const manifestPath = path.join(publishRoot, "deployment.json");
  if (!existsSync(manifestPath)) {
    return;
  }

  const manifest = JSON.parse(readFileSync(manifestPath, "utf-8")) as {
    artifacts?: { relativePath?: string; sha256?: string }[];
  };
  const artifact = manifest.artifacts?.find(
    (entry) => entry.relativePath === wasmFile
  );
  if (!artifact) {
    return;
  }

  artifact.sha256 = sha256File(path.join(publishRoot, wasmFile));
  writeFileSync(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`);

  const manifestHash = sha256File(manifestPath);
  const loader = readdirSync(publishRoot).find((file) => {
    if (!file.endsWith(".mjs") || file === "netwasm.page.mjs") {
      return false;
    }
    return readFileSync(path.join(publishRoot, file), "utf-8").includes(
      "manifestSha256"
    );
  });

  if (loader) {
    const loaderPath = path.join(publishRoot, loader);
    const content = readFileSync(loaderPath, "utf-8").replace(
      /manifestSha256: "[0-9a-f]{64}"/u,
      `manifestSha256: "${manifestHash}"`
    );
    writeFileSync(loaderPath, content);
  }

  console.log(`Refreshed deployment manifest for ${wasmFile}`);
};

const workerAssetLimit = 25 * 1024 * 1024;

const [command, ...rest] = process.argv.slice(2);

if (command === "--help" || command === "-h" || command === "help") {
  console.log(`reptar <command>

Commands:
  new, create, init Scaffold a new Reptar project (options: --template, --db, --auth, -y)
  make:model   Generate a TableDef + [ReptarEntity] DTO (flags: -f/--feature, -m/--migration, --force, --dry-run)
  make:feature Generate a CRUD feature slice for an existing model
  json:gen     Generate json-context.g.cs from [ReptarEntity], Rule<T>, and Produces<T> types
  dev          Run the example blog API on http://localhost:8787
  agent        Boot the interactive Reptar AI agent in the current workspace
               (flags: --model <name>, --provider <p>, --session <id>, --web [port])
  run          Run any file-based Reptar app: bun reptar run <path/to/app.cs>
  baas:dev     Run the ReptarBase BaaS engine & studio on http://localhost:8787
  gateway:dev  Run the AI gateway example API on http://localhost:8788
  rooms:dev    Run the real-time rooms WebSocket hub on http://localhost:8787
  inspect      Inspect database tables, routes, migrations, and project state (--json)
  doctor       Diagnose SDK toolchain, database connectivity, and RPC status (--json, --strict)
  mcp          Run the Model Context Protocol stdio server for AI agents
  db           Database operations: status, migrate, new <name>, plan (--json), generate
  codegen      Generate packages/rpc contracts from the C# route tree
  test         Run the C# framework tests
  test:scaffold End-to-end scaffold smoke: new → install → build → codegen → tsc
  test:realtime Two-host smoke: run the realtime-rooms protocol suite against Kestrel AND local workerd+DO
  check:netwasm Scan C# sources for NetWasm/Workers incompatibilities (--strict, default target examples/)
  check:host   Verify app.host.mjs exports match the generated interop manifest
  check:worker Measure the uncompressed Worker upload size (--app, --max-mb)
  dev:status   List running Reptar dev servers and their ports
  self-check   doctor + tests + host contract + netwasm lint + two-host realtime smoke in one command
  build        Build the framework core
  build:worker Publish the C# Wasm app and patch the NetWasm host for Workers
  build:worker:rooms Publish the realtime-rooms hub as a NetWasm Worker host

The .NET SDK is only a build tool. It is installed under .reptar/ when missing.
`);
  process.exit(0);
}

if (command === "new" || command === "create" || command === "init") {
  const createReptarEntry = path.join(
    repoRoot,
    "packages/create-reptar/src/index.ts"
  );
  const result = spawnSync(process.execPath, [createReptarEntry, ...rest], {
    cwd: process.cwd(),
    env: process.env,
    stdio: "inherit",
  });
  process.exit(result.status ?? 0);
}

if (command === "make:model" || command === "make:feature") {
  try {
    runMake(command, rest, repoRoot);
    process.exit(0);
  } catch (error) {
    console.error(
      `make failed: ${error instanceof Error ? error.message : String(error)}`
    );
    process.exit(1);
  }
}

if (command === "json:gen") {
  const appRoot = path.resolve(
    process.cwd(),
    argValue(rest, "--app") ?? "."
  );
  const result = generateJsonContext(appRoot);
  if (result.types.length === 0) {
    console.log("json:gen: no [ReptarEntity], Rule<T>, or Produces<T> types found.");
    process.exit(0);
  }
  const includeChanged = ensureJsonContextInclude(appRoot);
  console.log(
    `✓ ${result.file} (${result.types.length} type(s))` +
      (includeChanged ? " and wired into app.cs" : "")
  );
  process.exit(0);
}

const handleDbCommand = (args: string[]) => {
  const subCommand = args[0] ?? "status";
  const appArg = argValue(args, "--app", "-a");
  const targetDir = appArg
    ? path.resolve(process.cwd(), appArg)
    : process.cwd();

  const migrationsDir =
    argValue(args, "--dir") ?? resolveMigrationsDir(repoRoot, targetDir);

  const databaseTarget = resolveDatabaseTarget(
    repoRoot,
    targetDir,
    argValue(args, "--db")
  );
  const dbPath = databaseTarget.path;

  if (subCommand === "new") {
    const name = args[1]?.replace(/\.sql$/i, "");
    if (!name || name.startsWith("-")) {
      console.error("Usage: bun reptar db new <migration_name>");
      process.exit(1);
    }
    mkdirSync(migrationsDir, { recursive: true });
    const existing = existsSync(migrationsDir)
      ? readdirSync(migrationsDir).filter((f) => f.endsWith(".sql"))
      : [];
    let nextNum = 1;
    for (const f of existing) {
      const match = f.match(/^(\d+)_/);
      if (match) {
        const n = Number.parseInt(match[1], 10);
        if (n >= nextNum) {
          nextNum = n + 1;
        }
      }
    }
    const prefix = String(nextNum).padStart(4, "0");
    const sanitized = name.toLowerCase().replaceAll(/[^a-z0-9_]/g, "_");
    const filename = `${prefix}_${sanitized}.sql`;
    const targetPath = path.join(migrationsDir, filename);
    writeFileSync(
      targetPath,
      `-- Migration: ${filename}\n-- Created: ${new Date().toISOString()}\n\n`
    );
    console.log(`✓ Created migration: ${targetPath}`);
    process.exit(0);
  }

  if (databaseTarget.dialect !== "sqlite") {
    console.error(
      `The built-in migration runner only supports SQLite. Detected ${databaseTarget.dialect} at ${databaseTarget.path}.`
    );
    console.error(
      "Use your database's migration tool (or apply the generated SQL manually) for PostgreSQL/MySQL."
    );
    process.exit(1);
  }

  // `plan` and `generate` work even before a migrations directory exists.
  if (subCommand === "plan") {
    const isJson = args.includes("--json");
    const plan = planMigrations(repoRoot, targetDir, {
      dbPath,
      migrationsDir,
    });
    if (isJson) {
      console.log(JSON.stringify(plan, null, 2));
      process.exit(0);
    }

    console.log(`\nReptar Database Migration Plan`);
    console.log(`──────────────────────────────────────────`);
    console.log(`Database:   ${dbPath}`);
    console.log(
      `Status:     ${plan.status === "in_sync" ? "✓ Schema is in sync with TableDef" : "⚠ Schema drift detected"}\n`
    );

    if (plan.changes.length === 0) {
      console.log(
        "No pending schema changes. All TableDef tables and columns exist in database."
      );
      process.exit(0);
    }

    console.log(`Planned operations (${plan.changes.length}):`);
    for (const change of plan.changes) {
      console.log(`  • [${change.type}] ${change.detail}`);
    }
    console.log(`\nPlanned SQL:`);
    console.log(plan.sql);
    console.log(
      `\nRun 'bun reptar db generate' to write these changes to a migration file.`
    );
    process.exit(0);
  }

  if (subCommand === "generate") {
    const name =
      argValue(args, "--name") ??
      (args[1]?.startsWith("-") ? undefined : args[1]);
    const res = generateMigrationFile(repoRoot, targetDir, name, {
      dbPath,
      migrationsDir,
    });
    if (res.created) {
      console.log(`✓ Created migration: ${res.path}`);
      console.log(
        `  Planned ${res.changes} change(s). Run 'bun reptar db migrate' to apply.`
      );
    } else {
      console.log(`✓ ${res.message}`);
    }
    process.exit(0);
  }

  if (!existsSync(migrationsDir)) {
    console.log(
      `No migrations directory found at ${migrationsDir}. Run 'bun reptar db new <name>' to create your first migration.`
    );
    process.exit(0);
  }

  const migrationFiles = readdirSync(migrationsDir)
    .filter((f) => f.endsWith(".sql"))
    .sort();

  if (migrationFiles.length === 0) {
    console.log(`No .sql migration files found in ${migrationsDir}.`);
    process.exit(0);
  }

  mkdirSync(path.dirname(dbPath), { recursive: true });
  const db = new Database(dbPath);

  db.run(`
    CREATE TABLE IF NOT EXISTS "__reptar_migrations" (
      "id" TEXT PRIMARY KEY,
      "name" TEXT NOT NULL,
      "applied_at" TEXT NOT NULL
    );
  `);

  const appliedRows = db
    .query(`SELECT "id", "name", "applied_at" FROM "__reptar_migrations"`)
    .all() as { id: string; name: string; applied_at: string }[];

  const appliedMap = new Map<string, string>();
  for (const row of appliedRows) {
    appliedMap.set(row.id, row.applied_at);
  }

  if (subCommand === "status") {
    console.log(`Database: ${dbPath}`);
    console.log(`Migrations directory: ${migrationsDir}\n`);
    console.log(`Status        Migration`);
    console.log(`────────────  ──────────────────────────────────────────`);
    for (const file of migrationFiles) {
      const id = file.replace(/\.sql$/i, "");
      const appliedAt = appliedMap.get(id);
      if (appliedAt) {
        console.log(`✓ Applied     ${file} (${appliedAt})`);
      } else {
        console.log(`○ Pending     ${file}`);
      }
    }
    process.exit(0);
  }

  if (subCommand === "migrate") {
    const pending = migrationFiles.filter(
      (f) => !appliedMap.has(f.replace(/\.sql$/i, ""))
    );
    if (pending.length === 0) {
      console.log(
        `✓ Database is up to date (${migrationFiles.length} applied, 0 pending).`
      );
      process.exit(0);
    }

    console.log(
      `Applying ${pending.length} pending migration(s) to ${dbPath}...`
    );
    for (const file of pending) {
      const id = file.replace(/\.sql$/i, "");
      const filePath = path.join(migrationsDir, file);
      const sql = readFileSync(filePath, "utf-8");
      const start = performance.now();

      db.transaction(() => {
        db.run(sql);
        db.run(
          `INSERT INTO "__reptar_migrations" ("id", "name", "applied_at") VALUES (?, ?, ?)`,
          [id, file, new Date().toISOString()]
        );
      })();

      const duration = (performance.now() - start).toFixed(1);
      console.log(`✓ Applied ${file} (${duration}ms)`);
    }

    console.log(`\nAll ${pending.length} migration(s) applied successfully.`);
    process.exit(0);
  }

  console.error(
    `Unknown db command: '${subCommand}'. Try: db status, db migrate, db new <name>, db plan, db generate`
  );
  process.exit(1);
};

if (command === "db") {
  handleDbCommand(rest);
}

if (command === "inspect") {
  const isJson = rest.includes("--json");
  let targetDir = process.cwd();
  const appIdx = rest.findIndex((r) => r === "--app" || r === "-a");
  if (appIdx !== -1 && rest[appIdx + 1]) {
    targetDir = path.resolve(process.cwd(), rest[appIdx + 1]);
  }
  const inspection = inspectProject(repoRoot, targetDir);
  if (isJson) {
    console.log(JSON.stringify(inspection, null, 2));
    process.exit(0);
  }

  console.log(`\nReptar Project Inspection`);
  console.log(`──────────────────────────────────────────`);
  console.log(
    `Name:        ${inspection.project.name} (${inspection.project.version})`
  );
  console.log(`Root:        ${inspection.project.root}`);
  console.log(
    `Database:    ${inspection.database.driver} (${inspection.database.connected ? "connected" : "unconnected"})`
  );
  console.log(`DB Path:     ${inspection.database.path}`);
  console.log(`Tables:      ${inspection.tables.length} table(s)`);
  for (const t of inspection.tables) {
    console.log(
      `  • ${t.name} (${t.columns.length} columns, ${t.indexes.length} indexes)`
    );
  }
  console.log(`Routes:      ${inspection.routes.length} route(s) registered`);
  const aiRoutes = inspection.routes.filter((r) => r.isAiTool);
  if (aiRoutes.length > 0) {
    console.log(`AI Tools:    ${aiRoutes.length} route(s) exposed as AI tools`);
  }
  console.log(
    `Migrations:  ${inspection.migrations.applied.length} applied, ${inspection.migrations.pending.length} pending`
  );
  console.log(
    `Contracts:   ${inspection.generated.status} (${inspection.generated.openapi})`
  );
  process.exit(0);
}

if (command === "doctor") {
  let targetDir = process.cwd();
  const appIdx = rest.findIndex((r) => r === "--app" || r === "-a");
  if (appIdx !== -1 && rest[appIdx + 1]) {
    targetDir = path.resolve(process.cwd(), rest[appIdx + 1]);
  }
  if (rest.includes("--fix")) {
    upsertReptarAgentsContext(targetDir);
  }
  const isJson = rest.includes("--json");
  const strict = rest.includes("--strict");
  const report = doctorProject(repoRoot, targetDir, { strict });
  if (isJson) {
    console.log(JSON.stringify(report, null, 2));
    process.exit(report.healthy ? 0 : 1);
  }

  console.log(`\nReptar Doctor Diagnostics${strict ? " (strict)" : ""}`);
  console.log(`──────────────────────────────────────────`);
  for (const check of report.checks) {
    const symbol = check.passed ? (check.warning ? "⚠" : "✓") : "✗";
    console.log(`${symbol} ${check.title}: ${check.details ?? ""}`);
    if (!check.passed && check.advice) {
      console.log(`    ↳ Action: ${check.advice}`);
    } else if (check.advice) {
      // Soft warning: the check passes overall, but advice is worth showing
      // (e.g. PATH/pinned SDK divergence that only affects bare dotnet calls).
      console.log(`    ↳ Note: ${check.advice}`);
    }
  }
  console.log(
    `\nStatus: ${
      report.healthy
        ? "All checks passed! Reptar environment is healthy."
        : strict && report.checks.some((check) => check.warning)
          ? "Strict mode: warnings are treated as failures. See notes above."
          : "Some checks failed. See actions above."
    }`
  );
  process.exit(report.healthy ? 0 : 1);
}

if (command === "mcp") {
  runMcpServer(repoRoot, process.cwd());
  // Intentional never-resolving promise: keeps the stdio server alive.
  // eslint-disable-next-line no-empty-function, promise/avoid-new
  await new Promise(() => {});
}

ensureSdk();

switch (command) {
  case "dev": {
    const appDir = resolveAppDir(rest);
    const passthrough = withoutArgs(rest, "--app", "-a");
    run(["run", "app.cs", ...passthrough], appDir);
    break;
  }

  case "agent": {
    if (rest.includes("--help") || rest.includes("-h")) {
      console.log(`reptar agent — interactive AI agent for your workspace

Usage:
  bun reptar agent                          start the terminal agent here
  bun reptar agent --connect [provider]     connect a provider (paste API key,
                                             hidden input, connection test, saved
                                             to ~/.reptar/agent/auth.json)
  bun reptar agent --providers              show provider connection status
  bun reptar agent --model <provider/model>  pick a model (openai/gpt-4o-mini,
                                             anthropic/claude-sonnet-4-5, gemini/..., ollama/...)
  bun reptar agent --session <id>            resume a saved session
  bun reptar agent --web [port]              serve the web companion (default 4040)
  bun reptar agent --self-test               verify the workspace tools offline

In-session: /connect (arrow-key provider picker + hidden key + live test),
/providers (bordered status table), /models (live catalog, falls back to the
free models.dev catalog — no key needed), /disconnect, plus /reset /history
/tools /model /sessions /exit.

Editor: type / for a filtered command dropdown, @file to attach files
(images included), !cmd for shell passthrough, ctrl+p for the command
palette; a status bar shows cwd, git branch, model, tokens and cost.
Interactive widgets degrade to numbered input when stdin is piped.

Model resolution order: --model/--provider flags, REPTAR_AGENT_MODEL/PROVIDER,
provider env keys (OPENAI_API_KEY / ANTHROPIC_API_KEY / GEMINI_API_KEY /
OLLAMA_HOST / OPENROUTER_API_KEY / AI_GATEWAY_API_KEY), credentials saved by
/connect, then offline echo mode. Env keys win over saved credentials; the
/providers table always shows which source is in use.

Slash commands inside the session: /reset /history /tools /model /sessions
/session <id> /new <title> /help /exit`);
      process.exit(0);
    }

    const workspaceFlag = rest.indexOf("--workspace");
    const workspace =
      workspaceFlag !== -1 && rest[workspaceFlag + 1]
        ? path.resolve(rest[workspaceFlag + 1])
        : process.cwd();

    // Drop an explicit --workspace flag (and its value); the CLI injects the
    // caller's cwd as the workspace root by default.
    const passThrough =
      workspaceFlag === -1
        ? rest
        : rest.filter(
            (_, index) => index !== workspaceFlag && index !== workspaceFlag + 1
          );

    run(
      ["run", "app.cs", "--", "--workspace", workspace, ...passThrough],
      path.join(repoRoot, "examples/agent-cli")
    );
    break;
  }

  case "run": {
    const [target, ...runArgs] = rest;
    if (!target || target.startsWith("-")) {
      console.error("Usage: bun reptar run <path/to/app.cs> [args...]");
      process.exit(1);
    }

    const appPath = path.isAbsolute(target)
      ? target
      : ([path.join(process.cwd(), target), path.join(repoRoot, target)].find(
          (candidate) => existsSync(candidate)
        ) ?? path.join(process.cwd(), target));

    run(
      ["run", path.basename(appPath), "--", ...runArgs],
      path.dirname(appPath)
    );
    break;
  }

  case "realworld:dev": {
    run(["run", "app.cs", ...rest], path.join(repoRoot, "examples/realworld"));
    break;
  }

  case "baas:dev": {
    run(["run", "app.cs", ...rest], path.join(repoRoot, "examples/baas"));
    break;
  }

  case "baas:test": {
    const res = spawnSync("bash", ["test.sh"], {
      cwd: path.join(repoRoot, "examples/baas"),
      stdio: "inherit",
    });
    process.exit(res.status ?? 0);
    break;
  }

  case "gateway:dev": {
    run(["run", "app.cs", ...rest], path.join(repoRoot, "examples/ai-gateway"));
    break;
  }

  case "rooms:dev": {
    run(
      ["run", "app.cs", ...rest],
      path.join(repoRoot, "examples/realtime-rooms")
    );
    break;
  }

  case "codegen": {
    preflightBuildLocks(rest);
    const appDir = resolveAppDir(rest);
    const appCs = path.join(appDir, "app.cs");
    if (!existsSync(appCs)) {
      console.error(`No app.cs found at ${appCs}. Use --app <dir> to target an app.`);
      process.exit(1);
    }

    // Fail fast instead of starting a server for apps that do not implement
    // the codegen entry point.
    const appSource = readFileSync(appCs, "utf-8");
    if (!appSource.includes("--codegen")) {
      console.error(
        `${path.relative(repoRoot, appCs)} does not implement --codegen. ` +
          "Use the app's own codegen script instead."
      );
      process.exit(1);
    }

    const check = rest.includes("--check");
    const outArg = argValue(rest, "--out");
    const outDir = outArg
      ? path.resolve(process.cwd(), outArg)
      : path.join(appDir, "rpc");
    const passthrough = withoutArgs(
      rest,
      "--app",
      "-a",
      "--out",
      "--check"
    );
    const generatedDir = check
      ? mkdtempSync(path.join(tmpdir(), "reptar-codegen-"))
      : outDir;

    try {
      run(
        [
          "run",
          "app.cs",
          "--",
          "--codegen",
          "--out",
          generatedDir,
          ...passthrough,
        ],
        appDir
      );

      if (check) {
        // The normal codegen path formats the output before committing it.
        // Format the temp output too, otherwise the check compares formatted
        // files against unformatted ones and always reports stale.
        formatPaths([generatedDir]);

        const files = [
          "app-type.d.ts",
          "types.ts",
          "schemas.ts",
          "openapi.json",
          "schema.json",
        ];
        const stale: string[] = [];

        for (const file of files) {
          const generatedPath = path.join(generatedDir, file);
          const currentPath = path.join(outDir, file);

          if (!existsSync(generatedPath)) {
            continue;
          }

          if (
            !existsSync(currentPath) ||
            readFileSync(generatedPath, "utf-8") !==
              readFileSync(currentPath, "utf-8")
          ) {
            stale.push(path.relative(repoRoot, currentPath));
          }
        }

        if (stale.length > 0) {
          console.error("Generated contracts are stale:");
          for (const file of stale) {
            console.error(`  ✗ ${file}`);
          }
          console.error(
            "Run `bun reptar codegen` and commit the generated files."
          );
          process.exit(1);
        }

        console.log(`✓ ${path.relative(repoRoot, outDir)} is up to date`);
        break;
      }

      // If the output sits next to a local client runtime, point AppType at it.
      const appType = path.join(outDir, "app-type.d.ts");
      if (existsSync(appType) && existsSync(path.join(outDir, "client.ts"))) {
        const content = readFileSync(appType, "utf-8").replaceAll(
          'from "@reptar/client"',
          'from "./client"'
        );
        writeFileSync(appType, content, "utf-8");
      }

      const isBlog =
        path.resolve(appDir) === path.resolve(repoRoot, "examples/blog");
      if (isBlog) {
        syncRpcContracts();
        formatGenerated();
      } else {
        formatPaths([outDir]);
      }
    } finally {
      if (check) {
        rmSync(generatedDir, { force: true, recursive: true });
      }
    }
    break;
  }

  case "test": {
    preflightBuildLocks(rest);
    run(["test", "framework/Reptar.Tests/Reptar.Tests.csproj", ...rest]);
    break;
  }

  case "test:scaffold": {
    preflightBuildLocks(rest);
    const result = spawnSync(
      process.execPath,
      [path.join(repoRoot, "scripts/test-scaffold.ts"), ...rest],
      { cwd: process.cwd(), stdio: "inherit" }
    );
    process.exit(result.status ?? 1);
  }

  case "build": {
    preflightBuildLocks(rest);
    run(["build", "framework/Reptar/Reptar.csproj", ...rest]);
    break;
  }

  case "build:realworld": {
    const workerRoot = path.join(repoRoot, "spikes/realworld-worker");
    const csharpRoot = path.join(workerRoot, "csharp");
    const publishRoot = path.join(
      csharpRoot,
      "bin/Release/netwasm0.1/publish/browser"
    );
    const browserRoot = path.join(workerRoot, "public/browser");

    // The NetWasm page host is generated per app. A stale incremental build
    // can leave the blog page host in the RealWorld output, so force a clean
    // publish for this separate Worker.
    rmSync(path.join(csharpRoot, "bin"), { force: true, recursive: true });
    rmSync(path.join(csharpRoot, "obj"), { force: true, recursive: true });

    run(
      [
        "publish",
        "-c",
        "Release",
        "-p:DebugType=None",
        "-p:DebugSymbols=false",
      ],
      csharpRoot
    );

    if (existsSync(publishRoot)) {
      rmSync(browserRoot, { force: true, recursive: true });
      cpSync(publishRoot, browserRoot, { recursive: true });
    }

    if (existsSync(browserRoot)) {
      const patched = patchNetWasmHost(browserRoot);
      console.log(`Patched ${patched} NetWasm host replacement(s).`);
    } else {
      console.error(
        `Error: No RealWorld browser artifacts found at ${browserRoot} or ${publishRoot}`
      );
      process.exit(1);
    }
    break;
  }

  case "build:worker": {
    const workerRoot = path.join(repoRoot, "spikes/netwasm-worker");
    const csharpRoot = path.join(workerRoot, "csharp");
    const publishRoot = path.join(
      csharpRoot,
      "bin/Release/netwasm0.1/publish/browser"
    );
    const browserRoot = path.join(workerRoot, "public/browser");

    run(
      [
        "publish",
        "-c",
        "Release",
        "-p:DebugType=None",
        "-p:DebugSymbols=false",
      ],
      csharpRoot
    );

    if (existsSync(publishRoot)) {
      const wasmFile = readdirSync(publishRoot).find((file) =>
        file.endsWith(".wasm")
      );
      if (wasmFile) {
        const optimizedSize = optimizeWasmModule(
          path.join(publishRoot, wasmFile)
        );
        if (optimizedSize !== null) {
          refreshWasmManifest(publishRoot, wasmFile);
        }
        if (optimizedSize !== null && optimizedSize > workerAssetLimit) {
          console.error(
            `Error: ${wasmFile} is ${(optimizedSize / 1024 / 1024).toFixed(1)} MiB, over Cloudflare's 25 MiB per-asset limit.`
          );
          process.exit(1);
        }
      }

      rmSync(browserRoot, { force: true, recursive: true });
      cpSync(publishRoot, browserRoot, { recursive: true });
    }

    if (existsSync(browserRoot)) {
      const patched = patchNetWasmHost(browserRoot);
      console.log(`Patched ${patched} NetWasm host replacement(s).`);
    } else {
      console.error(
        `Error: No NetWasm browser artifacts found at ${browserRoot} or ${publishRoot}`
      );
      process.exit(1);
    }
    break;
  }

  case "build:worker:rooms": {
    const workerRoot = path.join(repoRoot, "examples/realtime-rooms/worker");
    const csharpRoot = path.join(workerRoot, "csharp");
    const publishRoot = path.join(
      csharpRoot,
      "bin/Release/netwasm0.1/publish/browser"
    );
    const browserRoot = path.join(workerRoot, "public/browser");

    run(
      [
        "publish",
        "-c",
        "Release",
        "-p:DebugType=None",
        "-p:DebugSymbols=false",
      ],
      csharpRoot
    );
    if (existsSync(publishRoot)) {
      rmSync(browserRoot, { force: true, recursive: true });
      cpSync(publishRoot, browserRoot, { recursive: true });
    }

    if (existsSync(browserRoot)) {
      const patched = patchNetWasmHost(browserRoot);
      console.log(`Patched ${patched} NetWasm host replacement(s).`);
    } else {
      console.error(
        `Error: No NetWasm browser artifacts found at ${browserRoot} or ${publishRoot}`
      );
      process.exit(1);
    }
    break;
  }

  case "check:host": {
    const result = spawnSync(
      process.execPath,
      [path.join(repoRoot, "scripts/check-host-contract.ts")],
      { cwd: repoRoot, stdio: "inherit" }
    );
    process.exit(result.status ?? 1);
  }

  case "check:worker": {
    const result = spawnSync(
      process.execPath,
      [path.join(repoRoot, "scripts/check-worker-budget.ts"), ...rest],
      { cwd: repoRoot, stdio: "inherit" }
    );
    process.exit(result.status ?? 1);
  }

  case "check:netwasm": {
    const strict = rest.includes("--strict");
    const targets = rest.filter((arg) => !arg.startsWith("--"));
    const absoluteTargets = (targets.length > 0 ? targets : ["examples/"]).map(
      (target) =>
        path.isAbsolute(target) ? target : path.join(repoRoot, target)
    );
    const { exitCode } = runCheck(absoluteTargets, { strict });
    process.exit(exitCode);
    break;
  }

  case "test:realtime": {
    const ok = await runRealtimeSmoke({
      kestrelOnly: rest.includes("--kestrel"),
      workersOnly: rest.includes("--workers"),
    });
    process.exit(ok ? 0 : 1);
    break;
  }

  case "dev:status": {
    listDevServers();
    break;
  }

  case "self-check": {
    preflightBuildLocks(rest);
    console.log(
      "Reptar self-check — doctor, tests, host contract, netwasm lint, two-host smoke"
    );
    console.log("───────────────────────────────────────────");

    console.log("\n[1/5] doctor...");
    const doctorReport = doctorProject(repoRoot, process.cwd());
    if (doctorReport.checks.some((check) => !check.passed)) {
      for (const check of doctorReport.checks.filter((c) => !c.passed)) {
        console.log(`  ✗ ${check.title}: ${check.details ?? ""}`);
      }
      process.exit(1);
    }
    console.log("  ✓ doctor healthy");

    console.log("\n[2/5] framework tests...");
    const tests = spawnSync(
      dotnetPath(),
      [
        "test",
        "framework/Reptar.Tests/Reptar.Tests.csproj",
        "--nologo",
        "-v",
        "q",
      ],
      { cwd: repoRoot, encoding: "utf-8", env: dotnetEnv() }
    );
    const testSummary = tests.stdout
      .split("\n")
      .find((line) => line.includes("Passed!") || line.includes("Failed!"));
    console.log(
      `  ${tests.status === 0 ? "✓" : "✗"} ${testSummary?.trim() ?? "test run finished"}`
    );
    if (tests.status !== 0) {
      process.exit(1);
    }

    console.log("\n[3/5] host contract...");
    const hostCheck = spawnSync(
      process.execPath,
      [path.join(repoRoot, "scripts/check-host-contract.ts")],
      { cwd: repoRoot, encoding: "utf-8" }
    );
    if (hostCheck.status !== 0) {
      console.error(hostCheck.stdout ?? "");
      console.error(hostCheck.stderr ?? "");
      console.error("  ✗ host contract check failed");
      process.exit(hostCheck.status ?? 1);
    }
    console.log("  ✓ host contract clean");

    console.log("\n[4/5] netwasm lint (examples/realtime-rooms)...");
    const lint = runCheck([path.join(repoRoot, "examples/realtime-rooms")], {
      strict: true,
    });
    if (lint.exitCode !== 0) {
      console.error("  ✗ netwasm lint failed (see findings above)");
      process.exit(lint.exitCode);
    }
    console.log("  ✓ netwasm lint clean");

    console.log("\n[5/5] two-host realtime smoke (workers + kestrel)...");
    const smokePassed = await runRealtimeSmoke({});
    if (!smokePassed) {
      console.error("  ✗ realtime smoke failed (see output above)");
      process.exit(1);
    }

    console.log("\n───────────────────────────────────────────");
    console.log("self-check: ALL GREEN");
    break;
  }

  default: {
    console.error(
      `Unknown command: ${command ?? "(none)"}. Try: bun reptar --help`
    );
    process.exit(1);
  }
}
