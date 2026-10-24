import { createReptarHost } from "@reptar/host";
import type {
  D1DatabaseLike,
  ReptarBindings,
  ReptarWasmApp,
} from "@reptar/host";

import wasmModule from "../public/browser/HelloReptar.wasm";

interface Env {
  AI: Ai;
  APP_NAME?: string;
  ASSETS: { fetch: (request: Request) => Promise<Response> };
  BUCKET: R2Bucket;
  CORS_ORIGINS?: string;
  COUNTER: DurableObjectNamespace;
  DB: D1Database;
  KV: KVNamespace;
  QUEUE: Queue;
}

export class Counter {
  private readonly state: DurableObjectState;

  constructor(state: DurableObjectState) {
    this.state = state;
  }

  async fetch(): Promise<Response> {
    const current = (await this.state.storage.get<number>("count")) ?? 0;
    const next = current + 1;
    await this.state.storage.put("count", next);
    return Response.json({ count: next });
  }
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
    "access-control-allow-headers": "content-type",
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
    ai: env.AI as unknown as ReptarBindings["ai"],
    db: env.DB as unknown as D1DatabaseLike,
    durable: {
      counter: env.COUNTER as unknown as NonNullable<
        ReptarBindings["durable"]
      >[string],
    },
    kv: env.KV as unknown as ReptarBindings["kv"],
    queues: {
      events: env.QUEUE as unknown as NonNullable<
        ReptarBindings["queues"]
      >[string],
    },
    r2: {
      delete: async (key) => {
        await env.BUCKET.delete(key);
      },
      exists: async (key) => {
        const head = await env.BUCKET.head(key);
        return head !== null;
      },
      get: async (key) => {
        const obj = await env.BUCKET.get(key);
        if (!obj) {
          return null;
        }
        const buf = await obj.arrayBuffer();
        return new Uint8Array(buf);
      },
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
    const cors = corsHeaders(origin, env.CORS_ORIGINS);

    if (request.method === "OPTIONS") {
      if (origin && !cors["access-control-allow-origin"]) {
        return new Response(null, { status: 403 });
      }

      return new Response(null, { headers: cors });
    }

    if (url.pathname.startsWith("/browser/")) {
      return env.ASSETS.fetch(request);
    }

    if (url.pathname.startsWith("/images/") && request.method === "GET") {
      const key = decodeURIComponent(url.pathname.slice("/images/".length));
      const object = await env.BUCKET.get(key);

      if (!object) {
        return withCors(
          new Response("Not found", { status: 404 }),
          origin,
          env.CORS_ORIGINS
        );
      }

      return withCors(
        new Response(object.body, {
          headers: {
            "cache-control": "public, max-age=31536000, immutable",
            "content-type":
              object.httpMetadata?.contentType ?? "application/octet-stream",
          },
        }),
        origin,
        env.CORS_ORIGINS
      );
    }

    return withCors(await host.fetch(request, env), origin, env.CORS_ORIGINS);
  },
};
