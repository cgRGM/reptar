import { createReptarHost } from "@reptar/host";
import type { D1DatabaseLike, ReptarWasmApp } from "@reptar/host";

import wasmModule from "../public/browser/HelloReptar.wasm";

interface Env {
  ASSETS: { fetch: (request: Request) => Promise<Response> };
  BUCKET: R2Bucket;
  DB: D1Database;
}

const corsHeaders = (origin: string | null) => ({
  "access-control-allow-credentials": "true",
  "access-control-allow-headers": "content-type",
  "access-control-allow-methods": "GET, POST, OPTIONS",
  "access-control-allow-origin": origin ?? "*",
  vary: "Origin",
});

const withCors = (response: Response, origin: string | null): Response => {
  for (const [key, value] of Object.entries(corsHeaders(origin))) {
    response.headers.set(key, value);
  }

  return response;
};

const host = createReptarHost<Env>({
  assets: (env) => env.ASSETS,
  bindings: (env) => ({
    db: env.DB as unknown as D1DatabaseLike,
    r2: {
      put: async (key, bytes, contentType) => {
        await env.BUCKET.put(key, bytes, {
          httpMetadata: { contentType },
        });
        return `/images/${key}`;
      },
    },
  }),
  open: async () => {
    const module = await import("../public/browser/HelloReptar.mjs");
    return (await module.open()) as ReptarWasmApp;
  },
  wasm: wasmModule,
});

export default {
  async fetch(request: Request, env: Env) {
    const url = new URL(request.url);
    const origin = request.headers.get("origin");

    if (request.method === "OPTIONS") {
      return new Response(null, { headers: corsHeaders(origin) });
    }

    if (url.pathname.startsWith("/browser/")) {
      return env.ASSETS.fetch(request);
    }

    if (url.pathname.startsWith("/images/") && request.method === "GET") {
      const key = decodeURIComponent(url.pathname.slice("/images/".length));
      const object = await env.BUCKET.get(key);

      if (!object) {
        return withCors(new Response("Not found", { status: 404 }), origin);
      }

      return withCors(
        new Response(object.body, {
          headers: {
            "cache-control": "public, max-age=31536000, immutable",
            "content-type":
              object.httpMetadata?.contentType ?? "application/octet-stream",
          },
        }),
        origin
      );
    }

    return withCors(await host.fetch(request, env), origin);
  },
};
