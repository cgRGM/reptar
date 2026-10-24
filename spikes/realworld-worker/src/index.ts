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
  async fetch(request: Request, env: Env) {
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

    return withCors(await host.fetch(request, env), origin, env.CORS_ORIGINS);
  },
};
