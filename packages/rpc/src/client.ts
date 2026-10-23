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

type HasKeys<T> = keyof T extends never ? false : true;

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

export const createClient = <T extends RouteTree>(
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
      method: method.toUpperCase(),
      ...options.init,
    };

    if (args?.json !== undefined) {
      headers.set("content-type", "application/json");
      init.body = JSON.stringify(args.json);
    }

    const response = await (options.fetch ?? fetch)(
      buildUrl(baseUrl, segments, args),
      init
    );
    return response as ClientResponse;
  };

  const createProxy = (segments: string[]): unknown =>
    new Proxy(() => null, {
      apply(_target, _thisArg, argumentsList: unknown[]) {
        const [args] = argumentsList as [RequestArgs?];
        const last = segments.at(-1);

        if (last?.startsWith("$")) {
          return call(segments.slice(0, -1), last.slice(1), args);
        }

        return call(segments, "get", args);
      },
      get(_target, key) {
        if (typeof key !== "string" || key === "then") {
          return;
        }

        return createProxy([...segments, key]);
      },
    });

  return createProxy([]) as Client<T>;
};
