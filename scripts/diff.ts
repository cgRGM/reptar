import { Database } from "bun:sqlite";
import {
  existsSync,
  mkdirSync,
  readdirSync,
  readFileSync,
  writeFileSync,
} from "node:fs";
import path from "node:path";

import { inspectProject, resolveDbPath, resolveMigrationsDir } from "./inspect";

export interface ParsedColumn {
  name: string;
  type: string;
  primaryKey: boolean;
  notNull: boolean;
  unique: boolean;
  indexed: boolean;
  defaultValue?: string;
  defaultSql?: string;
  referencesTable?: string;
  referencesCol?: string;
}

export interface ParsedTable {
  name: string;
  columns: ParsedColumn[];
}

export interface SchemaChange {
  type: "create_table" | "add_column" | "create_index";
  table: string;
  detail: string;
  sql: string;
}

export interface MigrationPlan {
  status: "in_sync" | "drift_detected";
  changes: SchemaChange[];
  sql: string;
}

const findCsFiles = (dir: string): string[] => {
  if (!existsSync(dir)) {
    return [];
  }
  const results: string[] = [];
  try {
    const entries = readdirSync(dir, { withFileTypes: true });
    for (const entry of entries) {
      const full = path.join(dir, entry.name);
      if (entry.isDirectory()) {
        if (
          !["bin", "obj", ".git", "node_modules", ".reptar"].includes(
            entry.name
          )
        ) {
          results.push(...findCsFiles(full));
        }
      } else if (entry.name.endsWith(".cs")) {
        results.push(full);
      }
    }
  } catch {}
  return results;
};

export const parseTableDefinitionsFromCs = (
  csContent: string
): ParsedTable[] => {
  const tables: ParsedTable[] = [];
  const tableRegex = /DefineTable\s*\(\s*"([^"]+)"([\s\S]*?)\);/g;

  let match: RegExpExecArray | null;
  while ((match = tableRegex.exec(csContent)) !== null) {
    const tableName = match[1];
    const body = match[2];
    const columns: ParsedColumn[] = [];

    const colRegex =
      /(?:ReptarApi\.)?Col\.(Text|Int|Real|Blob)\s*\(\s*"([^"]+)"\s*\)([^,\n\r);]*)/g;
    let colMatch: RegExpExecArray | null;

    while ((colMatch = colRegex.exec(body)) !== null) {
      const typeStr = colMatch[1];
      const colName = colMatch[2];
      const modifiers = colMatch[3] ?? "";

      const colType =
        typeStr === "Text"
          ? "TEXT"
          : typeStr === "Int"
            ? "INTEGER"
            : typeStr === "Real"
              ? "REAL"
              : "BLOB";

      const primaryKey = modifiers.includes(".PrimaryKey()");
      const notNull = modifiers.includes(".NotNull()") || primaryKey;
      const unique = modifiers.includes(".Unique()");
      const indexed = modifiers.includes(".Indexed()");

      let defaultValue: string | undefined;
      const defMatch = modifiers.match(/\.Default\s*\(([^)]+)\)/);
      if (defMatch) {
        defaultValue = defMatch[1].trim();
      }

      let defaultSql: string | undefined;
      const defSqlMatch = modifiers.match(/\.DefaultSql\s*\(\s*"([^"]+)"\s*\)/);
      if (defSqlMatch) {
        defaultSql = defSqlMatch[1];
      }

      let referencesTable: string | undefined;
      let referencesCol: string | undefined;
      const refMatch = modifiers.match(
        /\.References\s*\(\s*"([^"]+)"(?:\s*,\s*"([^"]+)")?/
      );
      if (refMatch) {
        referencesTable = refMatch[1];
        referencesCol = refMatch[2] ?? "id";
      }

      columns.push({
        defaultSql,
        defaultValue,
        indexed,
        name: colName,
        notNull,
        primaryKey,
        referencesCol,
        referencesTable,
        type: colType,
        unique,
      });
    }

    if (columns.length > 0) {
      tables.push({
        columns,
        name: tableName,
      });
    }
  }

  return tables;
};

export const discoverProjectTables = (
  repoRoot: string,
  cwd: string
): ParsedTable[] => {
  const searchDirs = [
    path.join(cwd, "schema"),
    path.join(cwd, "features"),
    cwd,
    path.join(repoRoot, "examples/blog/schema"),
    path.join(repoRoot, "examples/realworld/schema"),
  ].filter((d) => existsSync(d));

  const seenTables = new Map<string, ParsedTable>();

  for (const dir of searchDirs) {
    const csFiles = findCsFiles(dir);
    for (const file of csFiles) {
      const content = readFileSync(file, "utf-8");
      if (content.includes("DefineTable")) {
        const parsed = parseTableDefinitionsFromCs(content);
        for (const t of parsed) {
          if (!seenTables.has(t.name)) {
            seenTables.set(t.name, t);
          }
        }
      }
    }
  }

  return [...seenTables.values()];
};

