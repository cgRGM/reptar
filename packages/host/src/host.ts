import { d1Batch, d1Execute, d1Query } from "./bindings";
import type { QueueLike, ReptarBindings } from "./bindings";
import {
  hashPassword,
  hmacSha256,
  randomToken,
  sha256,
  verifyPassword,
} from "./crypto";

export interface ReptarWasmApp {
  handle?: (requestJson: string) => void | Promise<void>;
  handleQueue?: (batchJson: string) => void | Promise<void>;
  handleScheduled?: (scheduledJson: string) => void | Promise<void>;
  handleWebSocketClose?: (closeJson: string) => void | Promise<void>;
  handleWebSocketMessage?: (messageJson: string) => void | Promise<void>;
  handleWebSocketOpen?: (openJson: string) => void | Promise<void>;
  /** When true the app calls wsMessageDone(sessionId) after each WebSocket
   * event's async pipeline completes, letting the host hold the event open
   * for binding I/O (D1 writes etc.). */
  supportsWsMessageDone?: () => boolean;
  [key: string]: unknown;
}

export interface MessageBatch<T = unknown> {
  ackAll?: () => void;
  readonly messages: readonly {
    ack?: () => void;
    readonly attempts: number;
    readonly body: T;
    readonly id: string;
    retry?: (options?: { delaySeconds?: number }) => void;
    readonly timestamp: Date | string;
  }[];
  readonly queue: string;
  retryAll?: () => void;
}

interface WorkerWebSocket extends WebSocket {
  accept: () => void;
}

export interface ExecutionContextLike {
  passThroughOnException?: () => void;
  waitUntil: (promise: Promise<unknown>) => void;
}

export interface ReptarTelemetryEvent {
  [key: string]: unknown;
  cache?: string;
  cf_ray?: string;
  colo?: string;
  db_ms?: number;
  db_queries?: number;
  duration_ms?: number;
  error_code?: string;
  method?: string;
  parent_span_id?: string;
  path?: string;
  request_id?: string;
  route?: string;
  span_id?: string;
  status?: number;
  trace_id?: string;
  trigger?: string;
  user_id?: string;
  wasm_ms?: number;
}

export interface ReptarHostOptions<TEnv> {
  assets?: (env: TEnv) => {
    fetch: (request: Request) => Promise<Response>;
  };
  bindings: (env: TEnv) => ReptarBindings;
  httpAllowedHosts?: string[];
  manifestUrl?: (origin: string) => string;
  /** Receives one structured request event. Use createOtlpExporter() for OTLP. */
  onTelemetry?: (event: ReptarTelemetryEvent) => void | Promise<void>;
  open: () => Promise<ReptarWasmApp>;
  /** Non-streaming HTTP request timeout. Defaults to 30 seconds. */
  requestTimeoutMs?: number;
  /** Queue/scheduled consumer timeout. Defaults to 60 seconds. */
  consumerTimeoutMs?: number;
  wasm: WebAssembly.Module;
}

export interface ReptarHost<TEnv> {
  fetch: (
    request: Request,
    env: TEnv,
    ctx?: ExecutionContextLike
  ) => Promise<Response>;
  queue: (
    batch: MessageBatch<unknown>,
    env: TEnv,
    ctx?: ExecutionContextLike
  ) => Promise<void>;
  scheduled: (
    event: { cron: string; scheduledTime: number | Date },
    env: TEnv,
    ctx?: ExecutionContextLike
  ) => Promise<void>;
}

interface HostResponseEnvelope {
  body?: string;
  headers?: Record<string, string>;
  setCookies?: string[];
  status?: number;
}

