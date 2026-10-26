#!/usr/bin/env bun
/**
 * Scaffold end-to-end smoke test.
 *
 * This is the gate the first-run experience was missing: scaffold a fresh
 * project from the working tree, install it, build the C# API with the pinned
 * SDK, regenerate the TypeScript contracts, and typecheck the frontend.
 *
 * Usage:
 *   bun scripts/test-scaffold.ts            # run, then clean up
 *   bun scripts/test-scaffold.ts --keep     # keep the generated app for debugging
 */

import { spawn, spawnSync } from "node:child_process";
import {
  existsSync,
  mkdirSync,
  readdirSync,
  readFileSync,
  rmSync,
  statSync,
} from "node:fs";
import path from "node:path";

const repoRoot = path.resolve(import.meta.dir, "..");
const workRoot = path.join(repoRoot, ".reptar", "scaffold-smoke");
const appName = "scaffold-smoke";
const targetDir = path.join(workRoot, appName);
const keep = process.argv.includes("--keep");

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

const fail = (message: string): never => {
  console.error(`\n✗ ${message}`);
  process.exit(1);
};

const run = (
  label: string,
  command: string,
  args: string[],
  cwd: string,
  env?: Record<string, string>
): void => {
  const started = performance.now();
  const result = spawnSync(command, args, {
    cwd,
    env: { ...process.env, ...env },
    stdio: "inherit",
    timeout: 10 * 60_000,
  });
  const seconds = ((performance.now() - started) / 1000).toFixed(1);

  if (result.error) {
    fail(`${label}: ${result.error.message}`);
  }
  if (result.status !== 0) {
    fail(`${label} failed (exit ${result.status ?? "null"}) after ${seconds}s`);
  }

  console.log(`  ✓ ${label} (${seconds}s)`);
};

const freePort = (): number => {
  const probe = Bun.serve({
    fetch: () => new Response(""),
    port: 0,
  });
  const port = probe.port;
  probe.stop(true);
  return port;
};

const waitForHealth = async (port: number, logs: () => string): Promise<void> => {
  const deadline = Date.now() + 60_000;
  while (Date.now() < deadline) {
    try {
      const response = await fetch(`http://127.0.0.1:${port}/health`, {
        signal: AbortSignal.timeout(1000),
      });
      if (response.ok) {
        return;
      }
    } catch {
      // server still booting
    }
    await new Promise((resolve) => setTimeout(resolve, 500));
  }
  throw new Error(`API did not become healthy on :${port}\n${logs()}`);
};

const stopServer = (child: ReturnType<typeof spawn>): void => {
  if (!child.pid) {
    return;
  }
  try {
    if (process.platform === "win32") {
      spawnSync("taskkill", ["/pid", String(child.pid), "/T", "/F"], {
        stdio: "ignore",
      });
    } else {
      process.kill(-child.pid, "SIGTERM");
    }
  } catch {
    try {
      child.kill("SIGTERM");
    } catch {
      // already gone
    }
  }
};

const findUnreplacedPlaceholders = (dir: string, found: string[] = []): string[] => {
  for (const entry of readdirSync(dir)) {
    if (
      entry === "node_modules" ||
      entry === ".git" ||
      entry === "bin" ||
      entry === "obj" ||
      entry === "dist" ||
      entry === ".turbo" ||
      entry === ".reptar"
    ) {
      continue;
    }

    const full = path.join(dir, entry);
    const stat = statSync(full);
    if (stat.isDirectory()) {
      findUnreplacedPlaceholders(full, found);
      continue;
    }

    if (!textExtensions.has(path.extname(entry)) && !entry.startsWith(".")) {
      continue;
    }

    const content = readFileSync(full, "utf-8");
    // Only all-caps mustache tokens are placeholders; JSX object literals
    // such as `{{ open, setOpen }}` must not match.
    const matches = content.match(/\{\{\s*[A-Z][A-Z0-9_]*\s*\}\}/gu);
    if (matches) {
      found.push(`${path.relative(targetDir, full)}: ${[...new Set(matches)].join(", ")}`);
    }
  }

  return found;
};

console.log("Reptar scaffold E2E");
console.log("──────────────────────────────────────────");
console.log(`Workspace: ${path.relative(repoRoot, workRoot)}`);

rmSync(workRoot, { force: true, recursive: true });
mkdirSync(workRoot, { recursive: true });

