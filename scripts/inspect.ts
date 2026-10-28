/* oxlint-disable complexity -- schema inspection is one linear parser */
/* oxlint-disable prefer-named-capture-group -- groups mirror SQLite/DDL shapes */
/* oxlint-disable no-explicit-any -- raw SQLite pragma rows */
/* oxlint-disable no-nested-ternary -- compact display formatting */
/* oxlint-disable no-empty -- best-effort probes ignore unreadable inputs */
/* oxlint-disable import/no-cycle -- inspect/diff share schema discovery */
/* oxlint-disable prefer-single-call -- diagnostic blocks stay readable */
import { Database } from "bun:sqlite";
import { spawnSync } from "node:child_process";
import { existsSync, readdirSync, readFileSync } from "node:fs";
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

export type DatabaseDialect = "sqlite" | "postgres";
export type DatabaseDriver = "sqlite" | "postgres" | "d1";

/**
 * A resolved database target. `path` is always safe to print: for PostgreSQL
 * it is the redacted URL (no credentials), and for SQLite it is the file path.
 */
export interface DatabaseTarget {
  dialect: DatabaseDialect;
  driver: DatabaseDriver;
  path: string;
  host?: string;
  port?: number;
  error?: string;
}

export interface DatabaseInfo extends DatabaseTarget {
  connected: boolean;
}

export interface ReptarInspection {
  project: {
    name: string;
    root: string;
    version: string;
  };
  database: DatabaseInfo;
  schemaSource: "compiler" | "sqlite";
  tables: TableInfo[];
  databaseTables: TableInfo[];
  routes: RouteInfo[];
  /** Present when the database carries the DB queue adapter's jobs table. */
  queue?: {
    table: "reptar_jobs";
    pending: number;
    completed: number;
    dead: number;
    oldestPendingSeconds?: number;
  };
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
  /** A soft warning: passes in normal mode, fails under `doctor --strict`. */
  warning?: boolean;
}

export interface ReptarDoctorReport {
  healthy: boolean;
  checks: CheckItem[];
  strict?: boolean;
}

/**
 * Probe a TCP endpoint synchronously. `inspectProject` and the MCP server are
 * synchronous, so this runs a tiny Bun/Node child that performs the connect
 * and exits 0 on success. Cross-platform, and bounded by `timeoutMs`.
 */
const probeTcpSync = (
  host: string,
  port: number,
  timeoutMs = 1500
): boolean => {
  const script = `
    const net = require("node:net");
    const host = process.env.REPTAR_PROBE_HOST;
    const port = Number(process.env.REPTAR_PROBE_PORT);
    const socket = net.connect({ host, port });
    let settled = false;
    const done = (ok) => {
      if (settled) return;
      settled = true;
      socket.destroy();
      process.exit(ok ? 0 : 1);
    };
    socket.setTimeout(${timeoutMs});
    socket.once("connect", () => done(true));
    socket.once("timeout", () => done(false));
    socket.once("error", () => done(false));
  `;

  const result = spawnSync(process.execPath, ["-e", script], {
    env: {
      ...process.env,
      REPTAR_PROBE_HOST: host,
      REPTAR_PROBE_PORT: String(port),
    },
    stdio: "ignore",
    timeout: timeoutMs + 750,
  });

  return result.status === 0;
};

