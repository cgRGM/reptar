import { Database } from "bun:sqlite";
import { expect, test } from "bun:test";
import {
  mkdtempSync,
  mkdirSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";

import { planMigrations } from "./diff";
import { inspectProject } from "./inspect";

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

test("migration planning refuses an existing unreadable store instead of inventing an empty schema", () => {
  const { root, dbPath } = fixture();
  const corrupt = "not a SQLite database\n";
  try {
    writeFileSync(dbPath, corrupt);
    expect(() => planMigrations(root, root, { dbPath })).toThrow(
      "Cannot inspect existing SQLite database"
    );
    expect(readFileSync(dbPath, "utf-8")).toBe(corrupt);
  } finally {
    rmSync(root, { force: true, recursive: true });
  }
});

test("migration planning refuses a locked existing store without changing its data", () => {
  const { root, dbPath } = fixture();
  try {
    const initial = planMigrations(root, root, { dbPath });
    using db = new Database(dbPath);
    db.run(initial.sql);
    db.run("INSERT INTO projects VALUES ('preserved')");
    db.run("PRAGMA journal_mode = DELETE");
    db.run("BEGIN EXCLUSIVE");
    try {
      expect(() => planMigrations(root, root, { dbPath })).toThrow(
        "Cannot inspect existing SQLite database"
      );
    } finally {
      db.run("ROLLBACK");
    }
    expect(db.query("SELECT id FROM projects").all()).toEqual([
      { id: "preserved" },
    ]);
    expect(planMigrations(root, root, { dbPath }).status).toBe("in_sync");
  } finally {
    rmSync(root, { force: true, recursive: true });
  }
});

test("SQLite metadata inspection preserves quoted table and index identifiers", () => {
  const { root, dbPath } = fixture();
  try {
    using db = new Database(dbPath);
    db.run('CREATE TABLE "odd""name" ("id" TEXT)');
    db.run('CREATE INDEX "odd""index" ON "odd""name"("id")');
    const inspection = inspectProject(root, root, { dbPath });
    expect(inspection.database.connected).toBe(true);
    const table = inspection.databaseTables?.find(
      (item) => item.name === 'odd"name'
    );
    expect(table?.columns.map((item) => item.name)).toEqual(["id"]);
    expect(table?.indexes).toEqual(['odd"index']);
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
