import { Database } from "bun:sqlite";
import { spawnSync } from "node:child_process";
import { existsSync, readdirSync, readFileSync, statSync } from "node:fs";
import path from "node:path";

import { loadCompilerTables } from "./diff";

export interface ColumnInfo {
  name: string;
  clrName?: string;
  type: string;
  clrType?: string;
  primaryKey: boolean;
  notNull: boolean;
  unique?: boolean;
  indexed?: boolean;
  defaultValue?: string | null;
  defaultSql?: string | null;
  referencesTable?: string | null;
  referencesColumn?: string | null;
}

export interface TableInfo {
  name: string;
  columns: ColumnInfo[];
  indexes: string[];
  source?: "compiler" | "sqlite";
}

export interface RouteInfo {
  method: string;
  path: string;
  summary?: string;
  description?: string;
  isAiTool?: boolean;
  toolName?: string;
}

export interface ReptarInspection {
  project: {
    name: string;
    root: string;
    version: string;
  };
  database: {
    driver: "sqlite" | "postgres" | "d1";
    dialect: "sqlite" | "postgres";
    connected: boolean;
    path: string;
  };
  schemaSource: "compiler" | "sqlite";
  tables: TableInfo[];
  databaseTables: TableInfo[];
  routes: RouteInfo[];
  migrations: {
    directory: string;
    applied: string[];
    pending: string[];
  };
  generated: {
    openapi: string;
    types: string;
    status: "up-to-date" | "stale" | "missing";
  };
}

export interface CheckItem {
  name: string;
  title: string;
  passed: boolean;
  details?: string;
  advice?: string;
}

export interface ReptarDoctorReport {
  healthy: boolean;
  checks: CheckItem[];
}

export const resolveDbPath = (
  repoRoot: string,
  cwd: string,
  explicitDb?: string
): string => {
  if (explicitDb) {
    return explicitDb;
  }
  const envUrl = process.env.DATABASE_URL;
  if (envUrl) {
    return envUrl.replace(/^sqlite:/i, "").replace(/^Data Source=/i, "");
  }
  const candidates = [
    path.join(cwd, ".reptar/reptar.db"),
    path.join(repoRoot, ".reptar/reptar.db"),
    path.join(repoRoot, "examples/blog/.reptar/reptar.db"),
  ];
  return candidates.find((p) => existsSync(p)) ?? candidates[0];
};

export const resolveMigrationsDir = (
  repoRoot: string,
  cwd: string,
  explicitDir?: string
): string => {
  if (explicitDir) {
    return explicitDir;
  }
  const candidates = [
    path.join(cwd, "migrations"),
    path.join(repoRoot, "migrations"),
    path.join(repoRoot, "spikes/netwasm-worker/migrations"),
  ];
  return candidates.find((d) => existsSync(d)) ?? candidates[0];
};