const parsePostgresTarget = (raw: string): DatabaseTarget => {
  try {
    const url = new URL(raw);
    const host = url.hostname;
    const port = url.port ? Number(url.port) : 5432;
    if (!host) {
      throw new Error("missing host");
    }
    const database = url.pathname.replace(/^\//u, "") || "(default)";
    return {
      dialect: "postgres",
      driver: "postgres",
      host,
      path: `postgres://${host}:${port}/${database}`,
      port,
    };
  } catch (error) {
    return {
      dialect: "postgres",
      driver: "postgres",
      error: `Invalid PostgreSQL DATABASE_URL: ${(error as Error).message}`,
      path: "postgres://<invalid-url>",
    };
  }
};

const sqliteTarget = (rawPath: string, cwd: string): DatabaseTarget => {
  const cleaned = rawPath
    .trim()
    .replace(/^sqlite:/iu, "")
    .replace(/^Data Source=/iu, "")
    .replace(/^file:/iu, "");
  return {
    dialect: "sqlite",
    driver: "sqlite",
    path: path.isAbsolute(cleaned) ? cleaned : path.resolve(cwd, cleaned),
  };
};

/**
 * Resolves the active database target from `--db`/DATABASE_URL, then falls
 * back to the conventional local SQLite files. Detects PostgreSQL URLs so
 * callers never treat `postgres://…` as a SQLite filename.
 */
export const resolveDatabaseTarget = (
  repoRoot: string,
  cwd: string,
  explicitDb?: string
): DatabaseTarget => {
  const raw = explicitDb?.trim() || process.env.DATABASE_URL?.trim();
  if (raw) {
    if (/^postgres(ql)?:\/\//iu.test(raw)) {
      return parsePostgresTarget(raw);
    }
    return sqliteTarget(raw, cwd);
  }

  const cwdCandidates = [
    path.join(cwd, ".reptar/reptar.db"),
    path.join(cwd, ".reptar/agent.db"),
    path.join(cwd, "reptar.db"),
    path.join(cwd, "app.db"),
    path.join(cwd, "blog.db"),
    path.join(cwd, "worker/schema.db"),
  ];
  // Fall back to a single conventional SQLite file in the app directory.
  if (existsSync(cwd)) {
    const discovered = readdirSync(cwd)
      .filter(
        (file) =>
          file.endsWith(".db") &&
          !file.endsWith("-shm") &&
          !file.endsWith("-wal")
      )
      .toSorted()
      .map((file) => path.join(cwd, file));
    for (const candidate of discovered) {
      if (!cwdCandidates.includes(candidate)) {
        cwdCandidates.push(candidate);
      }
    }
  }
  const found = cwdCandidates.find((p) => existsSync(p));
  if (found) {
    return { dialect: "sqlite", driver: "sqlite", path: found };
  }
  if (cwd === repoRoot) {
    const rootCandidates = [
      path.join(repoRoot, ".reptar/reptar.db"),
      path.join(repoRoot, "examples/blog/.reptar/reptar.db"),
    ];
    return {
      dialect: "sqlite",
      driver: "sqlite",
      path: rootCandidates.find((p) => existsSync(p)) ?? rootCandidates[0],
    };
  }
  return {
    dialect: "sqlite",
    driver: "sqlite",
    path: path.join(cwd, ".reptar/reptar.db"),
  };
};

export const resolveDbPath = (
  repoRoot: string,
  cwd: string,
  explicitDb?: string
): string => resolveDatabaseTarget(repoRoot, cwd, explicitDb).path;

export const resolveMigrationsDir = (
  repoRoot: string,
  cwd: string,
  explicitDir?: string
): string => {
  if (explicitDir) {
    return explicitDir;
  }
  const cwdCandidates = [
    path.join(cwd, "migrations"),
    path.join(cwd, "worker/migrations"),
  ];
  const found = cwdCandidates.find((d) => existsSync(d));
  if (found) {
    return found;
  }
  if (cwd === repoRoot) {
    const rootCandidates = [
      path.join(repoRoot, "migrations"),
      path.join(repoRoot, "spikes/netwasm-worker/migrations"),
    ];
    return rootCandidates.find((d) => existsSync(d)) ?? rootCandidates[0];
  }
  return path.join(cwd, "migrations");
};

export const inspectProject = (
  repoRoot: string,
  cwd: string = process.cwd(),
  options?: { dbPath?: string; migrationsDir?: string }
): ReptarInspection => {
  const databaseTarget = resolveDatabaseTarget(repoRoot, cwd, options?.dbPath);
  const dbPath = databaseTarget.path;
  const migrationsDir = resolveMigrationsDir(
    repoRoot,
    cwd,
    options?.migrationsDir
  );

  let pkgName = "reptar-app";
  let pkgVersion = "0.1.0";
  const pkgJsonPath =
    [path.join(cwd, "package.json"), path.join(repoRoot, "package.json")].find(
      (p) => existsSync(p)
    ) ?? path.join(repoRoot, "package.json");
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
  let queueStats: ReptarInspection["queue"];

  if (databaseTarget.dialect === "postgres") {
    // PostgreSQL is reachable over TCP; table introspection needs a SQL
    // client, so `databaseTables` stays empty and the compiler schema is used.
    dbConnected = Boolean(
      databaseTarget.host &&
      databaseTarget.port &&
      !databaseTarget.error &&
      probeTcpSync(databaseTarget.host, databaseTarget.port)
    );
  } else if (existsSync(dbPath)) {
    try {
      using db = new Database(dbPath, { readonly: true });
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
          const quotedName = row.name.replaceAll('"', '""');
          const colRows = db
            .query(`PRAGMA table_info("${quotedName}")`)
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
            .query(`PRAGMA index_list("${quotedName}")`)
            .all() as {
            name: string;
            unique: number;
          }[];

          for (const idx of idxRows) {
            if (!idx.name.startsWith("sqlite_autoindex_")) {
              indexes.push(idx.name);
            }
          }
        } catch (error) {
          throw new Error(
            `Cannot inspect SQLite metadata for table ${row.name}`,
            { cause: error }
          );
        }

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

      // Queue adapter stats: the DB queue's jobs table is a regular
      // TableDef, so inspection reads it like any application table.
      const hasJobsTable = db
        .query(
          `SELECT 1 FROM sqlite_master WHERE type='table' AND name='reptar_jobs'`
        )
        .get();
      if (hasJobsTable) {
        const stateRows = db
          .query(
            `SELECT state, COUNT(*) AS count FROM reptar_jobs GROUP BY state`
          )
          .all() as { state: string; count: number }[];
        const counts: Record<string, number> = {};
        for (const row of stateRows) {
          counts[row.state] = row.count;
        }

        const oldest = db
          .query(
            `SELECT MIN(created_at) AS oldest FROM reptar_jobs WHERE state = 'pending'`
          )
          .get() as { oldest: string | null };

        const oldestMs = oldest?.oldest
          ? Date.parse(oldest.oldest)
          : Number.NaN;

        queueStats = {
          completed: counts.completed ?? 0,
          dead: counts.dead ?? 0,
          oldestPendingSeconds:
            Number.isFinite(oldestMs) && oldestMs > 0
              ? Math.max(0, Math.round((Date.now() - oldestMs) / 1000))
              : undefined,
          pending: counts.pending ?? 0,
          table: "reptar_jobs",
        };
      }
    } catch {
      dbConnected = false;
    }
  }

  // Pending migrations
  const pendingMigrations: string[] = [];
  if (existsSync(migrationsDir)) {
    const files = readdirSync(migrationsDir)
      .filter((f) => f.endsWith(".sql"))
      .toSorted();
    for (const f of files) {
      const id = f.replace(/\.sql$/iu, "");
      const isApplied = appliedMigrations.some(
        (a) => a === f || a.replace(/\.sql$/iu, "") === id
      );
      if (!isApplied) {
        pendingMigrations.push(f);
      }
    }
  }

  // Routes
  const routes: RouteInfo[] = [];
  const openApiPath = [
    path.join(cwd, "rpc/openapi.json"),
    path.join(cwd, "packages/client/src/openapi.json"),
    path.join(cwd, ".reptar/openapi.json"),
    path.join(cwd, "openapi.json"),
    ...(cwd === repoRoot
      ? [
          path.join(repoRoot, "packages/rpc/openapi.json"),
          path.join(repoRoot, "examples/blog/rpc/openapi.json"),
        ]
      : []),
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
              const isAiTool = Boolean(op["x-ai-tool"]);
              routes.push({
                description: op.description,
                isAiTool,
                method: method.toUpperCase(),
                path: routePath,
                summary: op.summary,
                // Only explicit AI tools get a tool name; a standard HTTP
                // route's operationId is not an LLM-callable tool.
                toolName: isAiTool
                  ? (op["x-ai-tool-name"] ?? op.operationId)
                  : undefined,
              });
            }
          }
        }
      }
    } catch {}
  }

  // Fallback: discover routes directly from .cs files in cwd / features/
  if (routes.length === 0) {
    const csFiles: string[] = [];
    try {
      const topLevel = readdirSync(cwd)
        .filter((f) => f.endsWith(".cs"))
        .map((f) => path.join(cwd, f));
      csFiles.push(...topLevel);
    } catch {}
    const featuresDir = path.join(cwd, "features");
    if (existsSync(featuresDir)) {
      try {
        const files = readdirSync(featuresDir)
          .filter((f) => f.endsWith(".cs"))
          .map((f) => path.join(featuresDir, f));
        csFiles.push(...files);
      } catch {}
    }
    const routeRegex =
      /app\.(Get|Post|Put|Delete|Patch|MapWebSocket|OnWebSocket)\s*(?:<[^>]+>)?\s*\(\s*"([^"]+)"/gu;
    for (const file of csFiles) {
      try {
        const content = readFileSync(file, "utf-8");
        let match: RegExpExecArray | null;
        while ((match = routeRegex.exec(content)) !== null) {
          const [, routeMethod, routePath] = match;
          const method =
            routeMethod === "MapWebSocket" || routeMethod === "OnWebSocket"
              ? "WS"
              : routeMethod.toUpperCase();
          if (
            !routes.some((r) => r.method === method && r.path === routePath)
          ) {
            routes.push({
              method,
              path: routePath,
            });
          }
        }
      } catch {}
    }
  }

  // Generated status
  const openApiExists = Boolean(openApiPath && existsSync(openApiPath));
  const typesPath =
    [
      path.join(cwd, "packages/client/src/types.ts"),
      path.join(cwd, "rpc/types.ts"),
      path.join(cwd, ".reptar/types.ts"),
      ...(cwd === repoRoot
        ? [path.join(repoRoot, "packages/rpc/src/types.ts")]
        : []),
    ].find((p) => existsSync(p)) ??
    path.join(cwd, "packages/client/src/types.ts");
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
      dialect: databaseTarget.dialect,
      driver: databaseTarget.driver,
      path: dbPath,
      ...(databaseTarget.host ? { host: databaseTarget.host } : {}),
      ...(databaseTarget.port ? { port: databaseTarget.port } : {}),
      ...(databaseTarget.error ? { error: databaseTarget.error } : {}),
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
      root: cwd,
      version: pkgVersion,
    },
    ...(queueStats ? { queue: queueStats } : {}),
    routes,
    schemaSource,
    tables,
  };
};

