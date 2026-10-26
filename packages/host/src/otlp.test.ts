import { afterEach, describe, expect, test } from "bun:test";

import { createOtlpExporter } from "./otlp";

const originalFetch = globalThis.fetch;

afterEach(() => {
  globalThis.fetch = originalFetch;
});

describe("createOtlpExporter", () => {
  test("maps a request event to an OTLP/HTTP JSON span", async () => {
    let body: unknown;
    let headers: HeadersInit | undefined;
    globalThis.fetch = ((input, init) => {
      expect(String(input)).toBe("https://otel.test/v1/traces");
      body = JSON.parse(String(init?.body));
      headers = init?.headers;
      return Promise.resolve(new Response(null, { status: 200 }));
    }) as typeof fetch;

    const exportEvent = createOtlpExporter({
      endpoint: "https://otel.test/v1/traces",
      headers: { authorization: "Bearer test" },
      serviceName: "reptar-test",
    });

    await exportEvent({
      cf_ray: "8d39f41b2-CMH",
      colo: "CMH",
      db_ms: 1.2,
      db_queries: 2,
      duration_ms: 4.5,
      method: "GET",
      path: "/posts/1",
      request_id: "req_1",
      route: "/posts/:id",
      span_id: "00f067aa0ba902b7",
      status: 200,
      trace_id: "4bf92f3577b34da6a3ce929d0e0e4736",
      wasm_ms: 3.1,
    });

    const { resourceSpans } = body as {
      resourceSpans: {
        resource: {
          attributes: { key: string; value: { stringValue: string } }[];
        };
        scopeSpans: {
          spans: {
            attributes: { key: string }[];
            name: string;
            spanId: string;
            status: { code: number };
            traceId: string;
          }[];
        }[];
      }[];
    };
    const span = resourceSpans[0]?.scopeSpans[0]?.spans[0];

    expect(span?.traceId).toBe("4bf92f3577b34da6a3ce929d0e0e4736");
    expect(span?.spanId).toBe("00f067aa0ba902b7");
    expect(span?.name).toBe("GET /posts/:id");
    expect(span?.status.code).toBe(0);
    expect(span?.attributes.map((attribute) => attribute.key)).toContain(
      "reptar.db_ms"
    );
    expect(resourceSpans[0]?.resource.attributes[0]?.value.stringValue).toBe(
      "reptar-test"
    );
    expect((headers as Record<string, string>).authorization).toBe(
      "Bearer test"
    );
  });

  test("marks 5xx spans as errors", async () => {
    let body: unknown;
    globalThis.fetch = ((_input, init) => {
      body = JSON.parse(String(init?.body));
      return Promise.resolve(new Response(null, { status: 200 }));
    }) as typeof fetch;

    const exportEvent = createOtlpExporter({
      endpoint: "https://otel.test/v1/traces",
    });

    await exportEvent({
      duration_ms: 1,
      method: "GET",
      status: 503,
      trace_id: "4bf92f3577b34da6a3ce929d0e0e4736",
    });

    const span = (
      body as {
        resourceSpans: {
          scopeSpans: { spans: { status: { code: number } }[] }[];
        }[];
      }
    ).resourceSpans[0]?.scopeSpans[0]?.spans[0];

    expect(span?.status.code).toBe(2);
  });
});
