import { d1Execute, d1Query } from "./bindings";
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
  [key: string]: unknown;
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
  __REPTAR_DB_EXECUTE?: (sql: string, paramsJson: string) => Promise<number>;
  __REPTAR_DB_QUERY?: (sql: string, paramsJson: string) => Promise<string>;
  __REPTAR_DURABLE_FETCH?: (
    namespace: string,
    id: string,
    path: string,
    method: string,
    body: string
  ) => Promise<string>;
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
  __REPTAR_WASM_MODULE?: WebAssembly.Module;
}

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
    const stubId = ns.idFromString ? ns.idFromString(id) : ns.idFromName(id);
    const stub = ns.get(stubId);
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
  const scope = globalThis as typeof globalThis & ReptarScope;

  scope.__REPTAR_RESPOND = (requestId, value) => {
    const resolve = pending.get(requestId);

    if (resolve) {
      pending.delete(requestId);
      resolve(value);
    }
  };

  const getApp = (origin: string, env: TEnv): Promise<ReptarWasmApp> => {
    scope.__REPTAR_MANIFEST_URL ??= (options.manifestUrl ?? defaultManifestUrl)(
      origin
    );
    scope.__REPTAR_WASM_MODULE ??= options.wasm;
    registerCryptoBindings(scope);

    const bindings = options.bindings(env);
    registerDbBindings(scope, bindings.db);
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

  const handleRequest = async (
    request: Request,
    env: TEnv
  ): Promise<Response> => {
    const url = new URL(request.url);
    const app = await getApp(url.origin, env);

    if (typeof app.handle !== "function") {
      return errorResponse("Reptar app does not export handle()");
    }

    const body = await request.text();
    const requestId = crypto.randomUUID();

    // The C# app responds through a JS import, so the host owns the promise.
    // The request ID keeps concurrent requests from crossing responses.
    // eslint-disable-next-line promise/avoid-new
    const response = new Promise<string>((resolve) => {
      pending.set(requestId, resolve);
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
      return errorResponse(
        error instanceof Error ? error.message : "Reptar app failed"
      );
    }

    const json = await response;
    pending.delete(requestId);

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

    return new Response(nullBody ? null : (payload.body ?? ""), {
      headers,
      status,
    });
  };

  return {
    fetch: handleRequest,
  };
};
