import { Buffer } from "node:buffer";

import type {
  APIGatewayProxyEventV2,
  APIGatewayProxyResultV2,
} from "aws-lambda";

export interface FetchHandler {
  fetch: (request: Request, env?: unknown) => Promise<Response>;
}

export const createLambdaHandler =
  (
    handler: FetchHandler,
    env?: unknown
  ): ((event: APIGatewayProxyEventV2) => Promise<APIGatewayProxyResultV2>) =>
  async (event: APIGatewayProxyEventV2): Promise<APIGatewayProxyResultV2> => {
    const rawPath = event.rawPath || "/";
    const queryString = event.rawQueryString ? `?${event.rawQueryString}` : "";
    const domain = event.requestContext.domainName ?? "lambda.local";
    const url = `https://${domain}${rawPath}${queryString}`;

    const headers = new Headers();
    if (event.headers) {
      for (const [key, value] of Object.entries(event.headers)) {
        if (value !== undefined) {
          headers.set(key, value);
        }
      }
    }

    if (event.cookies && event.cookies.length > 0) {
      headers.set("Cookie", event.cookies.join("; "));
    }

    const { method } = event.requestContext.http;
    let body: BodyInit | undefined;

    if (event.body) {
      body = event.isBase64Encoded
        ? Buffer.from(event.body, "base64")
        : event.body;
    }

    const request = new Request(url, {
      body: method === "GET" || method === "HEAD" ? undefined : body,
      headers,
      method,
    });

    const response = await handler.fetch(request, env);
    const responseHeaders: Record<string, string> = {};
    const cookies: string[] = [];

    for (const [key, value] of response.headers.entries()) {
      if (key.toLowerCase() === "set-cookie") {
        cookies.push(value);
      } else {
        responseHeaders[key] = value;
      }
    }

    const arrayBuffer = await response.arrayBuffer();
    const isBase64 = arrayBuffer.byteLength > 0;
    const responseBody = isBase64
      ? Buffer.from(arrayBuffer).toString("base64")
      : "";

    return {
      body: responseBody,
      cookies: cookies.length > 0 ? cookies : undefined,
      headers: responseHeaders,
      isBase64Encoded: isBase64,
      statusCode: response.status,
    };
  };
