import type { ReptarTelemetryEvent } from "./host";

export interface OtlpExporterOptions {
  /**
   * Full OTLP/HTTP traces endpoint, for example
   * https://api.axiom.co/v1/traces or http://localhost:4318/v1/traces.
   */
  endpoint: string;
  headers?: Record<string, string>;
  serviceName?: string;
  serviceVersion?: string;
}

const HEX = "0123456789abcdef";

const randomHex = (bytes: number): string => {
  const data = new Uint8Array(bytes);
  crypto.getRandomValues(data);
  let out = "";

  for (const byte of data) {
    out += HEX[Math.floor(byte / 16)] + HEX[byte % 16];
  }

  return out;
};

const isHex = (value: unknown, length: number): value is string =>
  typeof value === "string" &&
  value.length === length &&
  /^[0-9a-f]+$/iu.test(value);

const stringAttribute = (key: string, value: unknown) => {
  if (value === undefined || value === null || value === "") {
    return null;
  }

  return { key, value: { stringValue: String(value) } };
};

const intAttribute = (key: string, value: unknown) => {
  if (typeof value !== "number" || !Number.isFinite(value)) {
    return null;
  }

  return { key, value: { intValue: String(Math.trunc(value)) } };
};

const numberAttribute = (key: string, value: unknown) => {
  if (typeof value !== "number" || !Number.isFinite(value)) {
    return null;
  }

  return { key, value: { doubleValue: value } };
};

/**
 * Minimal OTLP/HTTP JSON exporter. It deliberately depends only on fetch and
 * the standard Web Crypto API so it can run in a Worker without an OTel SDK.
 * The host calls it once per sampled request and hands the returned promise to
 * ctx.waitUntil().
 */
export const createOtlpExporter =
  (options: OtlpExporterOptions) =>
  async (event: ReptarTelemetryEvent): Promise<void> => {
    const traceId = isHex(event.trace_id, 32) ? event.trace_id : randomHex(16);
    const spanId = isHex(event.span_id, 16) ? event.span_id : randomHex(8);
    const parentSpanId = isHex(event.parent_span_id, 16)
      ? event.parent_span_id
      : undefined;
    const durationMs =
      typeof event.duration_ms === "number" &&
      Number.isFinite(event.duration_ms)
        ? Math.max(0, event.duration_ms)
        : 0;
    const endMs = Date.now();
    const startMs = Math.max(0, endMs - durationMs);
    const status =
      typeof event.status === "number" ? Math.trunc(event.status) : 0;
    const route = String(event.route ?? event.path ?? "request");
    const method = String(event.method ?? "HTTP");
    const attributes = [
      stringAttribute("http.request.method", method),
      stringAttribute("url.path", event.path),
      stringAttribute("reptar.route", route),
      intAttribute("http.response.status_code", status),
      numberAttribute("reptar.duration_ms", event.duration_ms),
      numberAttribute("reptar.db_ms", event.db_ms),
      intAttribute("reptar.db_queries", event.db_queries),
      numberAttribute("reptar.wasm_ms", event.wasm_ms),
      stringAttribute("reptar.cache", event.cache),
      stringAttribute("reptar.user_id", event.user_id),
      stringAttribute("reptar.cf_ray", event.cf_ray),
      stringAttribute("reptar.colo", event.colo),
      stringAttribute("reptar.error_code", event.error_code),
    ].filter((attribute) => attribute !== null);

    const body = {
      resourceSpans: [
        {
          resource: {
            attributes: [
              {
                key: "service.name",
                value: {
                  stringValue: options.serviceName ?? "reptar",
                },
              },
              {
                key: "service.version",
                value: {
                  stringValue: options.serviceVersion ?? "0.1.0",
                },
              },
            ],
          },
          scopeSpans: [
            {
              scope: { name: "reptar", version: "0.1.0" },
              spans: [
                {
                  attributes,
                  endTimeUnixNano: `${endMs}000000`,
                  kind: 2,
                  name: `${method} ${route}`,
                  ...(parentSpanId ? { parentSpanId } : {}),
                  spanId,
                  startTimeUnixNano: `${startMs}000000`,
                  status: {
                    code: status >= 500 || event.error_code ? 2 : 0,
                  },
                  traceId,
                },
              ],
            },
          ],
        },
      ],
    };

    const response = await fetch(options.endpoint, {
      body: JSON.stringify(body),
      headers: {
        "content-type": "application/json",
        ...options.headers,
      },
      method: "POST",
    });

    if (!response.ok) {
      throw new Error(
        `OTLP export failed with HTTP ${response.status} ${response.statusText}`
      );
    }
  };
