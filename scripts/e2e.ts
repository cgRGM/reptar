#!/usr/bin/env bun

import { spawn, spawnSync } from "node:child_process";
import { mkdirSync, rmSync } from "node:fs";
import path from "node:path";

const repoRoot = path.resolve(import.meta.dir, "..");
const spikeRoot = path.join(repoRoot, "spikes/netwasm-worker");
const persistDir = path.join(spikeRoot, ".wrangler/e2e");
const port = process.env.E2E_PORT ?? "8790";
const baseUrl = `http://127.0.0.1:${port}`;
const allowedOrigin = "http://localhost:4321";

const fail = (message: string): never => {
  console.error(`\nE2E FAILED: ${message}`);
  process.exit(1);
};

const assertStatus = async (
  label: string,
  response: Response,
  expected: number
): Promise<Response> => {
  if (response.status !== expected) {
    const body = await response.text();
    fail(`${label}: expected ${expected}, got ${response.status}: ${body}`);
  }

  console.log(`  ✓ ${label} → ${response.status}`);
  return response;
};

const json = async <T>(response: Response): Promise<T> =>
  (await response.json()) as T;

const waitForServer = async (): Promise<void> => {
  const deadline = Date.now() + 45_000;

  while (Date.now() < deadline) {
    try {
      // eslint-disable-next-line no-await-in-loop
      const response = await fetch(`${baseUrl}/health`);
      if (response.ok) {
        return;
      }
    } catch {
      // Server not ready yet.
    }

    // eslint-disable-next-line no-await-in-loop
    await Bun.sleep(300);
  }

  fail("wrangler dev did not become ready");
};

const cookieFrom = (response: Response): string => {
  const header = response.headers.get("set-cookie");

  if (!header) {
    fail("expected a Set-Cookie header");
  }

  return header.split(";")[0] ?? fail("invalid Set-Cookie header");
};

const percentile = (values: number[], p: number): number => {
  const sorted = values.toSorted((a, b) => a - b);
  const index = Math.min(
    sorted.length - 1,
    Math.max(0, Math.ceil((p / 100) * sorted.length) - 1)
  );

  return sorted[index] ?? 0;
};

const benchmarkPosts = async (): Promise<void> => {
  for (let index = 0; index < 5; index += 1) {
    // eslint-disable-next-line no-await-in-loop
    await fetch(`${baseUrl}/posts`);
  }

  const samples: number[] = [];

  for (let index = 0; index < 30; index += 1) {
    const started = performance.now();
    // eslint-disable-next-line no-await-in-loop
    const response = await fetch(`${baseUrl}/posts`);
    // eslint-disable-next-line no-await-in-loop
    await response.arrayBuffer();
    samples.push(performance.now() - started);
  }

  console.log(
    `\n  /posts latency (30 warm requests): p50=${percentile(samples, 50).toFixed(1)}ms p95=${percentile(samples, 95).toFixed(1)}ms max=${Math.max(...samples).toFixed(1)}ms`
  );
};

const testWebSocket = async (): Promise<void> => {
  // eslint-disable-next-line promise/avoid-new
  await new Promise<void>((resolve, reject) => {
    const socket = new WebSocket(`ws://127.0.0.1:${port}/ws`);
    const timer = setTimeout(() => {
      socket.close();
      reject(new Error("WebSocket timed out"));
    }, 5000);
    let connected = false;

    socket.addEventListener("message", (event) => {
      const message =
        typeof event.data === "string"
          ? event.data
          : new TextDecoder().decode(event.data as ArrayBuffer);

      if (!connected) {
        connected = true;

        if (message !== "connected") {
          clearTimeout(timer);
          reject(new Error(`unexpected WebSocket greeting: ${message}`));
          return;
        }

        socket.send("hello");
        return;
      }

      clearTimeout(timer);

      if (message !== "echo:hello") {
        reject(new Error(`unexpected WebSocket echo: ${message}`));
        return;
      }

      socket.close();
      resolve();
    });

    socket.addEventListener("error", () => {
      clearTimeout(timer);
      reject(new Error("WebSocket connection failed"));
    });
  });

  console.log("  ✓ WebSocket echo");
};

