/* oxlint-disable complexity -- schema parsing is one linear pass over generated C# shapes */
/* oxlint-disable import/no-cycle -- inspect/diff share schema discovery; splitting it is a follow-up */
/* oxlint-disable prefer-named-capture-group -- groups mirror generated C# shapes and are positional by design */
import { spawnSync } from "node:child_process";
import {
  existsSync,
  mkdirSync,
  readdirSync,
  readFileSync,
  writeFileSync,
} from "node:fs";
import path from "node:path";

import {
  inspectProject,
  resolveDatabaseTarget,
  resolveMigrationsDir,
} from "./inspect";

export interface ParsedColumn {
  name: string;
  clrName?: string;
  type: string;
  clrType?: string;
  primaryKey: boolean;
  notNull: boolean;
  unique: boolean;
  indexed: boolean;
  defaultValue?: string | null;
  defaultSql?: string | null;
  referencesTable?: string | null;
  referencesCol?: string | null;
  onDelete?: string | null;
}

export interface ParsedTable {
  name: string;
  primaryKey?: string[];
  columns: ParsedColumn[];
  indexes?: string[];
}

export interface SchemaChange {
  type: "create_table" | "add_column" | "create_index";
  table: string;
  detail: string;
  sql: string;
}

export interface MigrationPlan {
  status: "in_sync" | "drift_detected";
  source: "compiler" | "regex_fallback";
  changes: SchemaChange[];
  sql: string;
}

const parseCompilerSchemaJson = (rawJson: string): ParsedTable[] | null => {
  try {
    const parsed = JSON.parse(rawJson) as {
      tables?: {
        name: string;
        primaryKey?: string[];
        indexes?: string[];
        columns: {
          name: string;
          clrName?: string;
          type: string;
          clrType?: string;
          primaryKey: boolean;
          notNull: boolean;
          unique: boolean;
          indexed: boolean;
          defaultValue?: string | null;
          defaultSql?: string | null;
          referencesTable?: string | null;
          referencesColumn?: string | null;
          onDelete?: string | null;
        }[];
      }[];
    };
    if (!Array.isArray(parsed.tables)) {
      return null;
    }
    return parsed.tables.map((t) => ({
      columns: t.columns.map((c) => ({
        clrName: c.clrName,
        clrType: c.clrType,
        defaultSql: c.defaultSql ?? undefined,
        defaultValue: c.defaultValue ?? undefined,
        indexed: Boolean(c.indexed),
        name: c.name,
        notNull: Boolean(c.notNull),
        onDelete: c.onDelete ?? undefined,
        primaryKey: Boolean(c.primaryKey),
        referencesCol: c.referencesColumn ?? undefined,
        referencesTable: c.referencesTable ?? undefined,
        type: c.type || "TEXT",
        unique: Boolean(c.unique),
      })),
      indexes: t.indexes ?? [],
      name: t.name,
      primaryKey: t.primaryKey ?? [],
    }));
  } catch {
    return null;
  }
};

export const loadCompilerTables = (
  repoRoot: string,
  cwd: string
): ParsedTable[] | null => {
  const schemaCandidates = [
    path.join(cwd, "rpc/schema.json"),
    path.join(cwd, "schema.json"),
    path.join(cwd, "packages/client/src/schema.json"),
    ...(cwd === repoRoot
      ? [
          path.join(repoRoot, "packages/rpc/schema.json"),
          path.join(repoRoot, "examples/blog/rpc/schema.json"),
        ]
      : []),
  ];

  for (const candidate of schemaCandidates) {
    if (existsSync(candidate)) {
      const tables = parseCompilerSchemaJson(readFileSync(candidate, "utf-8"));
      if (tables && tables.length > 0) {
        return tables;
      }
    }
  }

  // Try invoking the app with --schema if app.cs exists. Use the pinned SDK
  // when available: file-based app directives need it.
  const appCandidates = [
    path.join(cwd, "app.cs"),
    ...(cwd === repoRoot ? [path.join(repoRoot, "examples/blog/app.cs")] : []),
  ];
  const pinnedDotnet = path.join(repoRoot, ".reptar", "sdk", "dotnet");
  let dotnetCommand = "dotnet";

  if (existsSync(pinnedDotnet)) {
    dotnetCommand = pinnedDotnet;
  } else if (process.env.DOTNET_ROOT) {
    dotnetCommand = path.join(process.env.DOTNET_ROOT, "dotnet");
  }

  for (const appCs of appCandidates) {
    if (existsSync(appCs)) {
      try {
        const res = spawnSync(dotnetCommand, ["run", appCs, "--", "--schema"], {
          cwd: path.dirname(appCs),
          encoding: "utf-8",
          timeout: 15_000,
        });
        if (res.status === 0 && res.stdout) {
          const jsonStart = res.stdout.indexOf("{");
          if (jsonStart !== -1) {
            const tables = parseCompilerSchemaJson(res.stdout.slice(jsonStart));
            if (tables && tables.length > 0) {
              return tables;
            }
          }
        }
      } catch {
        // The app may not support --schema; fall back to source parsing.
      }
    }
  }

  return null;
};

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
  } catch {
    // Ignore unreadable directories during the best-effort source scan.
  }
  return results;
};

