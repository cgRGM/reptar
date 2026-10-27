#!/usr/bin/env bun
import { spawnSync } from "node:child_process";
/**
 * reptar check:worker — measure the uncompressed Worker upload size.
 *
 * Cloudflare's current limit is 64 MiB uncompressed, but a smaller bundle
 * starts faster and leaves room for app code. This gate keeps the current
 * NetWasm artifacts honest until the startup-time probe lands.
 */
import { existsSync, mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";

const repoRoot = path.resolve(import.meta.dir, "..");

const args = process.argv.slice(2);
const argValue = (name: string): string | undefined => {
  const index = args.indexOf(name);
  return index === -1 ? undefined : args[index + 1];
};

const app = path.resolve(
  repoRoot,
  argValue("--app") ?? "spikes/netwasm-worker"
);
// 48 MiB is the current hard ceiling: under Cloudflare's 64 MiB Worker
// script limit with headroom, and above the 38.4 MiB unoptimized reference
// build. 32 MiB is the optimization target for the Speed release.
const maxMb = Number(argValue("--max-mb") ?? "48");
const targetMb = Number(argValue("--target-mb") ?? "32");

if (!Number.isFinite(maxMb) || maxMb <= 0) {
  console.error("--max-mb must be a positive number");
  process.exit(1);
}

if (!Number.isFinite(targetMb) || targetMb <= 0 || targetMb > maxMb) {
  console.error(
    "--target-mb must be a positive number no larger than --max-mb"
  );
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
  const match = /Total Upload:\s+(?<size>[\d.]+)\s+KiB/iu.exec(output);
  const gzipMatch = /gzip:\s+(?<size>[\d.]+)\s+KiB/iu.exec(output);

  if (!match?.groups?.size) {
    console.error("Could not find `Total Upload` in wrangler output.");
    process.stderr.write(output);
    process.exit(1);
  }

  const uploadKiB = Number(match.groups.size);
  const uploadMiB = uploadKiB / 1024;
  const gzipMiB = gzipMatch?.groups?.size
    ? Number(gzipMatch.groups.size) / 1024
    : null;

  console.log(
    `Worker upload: ${uploadMiB.toFixed(2)} MiB uncompressed${
      gzipMiB === null ? "" : ` / ${gzipMiB.toFixed(2)} MiB gzip`
    }`
  );
  console.log(`Hard ceiling:  ${maxMb.toFixed(2)} MiB`);
  console.log(`Target:        ${targetMb.toFixed(2)} MiB`);

  if (uploadMiB > targetMb) {
    console.warn(
      `⚠ ${path.relative(repoRoot, app)} is ${(uploadMiB - targetMb).toFixed(2)} MiB above the optimization target; see the Speed release.`
    );
  }

  if (uploadMiB > maxMb) {
    console.error(
      `✗ ${path.relative(repoRoot, app)} exceeds the Worker size ceiling by ${(uploadMiB - maxMb).toFixed(2)} MiB.`
    );
    process.exit(1);
  }

  console.log("✓ Worker size budget passed");
} finally {
  rmSync(outDir, { force: true, recursive: true });
}
