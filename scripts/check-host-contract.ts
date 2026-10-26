#!/usr/bin/env bun
/**
 * reptar check:host — verify that every import declared by the NetWasm
 * interop manifest is actually exported by the app's JS host module.
 *
 * This is deliberately a source-level check: it runs without instantiating
 * Wasm and catches the most common failure mode (a C# [JSImport] added or
 * renamed while app.host.mjs was not updated).
 */
import { existsSync, readFileSync } from "node:fs";
import path from "node:path";

interface InteropImport {
  module: string;
  name: string;
}

interface InteropManifest {
  imports?: InteropImport[];
}

const repoRoot = path.resolve(import.meta.dir, "..");

const targets = [
  "spikes/netwasm-worker",
  "spikes/realworld-worker",
  "examples/realtime-rooms/worker",
];

const exportPattern =
  /export\s+(?:async\s+)?(?:const|let|var|function)\s+([A-Za-z_$][\w$]*)/gu;

const checkTarget = (target: string): string[] => {
  const root = path.join(repoRoot, target);
  const manifestPath = path.join(root, "public/browser/interop.json");
  const hostPath = path.join(root, "csharp/app.host.mjs");

  if (!existsSync(manifestPath) || !existsSync(hostPath)) {
    return [];
  }

  const manifest = JSON.parse(
    readFileSync(manifestPath, "utf-8")
  ) as InteropManifest;
  const source = readFileSync(hostPath, "utf-8");
  const exports = new Set<string>();

  for (const match of source.matchAll(exportPattern)) {
    if (match[1]) {
      exports.add(match[1]);
    }
  }

  const errors: string[] = [];
  const imports = (manifest.imports ?? []).filter(
    (entry) => entry.module === "app.host"
  );

  for (const entry of imports) {
    if (!exports.has(entry.name)) {
      errors.push(
        `${target}: app.host.mjs is missing export '${entry.name}' required by ${manifestPath}`
      );
    }
  }

  if (errors.length === 0) {
    console.log(
      `✓ ${target}: ${imports.length} host import(s) matched`
    );
  }

  return errors;
};

const errors = targets.flatMap(checkTarget);

if (errors.length > 0) {
  console.error("\nHost contract drift detected:\n");
  for (const error of errors) {
    console.error(`  ✗ ${error}`);
  }
  process.exit(1);
}

console.log("\nHost contract check passed.");
