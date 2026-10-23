import wasmModule from "../public/browser/HelloReptar.wasm";

interface Env {
  ASSETS: { fetch: (request: Request) => Promise<Response> };
  DB: D1Database;
}

interface SpikeApp {
  greet: (name: string) => string;
  query: (sql: string) => void;
}

let appPromise: Promise<SpikeApp> | null = null;

const getApp = async (origin: string, env: Env): Promise<SpikeApp> => {
  const scope = globalThis as typeof globalThis & {
    __REPTAR_DB_QUERY?: (sql: string) => Promise<string>;
    __REPTAR_MANIFEST_URL?: string;
    __REPTAR_WASM_MODULE?: WebAssembly.Module;
  };

  scope.__REPTAR_MANIFEST_URL ??= new URL(
    "/browser/deployment.json",
    origin
  ).href;
  scope.__REPTAR_WASM_MODULE ??= wasmModule;
  scope.__REPTAR_DB_QUERY ??= async (sql: string) => {
    const result = await env.DB.prepare(sql).all();
    return JSON.stringify(result.results);
  };

  if (!appPromise) {
    const { open } = await import("../public/browser/HelloReptar.mjs");
    appPromise = open() as Promise<SpikeApp>;
  }

  return appPromise;
};

export default {
  async fetch(request: Request, env: Env) {
    const url = new URL(request.url);

    if (url.pathname.startsWith("/browser/")) {
      return env.ASSETS.fetch(request);
    }

    if (url.pathname === "/greet") {
      const app = await getApp(url.origin, env);
      return new Response(app.greet(url.searchParams.get("name") ?? "Worker"));
    }

    if (url.pathname === "/db") {
      const app = await getApp(url.origin, env);
      const sql = url.searchParams.get("sql") ?? "select * from posts";
      const scope = globalThis as typeof globalThis & {
        __REPTAR_DB_RESOLVE?: (result: string) => void;
      };

      // The C# app resolves the query through a JS import.
      // eslint-disable-next-line promise/avoid-new
      const result = new Promise<string>((resolve) => {
        scope.__REPTAR_DB_RESOLVE = resolve;
      });

      app.query(sql);

      return new Response(await result, {
        headers: { "content-type": "application/json" },
      });
    }

    return new Response("reptar netwasm spike");
  },
};
