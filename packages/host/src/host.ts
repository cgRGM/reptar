import { d1Query } from "./bindings";
import type { ReptarBindings } from "./bindings";

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

const defaultManifestUrl = (origin: string): string =>
  new URL("/browser/deployment.json", origin).href;

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
    const scope = globalThis as typeof globalThis & {
      __REPTAR_DB_QUERY?: (sql: string) => Promise<string>;
      __REPTAR_MANIFEST_URL?: string;
      __REPTAR_WASM_MODULE?: WebAssembly.Module;
    };

    scope.__REPTAR_MANIFEST_URL ??= (options.manifestUrl ?? defaultManifestUrl)(
      origin
    );
    scope.__REPTAR_WASM_MODULE ??= options.wasm;

    const { db } = options.bindings(env);

    if (db) {
      scope.__REPTAR_DB_QUERY ??= (sql) => d1Query(db, sql);
    }

    appPromise ??= options.open();
    return appPromise;
  };

  return {
    async fetch(request, env) {
      const url = new URL(request.url);
      const app = await getApp(url.origin, env);

      if (typeof app.handle !== "function") {
        return new Response("Reptar app does not export handle()", {
          status: 500,
        });
      }

      const body = await request.text();
      const scope = globalThis as typeof globalThis & {
        __REPTAR_RESPOND?: (value: string) => void;
      };

      // The C# app responds through a JS import, so the host owns the promise.
      // eslint-disable-next-line promise/avoid-new
      const response = new Promise<string>((resolve) => {
        scope.__REPTAR_RESPOND = resolve;
      });

      app.handle(
        JSON.stringify({
          body,
          headers: Object.fromEntries(request.headers.entries()),
          method: request.method,
          path: url.pathname,
          query: url.search,
        })
      );

      const json = await response;

      return new Response(json, {
        headers: { "content-type": "application/json; charset=utf-8" },
      });
    },
  };
};
