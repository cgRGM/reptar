export interface D1PreparedStatementLike {
  all: () => Promise<{ results: unknown[] }>;
  bind: (...values: unknown[]) => D1PreparedStatementLike;
  run: () => Promise<{ meta?: { changes?: number } }>;
}

export interface D1DatabaseLike {
  batch?: (
    statements: D1PreparedStatementLike[]
  ) => Promise<{ meta?: { changes?: number } }[]>;
  prepare: (sql: string) => D1PreparedStatementLike;
}

export interface R2PutOptions {
  httpMetadata?: {
    contentType?: string;
  };
}

export interface R2ObjectLike {
  arrayBuffer: () => Promise<ArrayBuffer>;
}

export interface R2BucketLike {
  delete: (key: string) => Promise<unknown>;
  get: (key: string) => Promise<R2ObjectLike | null>;
  head: (key: string) => Promise<unknown | null>;
  put: (
    key: string,
    value: Uint8Array,
    options?: R2PutOptions
  ) => Promise<unknown>;
}

export interface ReptarStorageBinding {
  delete?: (key: string) => Promise<void>;
  exists?: (key: string) => Promise<boolean>;
  get?: (key: string) => Promise<Uint8Array | null>;
  put: (key: string, bytes: Uint8Array, contentType: string) => Promise<string>;
}

export interface KvNamespaceLike {
  delete: (key: string) => Promise<unknown>;
  get: (key: string) => Promise<string | null>;
  put: (
    key: string,
    value: string,
    options?: { expirationTtl?: number }
  ) => Promise<unknown>;
}

export interface QueueLike {
  send: (message: unknown) => Promise<void>;
}

export interface WorkersAiLike {
  run: (model: string, input: Record<string, unknown>) => Promise<unknown>;
}

export interface DurableObjectStubLike {
  fetch: (request: Request) => Promise<Response>;
}

export interface DurableObjectNamespaceLike {
  get: (id: unknown) => DurableObjectStubLike;
  idFromName: (name: string) => unknown;
  idFromString?: (id: string) => unknown;
}

export interface PostgresBindingLike {
  execute: (sql: string, paramsJson: string) => Promise<number>;
  query: (sql: string, paramsJson: string) => Promise<string>;
}

export interface HyperdriveLike {
  connectionString: string;
}

export interface ReptarBindings {
  ai?: WorkersAiLike;
  db?: D1DatabaseLike;
  durable?: Record<string, DurableObjectNamespaceLike>;
  hyperdrive?: HyperdriveLike;
  kv?: KvNamespaceLike;
  postgres?: PostgresBindingLike;
  queues?: Record<string, QueueLike>;
  r2?: ReptarStorageBinding;
}

const parseParams = (params: unknown): unknown[] => {
  if (Array.isArray(params)) {
    return params;
  }
  if (typeof params !== "string") {
    return [];
  }
  const parsed = JSON.parse(params) as unknown;
  return Array.isArray(parsed) ? parsed : [];
};

const bindStatement = (
  db: D1DatabaseLike,
  sql: string,
  params: unknown
): D1PreparedStatementLike => {
  const statement = db.prepare(sql);
  const values = parseParams(params);
  return values.length > 0 ? statement.bind(...values) : statement;
};

const SNAKE_CASE_REGEX = /_(?<char>[a-z0-9])/gu;

const snakeToCamel = (str: string): string =>
  str.replaceAll(SNAKE_CASE_REGEX, (_, char: string) => char.toUpperCase());

const normalizeRowKeys = (
  row: Record<string, unknown>
): Record<string, unknown> => {
  const normalized: Record<string, unknown> = { ...row };
  for (const [key, value] of Object.entries(row)) {
    if (key.includes("_")) {
      const camel = snakeToCamel(key);
      if (!(camel in normalized)) {
        normalized[camel] = value;
      }
    }
  }
  return normalized;
};

export const d1Query = async (
  db: D1DatabaseLike,
  sql: string,
  paramsJson = "[]"
): Promise<string> => {
  const result = await bindStatement(db, sql, paramsJson).all();
  const rows = (result.results ?? []).map((row) =>
    row && typeof row === "object"
      ? normalizeRowKeys(row as Record<string, unknown>)
      : row
  );
  return JSON.stringify(rows);
};

export const d1Execute = async (
  db: D1DatabaseLike,
  sql: string,
  paramsJson = "[]"
): Promise<number> => {
  const result = await bindStatement(db, sql, paramsJson).run();
  return result.meta?.changes ?? 0;
};

export const d1Batch = async (
  db: D1DatabaseLike,
  batchJson: string
): Promise<number> => {
  if (!db.batch) {
    throw new Error("D1 batch is not supported by this binding");
  }

  const parsed = JSON.parse(batchJson) as {
    params?: unknown;
    sql?: string;
  }[];

  const statements = parsed.map((item) =>
    bindStatement(db, item.sql ?? "", item.params ?? [])
  );
  const results = await db.batch(statements);

  return results.reduce(
    (total, result) => total + (result.meta?.changes ?? 0),
    0
  );
};
