import { d1Batch, d1Execute, d1Query } from "./bindings";
import type { ReptarBindings } from "./bindings";
import {
  hashPassword,
  hmacSha256,
  randomToken,
  sha256,
  verifyPassword,
} from "./crypto";

export interface ReptarWasmApp {
  handle?: (requestJson: string) => void;
  handleQueue?: (batchJson: string) => void;
  handleScheduled?: (scheduledJson: string) => void;
  handleWebSocketClose?: (closeJson: string) => void;
  handleWebSocketMessage?: (messageJson: string) => void;
  handleWebSocketOpen?: (openJson: string) => void;
  [key: string]: unknown;
}

export interface MessageBatch<T = unknown> {
  ackAll?: () => void;
  readonly messages: readonly {
    ack?: () => void;
    readonly attempts: number;
    readonly body: T;
    readonly id: string;
    retry?: () => void;
    readonly timestamp: Date | string;
  }[];
  readonly queue: string;
  retryAll?: () => void;
}

interface WorkerWebSocket extends WebSocket {
  accept: () => void;
}

export interface ReptarHostOptions<TEnv> {
  assets?: (env: TEnv) => {
    fetch: (request: Request) => Promise<Response>;
  };
  bindings: (env: TEnv) => ReptarBindings;
  httpAllowedHosts?: string[];
  manifestUrl?: (origin: string) => string;
  open: () => Promise<ReptarWasmApp>;
  wasm: WebAssembly.Module;
}