interface ReptarScope {
  __REPTAR_AI_RUN?: (model: string, prompt: string) => Promise<string>;
  __REPTAR_CRYPTO_HASH_PASSWORD?: (password: string) => Promise<string>;
  __REPTAR_CRYPTO_HMAC_SHA256?: (key: string, msg: string) => Promise<string>;
  __REPTAR_CRYPTO_RANDOM_TOKEN?: (bytes: number) => string;
  __REPTAR_CRYPTO_SHA256?: (input: string) => Promise<string>;
  __REPTAR_CRYPTO_VERIFY_PASSWORD?: (
    password: string,
    hash: string
  ) => Promise<boolean>;
  __REPTAR_DB_BATCH?: (batchJson: string) => Promise<number>;
  __REPTAR_DB_EXECUTE?: (sql: string, paramsJson: string) => Promise<number>;
  __REPTAR_DB_QUERY?: (sql: string, paramsJson: string) => Promise<string>;
  __REPTAR_DURABLE_FETCH?: (
    namespace: string,
    id: string,
    path: string,
    method: string,
    body: string
  ) => Promise<string>;
  __REPTAR_ENV_GET?: (name: string) => string | null;
  __REPTAR_FETCH?: (
    input: RequestInfo | URL,
    init?: RequestInit
  ) => Promise<Response>;
  __REPTAR_HTTP_FETCH?: (
    url: string,
    method: string,
    headersJson: string,
    body: string
  ) => Promise<string>;
  __REPTAR_HTTP_FETCH_BYTES?: (
    url: string,
    method: string,
    headersJson: string,
    body: Uint8Array | null,
    contentType: string | null
  ) => Promise<string>;
  __REPTAR_KV_DELETE?: (key: string) => Promise<void>;
  __REPTAR_KV_GET?: (key: string) => Promise<string | null>;
  __REPTAR_KV_PUT?: (
    key: string,
    value: string,
    ttlSeconds?: number
  ) => Promise<void>;
  __REPTAR_MANIFEST_URL?: string;
  __REPTAR_PG_EXECUTE?: (sql: string, paramsJson: string) => Promise<number>;
  __REPTAR_PG_QUERY?: (sql: string, paramsJson: string) => Promise<string>;
  __REPTAR_QUEUE_SEND?: (
    queueName: string,
    messageJson: string
  ) => Promise<void>;
  __REPTAR_QUEUE_SEND_OPTIONS?: (
    queueName: string,
    messageJson: string,
    delaySeconds: number
  ) => Promise<void>;
  __REPTAR_QUEUE_SEND_BATCH?: (
    queueName: string,
    messagesJson: string
  ) => Promise<void>;
  __REPTAR_R2_DELETE?: (key: string) => Promise<void>;
  __REPTAR_R2_DELETE_MANY?: (keys: string) => Promise<void>;
  __REPTAR_R2_EXISTS?: (key: string) => Promise<boolean>;
  __REPTAR_R2_GET?: (key: string) => Promise<Uint8Array | null>;
  __REPTAR_R2_GET_RANGE?: (
    key: string,
    offset: number,
    length: number
  ) => Promise<Uint8Array | null>;
  __REPTAR_R2_HEAD?: (key: string) => Promise<string | null>;
  __REPTAR_R2_LIST?: (
    prefix: string,
    cursor: string,
    limit: number
  ) => Promise<string>;
  __REPTAR_R2_PUT?: (
    key: string,
    bytes: Uint8Array,
    contentType: string
  ) => Promise<string>;
  __REPTAR_R2_PUT_OPTIONS?: (
    key: string,
    bytes: Uint8Array,
    contentType: string,
    cacheControl: string,
    metadataJson: string,
    onlyIfAbsent: boolean
  ) => Promise<string>;
  __REPTAR_RESPOND?: (requestId: string, value: string) => void;
  __REPTAR_RESPOND_QUEUE?: (batchId: string, value: string) => void;
  __REPTAR_RESPOND_SCHEDULED?: (scheduleId: string, value: string) => void;
  __REPTAR_STREAM_CHUNK?: (requestId: string, base64: string) => void;
  __REPTAR_STREAM_END?: (requestId: string) => void;
  __REPTAR_STREAM_ERROR?: (requestId: string, message: string) => void;
  __REPTAR_WS_CLOSE?: (sessionId: string, code: number, reason: string) => void;
  __REPTAR_WS_MESSAGE_DONE?: (sessionId: string) => void;
  __REPTAR_WS_SEND?: (sessionId: string, message: string) => void;
  __REPTAR_STREAM_START?: (
    requestId: string,
    status: number,
    headersJson: string,
    cookiesJson: string
  ) => void;
  __REPTAR_TELEMETRY?: (json: string) => void;
  __REPTAR_WASM_MODULE?: WebAssembly.Module;
}

const bytesToBase64 = (bytes: Uint8Array): string => {
  let binary = "";
  const chunkSize = 0x80_00;

  for (let index = 0; index < bytes.length; index += chunkSize) {
    binary += String.fromCodePoint(...bytes.subarray(index, index + chunkSize));
  }

  return btoa(binary);
};

const base64ToBytes = (value: string): Uint8Array => {
  if (!value) {
    return new Uint8Array(0);
  }
  try {
    const binary = atob(value);
    const bytes = new Uint8Array(binary.length);

    for (let index = 0; index < binary.length; index += 1) {
      bytes[index] = binary.codePointAt(index) ?? 0;
    }

    return bytes;
  } catch {
    return new TextEncoder().encode(value);
  }
};

const defaultManifestUrl = (origin: string): string =>
  new URL("/browser/deployment.json", origin).href;

const errorResponse = (message: string, status = 500): Response =>
  new Response(message, {
    headers: { "content-type": "text/plain; charset=utf-8" },
    status,
  });

const HEX = "0123456789abcdef";

const randomHex = (bytes: number): string => {
  const data = new Uint8Array(bytes);
  crypto.getRandomValues(data);
  let out = "";

  for (const byte of data) {
    out += HEX[byte >> 4] + HEX[byte & 0x0f];
  }

  return out;
};

const isHex = (value: string, length: number): boolean =>
  value.length === length && /^[0-9a-f]+$/iu.test(value);

interface ParsedTraceParent {
  sampled: boolean;
  spanId: string;
  traceId: string;
}

const parseTraceParent = (value: string | null): ParsedTraceParent | null => {
  if (!value) {
    return null;
  }

  const parts = value.split("-");
  if (
    parts.length !== 4 ||
    parts[0] !== "00" ||
    !isHex(parts[1], 32) ||
    !isHex(parts[2], 16) ||
    !isHex(parts[3], 2)
  ) {
    return null;
  }

  if (/^0+$/u.test(parts[1]) || /^0+$/u.test(parts[2])) {
    return null;
  }

  return {
    sampled: (Number.parseInt(parts[3], 16) & 1) === 1,
    spanId: parts[2],
    traceId: parts[1],
  };
};

const coloFromRay = (cfRay: string | null): string | undefined => {
  if (!cfRay) {
    return;
  }

  const dash = cfRay.lastIndexOf("-");
  return dash !== -1 && dash < cfRay.length - 1
    ? cfRay.slice(dash + 1)
    : undefined;
};

const traceParentFor = (request: Request): string => {
  const incoming = parseTraceParent(request.headers.get("traceparent"));
  if (incoming) {
    return `00-${incoming.traceId}-${randomHex(8)}-${incoming.sampled ? "01" : "00"}`;
  }

  return `00-${randomHex(16)}-${randomHex(8)}-01`;
};

const isThenable = (value: unknown): value is Promise<unknown> =>
  typeof (value as { then?: unknown } | null)?.then === "function";

const timeoutAfter = <T>(
  ms: number,
  value: T
): { cancel: () => void; promise: Promise<T> } => {
  let timer: ReturnType<typeof setTimeout> | undefined;
  // eslint-disable-next-line promise/avoid-new
  const promise = new Promise<T>((resolve) => {
    timer = setTimeout(() => resolve(value), ms);
  });

  return {
    cancel: () => {
      if (timer !== undefined) {
        clearTimeout(timer);
      }
    },
    promise,
  };
};

