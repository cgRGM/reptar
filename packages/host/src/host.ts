import { d1Execute, d1Query } from "./bindings";
import type { ReptarBindings } from "./bindings";
import { hashPassword, randomToken, sha256, verifyPassword } from "./crypto";

export interface ReptarWasmApp {
  handle?: (requestJson: string) => void;
  [key: string]: unknown;
}

export interface ReptarHostOptions<TEnv> {
  bindings: (env: TEnv) => ReptarBindings;
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
  __REPTAR_CRYPTO_HASH_PASSWORD?: (password: string) => Promise<string>;
  __REPTAR_CRYPTO_RANDOM_TOKEN?: (bytes: number) => string;
  __REPTAR_CRYPTO_SHA256?: (input: string) => Promise<string>;
  __REPTAR_CRYPTO_VERIFY_PASSWORD?: (
    password: string,
    hash: string
  ) => Promise<boolean>;
  __REPTAR_DB_EXECUTE?: (sql: string, paramsJson: string) => Promise<number>;
  __REPTAR_DB_QUERY?: (sql: string, paramsJson: string) => Promise<string>;
  __REPTAR_MANIFEST_URL?: string;
  __REPTAR_R2_PUT?: (
    key: string,
    bytes: Uint8Array,
    contentType: string
  ) => Promise<string>;
  __REPTAR_RESPOND?: (value: string) => void;
  __REPTAR_WASM_MODULE?: WebAssembly.Module;
}

const defaultManifestUrl = (origin: string): string =>
  new URL("/browser/deployment.json", origin).href;

const errorResponse = (message: string, status = 500): Response =>
  new Response(message, {
    headers: { "content-type": "text/plain; charset=utf-8" },
    status,
  });

/**
 * Wraps a NetWasm app in a Cloudflare Worker host.
 *
 * Contract with the C# app:
 * - export `handle(string)` and return void
 * - call the `respond(string)` JS import when the response is ready
 * - call async JS imports (db, AI, storage) and await them before responding
 */
export const createReptarHost = <TEnv>(
  options: ReptarHostOptions<TEnv>
): ReptarHost<TEnv> => {
  let appPromise: Promise<ReptarWasmApp> | null = null;

  const getApp = (origin: string, env: TEnv): Promise<ReptarWasmApp> => {
    const scope = globalThis as typeof globalThis & ReptarScope;

    scope.__REPTAR_MANIFEST_URL ??= (options.manifestUrl ?? defaultManifestUrl)(
      origin
    );
    scope.__REPTAR_WASM_MODULE ??= options.wasm;
    scope.__REPTAR_CRYPTO_HASH_PASSWORD ??= hashPassword;
    scope.__REPTAR_CRYPTO_RANDOM_TOKEN ??= randomToken;
    scope.__REPTAR_CRYPTO_SHA256 ??= sha256;
    scope.__REPTAR_CRYPTO_VERIFY_PASSWORD ??= verifyPassword;

    const { db, r2 } = options.bindings(env);

    if (db) {
      scope.__REPTAR_DB_QUERY ??= (sql, paramsJson) =>
        d1Query(db, sql, paramsJson);
      scope.__REPTAR_DB_EXECUTE ??= (sql, paramsJson) =>
        d1Execute(db, sql, paramsJson);
    }

    if (r2) {
      scope.__REPTAR_R2_PUT ??= (key, bytes, contentType) =>
        r2.put(key, bytes, contentType);
    }

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
    const scope = globalThis as typeof globalThis & ReptarScope;

    // The C# app responds through a JS import, so the host owns the promise.
    // Requests are serialized per isolate because the responder is a global.
    // eslint-disable-next-line promise/avoid-new
    const response = new Promise<string>((resolve) => {
      scope.__REPTAR_RESPOND = resolve;
    });

    try {
      app.handle(
        JSON.stringify({
          body,
          headers: Object.fromEntries(request.headers.entries()),
          method: request.method,
          path: url.pathname,
          query: url.search.replace(/^\?/u, ""),
        })
      );
    } catch (error) {
      delete scope.__REPTAR_RESPOND;
      return errorResponse(
        error instanceof Error ? error.message : "Reptar app failed"
      );
    }

    const json = await response;
    scope.__REPTAR_RESPOND = undefined;

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

    return new Response(payload.body ?? "", {
      headers,
      status: payload.status ?? 200,
    });
  };

  let tail: Promise<unknown> = Promise.resolve();

  const enqueue = async <T>(task: () => Promise<T>): Promise<T> => {
    const previous = tail;
    const gate = Promise.withResolvers<boolean>();
    tail = gate.promise;

    try {
      await previous;
    } catch {
      // A failed request must not block the queue.
    }

    try {
      return await task();
    } finally {
      gate.resolve(true);
    }
  };

  return {
    fetch: (request, env) => enqueue(() => handleRequest(request, env)),
  };
};
