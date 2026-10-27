import { createReptarHost } from "@reptar/host";
import type {
  D1DatabaseLike,
  MessageBatch,
  R2BucketLike,
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

interface WorkerWebSocket extends WebSocket {
  accept: () => void;
}

const handleWebSocketEcho = (): Response => {
  const Pair = (
    globalThis as unknown as {
      WebSocketPair: new () => [WorkerWebSocket, WorkerWebSocket];
    }
  ).WebSocketPair;
  const [client, server] = new Pair();
  server.accept();
  server.send("connected");
  server.addEventListener("message", (event: MessageEvent) => {
    server.send(`echo:${typeof event.data === "string" ? event.data : ""}`);
  });

  return new Response(null, {
    status: 101,
    webSocket: client,
  } as ResponseInit & { webSocket: WorkerWebSocket });
};

const sseResponse = (): Response => {
  const encoder = new TextEncoder();
  const stream = new ReadableStream<Uint8Array>({
    start(controller) {
      for (let index = 1; index <= 3; index += 1) {
        controller.enqueue(encoder.encode(`data: chunk-${index}\n\n`));
      }
      controller.close();
    },
  });

  return new Response(stream, {
    headers: {
      "cache-control": "no-cache",
      "content-type": "text/event-stream",
    },
  });
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
      bucket: env.BUCKET as unknown as R2BucketLike,
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
  async fetch(request: Request, env: Env, ctx: ExecutionContext) {
    const url = new URL(request.url);
    const origin = request.headers.get("origin");
    const cors = corsHeaders(origin, env.CORS_ORIGINS);

    if (
      url.pathname === "/ws" &&
      request.headers.get("upgrade")?.toLowerCase() === "websocket"
    ) {
      return handleWebSocketEcho();
    }

    if (url.pathname === "/platform/stream") {
      return withCors(sseResponse(), origin, env.CORS_ORIGINS);
    }

    if (url.pathname === "/platform/durable") {
      const id = env.COUNTER.idFromName("demo");
      const response = await env.COUNTER.get(id).fetch(
        new Request("https://do/")
      );

      return withCors(
        new Response(await response.text(), {
          headers: { "content-type": "application/json" },
        }),
        origin,
        env.CORS_ORIGINS
      );
    }

    if (url.pathname === "/platform/ai" && request.method === "POST") {
      const input = (await request.json()) as {
        model?: string;
        prompt?: string;
      };

      if (!input.prompt) {
        return withCors(
          Response.json({ error: "Prompt is required." }, { status: 400 }),
          origin,
          env.CORS_ORIGINS
        );
      }

      const result = await env.AI.run(
        input.model ?? "@cf/meta/llama-3.2-1b-instruct",
        { prompt: input.prompt }
      );

      return withCors(Response.json(result), origin, env.CORS_ORIGINS);
    }

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
            "content-security-policy": "default-src 'none'; sandbox",
            "content-type":
              object.httpMetadata?.contentType ?? "application/octet-stream",
            "x-content-type-options": "nosniff",
          },
        }),
        origin,
        env.CORS_ORIGINS
      );
    }

    return withCors(
      await host.fetch(request, env, ctx),
      origin,
      env.CORS_ORIGINS
    );
  },
  async queue(batch: MessageBatch<unknown>, env: Env, ctx: ExecutionContext) {
    // Workerd delivers the PHYSICAL queue name ("reptar-events"); the C#
    // handlers register LOGICAL names ("events" — matching the producer
    // binding). Map before dispatch so OnQueue("events") receives it.
    await host.queue({ ...batch, queue: "events" }, env, ctx);
  },
  async scheduled(
    event: { cron: string; scheduledTime: number | Date },
    env: Env,
    ctx: ExecutionContext
  ) {
    await host.scheduled(event, env, ctx);
  },
};