export const parseTableDefinitionsFromCs = (
  csContent: string
): ParsedTable[] => {
  const tables: ParsedTable[] = [];
  const tableRegex = /DefineTable\s*\(\s*"([^"]+)"([\s\S]*?)\);/gu;

  let match: RegExpExecArray | null;
  while ((match = tableRegex.exec(csContent)) !== null) {
    const [, tableName, body] = match;
    const columns: ParsedColumn[] = [];

    const colRegex =
      /(?:ReptarApi\.)?Col\.(Text|Int|Real|Blob)\s*\(\s*"([^"]+)"\s*\)([^,\n\r);]*)/gu;
    let colMatch: RegExpExecArray | null;

    while ((colMatch = colRegex.exec(body)) !== null) {
      const [, typeStr, colName, modifiers = ""] = colMatch;

      let colType = "BLOB";
      if (typeStr === "Text") {
        colType = "TEXT";
      } else if (typeStr === "Int") {
        colType = "INTEGER";
      } else if (typeStr === "Real") {
        colType = "REAL";
      }

      const primaryKey = modifiers.includes(".PrimaryKey()");
      const notNull = modifiers.includes(".NotNull()") || primaryKey;
      const unique = modifiers.includes(".Unique()");
      const indexed = modifiers.includes(".Indexed()");

      let defaultValue: string | undefined;
      const defMatch = modifiers.match(/\.Default\s*\(([^)]+)\)/u);
      if (defMatch) {
        const [, rawDefault] = defMatch;
        defaultValue = rawDefault.trim();
      }

      let defaultSql: string | undefined;
      const defSqlMatch = modifiers.match(
        /\.DefaultSql\s*\(\s*"([^"]+)"\s*\)/u
      );
      if (defSqlMatch) {
        const [, sql] = defSqlMatch;
        defaultSql = sql;
      }

      let referencesTable: string | undefined;
      let referencesCol: string | undefined;
      const refMatch = modifiers.match(
        /\.References\s*\(\s*"([^"]+)"(?:\s*,\s*"([^"]+)")?/u
      );
      if (refMatch) {
        const [, refTable, refCol] = refMatch;
        referencesTable = refTable;
        referencesCol = refCol ?? "id";
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

export const discoverProjectTablesWithSource = (
  repoRoot: string,
  cwd: string
): { tables: ParsedTable[]; source: "compiler" | "regex_fallback" } => {
  const compilerTables = loadCompilerTables(repoRoot, cwd);
  if (compilerTables && compilerTables.length > 0) {
    return { source: "compiler", tables: compilerTables };
  }

  const searchDirs = [
    path.join(cwd, "schema"),
    path.join(cwd, "features"),
    cwd,
    // Repo-root fallbacks must not leak the blog/realworld schemas into an
    // explicitly targeted app.
    ...(cwd === repoRoot
      ? [
          path.join(repoRoot, "examples/blog/schema"),
          path.join(repoRoot, "examples/realworld/schema"),
        ]
      : []),
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

  return { source: "regex_fallback", tables: [...seenTables.values()] };
};

export const discoverProjectTables = (
  repoRoot: string,
  cwd: string
): ParsedTable[] => discoverProjectTablesWithSource(repoRoot, cwd).tables;

export const planMigrations = (
  repoRoot: string,
  cwd: string = process.cwd(),
  options?: { dbPath?: string; migrationsDir?: string }
): MigrationPlan => {
  const databaseTarget = resolveDatabaseTarget(repoRoot, cwd, options?.dbPath);
  if (databaseTarget.dialect !== "sqlite") {
    throw new Error(
      `Schema planning currently supports SQLite only (detected ${databaseTarget.dialect} at ${databaseTarget.path}).`
    );
  }
  const inspection = inspectProject(repoRoot, cwd, options);
  const { tables: codeTables, source } = discoverProjectTablesWithSource(
    repoRoot,
    cwd
  );

  const changes: SchemaChange[] = [];
  const existingTableMap = new Map<string, (typeof inspection.tables)[0]>();
  for (const t of inspection.databaseTables ?? inspection.tables) {
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
            if (col.defaultSql) {
              colDef += ` NOT NULL DEFAULT ${col.defaultSql}`;
            } else if (col.defaultValue) {
              colDef += ` NOT NULL DEFAULT ${col.defaultValue}`;
            } else {
              colDef += ` NOT NULL DEFAULT ''`;
            }
          }
          const addColSql = `ALTER TABLE "${targetTable.name}" ADD COLUMN ${colDef};`;
          changes.push({
            detail: `Add column ${col.name} (${col.type}) to ${targetTable.name}`,
            sql: addColSql,
            table: targetTable.name,
            type: "add_column",
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
              detail: `Create unique index ${idxName}`,
              sql: `CREATE UNIQUE INDEX IF NOT EXISTS "${idxName}" ON "${targetTable.name}"("${col.name}");`,
              table: targetTable.name,
              type: "create_index",
            });
          }
        } else if (col.indexed) {
          const idxName = `${targetTable.name}_${col.name}_idx`;
          if (!existingIdx.has(idxName.toLowerCase())) {
            changes.push({
              detail: `Create index ${idxName}`,
              sql: `CREATE INDEX IF NOT EXISTS "${idxName}" ON "${targetTable.name}"("${col.name}");`,
              table: targetTable.name,
              type: "create_index",
            });
          }
        }
      }
    } else {
      // Compiler metadata marks each composite-key member as primaryKey.
      // SQLite requires one table-level constraint, not several inline keys.
      const primaryKey = targetTable.primaryKey?.length
        ? targetTable.primaryKey
        : targetTable.columns
            .filter((column) => column.primaryKey)
            .map((column) => column.name);
      const compositePrimaryKey = primaryKey.length > 1;
      const lines: string[] = [];
      for (const col of targetTable.columns) {
        let line = `  "${col.name}" ${col.type}`;
        if (col.primaryKey && !compositePrimaryKey) {
          line += " PRIMARY KEY";
        }
        if (col.notNull && (!col.primaryKey || compositePrimaryKey)) {
          line += " NOT NULL";
        }
        if (col.unique && !col.primaryKey) {
          line += " UNIQUE";
        }
        if (col.defaultSql) {
          line += ` DEFAULT ${col.defaultSql}`;
        } else if (col.defaultValue) {
          line += ` DEFAULT ${col.defaultValue}`;
        }
        if (col.referencesTable) {
          line += ` REFERENCES "${col.referencesTable}"("${col.referencesCol ?? "id"}")`;
          if (col.onDelete) {
            line += ` ON DELETE ${col.onDelete.toUpperCase()}`;
          }
        }
        lines.push(line);
      }
      if (compositePrimaryKey) {
        lines.push(
          `  PRIMARY KEY (${primaryKey.map((name) => `"${name}"`).join(", ")})`
        );
      }

      const createTableSql = `CREATE TABLE IF NOT EXISTS "${targetTable.name}" (\n${lines.join(",\n")}\n);`;
      changes.push({
        detail: `Create table ${targetTable.name} with ${targetTable.columns.length} columns`,
        sql: createTableSql,
        table: targetTable.name,
        type: "create_table",
      });

      // Indexes
      for (const col of targetTable.columns) {
        if (col.unique && !col.primaryKey) {
          const idxName = `${targetTable.name}_${col.name}_uq`;
          changes.push({
            detail: `Create unique index ${idxName}`,
            sql: `CREATE UNIQUE INDEX IF NOT EXISTS "${idxName}" ON "${targetTable.name}"("${col.name}");`,
            table: targetTable.name,
            type: "create_index",
          });
        } else if (col.indexed) {
          const idxName = `${targetTable.name}_${col.name}_idx`;
          changes.push({
            detail: `Create index ${idxName}`,
            sql: `CREATE INDEX IF NOT EXISTS "${idxName}" ON "${targetTable.name}"("${col.name}");`,
            table: targetTable.name,
            type: "create_index",
          });
        }
      }
    }
  }

  const sql = changes.map((c) => c.sql).join("\n\n");
  return {
    changes,
    source,
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
    const match = f.match(/^(\d+)_/u);
    if (match) {
      const [, digits] = match;
      const n = Math.trunc(Number(digits));
      if (n >= nextNum) {
        nextNum = n + 1;
      }
    }
  }

  const prefix = String(nextNum).padStart(4, "0");
  const sanitized = (name || "auto_schema_sync")
    .toLowerCase()
    .replaceAll(/[^a-z0-9_]/gu, "_");
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