const run = async (): Promise<void> => {
  rmSync(persistDir, { force: true, recursive: true });
  mkdirSync(persistDir, { recursive: true });

  console.log("Applying the local D1 schema...");
  const schema = spawnSync(
    "bunx",
    [
      "wrangler",
      "d1",
      "execute",
      "DB",
      "--local",
      "--persist-to",
      persistDir,
      "--file",
      "schema.sql",
    ],
    { cwd: spikeRoot, stdio: "inherit" }
  );

  if (schema.status !== 0) {
    fail("wrangler d1 execute failed");
  }

  console.log(`Starting wrangler dev on ${baseUrl}...`);
  const server = spawn(
    "bunx",
    ["wrangler", "dev", "--port", port, "--local", "--persist-to", persistDir],
    { cwd: spikeRoot, stdio: "inherit" }
  );

  try {
    await waitForServer();

    console.log("\nCore routes");
    await assertStatus("GET /health", await fetch(`${baseUrl}/health`), 200);

    const created = await assertStatus(
      "POST /posts",
      await fetch(`${baseUrl}/posts`, {
        body: JSON.stringify({
          content: "e2e content",
          imageKey: "e2e.svg",
          title: "E2E post",
        }),
        headers: { "content-type": "application/json" },
        method: "POST",
      }),
      201
    );
    const post = await json<{ id: string }>(created);

    await assertStatus(
      "GET /posts/:id",
      await fetch(`${baseUrl}/posts/${post.id}`),
      200
    );
    await assertStatus(
      "PUT /posts/:id",
      await fetch(`${baseUrl}/posts/${post.id}`, {
        body: JSON.stringify({ title: "E2E post updated" }),
        headers: { "content-type": "application/json" },
        method: "PUT",
      }),
      200
    );
    await assertStatus(
      "DELETE /posts/:id",
      await fetch(`${baseUrl}/posts/${post.id}`, { method: "DELETE" }),
      204
    );
    await assertStatus(
      "GET deleted /posts/:id",
      await fetch(`${baseUrl}/posts/${post.id}`),
      404
    );

    console.log("\nAuth");
    const anonymous = await assertStatus(
      "POST /auth/anonymous",
      await fetch(`${baseUrl}/auth/anonymous`, { method: "POST" }),
      201
    );
    const anonymousCookie = cookieFrom(anonymous);
    await assertStatus(
      "GET /auth/session (anonymous)",
      await fetch(`${baseUrl}/auth/session`, {
        headers: { cookie: anonymousCookie },
      }),
      200
    );
    await assertStatus(
      "GET /me (anonymous)",
      await fetch(`${baseUrl}/me`, {
        headers: { cookie: anonymousCookie },
      }),
      200
    );

    console.log("\nR2 (Authenticated Image Upload)");
    const pngBytes = new Uint8Array([
      0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x00, 0x00, 0x00, 0x0d,
    ]);
    await assertStatus(
      "POST /upload (unauthenticated rejected)",
      await fetch(`${baseUrl}/upload`, {
        body: pngBytes,
        headers: { "content-type": "image/png" },
        method: "POST",
      }),
      401
    );
    await assertStatus(
      "POST /upload (SVG / invalid magic bytes rejected)",
      await fetch(`${baseUrl}/upload`, {
        body: "<svg onload=alert(1)></svg>",
        headers: {
          "content-type": "image/svg+xml",
          cookie: anonymousCookie,
        },
        method: "POST",
      }),
      415
    );
    const uploadRes = await assertStatus(
      "POST /upload (valid PNG)",
      await fetch(`${baseUrl}/upload?key=attacker_ignored.svg`, {
        body: pngBytes,
        headers: {
          "content-type": "image/png",
          cookie: anonymousCookie,
        },
        method: "POST",
      }),
      201
    );
    const uploaded = await json<{ key: string; url: string }>(uploadRes);
    if (
      uploaded.key === "attacker_ignored.svg" ||
      !uploaded.key.endsWith(".png")
    ) {
      fail(`expected server-generated .png key, got ${uploaded.key}`);
    }
    const image = await assertStatus(
      `GET /images/${uploaded.key}`,
      await fetch(`${baseUrl}/images/${uploaded.key}`),
      200
    );
    const imageBytes = new Uint8Array(await image.arrayBuffer());
    if (
      imageBytes.length !== pngBytes.length ||
      pngBytes.some((value, index) => imageBytes[index] !== value)
    ) {
      fail("PNG upload round-trip changed the bytes");
    }

    await assertStatus(
      "POST /auth/signout",
      await fetch(`${baseUrl}/auth/signout`, {
        headers: { cookie: anonymousCookie },
        method: "POST",
      }),
      204
    );

    const email = `e2e-${Date.now()}@reptar.dev`;
    const signup = await assertStatus(
      "POST /auth/signup",
      await fetch(`${baseUrl}/auth/signup`, {
        body: JSON.stringify({
          email,
          name: "E2E User",
          password: "hunter2hunter2",
        }),
        headers: { "content-type": "application/json" },
        method: "POST",
      }),
      201
    );
    const signupCookie = cookieFrom(signup);
    await assertStatus(
      "GET /auth/session (signup)",
      await fetch(`${baseUrl}/auth/session`, {
        headers: { cookie: signupCookie },
      }),
      200
    );
    await assertStatus(
      "POST /auth/signin",
      await fetch(`${baseUrl}/auth/signin`, {
        body: JSON.stringify({
          email,
          password: "hunter2hunter2",
        }),
        headers: { "content-type": "application/json" },
        method: "POST",
      }),
      200
    );

    console.log("\nPlatform bindings");
    const env = await assertStatus(
      "GET /platform/env",
      await fetch(`${baseUrl}/platform/env`),
      200
    );
    const envBody = await json<{ appName: string }>(env);
    if (envBody.appName !== "reptar-local") {
      fail(`unexpected APP_NAME: ${envBody.appName}`);
    }

    await assertStatus(
      "PUT /platform/kv",
      await fetch(`${baseUrl}/platform/kv`, {
        body: JSON.stringify({ ttlSeconds: 60, value: "e2e-value" }),
        headers: { "content-type": "application/json" },
        method: "PUT",
      }),
      204
    );
    const kv = await assertStatus(
      "GET /platform/kv",
      await fetch(`${baseUrl}/platform/kv`),
      200
    );
    const kvBody = await json<{ value: string }>(kv);
    if (kvBody.value !== "e2e-value") {
      fail(`unexpected KV value: ${kvBody.value}`);
    }

    await assertStatus(
      "POST /platform/queue",
      await fetch(`${baseUrl}/platform/queue`, {
        body: JSON.stringify({ message: "e2e-job" }),
        headers: { "content-type": "application/json" },
        method: "POST",
      }),
      202
    );

    await assertStatus(
      "GET /platform/durable",
      await fetch(`${baseUrl}/platform/durable`),
      200
    );

    const stream = await assertStatus(
      "GET /platform/stream",
      await fetch(`${baseUrl}/platform/stream`),
      200
    );
    const streamText = await stream.text();
    if (!streamText.includes("data: chunk-1")) {
      fail(`unexpected SSE body: ${streamText}`);
    }

    await testWebSocket();

    console.log("\nCORS");
    await assertStatus(
      "OPTIONS /posts (allowed origin)",
      await fetch(`${baseUrl}/posts`, {
        headers: {
          "access-control-request-method": "POST",
          origin: allowedOrigin,
        },
        method: "OPTIONS",
      }),
      200
    );
    await assertStatus(
      "OPTIONS /posts (denied origin)",
      await fetch(`${baseUrl}/posts`, {
        headers: {
          "access-control-request-method": "POST",
          origin: "https://evil.example",
        },
        method: "OPTIONS",
      }),
      403
    );

    await benchmarkPosts();
    console.log("\nE2E PASSED");
  } finally {
    server.kill("SIGTERM");
    spawnSync("pkill", ["-f", `wrangler dev --port ${port}`], {
      stdio: "ignore",
    });
  }
};

await run();