export interface ReptarHost<TEnv> {
  fetch: (request: Request, env: TEnv) => Promise<Response>;
  queue: (batch: MessageBatch<unknown>, env: TEnv) => Promise<void>;
  scheduled: (
    event: { cron: string; scheduledTime: number | Date },
    env: TEnv
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
  __REPTAR_R2_DELETE?: (key: string) => Promise<void>;
  __REPTAR_R2_EXISTS?: (key: string) => Promise<boolean>;
  __REPTAR_R2_GET?: (key: string) => Promise<Uint8Array | null>;
  __REPTAR_R2_PUT?: (
    key: string,
    bytes: Uint8Array,
    contentType: string
  ) => Promise<string>;
  __REPTAR_RESPOND?: (requestId: string, value: string) => void;
  __REPTAR_RESPOND_QUEUE?: (batchId: string, value: string) => void;
  __REPTAR_RESPOND_SCHEDULED?: (scheduleId: string, value: string) => void;
  __REPTAR_STREAM_CHUNK?: (requestId: string, base64: string) => void;
  __REPTAR_STREAM_END?: (requestId: string) => void;
  __REPTAR_STREAM_ERROR?: (requestId: string, message: string) => void;
  __REPTAR_WS_CLOSE?: (sessionId: string, code: number, reason: string) => void;
  __REPTAR_WS_SEND?: (sessionId: string, message: string) => void;
  __REPTAR_STREAM_START?: (
    requestId: string,
    status: number,
    headersJson: string,
    cookiesJson: string
  ) => void;
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

const registerR2Bindings = (
  scope: ReptarScope,
  r2: ReptarBindings["r2"]
): void => {
  if (!r2) {
    return;
  }
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
  scope.__REPTAR_QUEUE_SEND ??= async (queueName, messageJson) => {
    const queue = queues[queueName];
    if (!queue) {
      throw new Error(`Queue binding '${queueName}' not found`);
    }
    await queue.send(JSON.parse(messageJson) as unknown);
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
  const sockets = new Map<string, WorkerWebSocket>();
  const pendingStreams = new Map<
    string,
    {
      controller?: ReadableStreamDefaultController<Uint8Array>;
      resolve: (response: Response) => void;
    }
  >();
  const scope = globalThis as typeof globalThis & ReptarScope;

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

    appPromise ??= options.open();
    return appPromise;
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

    const Pair = (
      globalThis as unknown as {
        WebSocketPair: new () => [WorkerWebSocket, WorkerWebSocket];
      }
    ).WebSocketPair;
    const [client, server] = new Pair();
    const sessionId = crypto.randomUUID();
    server.accept();
    sockets.set(sessionId, server);

    const close = () => {
      sockets.delete(sessionId);
      app.handleWebSocketClose?.(JSON.stringify({ sessionId }));
    };

    server.addEventListener("message", (event: MessageEvent) => {
      const message =
        typeof event.data === "string"
          ? event.data
          : bytesToBase64(new Uint8Array(event.data as ArrayBuffer));
      app.handleWebSocketMessage?.(JSON.stringify({ message, sessionId }));
    });
    server.addEventListener("close", close);
    server.addEventListener("error", close);

    try {
      app.handleWebSocketOpen(
        JSON.stringify({
          headers: Object.fromEntries(request.headers.entries()),
          path: url.pathname,
          query: url.search.replace(/^\?/u, ""),
          sessionId,
        })
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
    env: TEnv
  ): Promise<Response> => {
    const url = new URL(request.url);

    if (request.headers.get("upgrade")?.toLowerCase() === "websocket") {
      return handleWebSocket(request, env);
    }

    const app = await getApp(url.origin, env);

    if (typeof app.handle !== "function") {
      return errorResponse("Reptar app does not export handle()");
    }

    const body = bytesToBase64(new Uint8Array(await request.arrayBuffer()));
    const requestId = crypto.randomUUID();

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

    try {
      app.handle(
        JSON.stringify({
          body,
          headers: Object.fromEntries(request.headers.entries()),
          method: request.method,
          path: url.pathname,
          query: url.search.replace(/^\?/u, ""),
          requestId,
        })
      );
    } catch (error) {
      pending.delete(requestId);
      pendingStreams.delete(requestId);
      return errorResponse(
        error instanceof Error ? error.message : "Reptar app failed"
      );
    }

    const first = await Promise.race([
      response.then((json) => ({ json, kind: "normal" as const })),
      streaming.then((stream) => ({ kind: "stream" as const, stream })),
    ]);

    pending.delete(requestId);

    if (first.kind === "stream") {
      return first.stream;
    }

    pendingStreams.delete(requestId);

    const { json } = first;
    let payload: HostResponseEnvelope;

    try {
      payload = JSON.parse(json) as HostResponseEnvelope;
    } catch {
      return errorResponse(`Invalid Reptar response: ${json}`, 502);
    }

    const headers = new Headers(payload.headers);

    for (const cookie of payload.setCookies ?? []) {
      headers.append("set-cookie", cookie);
    }

    const status = payload.status ?? 200;
    const nullBody = status === 204 || status === 205 || status === 304;

    return new Response(
      nullBody
        ? null
        : (base64ToBytes(payload.body ?? "") as unknown as BodyInit),
      {
        headers,
        status,
      }
    );
  };

  const handleQueue = async (
    batch: MessageBatch<unknown>,
    env: TEnv
  ): Promise<void> => {
    const app = await getApp("https://queue.local", env);

    if (typeof app.handleQueue !== "function") {
      console.warn("Reptar app does not export handleQueue()");
      return;
    }

    const batchId = crypto.randomUUID();
    // eslint-disable-next-line promise/avoid-new
    const response = new Promise<string>((resolve) => {
      pendingQueue.set(batchId, resolve);
    });

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

    try {
      app.handleQueue(JSON.stringify(payload));
    } catch (error) {
      pendingQueue.delete(batchId);
      throw error;
    }

    const json = await response;
    pendingQueue.delete(batchId);

    try {
      const result = JSON.parse(json) as {
        failedMessageIds?: string[];
        success?: boolean;
      };
      const failed = new Set(result.failedMessageIds);

      for (const message of batch.messages) {
        if (failed.has(message.id)) {
          message.retry?.();
        } else {
          message.ack?.();
        }
      }
    } catch {
      // Fall back to batch completion
    }
  };

  const handleScheduled = async (
    event: { cron: string; scheduledTime: number | Date },
    env: TEnv
  ): Promise<void> => {
    const app = await getApp("https://scheduled.local", env);

    if (typeof app.handleScheduled !== "function") {
      console.warn("Reptar app does not export handleScheduled()");
      return;
    }

    const scheduleId = crypto.randomUUID();
    // eslint-disable-next-line promise/avoid-new
    const response = new Promise<string>((resolve) => {
      pendingScheduled.set(scheduleId, resolve);
    });

    const scheduledTime =
      event.scheduledTime instanceof Date
        ? event.scheduledTime.toISOString()
        : new Date(event.scheduledTime).toISOString();

    try {
      app.handleScheduled(
        JSON.stringify({
          cron: event.cron,
          scheduleId,
          scheduledTime,
        })
      );
    } catch (error) {
      pendingScheduled.delete(scheduleId);
      throw error;
    }

    await response;
    pendingScheduled.delete(scheduleId);
  };

  return {
    fetch: handleRequest,
    queue: handleQueue,
    scheduled: handleScheduled,
  };
};
