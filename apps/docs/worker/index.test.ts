import { expect, test } from "bun:test";

import worker from "./index";

test("benchmark shortcut redirects to the verified report without fetching assets", async () => {
  const response = await worker.fetch(
    new Request(
      "https://reptar-bench.rocktownlabs.app/?next=https://example.test"
    ),
    {
      ASSETS: {
        fetch: () => {
          throw new Error("The benchmark shortcut must not fetch assets");
        },
      },
    }
  );
  expect(response.status).toBe(302);
  expect(response.headers.get("location")).toBe(
    "https://reptar-docs.rocktownlabs.app/overview/release-benchmarks"
  );
});

test("custom-domain docs preserve HTML and existing Vary headers", async () => {
  const response = await worker.fetch(
    new Request("https://reptar-docs.rocktownlabs.app/core/routing"),
    {
      ASSETS: {
        fetch: () =>
          Promise.resolve(
            new Response("<h1>Routing</h1>", {
              headers: { vary: "Accept-Encoding" },
            })
          ),
      },
    }
  );
  expect(await response.text()).toBe("<h1>Routing</h1>");
  expect(response.headers.get("vary")).toContain("Accept-Encoding");
  expect(response.headers.get("vary")).toContain("Accept");
});

test("custom-domain Markdown negotiation fetches the correct twin", async () => {
  let requested = "";
  const response = await worker.fetch(
    new Request(
      "https://reptar-docs.rocktownlabs.app/overview/release-benchmarks",
      {
        headers: { accept: "text/markdown" },
      }
    ),
    {
      ASSETS: {
        fetch: (input) => {
          requested = String(input);
          return Promise.resolve(new Response("# Verified benchmarks"));
        },
      },
    }
  );
  expect(requested).toBe(
    "https://reptar-docs.rocktownlabs.app/overview/release-benchmarks.md"
  );
  expect(response.headers.get("content-type")).toContain("text/markdown");
  expect(await response.text()).toBe("# Verified benchmarks");
});

test("legacy workers.dev continues serving HTML without redirecting", async () => {
  const response = await worker.fetch(
    new Request("https://reptar-docs.rocktown-labs.workers.dev/"),
    { ASSETS: { fetch: () => Promise.resolve(new Response("legacy docs")) } }
  );
  expect(response.status).toBe(200);
  expect(await response.text()).toBe("legacy docs");
});

test("Markdown 404 links use the canonical domain on legacy requests", async () => {
  const response = await worker.fetch(
    new Request("https://reptar-docs.rocktown-labs.workers.dev/missing", {
      headers: { accept: "text/markdown" },
    }),
    {
      ASSETS: {
        fetch: () => Promise.resolve(new Response(null, { status: 404 })),
      },
    }
  );
  expect(response.status).toBe(404);
  expect(await response.text()).toContain(
    "https://reptar-docs.rocktownlabs.app/llms.txt"
  );
});
