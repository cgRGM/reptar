import path from "node:path";

import { required } from "./required";

const { resolve } = path;

const root = import.meta.dir;
const api = process.env.SWITCHYARD_API ?? "http://127.0.0.1:8795";
const build = await Bun.build({
  entrypoints: [resolve(root, "web/app.ts")],
  minify: true,
  target: "browser",
  tsconfig: resolve(root, "tsconfig.json"),
});
if (!build.success) {
  throw new AggregateError(build.logs, "Frontend build failed");
}
const script = await required(build.outputs[0], "frontend bundle").text();
const server = Bun.serve({
  async fetch(request) {
    const url = new URL(request.url);
    if (url.pathname.startsWith("/api/") || url.pathname === "/health") {
      // This proxy is local-development infrastructure, not a public TLS edge.
      const origin = request.headers.get("origin");
      if (
        request.method !== "GET" &&
        request.method !== "HEAD" &&
        origin &&
        origin !== url.origin
      ) {
        return new Response("Cross-origin mutation rejected", { status: 403 });
      }
      const upstream = new URL(url.pathname + url.search, api);
      const response = await fetch(new Request(upstream, request), {
        redirect: "manual",
      });
      // Bun fetch decompresses upstream bodies; forwarding the original encoding
      // and length makes browsers attempt a second decompression.
      const headers = new Headers(response.headers);
      headers.delete("content-encoding");
      headers.delete("content-length");
      return new Response(response.body, { headers, status: response.status });
    }
    if (url.pathname === "/app.js") {
      return new Response(script, {
        headers: { "content-type": "text/javascript; charset=utf-8" },
      });
    }
    if (url.pathname === "/style.css") {
      return new Response(Bun.file(resolve(root, "web/style.css")));
    }
    if (url.pathname === "/") {
      return new Response(Bun.file(resolve(root, "web/index.html")));
    }
    return new Response("Not found", { status: 404 });
  },
  hostname: "127.0.0.1",
  port: Number(process.env.SWITCHYARD_WEB_PORT ?? 8796),
});
process.stdout.write(`Switchyard web: ${server.url}\n`);
