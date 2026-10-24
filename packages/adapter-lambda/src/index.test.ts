import { describe, expect, it } from "bun:test";
import { Buffer } from "node:buffer";

import type {
  APIGatewayProxyEventV2,
  APIGatewayProxyStructuredResultV2,
} from "aws-lambda";

import { createLambdaHandler } from "./index";

describe("createLambdaHandler", () => {
  it("adapts API Gateway v2 event to Fetch Request and returns APIGatewayProxyResultV2", async () => {
    const handler = {
      fetch: async (req: Request) => {
        expect(req.method).toBe("POST");
        expect(req.url).toBe("https://api.example.com/posts?page=1");
        expect(req.headers.get("x-custom")).toBe("value");
        expect(req.headers.get("cookie")).toBe("session=abc");
        const bodyText = await req.text();
        expect(bodyText).toBe('{"hello":"world"}');

        return new Response('{"status":"ok"}', {
          headers: {
            "content-type": "application/json",
            "set-cookie": "session=updated; HttpOnly",
          },
          status: 201,
        });
      },
    };

    const lambdaHandler = createLambdaHandler(handler);

    const event: APIGatewayProxyEventV2 = {
      body: Buffer.from('{"hello":"world"}').toString("base64"),
      cookies: ["session=abc"],
      headers: {
        "x-custom": "value",
      },
      isBase64Encoded: true,
      rawPath: "/posts",
      rawQueryString: "page=1",
      requestContext: {
        accountId: "123456789012",
        apiId: "api-id",
        domainName: "api.example.com",
        domainPrefix: "api",
        http: {
          method: "POST",
          path: "/posts",
          protocol: "HTTP/1.1",
          sourceIp: "127.0.0.1",
          userAgent: "test",
        },
        requestId: "req-1",
        routeKey: "POST /posts",
        stage: "$default",
        time: "12/Mar/2026:19:00:00 +0000",
        timeEpoch: 1_428_582_256_000,
      },
      routeKey: "POST /posts",
      version: "2.0",
    };

    const result = (await lambdaHandler(
      event
    )) as APIGatewayProxyStructuredResultV2;
    expect(result.statusCode).toBe(201);
    expect(result.isBase64Encoded).toBe(true);
    expect(result.cookies).toEqual(["session=updated; HttpOnly"]);
    const decodedBody = Buffer.from(result.body ?? "", "base64").toString(
      "utf-8"
    );
    expect(decodedBody).toBe('{"status":"ok"}');
  });
});
