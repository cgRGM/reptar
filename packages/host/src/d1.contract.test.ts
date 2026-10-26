import { afterAll, beforeAll, describe, expect, test } from "bun:test";

import { Miniflare } from "miniflare";

import type { D1DatabaseLike } from "./bindings";
import { d1Batch, d1Execute, d1Query } from "./bindings";

/**
 * D1 has no interactive transactions. The contract is therefore:
 * single statements are atomic, and `batch` is the only multi-statement
 * atomic unit. This runs the real D1 engine through Miniflare and the same
 * host bindings the C# D1DbClient calls.
 */
describe("D1 provider contract", () => {
  let mf: Miniflare;
  let db: D1DatabaseLike;

  beforeAll(async () => {
    mf = new Miniflare({
      d1Databases: { DB: "reptar-provider-matrix" },
      modules: true,
      script: "export default { fetch() { return new Response('ok'); } };",
    });
    db = (await mf.getD1Database("DB")) as unknown as D1DatabaseLike;

    await d1Execute(
      db,
      `CREATE TABLE provider_widgets (
        id TEXT PRIMARY KEY,
        name TEXT NOT NULL,
        count INTEGER NOT NULL DEFAULT 0
      )`
    );
  });

  afterAll(async () => {
    await mf.dispose();
  });

  test("CRUD round-trip normalizes snake_case columns", async () => {
    await d1Execute(
      db,
      "INSERT INTO provider_widgets (id, name, count) VALUES (?, ?, ?)",
      JSON.stringify(["d1-1", "Gear", 10])
    );

    const rows = JSON.parse(
      await d1Query(
        db,
        "SELECT * FROM provider_widgets WHERE id = ?",
        JSON.stringify(["d1-1"])
      )
    ) as { count: number; id: string; name: string }[];

    expect(rows).toHaveLength(1);
    expect(rows[0]?.name).toBe("Gear");
    expect(rows[0]?.count).toBe(10);

    await d1Execute(
      db,
      "UPDATE provider_widgets SET name = ?, count = ? WHERE id = ?",
      JSON.stringify(["Super Gear", 99, "d1-1"])
    );

    const updated = JSON.parse(
      await d1Query(
        db,
        "SELECT * FROM provider_widgets WHERE id = ?",
        JSON.stringify(["d1-1"])
      )
    ) as { count: number; name: string }[];

    expect(updated[0]?.name).toBe("Super Gear");
    expect(updated[0]?.count).toBe(99);

    await d1Execute(
      db,
      "DELETE FROM provider_widgets WHERE id = ?",
      JSON.stringify(["d1-1"])
    );

    const afterDelete = JSON.parse(
      await d1Query(
        db,
        "SELECT * FROM provider_widgets WHERE id = ?",
        JSON.stringify(["d1-1"])
      )
    ) as unknown[];

    expect(afterDelete).toHaveLength(0);
  });

  test("batch is atomic when one statement fails", async () => {
    const batch = JSON.stringify([
      {
        params: JSON.stringify(["d1-batch", "Batch", 1]),
        sql: "INSERT INTO provider_widgets (id, name, count) VALUES (?, ?, ?)",
      },
      {
        params: JSON.stringify(["x"]),
        sql: "INSERT INTO provider_missing_table (id) VALUES (?)",
      },
    ]);

    await expect(d1Batch(db, batch)).rejects.toThrow();

    const rows = JSON.parse(
      await d1Query(
        db,
        "SELECT * FROM provider_widgets WHERE id = ?",
        JSON.stringify(["d1-batch"])
      )
    ) as unknown[];

    expect(rows).toHaveLength(0);
  });

  test("concurrent single-statement writes all land", async () => {
    await Promise.all(
      Array.from({ length: 16 }, (_, index) =>
        d1Execute(
          db,
          "INSERT INTO provider_widgets (id, name, count) VALUES (?, ?, ?)",
          JSON.stringify([`d1-concurrent-${index}`, `Worker ${index}`, index])
        )
      )
    );

    const rows = JSON.parse(
      await d1Query(
        db,
        "SELECT * FROM provider_widgets WHERE id LIKE 'd1-concurrent-%'"
      )
    ) as unknown[];

    expect(rows).toHaveLength(16);
  });
});
