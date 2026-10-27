import { Database } from "bun:sqlite";
import { expect, test } from "bun:test";
import { spawnSync } from "node:child_process";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";

const repo = path.resolve(import.meta.dir, "..");

test("inspect and doctor honor --db rather than guessing an app-local database", () => {
  const root = mkdtempSync(path.join(tmpdir(), "reptar-inspect-db-"));
  try {
    const databasePath = path.join(root, "active.db");
    using database = new Database(databasePath);
    database.run("CREATE TABLE deployed_table (id TEXT PRIMARY KEY)");
    const inspect = spawnSync(
      process.execPath,
      [
        "scripts/reptar.ts",
        "inspect",
        "--app",
        root,
        "--db",
        databasePath,
        "--json",
      ],
      { cwd: repo, encoding: "utf-8" }
    );
    expect(inspect.status).toBe(0);
    const state = JSON.parse(inspect.stdout);
    expect(state.database.path).toBe(databasePath);
    expect(state.database.connected).toBe(true);
    expect(
      state.databaseTables.map((table: { name: string }) => table.name)
    ).toContain("deployed_table");
    const doctor = spawnSync(
      process.execPath,
      [
        "scripts/reptar.ts",
        "doctor",
        "--app",
        root,
        "--db",
        databasePath,
        "--json",
      ],
      { cwd: repo, encoding: "utf-8" }
    );
    const report = JSON.parse(doctor.stdout);
    const check = report.checks.find(
      (item: { name: string }) => item.name === "database"
    );
    expect(check.passed).toBe(true);
    expect(check.details).toContain(databasePath);
  } finally {
    rmSync(root, { force: true, recursive: true });
  }
});
