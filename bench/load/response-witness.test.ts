import { expect, test } from "bun:test";
import { once } from "node:events";
import { createServer } from "node:http";
import type { Server } from "node:http";
import type { AddressInfo } from "node:net";
import { brotliCompressSync, gzipSync } from "node:zlib";

import { assertEquivalentResponse } from "./protocol";
import { captureResponse, sha256 } from "./response-witness";

const content = Buffer.from(' {"message":"Hello, World!"} ');

const listen = async (server: Server): Promise<void> => {
  const ready = once(server, "listening");
  server.listen(0, "127.0.0.1");
  await ready;
};

const close = async (server: Server): Promise<void> => {
  const closed = once(server, "close");
  server.close();
  await closed;
};

for (const encoding of ["identity", "gzip", "br"] as const) {
  test(`records actual ${encoding} wire bytes and validates decoded content`, async () => {
    let encoded = content;
    if (encoding === "gzip") {
      encoded = gzipSync(content);
    } else if (encoding === "br") {
      encoded = brotliCompressSync(content);
    }
    const server = createServer((request, response) => {
      expect(request.headers["accept-encoding"]).toBe("br, gzip");
      response.writeHead(200, {
        "Content-Encoding": encoding,
        "Content-Type": "application/json",
      });
      response.end(encoded);
    });
    await listen(server);
    try {
      const witness = await captureResponse({
        headers: [["Accept-Encoding", "br, gzip"]],
        method: "GET",
        url: `http://127.0.0.1:${(server.address() as AddressInfo).port}/`,
        validate: (status, decoded) => {
          expect(status).toBe(200);
          expect(JSON.parse(decoded.toString())).toEqual({
            message: "Hello, World!",
          });
          return sha256("validated-hello");
        },
      });
      expect(witness.encoding).toBe(encoding);
      expect(witness.encodedSha256).toBe(sha256(encoded));
      expect(witness.encodedBytes).toBe(encoded.length);
      expect(witness.decodedSha256).toBe(sha256(content));
      expect(witness.decodedBytes).toBe(content.length);
    } finally {
      await close(server);
    }
  });
}

test("the same request preference cannot hide an actual server compression mismatch", async () => {
  const server = createServer((request, response) => {
    const compressed = request.url === "/compressed";
    response.writeHead(200, {
      "Content-Encoding": compressed ? "gzip" : "identity",
      "Content-Type": "application/json",
    });
    response.end(compressed ? gzipSync(content) : content);
  });
  await listen(server);
  try {
    const capture = async (route: string) =>
      await captureResponse({
        headers: [["Accept-Encoding", "gzip"]],
        method: "GET",
        url: `http://127.0.0.1:${(server.address() as AddressInfo).port}/${route}`,
        validate: (_status, decoded) => {
          expect(decoded).toEqual(content);
          return sha256("same validated semantics");
        },
      });
    const compressed = await capture("compressed");
    const identity = await capture("identity");
    expect(compressed.decodedSha256).toBe(identity.decodedSha256);
    expect(() => assertEquivalentResponse(compressed, identity)).toThrow(
      "Content-Encoding"
    );
  } finally {
    await close(server);
  }
});

test("does not interpret a compression header as proof of valid compressed bytes", async () => {
  const server = createServer((_request, response) => {
    response.writeHead(200, { "Content-Encoding": "gzip" });
    response.end(content);
  });
  await listen(server);
  try {
    await expect(
      captureResponse({
        headers: [],
        method: "GET",
        url: `http://127.0.0.1:${(server.address() as AddressInfo).port}/`,
        validate: () => "must-not-be-called",
      })
    ).rejects.toThrow();
  } finally {
    await close(server);
  }
});