export const inspectProject = (
  repoRoot: string,
  cwd: string = process.cwd(),
  options?: { dbPath?: string; migrationsDir?: string }
): ReptarInspection => {
  const dbPath = resolveDbPath(repoRoot, cwd, options?.dbPath);
  const migrationsDir = resolveMigrationsDir(
    repoRoot,
    cwd,
    options?.migrationsDir
  );

  let pkgName = "reptar-app";
  let pkgVersion = "0.1.0";
  const pkgJsonPath = path.join(repoRoot, "package.json");
  if (existsSync(pkgJsonPath)) {
    try {
      const parsed = JSON.parse(readFileSync(pkgJsonPath, "utf-8"));
      pkgName = parsed.name ?? pkgName;
      pkgVersion = parsed.version ?? pkgVersion;
    } catch {}
  }

  const tables: TableInfo[] = [];
  const appliedMigrations: string[] = [];
  let dbConnected = false;

  if (existsSync(dbPath)) {
    try {
      const db = new Database(dbPath, { readonly: true });
      dbConnected = true;

      const tableRows = db
        .query(
          `SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name NOT LIKE '__reptar_%' ORDER BY name`
        )
        .all() as { name: string }[];

      for (const row of tableRows) {
        const columns: ColumnInfo[] = [];
        const indexes: string[] = [];

        try {
          const colRows = db
            .query(`PRAGMA table_info("${row.name}")`)
            .all() as {
            name: string;
            type: string;
            notnull: number;
            dflt_value: string | null;
            pk: number;
          }[];

          for (const col of colRows) {
            columns.push({
              defaultValue: col.dflt_value,
              name: col.name,
              notNull: Boolean(col.notnull),
              primaryKey: Boolean(col.pk),
              type: col.type || "TEXT",
            });
          }

          const idxRows = db
            .query(`PRAGMA index_list("${row.name}")`)
            .all() as {
            name: string;
            unique: number;
          }[];

          for (const idx of idxRows) {
            if (!idx.name.startsWith("sqlite_autoindex_")) {
              indexes.push(idx.name);
            }
          }
        } catch {}

        tables.push({
          columns,
          indexes,
          name: row.name,
          source: "sqlite",
        });
      }

      const hasMigrationsTable = db
        .query(
          `SELECT 1 FROM sqlite_master WHERE type='table' AND name='__reptar_migrations'`
        )
        .get();

      if (hasMigrationsTable) {
        const rows = db
          .query(`SELECT id, name FROM "__reptar_migrations" ORDER BY id ASC`)
          .all() as { id: string; name: string }[];
        for (const r of rows) {
          appliedMigrations.push(r.name || `${r.id}.sql`);
        }
      }
      db.close();
    } catch {
      dbConnected = false;
    }
  }

  // Pending migrations
  const pendingMigrations: string[] = [];
  if (existsSync(migrationsDir)) {
    const files = readdirSync(migrationsDir)
      .filter((f) => f.endsWith(".sql"))
      .sort();
    for (const f of files) {
      const id = f.replace(/\.sql$/i, "");
      const isApplied = appliedMigrations.some(
        (a) => a === f || a.replace(/\.sql$/i, "") === id
      );
      if (!isApplied) {
        pendingMigrations.push(f);
      }
    }
  }

  // Routes
  const routes: RouteInfo[] = [];
  const openApiPath = [
    path.join(repoRoot, "packages/rpc/openapi.json"),
    path.join(repoRoot, "examples/blog/rpc/openapi.json"),
  ].find((p) => existsSync(p));

  if (openApiPath) {
    try {
      const openApi = JSON.parse(readFileSync(openApiPath, "utf-8"));
      const paths = openApi.paths || {};
      for (const [routePath, methods] of Object.entries(paths)) {
        if (typeof methods === "object" && methods !== null) {
          for (const [method, op] of Object.entries(
            methods as Record<string, any>
          )) {
            if (
              ["get", "post", "put", "delete", "patch"].includes(
                method.toLowerCase()
              )
            ) {
              routes.push({
                description: op.description,
                isAiTool: Boolean(op["x-ai-tool"]),
                method: method.toUpperCase(),
                path: routePath,
                summary: op.summary,
                toolName: op["x-ai-tool-name"] ?? op.operationId,
              });
            }
          }
        }
      }
    } catch {}
  }

  // Generated status
  const openApiExists = Boolean(openApiPath && existsSync(openApiPath));
  const typesPath = path.join(repoRoot, "packages/rpc/src/types.ts");
  const typesExists = existsSync(typesPath);
  let genStatus: "up-to-date" | "stale" | "missing" = "missing";
  if (openApiExists && typesExists) {
    genStatus = "up-to-date";
  }

  const databaseTables = [...tables];
  const compilerTables = loadCompilerTables(repoRoot, cwd);
  let schemaSource: "compiler" | "sqlite" = "sqlite";

  if (compilerTables && compilerTables.length > 0) {
    schemaSource = "compiler";
    const mergedMap = new Map<string, TableInfo>();
    for (const ct of compilerTables) {
      mergedMap.set(ct.name.toLowerCase(), {
        columns: ct.columns.map((c) => ({
          clrName: c.clrName,
          clrType: c.clrType,
          defaultSql: c.defaultSql ?? null,
          defaultValue: c.defaultValue ?? null,
          indexed: c.indexed,
          name: c.name,
          notNull: c.notNull,
          primaryKey: c.primaryKey,
          referencesColumn: c.referencesCol ?? null,
          referencesTable: c.referencesTable ?? null,
          type: c.type,
          unique: c.unique,
        })),
        indexes: ct.indexes ?? [],
        name: ct.name,
        source: "compiler",
      });
    }
    for (const dt of databaseTables) {
      if (!mergedMap.has(dt.name.toLowerCase())) {
        mergedMap.set(dt.name.toLowerCase(), dt);
      }
    }
    tables.length = 0;
    tables.push(...mergedMap.values());
  }

  return {
    database: {
      connected: dbConnected,
      dialect: "sqlite",
      driver: "sqlite",
      path: dbPath,
    },
    databaseTables,
    generated: {
      openapi: openApiPath ?? "packages/rpc/openapi.json",
      status: genStatus,
      types: typesPath,
    },
    migrations: {
      applied: appliedMigrations,
      directory: migrationsDir,
      pending: pendingMigrations,
    },
    project: {
      name: pkgName,
      root: repoRoot,
      version: pkgVersion,
    },
    routes,
    schemaSource,
    tables,
  };
};

