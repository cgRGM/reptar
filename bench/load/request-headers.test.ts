import { expect, test } from "bun:test";

import { buildRequestHeaders } from "./request-headers";

test("framework comparisons request identity encoding explicitly", () => {
  expect(
    buildRequestHeaders(undefined, "identity").get("Accept-Encoding")
  ).toBe("identity");
});

test("compression experiments retain authentication and content type", () => {
  const headers = buildRequestHeaders(
    {
      Authorization: "Bearer fixture-token",
      "Content-Type": "application/json",
    },
    "br"
  );
  expect(headers.get("accept-encoding")).toBe("br");
  expect(headers.get("authorization")).toBe("Bearer fixture-token");
  expect(headers.get("content-type")).toBe("application/json");
});

test("scenario encoding overrides are case insensitive and not duplicated", () => {
  const headers = buildRequestHeaders(
    { "accept-encoding": "gzip" },
    "identity"
  );
  expect(headers.get("Accept-Encoding")).toBe("gzip");
  expect([...headers.keys()]).toEqual(["accept-encoding"]);
});
