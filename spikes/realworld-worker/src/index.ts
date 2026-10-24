import { createReptarHost } from "@reptar/host";
import type { D1DatabaseLike, ReptarWasmApp } from "@reptar/host";

import wasmModule from "../public/browser/RealWorld.wasm";

interface Env {
  ASSETS: { fetch: (request: Request) => Promise<Response> };
  CORS_ORIGINS?: string;
  DB: D1Database;
  REALWORLD_JWT_SECRET?: string;
}

const parseOrigins = (value: string | undefined): string[] =>
  (value ?? "")
    .split(",")
    .map((origin) => origin.trim())
    .filter(Boolean);

const corsHeaders = (
  origin: string | null,
  configured: string | undefined
): Record<string, string> => {
  if (!origin || !parseOrigins(configured).includes(origin)) {
    return {};
  }

  return {
    "access-control-allow-credentials": "true",
    "access-control-allow-headers": "content-type, authorization",
    "access-control-allow-methods": "GET, POST, PUT, DELETE, OPTIONS",
    "access-control-allow-origin": origin,
    vary: "Origin",
  };
};

const withCors = (
  response: Response,
  origin: string | null,
  configured: string | undefined
): Response => {
  for (const [key, value] of Object.entries(corsHeaders(origin, configured))) {
    response.headers.set(key, value);
  }

  return response;
};

const CACHE_SECONDS = 15;
const CACHE_PURGE_PATHS = [
  "/api/tags",
  "/api/articles",
  "/api/articles?limit=20",
];

const isCacheableRead = (request: Request, url: URL): boolean => {
  if (request.method !== "GET") {
    return false;
  }
  if (request.headers.has("authorization") || request.headers.has("cookie")) {
    return false;
  }

  if (url.pathname === "/api/tags") {
    return true;
  }

  if (url.pathname === "/api/articles") {
    return url.search === "" || url.search === "?limit=20";
  }

  return (
    url.pathname.startsWith("/api/articles/") &&
    url.pathname !== "/api/articles/feed" &&
    !url.pathname.includes("/comments") &&
    !url.pathname.endsWith("/favorite")
  );
};

const cacheKeyFor = (url: URL): Request =>
  new Request(url.toString(), { method: "GET" });

const readCache = async (url: URL): Promise<Response | null> => {
  if (typeof caches === "undefined") {
    return null;
  }
  return await caches.default.match(cacheKeyFor(url));
};

const writeCache = async (url: URL, response: Response): Promise<void> => {
  if (typeof caches === "undefined" || response.status !== 200) {
    return;
  }

  const headers = new Headers(response.headers);
  headers.set(
    "cache-control",
    `public, max-age=${CACHE_SECONDS}, stale-while-revalidate=${CACHE_SECONDS * 2}`
  );
  headers.delete("set-cookie");
  const body = await response.arrayBuffer();
  await caches.default.put(
    cacheKeyFor(url),
    new Response(body, { headers, status: response.status })
  );
};

const purgeReadCache = async (url: URL, slug?: string): Promise<void> => {
  if (typeof caches === "undefined") {
    return;
  }

  const paths = [...CACHE_PURGE_PATHS];
  if (slug) {
    paths.push(`/api/articles/${slug}`);
  }

  await Promise.all(
    paths.map((path) =>
      caches.default.delete(cacheKeyFor(new URL(path, url.origin)))
    )
  );
};

const host = createReptarHost<Env>({
  assets: (env) => env.ASSETS,
  bindings: (env) => ({
    db: env.DB as unknown as D1DatabaseLike,
  }),
  open: async () => {
    const module = await import("../public/browser/RealWorld.mjs");
    return (await module.open()) as ReptarWasmApp;
  },
  wasm: wasmModule,
});

export default {
  async fetch(request: Request, env: Env, ctx: ExecutionContext) {
    const url = new URL(request.url);
    if (url.pathname.startsWith("/browser/")) {
      return env.ASSETS.fetch(request);
    }

    const origin = request.headers.get("origin");
    const cors = corsHeaders(origin, env.CORS_ORIGINS);

    if (request.method === "OPTIONS") {
      if (origin && !cors["access-control-allow-origin"]) {
        return new Response(null, { status: 403 });
      }

      return new Response(null, { headers: cors });
    }

    if (request.method === "GET" && (url.pathname === "/" || url.pathname === "/health")) {
      const body =
        url.pathname === "/health"
          ? { status: "ok", service: "reptar-realworld-bench" }
          : {
              service: "reptar-realworld-bench",
              status: "ok",
              api: "/api",
              health: "/health",
              runtime: "cloudflare-workers",
              cacheSeconds: CACHE_SECONDS,
            };
      return withCors(Response.json(body), origin, env.CORS_ORIGINS);
    }

    const cacheable = isCacheableRead(request, url);
    if (cacheable) {
      const cached = await readCache(url);
      if (cached) {
        return withCors(cached, origin, env.CORS_ORIGINS);
      }
    }

    const response = await host.fetch(request, env);

    if (cacheable && response.status === 200) {
      ctx.waitUntil(writeCache(url, response.clone()));
    } else if (request.method !== "GET" && response.status < 400) {
      const match = /^\/api\/articles\/(?<slug>[^/]+)$/u.exec(url.pathname);
      await purgeReadCache(url, match?.groups?.slug);
    }

    return withCors(response, origin, env.CORS_ORIGINS);
  },
};
