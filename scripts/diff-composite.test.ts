import { Database } from "bun:sqlite";
import { expect, test } from "bun:test";
import { mkdtempSync, mkdirSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";

import { planMigrations } from "./diff";

const column = (name: string, primaryKey = false) => ({
  indexed: false,
  name,
  notNull: true,
  primaryKey,
  type: "TEXT",
  unique: false,
});

const fixture = () => {
  const root = mkdtempSync(path.join(tmpdir(), "reptar-composite-plan-"));
  mkdirSync(path.join(root, "rpc"));
  writeFileSync(
    path.join(root, "rpc/schema.json"),
    JSON.stringify({
      tables: [
        { columns: [column("id", true)], name: "projects", primaryKey: ["id"] },
        {
          columns: [
            {
              ...column("project_id", true),
              onDelete: "cascade",
              referencesColumn: "id",
              referencesTable: "projects",
            },
            column("environment", true),
            column("key", true),
            column("value"),
          ],
          name: "flags",
          primaryKey: ["project_id", "environment", "key"],
        },
      ],
    })
  );
  return { dbPath: path.join(root, "test.db"), root };
};

test("migration planner emits one composite key, enforces uniqueness, and preserves cascades", () => {
  const { root, dbPath } = fixture();
  try {
    const plan = planMigrations(root, root, { dbPath });
    expect(plan.status).toBe("drift_detected");
    expect(plan.sql).toContain(
      'PRIMARY KEY ("project_id", "environment", "key")'
    );
    expect(plan.sql).toContain("ON DELETE CASCADE");
    using db = new Database(dbPath);
    db.run("PRAGMA foreign_keys = ON");
    db.run(plan.sql);
    db.run("INSERT INTO projects VALUES ('p')");
    db.run("INSERT INTO flags VALUES ('p', 'production', 'checkout', 'one')");
    db.run("INSERT INTO flags VALUES ('p', 'staging', 'checkout', 'two')");
    expect(() =>
      db.run(
        "INSERT INTO flags VALUES ('p', 'production', 'checkout', 'duplicate')"
      )
    ).toThrow();
    expect(planMigrations(root, root, { dbPath }).status).toBe("in_sync");
    db.run("DELETE FROM projects WHERE id='p'");
    expect(db.query("SELECT * FROM flags").all()).toEqual([]);
  } finally {
    rmSync(root, { force: true, recursive: true });
  }
});

test("single-column primary keys remain inline", () => {
  const { root, dbPath } = fixture();
  try {
    const plan = planMigrations(root, root, { dbPath });
    const projects = plan.changes.find((change) => change.table === "projects");
    expect(projects?.sql).toContain('"id" TEXT PRIMARY KEY');
  } finally {
    rmSync(root, { force: true, recursive: true });
  }
});
