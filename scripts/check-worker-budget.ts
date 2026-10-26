#!/usr/bin/env bun
/**
 * reptar check:worker — measure the uncompressed Worker upload size.
 *
 * Cloudflare's current limit is 64 MiB uncompressed, but a smaller bundle
 * starts faster and leaves room for app code. This gate keeps the current
 * NetWasm artifacts honest until the startup-time probe lands.
 */
import { existsSync, mkdtempSync, rmSync } from "node:fs";
import { spawnSync } from "node:child_process";
import { tmpdir } from "node:os";
import path from "node:path";

const repoRoot = path.resolve(import.meta.dir, "..");

const args = process.argv.slice(2);
const argValue = (name: string): string | undefined => {
  const index = args.indexOf(name);
  return index >= 0 ? args[index + 1] : undefined;
};

const app = path.resolve(repoRoot, argValue("--app") ?? "spikes/netwasm-worker");
const maxMb = Number(argValue("--max-mb") ?? "32");

if (!Number.isFinite(maxMb) || maxMb <= 0) {
  console.error("--max-mb must be a positive number");
  process.exit(1);
}

const wranglerCandidates = [
  path.join(app, "node_modules/.bin/wrangler"),
  path.join(repoRoot, "node_modules/.bin/wrangler"),
];
const wrangler = wranglerCandidates.find((candidate) => existsSync(candidate));

if (!wrangler) {
  console.error(`No wrangler binary found for ${app}`);
  process.exit(1);
}

const outDir = mkdtempSync(path.join(tmpdir(), "reptar-worker-budget-"));

try {
  const result = spawnSync(
    wrangler,
    ["deploy", "--dry-run", "--outdir", outDir],
    { cwd: app, encoding: "utf-8" }
  );

  if (result.status !== 0) {
    process.stderr.write(result.stdout ?? "");
    process.stderr.write(result.stderr ?? "");
    process.exit(result.status ?? 1);
  }

  const output = `${result.stdout ?? ""}\n${result.stderr ?? ""}`;
  const match = /Total Upload:\s+([\d.]+)\s+KiB/iu.exec(output);
  const gzipMatch = /gzip:\s+([\d.]+)\s+KiB/iu.exec(output);

  if (!match?.[1]) {
    console.error("Could not find `Total Upload` in wrangler output.");
    process.stderr.write(output);
    process.exit(1);
  }

  const uploadKiB = Number(match[1]);
  const uploadMiB = uploadKiB / 1024;
  const gzipMiB = gzipMatch?.[1]
    ? Number(gzipMatch[1]) / 1024
    : null;

  console.log(
    `Worker upload: ${uploadMiB.toFixed(2)} MiB uncompressed` +
      (gzipMiB === null ? "" : ` / ${gzipMiB.toFixed(2)} MiB gzip`)
  );
  console.log(`Budget:        ${maxMb.toFixed(2)} MiB`);

  if (uploadMiB > maxMb) {
    console.error(
      `✗ ${path.relative(repoRoot, app)} exceeds the Worker size budget by ${(uploadMiB - maxMb).toFixed(2)} MiB.`
    );
    process.exit(1);
  }

  console.log("✓ Worker size budget passed");
} finally {
  rmSync(outDir, { force: true, recursive: true });
}
