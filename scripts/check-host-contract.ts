#!/usr/bin/env bun
/**
 * reptar check:host — verify that every import declared by the NetWasm
 * interop manifest is actually exported by the app's JS host module.
 *
 * This is deliberately a source-level check: it runs without instantiating
 * Wasm and catches the most common failure mode (a C# [JSImport] added or
 * renamed while app.host.mjs was not updated).
 */
import { createHash } from "node:crypto";
import { existsSync, readFileSync } from "node:fs";
import path from "node:path";

interface InteropImport {
  module: string;
  name: string;
}

interface InteropManifest {
  imports?: InteropImport[];
}

interface DeploymentArtifact {
  relativePath?: string;
  role?: string;
  sha256?: string;
}

interface DeploymentManifest {
  artifacts?: DeploymentArtifact[];
}

const repoRoot = path.resolve(import.meta.dir, "..");

const targets = [
  "spikes/netwasm-worker",
  "spikes/realworld-worker",
  "examples/realtime-rooms/worker",
];

const exportPattern =
  /export\s+(?:async\s+)?(?:const|let|var|function)\s+(?<name>[A-Za-z_$][\w$]*)/gu;

const checkArtifactHashes = (target: string, root: string): string[] => {
  const deploymentPath = path.join(root, "public/browser/deployment.json");
  if (!existsSync(deploymentPath)) {
    return [];
  }

  let deployment: DeploymentManifest;
  try {
    deployment = JSON.parse(
      readFileSync(deploymentPath, "utf-8")
    ) as DeploymentManifest;
  } catch {
    return [`${target}: deployment.json is not valid JSON`];
  }

  const errors: string[] = [];
  const browserRoot = path.join(root, "public/browser");

  // The Worker host imports the application wasm as a WebAssembly.Module and
  // patches the loader to skip fetching it, so the manifest records the empty
  // hash. Every other artifact is still fetched and must match byte-for-byte.
  const emptySha256 =
    "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

  for (const artifact of deployment.artifacts ?? []) {
    if (!(artifact.relativePath && artifact.sha256)) {
      continue;
    }

    if (artifact.role === "application") {
      if (artifact.sha256 !== emptySha256) {
        errors.push(
          `${target}: application artifact must use the empty hash (the loader compiles the imported module) — rebuild the Worker artifacts`
        );
      }
      continue;
    }

    const filePath = path.join(browserRoot, artifact.relativePath);
    if (!existsSync(filePath)) {
      errors.push(
        `${target}: deployment.json references missing artifact '${artifact.relativePath}'`
      );
      continue;
    }

    const actual = createHash("sha256")
      .update(readFileSync(filePath))
      .digest("hex");

    if (actual !== artifact.sha256) {
      errors.push(
        `${target}: '${artifact.relativePath}' hash does not match deployment.json — rebuild the Worker artifacts`
      );
    }
  }

  return errors;
};

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
    if (match.groups?.name) {
      exports.add(match.groups.name);
    }
  }

  const errors = checkArtifactHashes(target, root);
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
      `✓ ${target}: ${imports.length} host import(s) matched, artifact hashes verified`
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