export const versionAtLeast = (actual: string, required: string): boolean => {
  const left = actual.split(".").map(Number);
  const right = required.split(".").map(Number);
  const length = Math.max(left.length, right.length);

  for (let index = 0; index < length; index += 1) {
    const a = left[index] ?? 0;
    const b = right[index] ?? 0;
    if (a !== b) {
      return a > b;
    }
  }

  return true;
};

export const doctorProject = (
  repoRoot: string,
  cwd: string = process.cwd(),
  options?: { dbPath?: string; strict?: boolean }
): ReptarDoctorReport => {
  const checks: CheckItem[] = [];

  // Check 1: .NET SDK — prefer the portable SDK Reptar installs under
  // .reptar/sdk, then DOTNET_ROOT, then anything on PATH.
  const requiredSdk = "10.0.401";
  let sdkPassed = false;
  let sdkVersion = "not found";
  let sdkSource = "PATH";
  try {
    const portable = path.join(repoRoot, ".reptar", "sdk", "dotnet");
    const candidates = [
      ...(existsSync(portable) ? [portable] : []),
      ...(process.env.DOTNET_ROOT
        ? [path.join(process.env.DOTNET_ROOT, "dotnet")]
        : []),
      "dotnet",
    ];
    for (const candidate of candidates) {
      const dotnetRes = spawnSync(candidate, ["--version"], {
        encoding: "utf-8",
      });
      if (dotnetRes.status === 0) {
        sdkVersion = dotnetRes.stdout.trim();
        sdkPassed = versionAtLeast(sdkVersion, requiredSdk);
        sdkSource = candidate === "dotnet" ? "PATH" : candidate;
        break;
      }
    }
  } catch {}

  checks.push({
    advice: sdkPassed
      ? undefined
      : "Run 'bun reptar <build-command>' to automatically install the portable SDK into .reptar/sdk",
    details: `Installed version: ${sdkVersion} via ${sdkSource} (required: >= ${requiredSdk})`,
    name: "dotnet_sdk",
    passed: sdkPassed,
    title: ".NET 10 SDK Toolchain",
  });

  // Check 1b: SDK divergence — bare `dotnet run app.cs` silently fails with
  // 'Unrecognized directive' when the PATH SDK is older than the pinned one,
  // because file-based app directives (#:include) need the pinned SDK.
  const portableSdk = path.join(repoRoot, ".reptar", "sdk", "dotnet");
  if (existsSync(portableSdk) && sdkSource !== "PATH") {
    const pathRes = spawnSync("dotnet", ["--version"], { encoding: "utf-8" });
    if (pathRes.status === 0) {
      const pathVersion = pathRes.stdout.trim();
      const portableRes = spawnSync(portableSdk, ["--version"], {
        encoding: "utf-8",
      });
      const portableVersion =
        portableRes.status === 0 ? portableRes.stdout.trim() : "?";
      if (pathVersion === portableVersion) {
        checks.push({
          details: `PATH dotnet matches pinned SDK (${portableVersion})`,
          name: "dotnet_sdk_divergence",
          passed: true,
          title: "PATH dotnet vs pinned .reptar/sdk",
        });
      } else {
        checks.push({
          advice: `Only the pinned SDK (${portableVersion}) supports file-based app directives. Prefer 'bun reptar dev|run' invocations, or export DOTNET_ROOT=${path.dirname(portableSdk)} before calling bare dotnet.`,
          details: `PATH dotnet is ${pathVersion} but the pinned .reptar/sdk is ${portableVersion} — bare 'dotnet run app.cs' will fail on #:include/#:project directives (non-fatal: 'bun reptar …' commands use the pinned SDK)`,
          name: "dotnet_sdk_divergence",
          // Soft warning: the pinned SDK handles all 'bun reptar' commands;
          // only bare `dotnet run app.cs` calls hit the divergence.
          passed: true,
          title: "PATH dotnet vs pinned .reptar/sdk",
          warning: true,
        });
      }
    }
  }

  // Check 2: Bun runtime
  const bunVer = process.versions.bun ?? "unknown";
  checks.push({
    details: `Bun version: ${bunVer}`,
    name: "bun_runtime",
    passed: Boolean(process.versions.bun),
    title: "Bun JavaScript Runtime",
  });

  // Check 3: Database & Migrations. Connectivity must be real: compiler
  // schema classes alone do not prove the runtime database exists.
  const inspection = inspectProject(repoRoot, cwd, { dbPath: options?.dbPath });
  const { database } = inspection;
  const isPostgres = database.dialect === "postgres";
  const databaseAdvice = database.connected
    ? undefined
    : isPostgres
      ? "Check DATABASE_URL and that the PostgreSQL host/port is reachable"
      : "Run 'bun reptar db migrate' to initialize the database schema";
  const databaseDetails = database.connected
    ? isPostgres
      ? `PostgreSQL reachable at ${database.path} (schema from compiler)`
      : `Reachable at ${database.path} (${inspection.databaseTables.length} table(s) discovered)`
    : database.error ||
      (isPostgres
        ? `PostgreSQL unreachable at ${database.path}`
        : `Database file not found at ${database.path}`);
  checks.push({
    advice: databaseAdvice,
    details: databaseDetails,
    name: "database",
    passed: database.connected,
    title: "Database Connectivity & Schema",
  });

  // Check 4: Migrations Status. The built-in runner is SQLite-only, so a
  // PostgreSQL URL can only produce a warning (not a false "pending" list).
  const migrationsPassed = isPostgres
    ? true
    : inspection.migrations.pending.length === 0;
  checks.push({
    advice: isPostgres
      ? "Use your database's migration tool; the built-in runner supports SQLite only"
      : migrationsPassed
        ? undefined
        : "Run 'bun reptar db migrate' to apply pending migrations",
    details: isPostgres
      ? "PostgreSQL detected; migration state is not inspected by the built-in SQLite runner"
      : `${inspection.migrations.applied.length} applied, ${inspection.migrations.pending.length} pending in ${inspection.migrations.directory}`,
    name: "migrations",
    passed: migrationsPassed,
    title: "Database Migrations",
    warning: isPostgres || undefined,
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

  const hasFailure = checks.some((c) => !c.passed);
  const hasWarning = checks.some((c) => c.warning);
  const strict = Boolean(options?.strict);
  const healthy = !hasFailure && !(strict && hasWarning);
  return { checks, healthy, strict };
};