export const planMigrations = (
  repoRoot: string,
  cwd: string = process.cwd(),
  options?: { dbPath?: string; migrationsDir?: string }
): MigrationPlan => {
  const dbPath = resolveDbPath(repoRoot, cwd, options?.dbPath);
  const inspection = inspectProject(repoRoot, cwd, options);
  const codeTables = discoverProjectTables(repoRoot, cwd);

  const changes: SchemaChange[] = [];
  const existingTableMap = new Map<string, (typeof inspection.tables)[0]>();
  for (const t of inspection.tables) {
    existingTableMap.set(t.name.toLowerCase(), t);
  }

  for (const targetTable of codeTables) {
    const existing = existingTableMap.get(targetTable.name.toLowerCase());

    if (existing) {
      // Table exists in DB - check missing columns
      const existingCols = new Set(
        existing.columns.map((c) => c.name.toLowerCase())
      );
      for (const col of targetTable.columns) {
        if (!existingCols.has(col.name.toLowerCase())) {
          let colDef = `"${col.name}" ${col.type}`;
          if (col.notNull) {
            if (col.defaultSql) colDef += ` NOT NULL DEFAULT ${col.defaultSql}`;
            else if (col.defaultValue)
              colDef += ` NOT NULL DEFAULT ${col.defaultValue}`;
            else colDef += ` NOT NULL DEFAULT ''`;
          }
          const addColSql = `ALTER TABLE "${targetTable.name}" ADD COLUMN ${colDef};`;
          changes.push({
            type: "add_column",
            table: targetTable.name,
            detail: `Add column ${col.name} (${col.type}) to ${targetTable.name}`,
            sql: addColSql,
          });
        }
      }

      // Check missing indexes
      const existingIdx = new Set(existing.indexes.map((i) => i.toLowerCase()));
      for (const col of targetTable.columns) {
        if (col.unique && !col.primaryKey) {
          const idxName = `${targetTable.name}_${col.name}_uq`;
          if (!existingIdx.has(idxName.toLowerCase())) {
            changes.push({
              type: "create_index",
              table: targetTable.name,
              detail: `Create unique index ${idxName}`,
              sql: `CREATE UNIQUE INDEX IF NOT EXISTS "${idxName}" ON "${targetTable.name}"("${col.name}");`,
            });
          }
        } else if (col.indexed) {
          const idxName = `${targetTable.name}_${col.name}_idx`;
          if (!existingIdx.has(idxName.toLowerCase())) {
            changes.push({
              type: "create_index",
              table: targetTable.name,
              detail: `Create index ${idxName}`,
              sql: `CREATE INDEX IF NOT EXISTS "${idxName}" ON "${targetTable.name}"("${col.name}");`,
            });
          }
        }
      }
    } else {
      // Entire table needs to be created
      const lines: string[] = [];
      for (const col of targetTable.columns) {
        let line = `  "${col.name}" ${col.type}`;
        if (col.primaryKey) line += " PRIMARY KEY";
        if (col.notNull && !col.primaryKey) line += " NOT NULL";
        if (col.unique && !col.primaryKey) line += " UNIQUE";
        if (col.defaultSql) line += ` DEFAULT ${col.defaultSql}`;
        else if (col.defaultValue) line += ` DEFAULT ${col.defaultValue}`;
        if (col.referencesTable) {
          line += ` REFERENCES "${col.referencesTable}"("${col.referencesCol ?? "id"}")`;
        }
        lines.push(line);
      }

      const createTableSql = `CREATE TABLE IF NOT EXISTS "${targetTable.name}" (\n${lines.join(",\n")}\n);`;
      changes.push({
        type: "create_table",
        table: targetTable.name,
        detail: `Create table ${targetTable.name} with ${targetTable.columns.length} columns`,
        sql: createTableSql,
      });

      // Indexes
      for (const col of targetTable.columns) {
        if (col.unique && !col.primaryKey) {
          const idxName = `${targetTable.name}_${col.name}_uq`;
          changes.push({
            type: "create_index",
            table: targetTable.name,
            detail: `Create unique index ${idxName}`,
            sql: `CREATE UNIQUE INDEX IF NOT EXISTS "${idxName}" ON "${targetTable.name}"("${col.name}");`,
          });
        } else if (col.indexed) {
          const idxName = `${targetTable.name}_${col.name}_idx`;
          changes.push({
            type: "create_index",
            table: targetTable.name,
            detail: `Create index ${idxName}`,
            sql: `CREATE INDEX IF NOT EXISTS "${idxName}" ON "${targetTable.name}"("${col.name}");`,
          });
        }
      }
    }
  }

  const sql = changes.map((c) => c.sql).join("\n\n");
  return {
    changes,
    sql,
    status: changes.length > 0 ? "drift_detected" : "in_sync",
  };
};

export const generateMigrationFile = (
  repoRoot: string,
  cwd: string = process.cwd(),
  name?: string,
  options?: { dbPath?: string; migrationsDir?: string }
): { created: boolean; path?: string; message: string; changes: number } => {
  const plan = planMigrations(repoRoot, cwd, options);
  if (plan.changes.length === 0) {
    return {
      changes: 0,
      created: false,
      message: "Database schema is already in sync with TableDef definitions.",
    };
  }

  const migrationsDir = resolveMigrationsDir(
    repoRoot,
    cwd,
    options?.migrationsDir
  );
  mkdirSync(migrationsDir, { recursive: true });

  const existing = existsSync(migrationsDir)
    ? readdirSync(migrationsDir).filter((f) => f.endsWith(".sql"))
    : [];

  let nextNum = 1;
  for (const f of existing) {
    const match = f.match(/^(\d+)_/);
    if (match) {
      const n = Number.parseInt(match[1], 10);
      if (n >= nextNum) {
        nextNum = n + 1;
      }
    }
  }

  const prefix = String(nextNum).padStart(4, "0");
  const sanitized = (name || "auto_schema_sync")
    .toLowerCase()
    .replaceAll(/[^a-z0-9_]/g, "_");
  const filename = `${prefix}_${sanitized}.sql`;
  const targetPath = path.join(migrationsDir, filename);

  const fileContent = `-- Migration: ${filename}
-- Created: ${new Date().toISOString()}
-- Changes: ${plan.changes.length} operation(s) planned from TableDef

${plan.sql}
`;

  writeFileSync(targetPath, fileContent);

  return {
    changes: plan.changes.length,
    created: true,
    message: `Generated migration file with ${plan.changes.length} change(s).`,
    path: targetPath,
  };
};