try {
  run(
    "scaffold (bun reptar new)",
    process.execPath,
    [path.join(repoRoot, "scripts/reptar.ts"), "new", appName, "-y", "--db", "sqlite", "--auth", "none"],
    workRoot
  );

  if (!existsSync(path.join(targetDir, "apps/api/app.cs"))) {
    fail("Scaffold did not create apps/api/app.cs");
  }

  const placeholders = findUnreplacedPlaceholders(targetDir);
  if (placeholders.length > 0) {
    fail(
      `Unreplaced template placeholders:\n${placeholders
        .map((line) => `  - ${line}`)
        .join("\n")}`
    );
  }
  console.log("  ✓ no unreplaced placeholders");

  run("bun install", process.execPath, ["install"], targetDir);
  run("api build (pinned SDK)", process.execPath, ["run", "api:build"], targetDir);
  run("api codegen", process.execPath, ["run", "api:codegen"], targetDir);
  run(
    "typecheck apps/web",
    process.execPath,
    ["x", "tsc", "--noEmit", "-p", "apps/web/tsconfig.json"],
    targetDir
  );
  run(
    "typecheck packages/client",
    process.execPath,
    ["x", "tsc", "--noEmit", "-p", "packages/client/tsconfig.json"],
    targetDir
  );

  // Runtime smoke: build success alone does not prove the source-generated
  // JSON codec can serialize the app's DTOs or that auth routes can bind
  // their inputs. Start the API and exercise health + signup + /api/me.
  const port = freePort();
  let serverLogs = "";
  const server = spawn(process.execPath, ["run", "api:dev"], {
    cwd: targetDir,
    detached: process.platform !== "win32",
    env: { ...process.env, PORT: String(port) },
    stdio: ["ignore", "pipe", "pipe"],
  });
  server.stdout?.on("data", (chunk) => {
    serverLogs += chunk.toString();
  });
  server.stderr?.on("data", (chunk) => {
    serverLogs += chunk.toString();
  });

  try {
    await waitForHealth(port, () => serverLogs);

    const email = `scaffold-${Date.now()}@example.com`;
    const signup = await fetch(
      `http://127.0.0.1:${port}/api/auth/sign-up/email`,
      {
        body: JSON.stringify({
          email,
          name: "Scaffold User",
          password: "password123",
        }),
        headers: { "content-type": "application/json" },
        method: "POST",
      }
    );
    if (signup.status !== 201) {
      throw new Error(
        `signup returned ${signup.status}: ${await signup.text()}\n${serverLogs}`
      );
    }

    const headersWithCookies = signup.headers as Headers & {
      getSetCookie?: () => string[];
    };
    const setCookies =
      headersWithCookies.getSetCookie?.() ??
      [signup.headers.get("set-cookie") ?? ""];
    const cookie = setCookies
      .map((value) => value.split(";")[0])
      .find((value) => value.startsWith("better-auth.session_token="));
    if (!cookie) {
      throw new Error(`signup did not set a session cookie\n${serverLogs}`);
    }

    const me = await fetch(`http://127.0.0.1:${port}/api/me`, {
      headers: { cookie },
    });
    const meBody = (await me.json()) as { authenticated?: boolean };
    if (!me.ok || meBody.authenticated !== true) {
      throw new Error(
        `/api/me did not resolve the session: ${JSON.stringify(meBody)}\n${serverLogs}`
      );
    }

    const created = await fetch(`http://127.0.0.1:${port}/api/items`, {
      body: JSON.stringify({
        content: "Created by the scaffold E2E",
        title: "Scaffold item",
      }),
      headers: { "content-type": "application/json", cookie },
      method: "POST",
    });
    if (created.status !== 201) {
      throw new Error(
        `item create returned ${created.status}: ${await created.text()}\n${serverLogs}`
      );
    }
    const createdBody = (await created.json()) as { id?: string };

    const list = await fetch(`http://127.0.0.1:${port}/api/items`, {
      headers: { cookie },
    });
    const items = (await list.json()) as { id?: string }[];
    if (!list.ok || !items.some((item) => item.id === createdBody.id)) {
      throw new Error(
        `created item missing from list: ${JSON.stringify(items)}\n${serverLogs}`
      );
    }

    console.log("  ✓ runtime health + auth + CRUD smoke");
  } finally {
    stopServer(server);
  }

  console.log("\n✓ scaffold E2E passed");
} finally {
  if (keep) {
    console.log(`\nKept generated app at ${targetDir}`);
  } else {
    rmSync(workRoot, { force: true, recursive: true });
  }
}
