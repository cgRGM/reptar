import { describe, expect, test } from "bun:test";

import { createReptarHost } from "./host";
import type {
  ExecutionContextLike,
  ReptarWasmApp,
} from "./host";

const wasm = {} as WebAssembly.Module;
const scope = globalThis as unknown as {
  __REPTAR_RESPOND?: (requestId: string, value: string) => void;
  __REPTAR_TELEMETRY?: (json: string) => void;
};

const jsonResponse = (status = 200, body = "ok") =>
  JSON.stringify({
    body: Buffer.from(body).toString("base64"),
    headers: { "content-type": "text/plain" },
    setCookies: [],
    status,
  });

const createHost = (
  app: ReptarWasmApp,
  options: {
    onTelemetry?: (event: Record<string, unknown>) => void;
    requestTimeoutMs?: number;
  } = {}
) =>
  createReptarHost<Record<string, never>>({
    bindings: () => ({}),
    onTelemetry: options.onTelemetry,
    open: async () => app,
    requestTimeoutMs: options.requestTimeoutMs,
    wasm,
  });

const request = (path = "/") =>
  new Request(`https://example.test${path}`, {
    headers: { "cf-ray": "8d39f41b2-CMH" },
  });

describe("createReptarHost boundary", () => {
  test("supports the legacy synchronous handle export", async () => {
    const host = createHost({
      handle: (json) => {
        const envelope = JSON.parse(json) as { requestId: string };
        scope.__REPTAR_RESPOND?.(envelope.requestId, jsonResponse());
      },
    });

    const response = await host.fetch(request(), {});

    expect(response.status).toBe(200);
    expect(await response.text()).toBe("ok");
    expect(response.headers.get("x-request-id")).toBeTruthy();
    expect(response.headers.get("x-trace-id")).toHaveLength(32);
  });

  test("awaits an async handle export", async () => {
    const host = createHost({
      handle: async (json) => {
        await Promise.resolve();
        const envelope = JSON.parse(json) as { requestId: string };
        scope.__REPTAR_RESPOND?.(envelope.requestId, jsonResponse(201, "made"));
      },
    });

    const response = await host.fetch(request("/made"), {});

    expect(response.status).toBe(201);
    expect(await response.text()).toBe("made");
  });

  test("turns a rejected async export into a 500 instead of hanging", async () => {
    const host = createHost({
      handle: async () => {
        throw new Error("boom");
      },
    });

    const response = await host.fetch(request(), {});

    expect(response.status).toBe(500);
    expect(await response.text()).toContain("failed");
  });

  test("reports an export that completes without responding", async () => {
    const host = createHost({
      handle: async () => {
        // Intentionally forgets to call respond().
      },
    });

    const response = await host.fetch(request(), {});

    expect(response.status).toBe(502);
    expect(await response.text()).toContain("without a response");
  });

  test("times out a handler that never responds", async () => {
    const host = createHost(
      {
        handle: () => {
          // Intentionally never responds.
        },
      },
      { requestTimeoutMs: 20 }
    );

    const response = await host.fetch(request(), {});

    expect(response.status).toBe(504);
  });

  test("hands the async export to ctx.waitUntil after responding", async () => {
    let release: (() => void) | undefined;
    const background = new Promise<void>((resolve) => {
      release = resolve;
    });
    const waitUntil: Promise<unknown>[] = [];
    const ctx: ExecutionContextLike = {
      waitUntil: (promise) => {
        waitUntil.push(promise);
      },
    };
    const host = createHost({
      handle: async (json) => {
        const envelope = JSON.parse(json) as { requestId: string };
        scope.__REPTAR_RESPOND?.(envelope.requestId, jsonResponse());
        await background;
      },
    });

    const response = await host.fetch(request(), {}, ctx);

    expect(response.status).toBe(200);
    expect(waitUntil).toHaveLength(1);

    release?.();
    await waitUntil[0];
  });

  test("enriches the C# request event with trace and edge fields", async () => {
    const events: Record<string, unknown>[] = [];
    const host = createHost(
      {
        handle: (json) => {
          const envelope = JSON.parse(json) as {
            requestId: string;
            headers: Record<string, string>;
          };
          expect(envelope.headers.traceparent).toMatch(
            /^00-[0-9a-f]{32}-[0-9a-f]{16}-01$/u
          );
          scope.__REPTAR_TELEMETRY?.(
            JSON.stringify({
              cf_ray: "8d39f41b2-CMH",
              duration_ms: 2.5,
              method: "GET",
              request_id: envelope.requestId,
              route: "/",
              status: 200,
              trace_id: envelope.headers.traceparent.split("-")[1],
            })
          );
          scope.__REPTAR_RESPOND?.(envelope.requestId, jsonResponse());
        },
      },
      { onTelemetry: (event) => events.push(event) }
    );

    await host.fetch(request(), {});

    expect(events).toHaveLength(1);
    expect(events[0]?.colo).toBe("CMH");
    expect(events[0]?.span_id).toHaveLength(16);
    expect(events[0]?.parent_span_id).toHaveLength(16);
    expect(events[0]?.wasm_ms).toBeGreaterThanOrEqual(0);
  });
});