export const doctorProject = (
  repoRoot: string,
  cwd: string = process.cwd()
): ReptarDoctorReport => {
  const checks: CheckItem[] = [];

  // Check 1: .NET SDK
  let sdkPassed = false;
  let sdkVersion = "not found";
  try {
    const dotnetRes = spawnSync("dotnet", ["--version"], { encoding: "utf-8" });
    if (dotnetRes.status === 0) {
      sdkVersion = dotnetRes.stdout.trim();
      const major = Number.parseInt(sdkVersion.split(".")[0] ?? "0", 10);
      sdkPassed = major >= 10;
    }
  } catch {}

  checks.push({
    advice: sdkPassed
      ? undefined
      : "Run 'bun reptar' to automatically install the portable SDK into .reptar/sdk",
    details: `Installed version: ${sdkVersion} (required: >= 10.0.401)`,
    name: "dotnet_sdk",
    passed: sdkPassed,
    title: ".NET 10 SDK Toolchain",
  });

  // Check 2: Bun runtime
  const bunVer = process.versions.bun ?? "unknown";
  checks.push({
    details: `Bun version: ${bunVer}`,
    name: "bun_runtime",
    passed: Boolean(process.versions.bun),
    title: "Bun JavaScript Runtime",
  });

  // Check 3: Database & Migrations
  const inspection = inspectProject(repoRoot, cwd);
  checks.push({
    advice: inspection.database.connected
      ? undefined
      : "Run 'bun reptar db migrate' to initialize the database schema",
    details: inspection.database.connected
      ? `Reachable at ${inspection.database.path} (${inspection.tables.length} table(s) discovered)`
      : `Database file not found at ${inspection.database.path}`,
    name: "database",
    passed: inspection.database.connected || inspection.tables.length > 0,
    title: "Database Connectivity & Schema",
  });

  // Check 4: Migrations Status
  const migrationsPassed = inspection.migrations.pending.length === 0;
  checks.push({
    advice: migrationsPassed
      ? undefined
      : "Run 'bun reptar db migrate' to apply pending migrations",
    details: `${inspection.migrations.applied.length} applied, ${inspection.migrations.pending.length} pending in ${inspection.migrations.directory}`,
    name: "migrations",
    passed: migrationsPassed,
    title: "Database Migrations",
  });

  // Check 5: RPC Contracts
  checks.push({
    advice:
      inspection.generated.status === "up-to-date"
        ? undefined
        : "Run 'bun reptar codegen' to synchronize RPC types",
    details: `Status: ${inspection.generated.status} (${inspection.routes.length} route(s) registered)`,
    name: "rpc_contracts",
    passed: inspection.generated.status === "up-to-date",
    title: "End-to-End RPC Contracts",
  });

  const healthy = checks.every((c) => c.passed);
  return { checks, healthy };
};
