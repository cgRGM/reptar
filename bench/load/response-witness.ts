import { createHash } from "node:crypto";
import { request as httpRequest } from "node:http";
import { request as httpsRequest } from "node:https";
import { brotliDecompressSync, gunzipSync, inflateSync } from "node:zlib";

const MAX_RESPONSE_BYTES = 8 * 1024 * 1024;
const DEADLINE_MS = 10_000;

export interface ResponseWitness {
  status: number;
  contentType: string;
  encoding: string;
  encodedBytes: number;
  decodedBytes: number;
  encodedSha256: string;
  decodedSha256: string;
  correctnessSignature: string;
}

interface RawResponse {
  status: number;
  encoding: string;
  contentType: string;
  body: Buffer;
}

export const sha256 = (value: string | Uint8Array): string =>
  createHash("sha256").update(value).digest("hex");

const decode = (response: RawResponse): Buffer => {
  const options = { maxOutputLength: MAX_RESPONSE_BYTES };
  switch (response.encoding) {
    case "identity": {
      return response.body;
    }
    case "gzip": {
      return gunzipSync(response.body, options);
    }
    case "br": {
      return brotliDecompressSync(response.body, options);
    }
    case "deflate": {
      return inflateSync(response.body, options);
    }
    default: {
      throw new Error(`Unsupported response encoding: ${response.encoding}`);
    }
  }
};

const receive = (
  url: URL,
  method: string,
  headers: [string, string][],
  body?: string
): Promise<RawResponse> => {
  const { promise, resolve, reject } = Promise.withResolvers<RawResponse>();
  const transport = url.protocol === "https:" ? httpsRequest : httpRequest;
  const request = transport(
    url,
    { headers: Object.fromEntries(headers), method },
    (response) => {
      const chunks: Buffer[] = [];
      let size = 0;
      response.on("data", (chunk: Buffer) => {
        size += chunk.length;
        if (size > MAX_RESPONSE_BYTES) {
          const error = new Error(
            "Response witness exceeded encoded byte budget"
          );
          reject(error);
          request.destroy(error);
          return;
        }
        chunks.push(chunk);
      });
      response.once("aborted", () =>
        reject(new Error("Response witness aborted"))
      );
      response.once("error", reject);
      response.once("end", () =>
        resolve({
          body: Buffer.concat(chunks),
          contentType: String(
            response.headers["content-type"] ?? ""
          ).toLowerCase(),
          encoding: String(response.headers["content-encoding"] ?? "identity")
            .trim()
            .toLowerCase(),
          status: response.statusCode ?? 0,
        })
      );
    }
  );
  const deadline = setTimeout(
    () => request.destroy(new Error("Response witness deadline exceeded")),
    DEADLINE_MS
  );
  request.once("close", () => clearTimeout(deadline));
  request.once("error", reject);
  request.end(body);
  return promise;
};

/** Node HTTP intentionally preserves encoded bytes; fetch may transparently decode them. */
export const captureResponse = async (options: {
  url: string;
  method: string;
  headers: [string, string][];
  body?: string;
  validate: (status: number, decoded: Buffer) => string;
}): Promise<ResponseWitness> => {
  const url = new URL(options.url);
  if (
    !["http:", "https:"].includes(url.protocol) ||
    url.username ||
    url.password
  ) {
    throw new Error(
      "Response witness requires an HTTP(S) URL without credentials"
    );
  }
  const response = await receive(
    url,
    options.method,
    options.headers,
    options.body
  );
  const decoded = decode(response);
  return {
    contentType: response.contentType,
    correctnessSignature: options.validate(response.status, decoded),
    decodedBytes: decoded.length,
    decodedSha256: sha256(decoded),
    encodedBytes: response.body.length,
    encodedSha256: sha256(response.body),
    encoding: response.encoding,
    status: response.status,
  };
};
