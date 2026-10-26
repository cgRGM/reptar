import { expect, test } from "bun:test";

import type { Endpoint } from "./client";
import { createClient } from "./client";

interface QueryApp {
  statusProbe: {
    $get: Endpoint<
      undefined,
      { enabled: boolean } | { error: string },
      200 | 400,
      { 200: { enabled: boolean }; 400: { error: string } }
    >;
  };
  flags: {
    ":key": {
      $get: Endpoint<
        {
          param: { key: string };
          query: { subject: string; limit?: number | null; enabled?: boolean };
        },
        { enabled: boolean },
        200 | 304
      >;
    };
  };
}

test("typed query client encodes values and omits absent nullable fields", async () => {
  let requested = "";
  const client = createClient<QueryApp>("https://example.test", {
    fetch: (input) => {
      requested = String(input);
      return Promise.resolve(Response.json({ enabled: true }));
    },
  });
  const response = await client.flags[":key"].$get({
    param: { key: "new checkout" },
    query: { enabled: false, limit: null, subject: "a+b & c" },
  });
  const url = new URL(requested);
  expect(url.pathname).toBe("/flags/new%20checkout");
  expect(url.searchParams.get("subject")).toBe("a+b & c");
  expect(url.searchParams.get("enabled")).toBe("false");
  expect(url.searchParams.has("limit")).toBe(false);
  expect(response.ok).toBe(true);
  expect(await response.json()).toEqual({ enabled: true });
});

test("304 is bodyless and status narrowing gives json a never result", async () => {
  const client = createClient<QueryApp>("https://example.test", {
    fetch: () => Promise.resolve(new Response(null, { status: 304 })),
  });
  const response = await client.flags[":key"].$get({
    param: { key: "checkout" },
    query: { subject: "subject" },
  });
  expect(response.status).toBe(304);
  if (response.status === 304) {
    // Compile-time regression: a bodyless response cannot promise a flag DTO.
    const json: () => Promise<never> = response.json;
    expect(typeof json).toBe("function");
    expect(response.ok).toBe(false);
  }
});

test("status maps narrow success and error JSON bodies without Response.json any leakage", async () => {
  await Promise.all(
    [200, 400].map(async (status) => {
      const client = createClient<QueryApp>("https://example.test", {
        fetch: () =>
          Promise.resolve(
            Response.json(
              status === 200 ? { enabled: true } : { error: "invalid" },
              { status }
            )
          ),
      });
      const response = await client.statusProbe.$get();
      if (response.ok) {
        const body: { enabled: boolean } = await response.json();
        expect(body.enabled).toBe(true);
      } else {
        const body: { error: string } = await response.json();
        expect(body.error).toBe("invalid");
      }
    })
  );
});