const registerCryptoBindings = (scope: ReptarScope): void => {
  scope.__REPTAR_CRYPTO_HASH_PASSWORD ??= hashPassword;
  scope.__REPTAR_CRYPTO_RANDOM_TOKEN ??= randomToken;
  scope.__REPTAR_CRYPTO_SHA256 ??= sha256;
  scope.__REPTAR_CRYPTO_HMAC_SHA256 ??= hmacSha256;
  scope.__REPTAR_CRYPTO_VERIFY_PASSWORD ??= verifyPassword;
};

const registerDbBindings = (
  scope: ReptarScope,
  db: ReptarBindings["db"]
): void => {
  if (!db) {
    return;
  }
  scope.__REPTAR_DB_QUERY ??= (sql, paramsJson) => d1Query(db, sql, paramsJson);
  scope.__REPTAR_DB_EXECUTE ??= (sql, paramsJson) =>
    d1Execute(db, sql, paramsJson);
  scope.__REPTAR_DB_BATCH ??= (batchJson) => d1Batch(db, batchJson);
};

const registerPostgresBindings = (
  scope: ReptarScope,
  postgres: ReptarBindings["postgres"]
): void => {
  if (postgres) {
    scope.__REPTAR_PG_QUERY = (sql, paramsJson) =>
      postgres.query(sql, paramsJson);
    scope.__REPTAR_PG_EXECUTE = (sql, paramsJson) =>
      postgres.execute(sql, paramsJson);
  } else {
    scope.__REPTAR_PG_QUERY ??= () => Promise.resolve("[]");
    scope.__REPTAR_PG_EXECUTE ??= () => Promise.resolve(0);
  }
};

const r2ObjectData = (object: {
  key?: string;
  size?: number;
  etag?: string;
  uploaded?: Date;
  httpMetadata?: { contentType?: string; cacheControl?: string };
  customMetadata?: Record<string, string>;
}) => ({
  cacheControl: object.httpMetadata?.cacheControl ?? "",
  contentType: object.httpMetadata?.contentType ?? "",
  etag: (object.etag ?? "").replaceAll('"', ""),
  key: object.key,
  metadata: object.customMetadata ?? {},
  size: object.size ?? 0,
  uploaded: object.uploaded ? object.uploaded.toISOString() : null,
});

const r2ObjectJson = (object: Parameters<typeof r2ObjectData>[0]): string =>
  JSON.stringify(r2ObjectData(object));

const registerR2Bindings = (
  scope: ReptarScope,
  r2: ReptarBindings["r2"]
): void => {
  if (!r2) {
    return;
  }
  const { bucket } = r2;

  scope.__REPTAR_R2_PUT ??= (key, bytes, contentType) =>
    r2.put(key, bytes, contentType);
  scope.__REPTAR_R2_GET ??= async (key) => (r2.get ? await r2.get(key) : null);
  scope.__REPTAR_R2_EXISTS ??= async (key) =>
    r2.exists ? await r2.exists(key) : false;
  scope.__REPTAR_R2_DELETE ??= async (key) => {
    if (r2.delete) {
      await r2.delete(key);
    }
  };

  scope.__REPTAR_R2_DELETE_MANY ??= async (keysJson) => {
    const keys = keysJson.split("\n").filter(Boolean);
    if (keys.length === 0) {
      return;
    }

    if (bucket?.delete) {
      await bucket.delete(keys);
      return;
    }

    if (r2.delete) {
      for (const key of keys) {
        // eslint-disable-next-line no-await-in-loop
        await r2.delete(key);
      }
    }
  };

  scope.__REPTAR_R2_HEAD ??= async (key) => {
    if (!bucket?.head) {
      return null;
    }
    const object = await bucket.head(key);
    return object ? r2ObjectJson(object) : null;
  };

  scope.__REPTAR_R2_GET_RANGE ??= async (key, offset, length) => {
    if (bucket?.get) {
      const object = await bucket.get(key, {
        range: length > 0 ? { length, offset } : { offset },
      });
      if (!object) {
        return null;
      }
      return new Uint8Array(await object.arrayBuffer());
    }

    // Fallback for adapters that only expose get(): download and slice.
    if (r2.get) {
      const all = await r2.get(key);
      if (!all) {
        return null;
      }
      return all.slice(offset, length > 0 ? offset + length : undefined);
    }

    return null;
  };

  scope.__REPTAR_R2_LIST ??= async (prefix, cursor, limit) => {
    if (!bucket?.list) {
      throw new Error("R2 listing requires bindings.r2.bucket");
    }

    const page = await bucket.list({
      cursor: cursor || undefined,
      include: ["httpMetadata"],
      limit,
      prefix: prefix || undefined,
    });

    return JSON.stringify({
      cursor: page.truncated ? (page.cursor ?? null) : null,
      items: page.objects.map(r2ObjectData),
    });
  };

  scope.__REPTAR_R2_PUT_OPTIONS ??= async (
    key,
    bytes,
    contentType,
    cacheControl,
    metadataJson,
    onlyIfAbsent
  ) => {
    if (!bucket?.put) {
      if (onlyIfAbsent) {
        throw new Error("R2 conditional put requires bindings.r2.bucket");
      }
      return r2.put(key, bytes, contentType);
    }

    const metadata = JSON.parse(metadataJson || "{}") as Record<string, string>;
    const options: {
      customMetadata?: Record<string, string>;
      httpMetadata?: { contentType?: string; cacheControl?: string };
      onlyIf?: { etagDoesNotMatch?: string };
    } = {};

    if (contentType || cacheControl) {
      options.httpMetadata = {
        ...(contentType ? { contentType } : {}),
        ...(cacheControl ? { cacheControl } : {}),
      };
    }
    if (Object.keys(metadata).length > 0) {
      options.customMetadata = metadata;
    }
    if (onlyIfAbsent) {
      options.onlyIf = { etagDoesNotMatch: "*" };
    }

    await bucket.put(key, bytes, options);
    return key;
  };
};

