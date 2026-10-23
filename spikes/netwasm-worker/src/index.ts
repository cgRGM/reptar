import wasmModule from "../public/browser/HelloReptar.wasm";

interface Env {
  ASSETS: { fetch: (request: Request) => Promise<Response> };
  BUCKET: R2Bucket;
  DB: D1Database;
}

interface BlogApp {
  createPost: (title: string, content: string, imageKey: string) => void;
  listPosts: () => void;
  uploadImage: (key: string, contentType: string, bytes: Uint8Array) => void;
}

let appPromise: Promise<BlogApp> | null = null;

const getApp = async (origin: string, env: Env): Promise<BlogApp> => {
  const scope = globalThis as typeof globalThis & {
    __REPTAR_DB_QUERY?: (sql: string, paramsJson: string) => Promise<string>;
    __REPTAR_MANIFEST_URL?: string;
    __REPTAR_R2_PUT?: (
      key: string,
      bytes: Uint8Array,
      contentType: string
    ) => Promise<string>;
    __REPTAR_WASM_MODULE?: WebAssembly.Module;
  };

  scope.__REPTAR_MANIFEST_URL ??= new URL(
    "/browser/deployment.json",
    origin
  ).href;
  scope.__REPTAR_WASM_MODULE ??= wasmModule;
  scope.__REPTAR_DB_QUERY ??= async (sql, paramsJson) => {
    const params = JSON.parse(paramsJson) as unknown[];
    const statement = env.DB.prepare(sql);
    const bound = params.length > 0 ? statement.bind(...params) : statement;
    const result = await bound.all();
    return JSON.stringify(result.results);
  };
  scope.__REPTAR_R2_PUT ??= async (key, bytes, contentType) => {
    await env.BUCKET.put(key, bytes, {
      httpMetadata: { contentType },
    });
    return `/images/${key}`;
  };

  if (!appPromise) {
    const { open } = await import("../public/browser/HelloReptar.mjs");
    appPromise = open() as Promise<BlogApp>;
  }

  return appPromise;
};

const callApp = (app: BlogApp, invoke: () => void): Promise<string> => {
  const scope = globalThis as typeof globalThis & {
    __REPTAR_RESOLVE?: (value: string) => void;
  };

  // The C# app resolves through a JS import, so the host owns the promise.
  // eslint-disable-next-line promise/avoid-new
  const response = new Promise<string>((resolve) => {
    scope.__REPTAR_RESOLVE = resolve;
  });

  invoke();
  return response;
};

const corsHeaders = {
  "access-control-allow-headers": "content-type",
  "access-control-allow-methods": "GET, POST, OPTIONS",
  "access-control-allow-origin": "*",
};

const withCors = (response: Response): Response => {
  for (const [key, value] of Object.entries(corsHeaders)) {
    response.headers.set(key, value);
  }
  return response;
};

const json = (value: string, status = 200): Response =>
  withCors(
    new Response(value, {
      headers: { "content-type": "application/json; charset=utf-8" },
      status,
    })
  );

export default {
  async fetch(request: Request, env: Env) {
    const url = new URL(request.url);

    if (request.method === "OPTIONS") {
      return new Response(null, { headers: corsHeaders });
    }

    if (url.pathname.startsWith("/browser/")) {
      return env.ASSETS.fetch(request);
    }

    if (url.pathname === "/posts" && request.method === "GET") {
      const app = await getApp(url.origin, env);
      return json(await callApp(app, () => app.listPosts()));
    }

    if (url.pathname === "/posts" && request.method === "POST") {
      const body = (await request.json()) as {
        content: string;
        imageKey?: string;
        title: string;
      };
      const app = await getApp(url.origin, env);
      return json(
        await callApp(app, () =>
          app.createPost(body.title, body.content, body.imageKey ?? "")
        ),
        201
      );
    }

    if (url.pathname === "/upload" && request.method === "POST") {
      const key = url.searchParams.get("key") ?? crypto.randomUUID();
      const contentType =
        request.headers.get("content-type") ?? "application/octet-stream";
      const bytes = new Uint8Array(await request.arrayBuffer());
      const app = await getApp(url.origin, env);
      return json(
        await callApp(app, () => app.uploadImage(key, contentType, bytes)),
        201
      );
    }

    if (url.pathname.startsWith("/images/") && request.method === "GET") {
      const key = decodeURIComponent(url.pathname.slice("/images/".length));
      const object = await env.BUCKET.get(key);

      if (!object) {
        return withCors(new Response("Not found", { status: 404 }));
      }

      return withCors(
        new Response(object.body, {
          headers: {
            "cache-control": "public, max-age=31536000, immutable",
            "content-type":
              object.httpMetadata?.contentType ?? "application/octet-stream",
          },
        })
      );
    }

    return withCors(new Response("reptar blog spike"));
  },
};
