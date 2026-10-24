export interface Endpoint<I, O, S extends number> {
  input: I;
  output: O;
  status: S;
}

export interface RouteTree {
  [key: string]: Endpoint<unknown, unknown, number> | RouteTree;
}

type SuccessStatus = 200 | 201 | 202 | 203 | 204 | 205 | 206;

export type ClientResponse<O = unknown, S extends number = number> = Response &
  (S extends SuccessStatus
    ? {
        json: () => Promise<O>;
        ok: true;
        status: S;
      }
    : {
        json: () => Promise<O>;
        ok: false;
        status: S;
      });

type HasKeys<T> = [T] extends [undefined | null | Record<string, never>]
  ? false
  : [keyof T] extends [never]
    ? false
    : true;

export type Client<T> = {
  [K in keyof T]: T[K] extends Endpoint<infer I, infer O, infer S>
    ? HasKeys<I> extends true
      ? (args: I) => Promise<ClientResponse<O, S>>
      : (args?: I) => Promise<ClientResponse<O, S>>
    : T[K] extends object
      ? Client<T[K]>
      : never;
};

export interface ClientOptions {
  fetch?: typeof fetch;
  credentials?: RequestCredentials;
  headers?: Record<string, string>;
  init?: RequestInit;
}

interface RequestArgs {
  json?: unknown;
  param?: Record<string, string>;
  query?: Record<string, string | number | boolean | undefined>;
}

const buildUrl = (
  baseUrl: string,
  segments: string[],
  args?: RequestArgs
): string => {
  let path = segments.join("/");

  if (args?.param) {
    for (const [key, value] of Object.entries(args.param)) {
      path = path.replaceAll(`:${key}`, encodeURIComponent(value));
    }
  }

  let url = `${baseUrl.replace(/\/+$/u, "")}/${path.replace(/^\/+/u, "")}`;

  if (args?.query) {
    const search = new URLSearchParams();

    for (const [key, value] of Object.entries(args.query)) {
      if (value !== undefined) {
        search.set(key, String(value));
      }
    }

    const query = search.toString();
    if (query) {
      url += `?${query}`;
    }
  }

  return url;
};

const proxyTarget = () => null;

export const createClient = <T extends object = RouteTree>(
  baseUrl: string,
  options: ClientOptions = {}
): Client<T> => {
  const call = async (
    segments: string[],
    method: string,
    args?: RequestArgs
  ): Promise<ClientResponse> => {
    const headers = new Headers(options.headers);
    const init: RequestInit = {
      credentials: options.credentials ?? "include",
      headers,
      method,
      ...options.init,
    };

    if (args?.json !== undefined) {
      headers.set("content-type", "application/json");
      init.body = JSON.stringify(args.json);
    }

    const fetchImpl = options.fetch ?? fetch;
    const response = await fetchImpl(buildUrl(baseUrl, segments, args), init);
    return response as ClientResponse;
  };

  const createProxy = (pathSegments: string[]): unknown =>
    new Proxy(proxyTarget, {
      apply: (_target, _thisArg, argArray) => {
        const method = pathSegments.at(-1) ?? "get";
        const routeSegments = pathSegments.slice(0, -1);
        return call(routeSegments, method.toUpperCase(), argArray[0]);
      },
      get: (_target, prop) => {
        if (typeof prop !== "string" || prop === "then") {
          return;
        }

        return createProxy([...pathSegments, prop]);
      },
    });

  return createProxy([]) as Client<T>;
};