const registerKvBindings = (
  scope: ReptarScope,
  kv: ReptarBindings["kv"]
): void => {
  if (!kv) {
    return;
  }
  scope.__REPTAR_KV_GET ??= async (key) => await kv.get(key);
  scope.__REPTAR_KV_PUT ??= async (key, val, ttl) => {
    await kv.put(key, val, ttl ? { expirationTtl: ttl } : undefined);
  };
  scope.__REPTAR_KV_DELETE ??= async (key) => {
    await kv.delete(key);
  };
};

const registerQueuesBinding = (
  scope: ReptarScope,
  queues: ReptarBindings["queues"]
): void => {
  if (!queues) {
    return;
  }
  const resolveQueue = (queueName: string): QueueLike => {
    const queue = queues[queueName];
    if (!queue) {
      throw new Error(`Queue binding '${queueName}' not found`);
    }
    return queue;
  };
  scope.__REPTAR_QUEUE_SEND ??= async (queueName, messageJson) => {
    await resolveQueue(queueName).send(JSON.parse(messageJson) as unknown);
  };
  scope.__REPTAR_QUEUE_SEND_OPTIONS ??= async (
    queueName,
    messageJson,
    delaySeconds
  ) => {
    await resolveQueue(queueName).send(JSON.parse(messageJson) as unknown, {
      delaySeconds,
    });
  };
  scope.__REPTAR_QUEUE_SEND_BATCH ??= async (queueName, messagesJson) => {
    const queue = resolveQueue(queueName);
    const messages = JSON.parse(messagesJson) as unknown[];
    if (queue.sendBatch) {
      await queue.sendBatch(messages);
      return;
    }
    for (const message of messages) {
      await queue.send(message);
    }
  };
};

const registerAiBinding = (
  scope: ReptarScope,
  ai: ReptarBindings["ai"]
): void => {
  if (!ai) {
    return;
  }
  scope.__REPTAR_AI_RUN ??= async (model, prompt) => {
    const result = await ai.run(model, { prompt });
    return JSON.stringify(result);
  };
};

const registerDurableBinding = (
  scope: ReptarScope,
  durable: ReptarBindings["durable"]
): void => {
  if (!durable) {
    return;
  }
  scope.__REPTAR_DURABLE_FETCH ??= async (
    namespace,
    id,
    path,
    method,
    body
  ) => {
    const ns = durable[namespace];
    if (!ns) {
      throw new Error(`Durable Object namespace '${namespace}' not found`);
    }
    const stub = ns.get(ns.idFromName(id));
    const res = await stub.fetch(
      new Request(`https://do${path}`, {
        body: body || undefined,
        method: method || "GET",
      })
    );
    return await res.text();
  };
};

const isPrivateHostName = (host: string): boolean =>
  host === "localhost" ||
  host.endsWith(".localhost") ||
  host.endsWith(".local") ||
  host === "metadata" ||
  host === "metadata.google.internal";

const isPrivateIpv6 = (host: string): boolean =>
  host === "::1" ||
  host.startsWith("fc") ||
  host.startsWith("fd") ||
  host.startsWith("fe80") ||
  host.startsWith("::ffff:127.");

const parseIpv4 = (host: string): number[] | null => {
  const parts = host.split(".");

  if (parts.length !== 4) {
    return null;
  }

  const octets: number[] = [];

  for (const part of parts) {
    if (!/^\d{1,3}$/u.test(part)) {
      return null;
    }

    const value = Number(part);

    if (value > 255) {
      return null;
    }

    octets.push(value);
  }

  return octets;
};

const isPrivateIpv4 = (octets: number[]): boolean => {
  const [a, b] = octets;

  return (
    a === 0 ||
    a === 10 ||
    a === 127 ||
    (a === 169 && b === 254) ||
    (a === 172 && b >= 16 && b <= 31) ||
    (a === 192 && b === 168) ||
    (a === 100 && b >= 64 && b <= 127) ||
    a >= 224
  );
};

const isPrivateHostname = (hostname: string): boolean => {
  const host = hostname.toLowerCase().replaceAll(/^\[|\]$/gu, "");

  if (isPrivateHostName(host) || isPrivateIpv6(host)) {
    return true;
  }

  const octets = parseIpv4(host);
  return octets !== null && isPrivateIpv4(octets);
};

const isAllowedHttpUrl = (
  url: string,
  allowedHosts: string[] | undefined
): boolean => {
  let parsed: URL;

  try {
    parsed = new URL(url);
  } catch {
    return false;
  }

  if (parsed.protocol !== "https:") {
    return false;
  }

  if (allowedHosts && allowedHosts.length > 0) {
    return allowedHosts.some(
      (host) => parsed.hostname === host || parsed.hostname.endsWith(`.${host}`)
    );
  }

  return !isPrivateHostname(parsed.hostname);
};

