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
  customMetadata?: Record<string, string>;
  httpMetadata?: {
    cacheControl?: string;
    contentDisposition?: string;
    contentEncoding?: string;
    contentLanguage?: string;
    contentType?: string;
  };
  onlyIf?: {
    etagDoesNotMatch?: string;
  };
}

export interface R2GetOptions {
  range?: {
    length?: number;
    offset?: number;
  };
}

export interface R2ObjectLike {
  arrayBuffer: () => Promise<ArrayBuffer>;
  customMetadata?: Record<string, string>;
  etag?: string;
  httpMetadata?: {
    cacheControl?: string;
    contentDisposition?: string;
    contentEncoding?: string;
    contentLanguage?: string;
    contentType?: string;
  };
  key?: string;
  size?: number;
  uploaded?: Date;
}

export interface R2ObjectsLike {
  cursor?: string;
  objects: R2ObjectLike[];
  truncated: boolean;
}

export interface R2UploadedPartLike {
  etag: string;
  partNumber: number;
}

export interface R2MultipartUploadLike {
  abort: () => Promise<void>;
  complete: (uploadedParts: R2UploadedPartLike[]) => Promise<unknown>;
  uploadId: string;
  uploadPart: (
    partNumber: number,
    value: Uint8Array
  ) => Promise<R2UploadedPartLike>;
}

export interface R2BucketLike {
  createMultipartUpload?: (
    key: string,
    options?: R2PutOptions
  ) => Promise<R2MultipartUploadLike>;
  delete: (key: string | string[]) => Promise<unknown>;
  get: (key: string, options?: R2GetOptions) => Promise<R2ObjectLike | null>;
  head: (key: string) => Promise<R2ObjectLike | null>;
  list?: (options?: {
    cursor?: string;
    include?: ("httpMetadata" | "customMetadata")[];
    limit?: number;
    prefix?: string;
  }) => Promise<R2ObjectsLike>;
  put: (
    key: string,
    value: Uint8Array,
    options?: R2PutOptions
  ) => Promise<unknown>;
  resumeMultipartUpload?: (
    key: string,
    uploadId: string
  ) => R2MultipartUploadLike;
}

export interface ReptarStorageBinding {
  /**
   * The raw R2 bucket. Providing it unlocks HEAD, range reads, listing, and
   * conditional puts without implementing each method by hand.
   */
  bucket?: R2BucketLike;
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

export interface QueueSendOptionsLike {
  delaySeconds?: number;
}

export interface QueueLike {
  send: (message: unknown, options?: QueueSendOptionsLike) => Promise<void>;
  sendBatch?: (
    messages: unknown[],
    options?: QueueSendOptionsLike
  ) => Promise<void>;
}

/**
 * Cloudflare Email Sending binding (send_email). `send` accepts the
 * structured EmailMessageBuilder payload ({ to, from, subject, html, text,
 * cc, bcc, replyTo, attachments, headers }) or a legacy raw-MIME
 * EmailMessage, resolves to { messageId } on success, and rejects with
 * standard Error objects carrying a platform code such as
 * E_SENDER_NOT_VERIFIED or E_RATE_LIMIT_EXCEEDED.
 */
export interface SendEmailLike {
  send: (message: unknown) => Promise<unknown>;
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
  email?: Record<string, SendEmailLike>;
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
