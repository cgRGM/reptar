export interface D1PreparedStatementLike {
  all: () => Promise<{ results: unknown[] }>;
  bind: (...values: unknown[]) => D1PreparedStatementLike;
  run: () => Promise<{ meta?: { changes?: number } }>;
}

export interface D1DatabaseLike {
  prepare: (sql: string) => D1PreparedStatementLike;
}

export interface R2PutOptions {
  httpMetadata?: {
    contentType?: string;
  };
}

export interface R2BucketLike {
  put: (
    key: string,
    value: Uint8Array,
    options?: R2PutOptions
  ) => Promise<unknown>;
}

export interface ReptarStorageBinding {
  put: (key: string, bytes: Uint8Array, contentType: string) => Promise<string>;
}

export interface ReptarBindings {
  db?: D1DatabaseLike;
  r2?: ReptarStorageBinding;
}

const parseParams = (paramsJson: string): unknown[] => {
  const parsed = JSON.parse(paramsJson) as unknown;
  return Array.isArray(parsed) ? parsed : [];
};

const bindStatement = (
  db: D1DatabaseLike,
  sql: string,
  paramsJson: string
): D1PreparedStatementLike => {
  const statement = db.prepare(sql);
  const params = parseParams(paramsJson);
  return params.length > 0 ? statement.bind(...params) : statement;
};

export const d1Query = async (
  db: D1DatabaseLike,
  sql: string,
  paramsJson = "[]"
): Promise<string> => {
  const result = await bindStatement(db, sql, paramsJson).all();
  return JSON.stringify(result.results);
};

export const d1Execute = async (
  db: D1DatabaseLike,
  sql: string,
  paramsJson = "[]"
): Promise<number> => {
  const result = await bindStatement(db, sql, paramsJson).run();
  return result.meta?.changes ?? 0;
};