const registerHttpFetch = (
  scope: ReptarScope,
  allowedHosts: string[] | undefined
): void => {
  scope.__REPTAR_HTTP_FETCH ??= async (url, method, headersJson, body) => {
    if (!isAllowedHttpUrl(url, allowedHosts)) {
      throw new Error(`Outbound HTTP host is not allowed: ${url}`);
    }

    const headers = JSON.parse(headersJson || "{}") as Record<string, string>;
    const res = await fetch(url, {
      body: body || undefined,
      headers,
      method: method || "GET",
    });
    const text = await res.text();
    return JSON.stringify({
      body: text,
      headers: Object.fromEntries(res.headers.entries()),
      status: res.status,
    });
  };

  scope.__REPTAR_HTTP_FETCH_BYTES ??= async (
    url,
    method,
    headersJson,
    body,
    contentType
  ) => {
    if (!isAllowedHttpUrl(url, allowedHosts)) {
      throw new Error(`Outbound HTTP host is not allowed: ${url}`);
    }

    const headers = JSON.parse(headersJson || "{}") as Record<string, string>;
    if (contentType) {
      headers["content-type"] = contentType;
    }

    const requestBody =
      body && body.length > 0 ? new Uint8Array(body).buffer : undefined;
    const res = await fetch(url, {
      body: requestBody,
      headers,
      method: method || "GET",
    });
    const bytes = new Uint8Array(await res.arrayBuffer());
    return JSON.stringify({
      body: bytesToBase64(bytes),
      headers: Object.fromEntries(res.headers.entries()),
      status: res.status,
    });
  };
};

/**
 * Wraps a NetWasm app in a Cloudflare Worker host.
 *
 * Contract with the C# app:
 * - export `handle(string)` and return void
 * - call the `respond(string)` JS import when the response is ready
 * - call async JS imports (db, AI, storage, etc.) and await them before responding
 */
