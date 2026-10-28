import { expect, test } from "bun:test";
import { spawnSync } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import path from "node:path";

test("VPS orchestration parses without executing any server/container command", () => {
  const script = path.resolve(import.meta.dir, "..", "run-vps.sh");
  expect(spawnSync("bash", ["-n", script]).status).toBe(0);
});

test("existing benchmark evidence is refused before seeding or process mutation", () => {
  const root = path.resolve(import.meta.dir, "..", "results");
  const directory = mkdtempSync(path.join(root, "vps-safety-"));
  const sentinel = path.join(directory, "preserve.txt");
  writeFileSync(sentinel, "existing evidence must remain unchanged");
  try {
    const result = spawnSync(
      "bash",
      [path.resolve(import.meta.dir, "..", "run-vps.sh")],
      {
        encoding: "utf-8",
        env: {
          ...process.env,
          BENCH_STOP_CONTAINERS: "",
          OHA_BIN: process.execPath,
          STAMP: path.basename(directory).slice("vps-".length),
        },
        timeout: 5000,
      }
    );
    expect(result.status).not.toBe(0);
    expect(result.stderr).toContain(
      process.platform === "linux"
        ? "refusing to overwrite"
        : "requires Linux setsid"
    );
    expect(readFileSync(sentinel, "utf-8")).toBe(
      "existing evidence must remain unchanged"
    );
  } finally {
    rmSync(directory, { recursive: true });
  }
});
