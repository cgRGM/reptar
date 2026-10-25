#!/usr/bin/env bun

import { Database } from "bun:sqlite";
import { spawnSync } from "node:child_process";
import {
  copyFileSync,
  cpSync,
  existsSync,
  mkdirSync,
  readFileSync,
  readdirSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import path from "node:path";

import { patchNetWasmHost } from "../packages/host/src/patch";
import { upsertReptarAgentsContext } from "../packages/create-reptar/src/scaffold";
import { planMigrations, generateMigrationFile } from "./diff";
import { inspectProject, doctorProject } from "./inspect";
import { runMcpServer } from "./mcp";

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
  NUGET_HTTP_CACHE_PATH: path.join(
    process.env.HOME ?? "",
    ".nuget",
    "http-cache"
  ),
});

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

const formatGenerated = (): void => {
  const result = spawnSync(
    process.execPath,
    ["x", "oxfmt", "--write", "examples/blog/rpc", "packages/rpc"],
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

const [command, ...rest] = process.argv.slice(2);

if (command === "--help" || command === "-h" || command === "help") {
  console.log(`reptar <command>

Commands:
  new          Scaffold a new fullstack Reptar + React + Better Auth project
  dev          Run the example blog API on http://localhost:8787
  inspect      Inspect database tables, routes, migrations, and project state (--json)
  doctor       Diagnose SDK toolchain, database connectivity, and RPC status (--json)
  mcp          Run the Model Context Protocol stdio server for AI agents
  db           Database operations: status, migrate, new <name>, plan (--json), generate
  codegen      Generate packages/rpc contracts from the C# route tree
  test         Run the C# framework tests
  build        Build the framework core
  build:worker Publish the C# Wasm app and patch the NetWasm host for Workers

The .NET SDK is only a build tool. It is installed under .reptar/ when missing.
`);
  process.exit(0);
}

if (command === "new" || command === "create") {
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

const handleDbCommand = (args: string[]) => {
  const subCommand = args[0] ?? "status";
  const getFlag = (name: string): string | undefined => {
    const idx = args.indexOf(name);
    return idx !== -1 && idx + 1 < args.length ? args[idx + 1] : undefined;
  };

  const migrationsDir =
    getFlag("--dir") ??
    [
      path.join(process.cwd(), "migrations"),
      path.join(repoRoot, "migrations"),
      path.join(repoRoot, "spikes/netwasm-worker/migrations"),
    ].find((d) => existsSync(d)) ??
    path.join(process.cwd(), "migrations");

  const dbPath =
    getFlag("--db") ??
    process.env.DATABASE_URL?.replace(/^sqlite:/i, "").replace(
      /^Data Source=/i,
      ""
    ) ??
    path.join(process.cwd(), ".reptar/reptar.db");

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

  if (subCommand === "plan") {
    const isJson = args.includes("--json");
    const plan = planMigrations(repoRoot, process.cwd(), {
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
      getFlag("--name") ?? (args[1]?.startsWith("-") ? undefined : args[1]);
    const res = generateMigrationFile(repoRoot, process.cwd(), name, {
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
  const inspection = inspectProject(repoRoot, process.cwd());
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

if (command === "init") {
  upsertReptarAgentsContext(process.cwd());
  console.log("✓ Updated Reptar managed context block in AGENTS.md");
  process.exit(0);
}

if (command === "doctor") {
  if (rest.includes("--fix")) {
    upsertReptarAgentsContext(process.cwd());
  }
  const isJson = rest.includes("--json");
  const report = doctorProject(repoRoot, process.cwd());
  if (isJson) {
    console.log(JSON.stringify(report, null, 2));
    process.exit(report.healthy ? 0 : 1);
  }

  console.log(`\nReptar Doctor Diagnostics`);
  console.log(`──────────────────────────────────────────`);
  for (const check of report.checks) {
    const symbol = check.passed ? "✓" : "✗";
    console.log(`${symbol} ${check.title}: ${check.details ?? ""}`);
    if (!check.passed && check.advice) {
      console.log(`    ↳ Action: ${check.advice}`);
    }
  }
  console.log(
    `\nStatus: ${report.healthy ? "All checks passed! Reptar environment is healthy." : "Some checks failed. See actions above."}`
  );
  process.exit(report.healthy ? 0 : 1);
}

if (command === "mcp") {
  runMcpServer(repoRoot, process.cwd());
  await new Promise(() => {});
}

ensureSdk();

switch (command) {
  case "dev": {
    run(["run", "app.cs", ...rest], path.join(repoRoot, "examples/blog"));
    break;
  }

  case "realworld:dev": {
    run(["run", "app.cs", ...rest], path.join(repoRoot, "examples/realworld"));
    break;
  }

  case "codegen": {
    run([
      "run",
      "examples/blog/app.cs",
      "--",
      "--codegen",
      "--out",
      "examples/blog/rpc",
      ...rest,
    ]);
    syncRpcContracts();
    formatGenerated();
    break;
  }

  case "test": {
    run(["test", "framework/Reptar.Tests/Reptar.Tests.csproj", ...rest]);
    break;
  }

  case "build": {
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

    run(["publish", "-c", "Release"], csharpRoot);

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

    run(["publish", "-c", "Release"], csharpRoot);
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

  default: {
    console.error(
      `Unknown command: ${command ?? "(none)"}. Try: bun reptar --help`
    );
    process.exit(1);
  }
}