export const createReptarHost = <TEnv>(
  options: ReptarHostOptions<TEnv>
): ReptarHost<TEnv> => {
  let appPromise: Promise<ReptarWasmApp> | null = null;
  const pending = new Map<string, (value: string) => void>();
  const pendingQueue = new Map<string, (value: string) => void>();
  const pendingScheduled = new Map<string, (value: string) => void>();
  const requestMeta = new Map<
    string,
    {
      cfRay: string | null;
      executionCtx?: ExecutionContextLike;
      started: number;
      traceParent: string;
    }
  >();
  const sockets = new Map<string, WorkerWebSocket>();
  const pendingStreams = new Map<
    string,
    {
      controller?: ReadableStreamDefaultController<Uint8Array>;
      resolve: (response: Response) => void;
    }
  >();
  const wsDoneQueues = new Map<string, (() => void)[]>();
  const scope = globalThis as typeof globalThis & ReptarScope;
  const telemetrySink =
    options.onTelemetry ??
    ((event: ReptarTelemetryEvent) => console.log(JSON.stringify(event)));

  const emitTelemetry = (
    event: ReptarTelemetryEvent,
    executionCtx?: ExecutionContextLike
  ): void => {
    const delivery = Promise.resolve(telemetrySink(event)).catch((error) => {
      console.error("Reptar telemetry export failed", error);
    });

    if (executionCtx) {
      executionCtx.waitUntil(delivery);
    } else {
      void delivery;
    }
  };

  scope.__REPTAR_TELEMETRY = (json: string) => {
    try {
      const event = JSON.parse(json) as ReptarTelemetryEvent;
      const meta = event.request_id
        ? requestMeta.get(event.request_id)
        : undefined;
      const parsed = parseTraceParent(meta?.traceParent ?? null);

      if (!event.trace_id) {
        event.trace_id = parsed?.traceId ?? randomHex(16);
      }
      if (!event.span_id) {
        event.span_id = randomHex(8);
      }
      if (parsed) {
        event.parent_span_id = parsed.spanId;
      }
      if (meta) {
        event.wasm_ms = Math.max(
          0,
          Number((performance.now() - meta.started).toFixed(3))
        );
        event.cf_ray ??= meta.cfRay ?? undefined;
      }
      event.colo ??= coloFromRay(
        typeof event.cf_ray === "string" ? event.cf_ray : null
      );
      event.trigger ??= "http";
      emitTelemetry(event, meta?.executionCtx);
    } catch (error) {
      console.error("Invalid Reptar telemetry event", error);
    }
  };

  // Called by Wasm apps that export supportsWsMessageDone: resolves one
  // held-open WebSocket event (message or close) per call, FIFO.
  scope.__REPTAR_WS_MESSAGE_DONE = (sessionId: string) => {
    const queue = wsDoneQueues.get(sessionId);
    const resolve = queue?.shift();
    if (queue && queue.length === 0) {
      wsDoneQueues.delete(sessionId);
    }
    resolve?.();
  };

  scope.__REPTAR_RESPOND = (requestId, value) => {
    const resolve = pending.get(requestId);

    if (resolve) {
      pending.delete(requestId);
      resolve(value);
    }
  };

  scope.__REPTAR_RESPOND_QUEUE = (batchId, value) => {
    const resolve = pendingQueue.get(batchId);

    if (resolve) {
      pendingQueue.delete(batchId);
      resolve(value);
    }
  };

  scope.__REPTAR_RESPOND_SCHEDULED = (scheduleId, value) => {
    const resolve = pendingScheduled.get(scheduleId);

    if (resolve) {
      pendingScheduled.delete(scheduleId);
      resolve(value);
    }
  };

  scope.__REPTAR_WS_SEND = (sessionId, message) => {
    sockets.get(sessionId)?.send(message);
  };

  scope.__REPTAR_WS_CLOSE = (sessionId, code, reason) => {
    const socket = sockets.get(sessionId);
    sockets.delete(sessionId);
    socket?.close(code, reason);
  };

  scope.__REPTAR_STREAM_START = (
    requestId,
    status,
    headersJson,
    cookiesJson
  ) => {
    const state = pendingStreams.get(requestId);

    if (!state) {
      return;
    }

    const headers = new Headers(
      JSON.parse(headersJson || "{}") as Record<string, string>
    );
    const cookies = JSON.parse(cookiesJson || "[]") as string[];

    for (const cookie of cookies) {
      headers.append("set-cookie", cookie);
    }

    const stream = new ReadableStream<Uint8Array>({
      start(controller) {
        state.controller = controller;
      },
    });

    state.resolve(new Response(stream, { headers, status }));
  };

  scope.__REPTAR_STREAM_CHUNK = (requestId, base64) => {
    const state = pendingStreams.get(requestId);
    state?.controller?.enqueue(base64ToBytes(base64));
  };

  scope.__REPTAR_STREAM_END = (requestId) => {
    const state = pendingStreams.get(requestId);

    if (!state) {
      return;
    }

    pendingStreams.delete(requestId);
    state.controller?.close();
  };

  scope.__REPTAR_STREAM_ERROR = (requestId, message) => {
    const state = pendingStreams.get(requestId);

    if (!state) {
      return;
    }

    pendingStreams.delete(requestId);
    state.controller?.error(new Error(message));
  };

  const getApp = (origin: string, env: TEnv): Promise<ReptarWasmApp> => {
    scope.__REPTAR_MANIFEST_URL ??= (options.manifestUrl ?? defaultManifestUrl)(
      origin
    );
    scope.__REPTAR_WASM_MODULE ??= options.wasm;
    scope.__REPTAR_ENV_GET ??= (name) => {
      const value = (env as Record<string, unknown>)[name];
      return typeof value === "string" ? value : null;
    };
    registerCryptoBindings(scope);

    const bindings = options.bindings(env);
    registerDbBindings(scope, bindings.db);
    registerPostgresBindings(scope, bindings.postgres);
    registerR2Bindings(scope, bindings.r2);
    registerKvBindings(scope, bindings.kv);
    registerQueuesBinding(scope, bindings.queues);
    registerAiBinding(scope, bindings.ai);
    registerDurableBinding(scope, bindings.durable);
    registerHttpFetch(scope, options.httpAllowedHosts);

    scope.__REPTAR_FETCH ??= (input, init) => {
      const request = new Request(input, init);
      const assets = options.assets?.(env);

      if (assets && new URL(request.url).pathname.startsWith("/browser/")) {
        return assets.fetch(request);
      }

      return fetch(request);
    };

    if (!appPromise) {
      const opening = options.open();
      appPromise = opening.catch((error) => {
        // Never cache a rejected instance; the next request gets a fresh
        // attempt instead of the same failure forever.
        appPromise = null;
        throw error;
      });
    }

    return appPromise;
  };

  const resetApp = (): void => {
    appPromise = null;
  };

  const handleWebSocket = async (
    request: Request,
    env: TEnv
  ): Promise<Response> => {
    const url = new URL(request.url);
    const app = await getApp(url.origin, env);

    if (typeof app.handleWebSocketOpen !== "function") {
      return errorResponse(
        "Reptar app does not export handleWebSocketOpen()",
        501
      );
    }

    // Workers cancel pending I/O the moment an event handler returns, but
    // the Wasm app processes events on async continuations that outlive the
    // JS call. Apps that opt in (supportsWsMessageDone export) call back
    // through __REPTAR_WS_MESSAGE_DONE once their pipeline — including any
    // binding I/O such as D1 writes — has completed, so the event can be
    // held open. The capability flag keeps older apps unaffected.
    const tracksCompletion =
      typeof app.supportsWsMessageDone === "function" &&
      app.supportsWsMessageDone();

    const Pair = (
      globalThis as unknown as {
        WebSocketPair: new () => [WorkerWebSocket, WorkerWebSocket];
      }
    ).WebSocketPair;
    const [client, server] = new Pair();
    const sessionId = crypto.randomUUID();
    server.accept();
    sockets.set(sessionId, server);

    // Promise wrapper: resolves from a timer or from the Wasm done callback
    // (two callback sources), so the event handler can await it. Async
    // exports can be awaited directly and cancel the hold.
    const holdEventOpen = (): {
      cancel: () => void;
      promise: Promise<void>;
    } => {
      let timer: ReturnType<typeof setTimeout> | undefined;
      let settled = false;
      let resolveHold: (() => void) | undefined;
      const promise = new Promise<void>((resolve) => {
        resolveHold = resolve;
      });

      const settle = (): void => {
        if (settled) {
          return;
        }
        settled = true;
        if (timer !== undefined) {
          clearTimeout(timer);
        }
        const queue = wsDoneQueues.get(sessionId);
        const index = queue && resolveHold ? queue.indexOf(resolveHold) : -1;
        if (index !== undefined && index >= 0) {
          queue?.splice(index, 1);
        }
        resolveHold?.();
      };

      if (!tracksCompletion) {
        settle();
        return { cancel: settle, promise };
      }

      const queue = wsDoneQueues.get(sessionId) ?? [];
      if (resolveHold) {
        queue.push(resolveHold);
      }
      wsDoneQueues.set(sessionId, queue);
      // Safety net: never wedge the event on an app bug.
      timer = setTimeout(settle, 30_000);

      return { cancel: settle, promise };
    };

    const awaitWsResult = async (
      result: void | Promise<void>,
      hold: { cancel: () => void; promise: Promise<void> }
    ): Promise<void> => {
      if (isThenable(result)) {
        hold.cancel();
        await result;
        return;
      }

      await hold.promise;
    };

    const close = async () => {
      sockets.delete(sessionId);
      const hold = holdEventOpen();
      await awaitWsResult(
        app.handleWebSocketClose?.(JSON.stringify({ sessionId })),
        hold
      );
    };

    server.addEventListener("message", async (event: MessageEvent) => {
      const message =
        typeof event.data === "string"
          ? event.data
          : bytesToBase64(new Uint8Array(event.data as ArrayBuffer));
      const hold = holdEventOpen();
      await awaitWsResult(
        app.handleWebSocketMessage?.(JSON.stringify({ message, sessionId })),
        hold
      );
    });
    server.addEventListener("close", close);
    server.addEventListener("error", close);

    try {
      const hold = holdEventOpen();
      await awaitWsResult(
        app.handleWebSocketOpen?.(
          JSON.stringify({
            headers: Object.fromEntries(request.headers.entries()),
            path: url.pathname,
            query: url.search.replace(/^\?/u, ""),
            sessionId,
          })
        ),
        hold
      );
    } catch (error) {
      close();
      return errorResponse(
        error instanceof Error ? error.message : "WebSocket open failed"
      );
    }

    return new Response(null, {
      status: 101,
      webSocket: client,
    } as ResponseInit & { webSocket: WebSocket });
  };

  const handleRequest = async (
    request: Request,
    env: TEnv,
    executionCtx?: ExecutionContextLike
  ): Promise<Response> => {
    const url = new URL(request.url);

    if (request.headers.get("upgrade")?.toLowerCase() === "websocket") {
      return handleWebSocket(request, env);
    }

    let app: ReptarWasmApp;

    try {
      app = await getApp(url.origin, env);
    } catch (error) {
      return errorResponse(
        error instanceof Error ? error.message : "Reptar app failed to start"
      );
    }

    if (typeof app.handle !== "function") {
      return errorResponse("Reptar app does not export handle()");
    }

    const body = bytesToBase64(new Uint8Array(await request.arrayBuffer()));
    const requestId = crypto.randomUUID();
    const traceParent = traceParentFor(request);
    const headers = Object.fromEntries(request.headers.entries());
    headers.traceparent ??= traceParent;
    const cfRay = request.headers.get("cf-ray");

    requestMeta.set(requestId, {
      cfRay,
      executionCtx,
      started: performance.now(),
      traceParent,
    });

    // The C# app responds through a JS import, so the host owns the promise.
    // The request ID keeps concurrent requests from crossing responses.
    // eslint-disable-next-line promise/avoid-new
    const response = new Promise<string>((resolve) => {
      pending.set(requestId, resolve);
    });
    // eslint-disable-next-line promise/avoid-new
    const streaming = new Promise<Response>((resolve) => {
      pendingStreams.set(requestId, { resolve });
    });

    const timeout = timeoutAfter(options.requestTimeoutMs ?? 30_000, {
      kind: "timeout" as const,
    });
    let exportOutcome: Promise<
      { kind: "completed" } | { error: unknown; kind: "error" }
    > | null = null;

    const cleanup = (): void => {
      pending.delete(requestId);
      pendingStreams.delete(requestId);
      requestMeta.delete(requestId);
      timeout.cancel();
    };

    try {
      const result = app.handle(
        JSON.stringify({
          body,
          headers,
          method: request.method,
          path: url.pathname,
          query: url.search.replace(/^\?/u, ""),
          requestId,
        })
      );

      if (isThenable(result)) {
        exportOutcome = Promise.resolve(result).then(
          () => ({ kind: "completed" as const }),
          (error: unknown) => ({ error, kind: "error" as const })
        );
      }
    } catch (error) {
      resetApp();
      cleanup();
      return errorResponse(
        error instanceof Error ? error.message : "Reptar app failed"
      );
    }

    const racers: Promise<
      | { kind: "completed" }
      | { error: unknown; kind: "error" }
      | { json: string; kind: "normal" }
      | { kind: "stream"; stream: Response }
      | { kind: "timeout" }
    >[] = [
      response.then((json) => ({ json, kind: "normal" as const })),
      streaming.then((stream) => ({ kind: "stream" as const, stream })),
      timeout.promise,
    ];

    if (exportOutcome) {
      racers.push(exportOutcome);
    }

    const first = await Promise.race(racers);
    timeout.cancel();

    if (first.kind === "timeout") {
      cleanup();
      if (executionCtx && exportOutcome) {
        executionCtx.waitUntil(exportOutcome);
      }
      return errorResponse("Reptar request timed out", 504);
    }

    if (first.kind === "error") {
      resetApp();
      cleanup();
      console.error(
        "Reptar Wasm handler failed",
        (first.error as Error)?.stack ?? first.error
      );
      return errorResponse("Reptar app failed");
    }

    if (first.kind === "completed") {
      cleanup();
      return errorResponse("Reptar app completed without a response", 502);
    }

    if (first.kind === "stream") {
      cleanup();
      if (executionCtx && exportOutcome) {
        executionCtx.waitUntil(exportOutcome);
      }
      return first.stream;
    }

    cleanup();

    const { json } = first;
    let payload: HostResponseEnvelope;

    try {
      payload = JSON.parse(json) as HostResponseEnvelope;
    } catch {
      return errorResponse(`Invalid Reptar response: ${json}`, 502);
    }

    const responseHeaders = new Headers(payload.headers);
    responseHeaders.set("x-request-id", requestId);
    const parsedTrace = parseTraceParent(traceParent);
    if (parsedTrace) {
      responseHeaders.set("x-trace-id", parsedTrace.traceId);
    }

    for (const cookie of payload.setCookies ?? []) {
      responseHeaders.append("set-cookie", cookie);
    }

    const status = payload.status ?? 200;
    const nullBody = status === 204 || status === 205 || status === 304;

    // Keep the Worker event alive until the async export (including
    // Ctx.WaitUntil work) settles. The response is already on the wire.
    if (executionCtx && exportOutcome) {
      executionCtx.waitUntil(exportOutcome);
    }

    return new Response(
      nullBody
        ? null
        : (base64ToBytes(payload.body ?? "") as unknown as BodyInit),
      {
        headers: responseHeaders,
        status,
      }
    );
  };

  const handleQueue = async (
    batch: MessageBatch<unknown>,
    env: TEnv,
    executionCtx?: ExecutionContextLike
  ): Promise<void> => {
    let app: ReptarWasmApp;

    try {
      app = await getApp("https://queue.local", env);
    } catch (error) {
      console.error("Reptar queue app failed to start", error);
      for (const message of batch.messages) {
        message.retry?.();
      }
      return;
    }

    if (typeof app.handleQueue !== "function") {
      console.warn("Reptar app does not export handleQueue()");
      for (const message of batch.messages) {
        message.retry?.();
      }
      return;
    }

    const batchId = crypto.randomUUID();
    // eslint-disable-next-line promise/avoid-new
    const response = new Promise<string>((resolve) => {
      pendingQueue.set(batchId, resolve);
    });
    const timeout = timeoutAfter(options.consumerTimeoutMs ?? 60_000, {
      kind: "timeout" as const,
    });
    let exportOutcome: Promise<
      { kind: "completed" } | { error: unknown; kind: "error" }
    > | null = null;

    const payload = {
      batchId,
      messages: batch.messages.map((message) => ({
        attempts: message.attempts,
        body:
          typeof message.body === "string"
            ? message.body
            : JSON.stringify(message.body),
        id: message.id,
        timestamp:
          message.timestamp instanceof Date
            ? message.timestamp.toISOString()
            : String(message.timestamp),
      })),
      queue: batch.queue,
    };

    const retryAll = (): void => {
      for (const message of batch.messages) {
        message.retry?.();
      }
    };

    try {
      const result = app.handleQueue(JSON.stringify(payload));
      if (isThenable(result)) {
        exportOutcome = Promise.resolve(result).then(
          () => ({ kind: "completed" as const }),
          (error: unknown) => ({ error, kind: "error" as const })
        );
      }
    } catch (error) {
      pendingQueue.delete(batchId);
      timeout.cancel();
      console.error("Reptar queue handler failed", error);
      retryAll();
      return;
    }

    const racers: Promise<
      | { kind: "completed" }
      | { error: unknown; kind: "error" }
      | { json: string; kind: "normal" }
      | { kind: "timeout" }
    >[] = [
      response.then((json) => ({ json, kind: "normal" as const })),
      timeout.promise,
    ];

    if (exportOutcome) {
      racers.push(exportOutcome);
    }

    const first = await Promise.race(racers);
    timeout.cancel();
    pendingQueue.delete(batchId);

    if (first.kind !== "normal") {
      if (first.kind === "error") {
        console.error("Reptar queue handler failed", first.error);
      } else if (first.kind === "timeout") {
        console.error("Reptar queue handler timed out");
      } else {
        console.error("Reptar queue handler completed without a result");
      }
      retryAll();
      return;
    }

    try {
      const result = JSON.parse(first.json) as {
        failedMessageIds?: string[];
        retries?: { delaySeconds?: number; messageId: string }[];
        success?: boolean;
      };
      const failed = new Set(result.failedMessageIds);
      const retryDelays = new Map<string, number>();
      for (const retry of result.retries ?? []) {
        if (typeof retry.delaySeconds === "number" && retry.delaySeconds > 0) {
          retryDelays.set(retry.messageId, retry.delaySeconds);
        }
      }

      for (const message of batch.messages) {
        if (failed.has(message.id)) {
          const delaySeconds = retryDelays.get(message.id);
          message.retry?.(
            delaySeconds === undefined ? undefined : { delaySeconds }
          );
        } else {
          message.ack?.();
        }
      }
    } catch {
      // Fall back to batch completion.
    }

    if (executionCtx && exportOutcome) {
      executionCtx.waitUntil(exportOutcome);
    }
  };

  const handleScheduled = async (
    event: { cron: string; scheduledTime: number | Date },
    env: TEnv,
    executionCtx?: ExecutionContextLike
  ): Promise<void> => {
    let app: ReptarWasmApp;

    try {
      app = await getApp("https://scheduled.local", env);
    } catch (error) {
      console.error("Reptar scheduled app failed to start", error);
      return;
    }

    if (typeof app.handleScheduled !== "function") {
      console.warn("Reptar app does not export handleScheduled()");
      return;
    }

    const scheduleId = crypto.randomUUID();
    // eslint-disable-next-line promise/avoid-new
    const response = new Promise<string>((resolve) => {
      pendingScheduled.set(scheduleId, resolve);
    });
    const timeout = timeoutAfter(options.consumerTimeoutMs ?? 60_000, {
      kind: "timeout" as const,
    });
    let exportOutcome: Promise<
      { kind: "completed" } | { error: unknown; kind: "error" }
    > | null = null;

    const scheduledTime =
      event.scheduledTime instanceof Date
        ? event.scheduledTime.toISOString()
        : new Date(event.scheduledTime).toISOString();

    try {
      const result = app.handleScheduled(
        JSON.stringify({
          cron: event.cron,
          scheduleId,
          scheduledTime,
        })
      );
      if (isThenable(result)) {
        exportOutcome = Promise.resolve(result).then(
          () => ({ kind: "completed" as const }),
          (error: unknown) => ({ error, kind: "error" as const })
        );
      }
    } catch (error) {
      pendingScheduled.delete(scheduleId);
      timeout.cancel();
      console.error("Reptar scheduled handler failed", error);
      return;
    }

    const racers: Promise<
      | { kind: "completed" }
      | { error: unknown; kind: "error" }
      | { json: string; kind: "normal" }
      | { kind: "timeout" }
    >[] = [
      response.then((json) => ({ json, kind: "normal" as const })),
      timeout.promise,
    ];

    if (exportOutcome) {
      racers.push(exportOutcome);
    }

    const first = await Promise.race(racers);
    timeout.cancel();
    pendingScheduled.delete(scheduleId);

    if (first.kind !== "normal") {
      if (first.kind === "error") {
        console.error("Reptar scheduled handler failed", first.error);
      } else if (first.kind === "timeout") {
        console.error("Reptar scheduled handler timed out");
      } else {
        console.error("Reptar scheduled handler completed without a result");
      }
      return;
    }

    if (executionCtx && exportOutcome) {
      executionCtx.waitUntil(exportOutcome);
    }
  };

  return {
    fetch: handleRequest,
    queue: handleQueue,
    scheduled: handleScheduled,
  };
};
